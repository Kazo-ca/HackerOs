using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Desktop;

/// <summary>
/// The desktop app's only view (ADR 0160): shows <c>~/Desktop</c> through the OS file control in its
/// desktop mode, keeps <c>desktop.config</c> in step, adds "View", "Sort by" and the background entries
/// to the desktop's menu, and writes the shortcuts "Send to ▸ Desktop" asks for.
/// </summary>
/// <remarks>
/// Everything runs with the user's rights (ADR 0043): the folder, the settings file and the shortcuts
/// are the user's files. The settings file is followed with a watch, so a change made in Settings or by
/// hand shows at once.
/// </remarks>
public partial class DesktopSurface : IAsyncDisposable
{
    /// <summary>Settings opens its "Appearance" page for "Change background…".</summary>
    private const string SettingsAppId = "org.hackeros.settings";

    private string? _desktopDirectory;
    private KazoDesktopSettingsDocument _settings = new(KazoDesktopOptions.Default, new Dictionary<string, KazoDesktopCell>());
    private IStringLocalizer<Strings>? _localizer;
    private Task _writes = Task.CompletedTask;
    private ITopicChannelSubscription<FileSystemChangeEvent>? _settingsWatch;
    private CancellationTokenSource? _watchCts;

    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    // Dot directories hold desktop metadata, including the mobile layout, rather than visible icons.
    private static bool IsVisibleDesktopItem(KazoFileViewItem item) => !item.Name.StartsWith('.');

