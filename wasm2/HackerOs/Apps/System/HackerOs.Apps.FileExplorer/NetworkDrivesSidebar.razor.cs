using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.FileExplorer;

/// <summary>
/// Lists the network drives through <see cref="HackDriveInfo"/> and mounts, reconnects and unmounts
/// them (ADR 0181, section 11). The browser opens its folder dialog only after a user gesture, so
/// every action runs straight from the click.
/// </summary>
public partial class NetworkDrivesSidebar
{
    private IReadOnlyList<HackDriveInfo> _drives = [];
    private bool _supported;
    private bool _busy;

    /// <summary>The explorer's texts in the language of its process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => AppContext.GetLocalizer<Strings>();

    /// <summary>The folder the explorer shows, to mark the drive it is on.</summary>
    [Parameter]
    public string? CurrentPath { get; set; }

    /// <summary>Opens a folder in the explorer.</summary>
    [Parameter]
    public EventCallback<string> Navigate { get; set; }

    /// <summary>Shows an error in the explorer.</summary>
    [Parameter]
    public EventCallback<string> Failed { get; set; }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            _supported = await HackDriveInfo.IsHostFolderSupportedAsync();
        }
        catch (NotSupportedException)
        {
            _supported = false;
        }

        await ReloadAsync();
    }

    /// <summary>Re-reads the drives; a drive mounted from the terminal shows up at the next navigation.</summary>
    protected override Task OnParametersSetAsync() => ReloadAsync();

    /// <summary>Re-reads the network drives.</summary>
    public async Task ReloadAsync()
    {
        try
        {
            _drives = [.. (await HackDriveInfo.GetDrivesAsync()).Where(static drive => drive.DriveType == HackDriveType.Network)];
        }
        catch (NotSupportedException)
        {
            _drives = [];
        }
    }

    private bool IsCurrent(HackDriveInfo drive) =>
        CurrentPath is { } path && (path == drive.Name || path.StartsWith(drive.Name + "/", StringComparison.Ordinal));

    private Task MountAsync() => RunAsync(async () =>
    {
        if (await HackDriveInfo.MountHostFolderAsync() is { } drive)
        {
            await ReloadAsync();
            await Navigate.InvokeAsync(drive.Name);
        }
    });

    private Task OpenAsync(HackDriveInfo drive) => RunAsync(async () =>
    {
        if (!drive.IsReady && !await HackDriveInfo.ReconnectAsync(drive.Name))
        {
            await Failed.InvokeAsync(L["Network.PermissionRefused", drive.VolumeLabel]);
            return;
        }

        await ReloadAsync();
        await Navigate.InvokeAsync(drive.Name);
    });

    private Task UnmountAsync(HackDriveInfo drive) => RunAsync(async () =>
    {
        bool inside = IsCurrent(drive);
        await HackDriveInfo.UnmountAsync(drive.Name);
        await ReloadAsync();
        if (inside)
        {
            await Navigate.InvokeAsync(HackUser.Describe(AppContext!).HomeDirectory);
        }
    });

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Failed.InvokeAsync(L["Network.Error", exception.Message]);
        }
        finally
        {
            _busy = false;
        }
    }
}
