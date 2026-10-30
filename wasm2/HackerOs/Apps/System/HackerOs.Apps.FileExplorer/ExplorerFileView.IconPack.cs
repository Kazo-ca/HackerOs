using HackerOs.AppSdk;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.Icons;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Settings;
using HackerOs.Apps.FileExplorer.FileControl.Icons;
using HackerOs.Theming.Abstractions.IconPacks;

namespace HackerOs.Apps.FileExplorer;

/// <summary>
/// The icon pack of the file control (ADR 0203 §4): the pack named by the OS document <c>icons</c>,
/// read with the host's rights through the public SDK, draws the folders, the files and the app
/// shortcuts; the control follows a change of <c>icons.config</c> at once.
/// </summary>
public sealed partial class ExplorerFileView
{
    private HackSettingsDocument? _iconSettings;
    private IconPack? _iconPack;
    private IShellIconProvider? _iconProvider;
    private string? _iconPackId;
    private int _iconPackGeneration;

    /// <summary>The icons of the files: the pack's provider, or <see langword="null"/> for the built-in icons.</summary>
    private IShellIconProvider? IconProvider => _iconProvider;

    /// <summary>The icon of an app shortcut: the pack's app or category image, else the shared Lucide icon (ADR 0160 §6).</summary>
    /// <param name="appId">The app id.</param>
    /// <param name="category">Its category, when the app is installed.</param>
    private ShellIconDescriptor AppShortcutIcon(string appId, string? category) =>
        _iconPack?.FindApp(appId, category) is { } url
            ? ShellIconDescriptor.Png(url)
            : ShellIconDescriptor.Vector(IconLibrary.Lucide, HackAppIcons.LucideName(appId, category));

    /// <summary>Opens <c>icons.config</c>, loads its pack and follows its changes. Never fails the control.</summary>
    private async Task StartIconPackAsync()
    {
        try
        {
            _iconSettings = await HackSettings.OpenSystemAsync(HostContext, IconPackLibrary.SettingsDocumentId, HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A host without OS settings (a test context): the built-in icons.
            return;
        }

        _iconSettings.Changed += OnIconSettingsChanged;
        await LoadIconPackAsync();
    }

    private void OnIconSettingsChanged(object? sender, HackSettingsChangedEventArgs args) =>
        _ = InvokeAsync(async () =>
        {
            await LoadIconPackAsync();
            if (_view is not null)
            {
                // Re-list: the shortcuts and the folder previews take the new images.
                await _view.RefreshAsync();
            }

            StateHasChanged();
        });

    /// <summary>Reads the selected pack; an unknown id, or a pack that cannot be read, keeps the built-in icons.</summary>
    private async Task LoadIconPackAsync()
    {
        if (_iconSettings is null)
        {
            return;
        }

        string id = _iconSettings.Get<string>(IconPackLibrary.PackIdKey);
        if (string.Equals(id, _iconPackId, StringComparison.Ordinal))
        {
            return;
        }

        // A newer change may start while this one reads: only the last one is applied.
        int generation = ++_iconPackGeneration;
        IconPack? pack = null;
        if (!string.Equals(id, IconPackLibrary.BuiltInId, StringComparison.Ordinal))
        {
            try
            {
                using IDisposable scope = HackExecutionContext.Enter(HostContext);
                IconPackFileAccess files = new(
                    async (directory, token) => await HackDirectory.GetDirectoriesAsync(directory, cancellationToken: token),
                    async (directory, token) => await HackDirectory.GetFilesAsync(directory, cancellationToken: token),
                    HackFile.ReadAllBytesAsync);
                string home = HackUser.Describe(HostContext).HomeDirectory;
                IconPackListResult packs = await IconPackLibrary.ListAsync(files, home, HostContext.CancellationToken);
                if (packs.Find(id) is { } info)
                {
                    pack = (await IconPackLibrary.LoadAsync(info, files, HostContext.CancellationToken)).Pack;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                pack = null;
            }
        }

        if (generation != _iconPackGeneration)
        {
            return;
        }

        _iconPackId = id;
        _iconPack = pack;
        _iconProvider = pack is null ? null : new IconPackShellIconProvider(pack);
    }

    private async Task StopIconPackAsync()
    {
        if (_iconSettings is null)
        {
            return;
        }

        _iconSettings.Changed -= OnIconSettingsChanged;
        await _iconSettings.DisposeAsync();
        _iconSettings = null;
    }
}