    /// <inheritdoc />
    /// <remarks>
    /// Reads the settings before the view is shown, so the first render places the icons where they
    /// were left; creates <c>~/Desktop</c> when it is missing, as a desktop does at first sign-in.
    /// </remarks>
    protected override async Task OnAppInitializedAsync()
    {
        string home = KazoUser.Describe(AppContext).HomeDirectory;
        string desktop = home.TrimEnd('/') + "/Desktop";
        try
        {
            if (!await KazoDirectory.ExistsAsync(desktop, AppContext.CancellationToken))
            {
                await KazoDirectory.CreateDirectoryAsync(desktop, AppContext.CancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ReportErrorAsync(L["Error.Folder", exception.Message]);
            return;
        }

        _settings = await KazoDesktopSettings.ReadAsync(AppContext.CancellationToken);
        _desktopDirectory = desktop;
        await WatchSettingsAsync(home);
    }

    /// <summary>Follows <c>~/.config/hackeros</c>: a change of <c>desktop.config</c> is read again.</summary>
    private async Task WatchSettingsAsync(string home)
    {
        string path = KazoDesktopSettings.GetPath(home);
        string directory = path[..path.LastIndexOf('/')];
        try
        {
            if (!await KazoDirectory.ExistsAsync(directory, AppContext.CancellationToken))
            {
                await KazoDirectory.CreateDirectoryAsync(directory, AppContext.CancellationToken);
            }

            _settingsWatch = await AppContext.Watch.WatchAsync(VirtualPath.Parse(directory), FileSystemWatchScope.ImmediateChildren, AppContext.CancellationToken);
            _watchCts = new CancellationTokenSource();
            _ = ReadSettingsChangesAsync(_settingsWatch, path, _watchCts.Token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AppGatewayAccessDeniedException or NotSupportedException or InvalidOperationException)
        {
            // No watch: the desktop keeps the settings it read, and its own changes.
        }
    }

    private async Task ReadSettingsChangesAsync(ITopicChannelSubscription<FileSystemChangeEvent> subscription, string path, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TopicMessage<FileSystemChangeEvent> _ in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await InvokeAsync(async () =>
                {
                    using IDisposable scope = KazoExecutionContext.Enter(AppContext);
                    KazoDesktopSettingsDocument read = await KazoDesktopSettings.ReadAsync(cancellationToken);
                    if (!SameSettings(read, _settings))
                    {
                        _settings = read;
                        StateHasChanged();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The desktop stopped.
        }
    }

    private static bool SameSettings(KazoDesktopSettingsDocument left, KazoDesktopSettingsDocument right) =>
        left.Options == right.Options
        && left.Positions.Count == right.Positions.Count
        && left.Positions.All(pair => right.Positions.TryGetValue(pair.Key, out KazoDesktopCell cell) && cell == pair.Value);

    // ---- Positions and view ----------------------------------------------------------------------------

    /// <summary>The control placed or moved icons: shown at once, written after the writes before it.</summary>
    private Task SavePositionsAsync(IReadOnlyDictionary<string, KazoDesktopCell> positions)
    {
        _settings = _settings with { Positions = positions };
        return EnqueueWrite(() => KazoDesktopSettings.SavePositionsAsync(positions, AppContext.CancellationToken));
    }

    private Task SaveOptionsAsync(KazoDesktopOptions options, bool clearPositions = false)
    {
        IReadOnlyDictionary<string, KazoDesktopCell> positions = clearPositions ? new Dictionary<string, KazoDesktopCell>() : _settings.Positions;
        _settings = new KazoDesktopSettingsDocument(options, positions);
        StateHasChanged();
        return EnqueueWrite(async () =>
        {
            await KazoDesktopSettings.SaveOptionsAsync(options, AppContext.CancellationToken);
            if (clearPositions)
            {
                await KazoDesktopSettings.SavePositionsAsync(positions, AppContext.CancellationToken);
            }
        });
    }

    /// <summary>One write at a time: each reads the file the previous one wrote.</summary>
    private Task EnqueueWrite(Func<Task> write)
    {
        async Task RunAsync(Task previous)
        {
            try
            {
                await previous;
            }
            catch (Exception)
            {
                // Reported by the write that failed.
            }

            try
            {
                await write();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await ReportErrorAsync(L["Error.Settings", exception.Message]);
            }
        }

        _writes = RunAsync(_writes);
        return _writes;
    }

    // ---- Menu ------------------------------------------------------------------------------------------

    /// <summary>
    /// The desktop's background menu (ADR 0160 §7): "View ▸" and "Sort by ▸" first, as on Windows, then
    /// the control's entries, then "Change background…".
    /// </summary>
    private void OnContextMenuOpening(KazoFileMenuOpeningEventArgs args)
    {
        if (args.Scope != FileMenuScope.Desktop)
        {
            return;
        }

        KazoDesktopOptions options = _settings.Options;
        KazoMenuGroupDefinition arrange = new();

        KazoMenuItemDefinition view = arrange.Add(KazoFileMenuItems.View, L["Menu.View"]);
        view.Icon = Icon("layout-grid");
        KazoMenuGroupDefinition sizes = new();
        SizeEntry(sizes, KazoDesktopIconSize.Large, "Menu.View.Large");
        SizeEntry(sizes, KazoDesktopIconSize.Medium, "Menu.View.Medium");
        SizeEntry(sizes, KazoDesktopIconSize.Small, "Menu.View.Small");
        KazoMenuGroupDefinition layout = new();
        Toggle(layout, "auto-arrange", "Menu.View.AutoArrange", options.AutoArrange, value => options with { AutoArrange = value });
        Toggle(layout, "align-to-grid", "Menu.View.AlignToGrid", options.AlignToGrid, value => options with { AlignToGrid = value });
        KazoMenuGroupDefinition shown = new();
        Toggle(shown, "show-home", "Menu.View.ShowHome", options.ShowHome, value => options with { ShowHome = value });
        Toggle(shown, "show-trash", "Menu.View.ShowTrash", options.ShowTrash, value => options with { ShowTrash = value });
        view.Children.Add(sizes);
        view.Children.Add(layout);
        view.Children.Add(shown);

        KazoMenuItemDefinition sort = arrange.Add(KazoFileMenuItems.Sort, L["Menu.Sort"]);
        sort.Icon = Icon("arrow-down-wide-narrow");
        KazoMenuGroupDefinition sorts = new();
        SortEntry(sorts, KazoDesktopSort.Name, "Menu.Sort.Name");
        SortEntry(sorts, KazoDesktopSort.Size, "Menu.Sort.Size");
        SortEntry(sorts, KazoDesktopSort.Type, "Menu.Sort.Type");
        SortEntry(sorts, KazoDesktopSort.Date, "Menu.Sort.Date");
        sort.Children.Add(sorts);
        args.Menu.Groups.Insert(0, arrange);

        KazoMenuGroupDefinition personalize = args.Menu.AddGroup();
        KazoMenuItemDefinition background = personalize.Add(KazoFileMenuItems.Background, L["Menu.Background"]);
        background.Icon = Icon("image");
        background.OnClick = Click(() => OpenSettingsAsync("appearance"));
        KazoMenuItemDefinition settings = personalize.Add("desktop-settings", L["Menu.DesktopSettings"]);
        settings.Icon = Icon("monitor-cog");
        settings.OnClick = Click(() => OpenSettingsAsync("desktop"));

        void SizeEntry(KazoMenuGroupDefinition group, KazoDesktopIconSize size, string text)
        {
            KazoMenuItemDefinition entry = group.Add("icon-size-" + size.ToString().ToLowerInvariant(), L[text]);
            entry.Icon = options.IconSize == size ? Icon("check") : null;
            entry.OnClick = Click(() => SaveOptionsAsync(options with { IconSize = size }));
        }

        void SortEntry(KazoMenuGroupDefinition group, KazoDesktopSort value, string text)
        {
            KazoMenuItemDefinition entry = group.Add("sort-" + value.ToString().ToLowerInvariant(), L[text]);
            entry.Icon = options.Sort == value ? Icon("check") : null;
            // ADR 0152 §3: "Sort by" arranges once; the saved positions go, the icons follow the order.
            entry.OnClick = Click(() => SaveOptionsAsync(options with { Sort = value }, clearPositions: true));
        }

        void Toggle(KazoMenuGroupDefinition group, string id, string text, bool current, Func<bool, KazoDesktopOptions> change)
        {
            KazoMenuItemDefinition entry = group.Add(id, L[text]);
            entry.Icon = current ? Icon("check") : null;
            entry.OnClick = Click(() => SaveOptionsAsync(change(!current)));
        }
    }

    private EventCallback<object?> Click(Func<Task> run) => EventCallback.Factory.Create<object?>(this, _ => run());

    private static KazoSurfaceIcon Icon(string name) => KazoIcons.Vector(IconLibrary.Lucide, name);

    private async Task OpenSettingsAsync(string page)
    {
        try
        {
            await AppContext.Intents.LaunchAsync(SettingsAppId, ["--page=" + page], AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            await ReportErrorAsync(exception.Message);
        }
    }

    // ---- Send to ▸ Desktop -------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>"Send to ▸ Desktop" from the Start menu: a shortcut to the app in <c>~/Desktop</c> (ADR 0160 §6).</remarks>
    protected override async Task OnIntentReceivedAsync(AppIntentDelivery intent)
    {
        if (intent.IntentId != KazoDesktopItems.CreateShortcutIntent || _desktopDirectory is null || ReadAppId(intent.PayloadJson) is not { } appId)
        {
            return;
        }

        string name = appId;
        try
        {
            IReadOnlyList<KazoAppInfo> apps = await AppContext.Catalog.GetInstalledAsync(AppContext.CancellationToken);
            name = apps.FirstOrDefault(app => app.Id == appId)?.Name ?? appId;
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            // The id names the shortcut.
        }

        try
        {
            await KazoDesktopEntry.CreateShortcutAsync(_desktopDirectory, appId, name, cancellationToken: AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ReportErrorAsync(L["Error.Shortcut", exception.Message]);
        }
    }

    /// <summary>The <c>appId</c> of a <c>{"appId":"…"}</c> payload; <see langword="null"/> for anything else.</summary>
    internal static string? ReadAppId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("appId", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && KazoDesktopEntry.IsAppId(id.GetString())
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Errors ------------------------------------------------------------------------------------

    private Task ReportErrorAsync(string message)
    {
        try
        {
            AppContext.Notifications.Post(NotificationSeverity.Warning, L["Notification.Title"], message);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            // Nowhere to show it.
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        if (_settingsWatch is not null)
        {
            await _settingsWatch.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
