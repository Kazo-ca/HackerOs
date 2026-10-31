using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Settings;

/// <summary>
/// The Settings app's window: appearance, accessibility, language, file associations, granted capabilities, permissions,
/// installed apps, optional server connection, backup and system (restart, factory reset).
/// </summary>
/// <remarks>
/// Uses only the public SDK (ADR 0055, ADR 0056 sections 8 to 11):
/// <list type="bullet">
/// <item><see cref="HackSettings.OpenSystemAsync(string, CancellationToken)"/> for
/// <c>appearance.config</c>; the shell applies a new theme by itself as soon as the file changes;</item>
/// <item><see cref="HackApps"/> for the file handlers of the installed apps;</item>
/// <item><see cref="HackServerConnection"/> (capability <c>server.connection.manage</c>) for the
/// optional server;</item>
/// <item><see cref="HackSystem"/> (capability <c>system.power</c>) for restart and factory reset;
/// the OS asks an administrator in its own window, this app never sees the password.</item>
/// <item><see cref="HackSystemBackup"/> (capability <c>system.backup</c>, ADR 0099) on the Backup page,
/// shown only when the host supports it.</item>
/// </list>
/// </remarks>
public partial class SettingsWindow : IAsyncDisposable
{
    /// <summary>OS document holding the look of the shell (ADR 0044 section 8).</summary>
    internal const string AppearanceDocumentId = "appearance";

    /// <summary>Key of the desktop theme id in <c>appearance.config</c>.</summary>
    internal const string DesktopThemeKey = "desktopThemeId";

    /// <summary>Key of the mobile theme id in <c>appearance.config</c>.</summary>
    internal const string MobileThemeKey = "mobileThemeId";

    /// <summary>Key of the accent id in <c>appearance.config</c>.</summary>
    internal const string AccentKey = "accent";

    /// <summary>Key of the animations switch in <c>appearance.config</c>.</summary>
    internal const string AnimationsKey = "animationsEnabled";

    private const string ResetConfirmPhrase = "RESET";

    private const string ThemeEditorAppId = "org.hackeros.theme-editor";
    private HackSettingsDocument? _appearance;
    private UserThemeLibraryResult _userThemes = UserThemeLibraryResult.Empty;
    private ThemePreferences _appearanceSettings = ThemePreferences.Default;
    private SettingLayer _targetLayer = SettingLayer.User;
    private bool _isSavingAppearance;
    private string? _appearanceMessage;

    private bool _isResetDialogOpen;
    private bool _isResetting;
    private bool _isRebooting;
    private string _confirmText = string.Empty;
    private string? _resetErrorMessage;
    private string? _systemMessage;

    private HackServerConnectionState _connectionState = HackServerConnectionState.NotConnected;
    private string _serverUrl = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _isConnecting;
    private string? _connectionErrorMessage;
    private bool _isSyncing;
    private string? _syncStatusMessage;
    private int _conflictCount;
    private HackSyncStatus _syncStatus = HackSyncStatus.Inactive;
    private IReadOnlyList<HackSyncConflict> _conflicts = [];

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>Settings' texts in the language of its own process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The culture of the dates shown (the process's <c>LC_TIME</c>, ADR 0104).</summary>
    private CultureInfo Formats => AppContext.FormatCulture;

    /// <summary>The user's authority, translated.</summary>
    private string AuthorityLabel => (AppContext?.UserAuthority ?? AppAuthority.User) switch
    {
        AppAuthority.Administrator => L["About.Authority.Administrator"],
        AppAuthority.System => L["About.Authority.System"],
        _ => L["About.Authority.User"]
    };

    /// <summary>Prefix of the argument naming the app whose page to show (<c>--app=&lt;id&gt;</c>).</summary>
    internal const string AppArgumentPrefix = "--app=";

    /// <summary>Prefix of the argument naming a Settings page to open (<c>--page=appearance</c>, <c>--page=desktop</c>, ...).</summary>
    internal const string PageArgumentPrefix = "--page=";

    private string? _appPageId;

    private bool IsConfirmed => string.Equals(_confirmText, ResetConfirmPhrase, StringComparison.Ordinal);

    // A user theme takes the accent behavior of its base theme (ADR 0103).
    private bool CurrentDesktopThemeSupportsAccent =>
        ThemeCatalog.TryGet(
            ResolvedTheme.Resolve(_appearanceSettings.DesktopThemeId, ThemePlatform.Desktop, _userThemes.Themes).BaseThemeId,
            out ThemeDefinition? theme)
        && theme is { SupportsAccentColor: true };

    private bool CanSubmitConnection =>
        Uri.TryCreate(_serverUrl, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(_username) &&
        !string.IsNullOrWhiteSpace(_password);

    /// <inheritdoc />
    protected override async Task OnAppInitializedAsync()
    {
        ShowAppPage(LaunchArguments);
        await OpenAppearanceAsync();
        await OpenMobilePreferencesAsync();
        await RefreshUserThemesAsync();

        await RefreshConnectionAsync();
    }

    /// <summary>
    /// Settings is a single instance: another app's <c>ShowAppSettingsIntent</c> reaches the open
    /// window here with <c>--app=&lt;id&gt;</c> (ADR 0096, sections 4 and 5).
    /// </summary>
    protected override Task OnIntentReceivedAsync(AppIntentDelivery intent)
    {
        ShowAppPage(intent.Arguments);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the "Application" page on the app named by <c>--app=&lt;id&gt;</c>, or the page named by
    /// <c>--page=</c> (the desktop's "Change background…" and "Desktop settings…", ADR 0160 §8).
    /// </summary>
    private void ShowAppPage(IReadOnlyList<string> arguments)
    {
        if (ReadAppArgument(arguments) is { } appId)
        {
            _appPageId = appId;
            _activePage = ApplicationPageId;
        }
        else if (ReadPageArgument(arguments) is { } page)
        {
            _activePage = page;
        }
    }

    /// <summary>Reads <c>--app=&lt;id&gt;</c> from Settings' command line; the last one wins, an empty id is ignored.</summary>
    internal static string? ReadAppArgument(IReadOnlyList<string> arguments) =>
        arguments
            .Where(static argument => argument.StartsWith(AppArgumentPrefix, StringComparison.Ordinal))
            .Select(static argument => argument[AppArgumentPrefix.Length..].Trim())
            .LastOrDefault(static id => id.Length > 0);
    /// <summary>Re-reads the user themes when the window comes back to the front (after the Theme Editor saved one).</summary>
    protected override Task OnWindowActivatedAsync() => RefreshUserThemesAsync();

    /// <summary>
    /// Reads the user themes of <c>/etc/hackeros/themes</c> and <c>~/.config/hackeros/themes</c>
    /// (ADR 0103 §2.2) with the SDK's file API; a missing folder simply means no theme.
    /// </summary>
    private async Task RefreshUserThemesAsync()
    {
        try
        {
            _userThemes = await UserThemeLibrary.LoadAsync(
                async (directory, token) => await HackDirectory.GetFilesAsync(directory, "*" + ThemeFile.Extension, cancellationToken: token),
                (path, token) => HackFile.ReadAllTextAsync(path, token),
                HackUser.Describe(AppContext).HomeDirectory,
                AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is HackIOException or HackUnauthorizedAccessException)
        {
            _userThemes = UserThemeLibraryResult.Empty;
        }
    }

    /// <summary>User themes offered on a form factor, in catalog order.</summary>
    private IEnumerable<UserTheme> UserThemesFor(ThemePlatform platform) =>
        _userThemes.Themes.Where(theme => theme.Supports(platform));

    /// <summary>Starts the Theme Editor, on a theme file when one is given.</summary>
    private async Task OpenThemeEditorAsync(string? themePath)
    {
        try
        {
            await HackApps.LaunchAsync(ThemeEditorAppId, themePath is null ? [] : [themePath], AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is HackAppLaunchException or HackUnauthorizedAccessException)
        {
            _appearanceMessage = exception.Message;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_mobilePreferences is not null)
        {
            _mobilePreferences.Changed -= MobilePreferencesChanged;
            await _mobilePreferences.DisposeAsync();
        }
        if (_appearance is not null)
        {
            _appearance.Changed -= OnAppearanceChanged;
            await _appearance.DisposeAsync();
        }
    }

    private async Task OpenAppearanceAsync()
    {
        try
        {
            _appearance = await HackSettings.OpenSystemAsync(AppContext, AppearanceDocumentId, AppContext.CancellationToken);
            _appearance.Changed += OnAppearanceChanged;
            _appearanceSettings = ReadAppearance(_appearance);
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _appearanceMessage = L["Appearance.Error.CannotRead", exception.Message];
        }
    }

    /// <summary>An external edit (<c>nano /etc/hackeros/appearance.config</c>) or another window changed the file.</summary>
    private void OnAppearanceChanged(object? sender, HackSettingsChangedEventArgs args) =>
        _ = InvokeAsync(() =>
        {
            if (_appearance is not null && !_isSavingAppearance)
            {
                _appearanceSettings = ReadAppearance(_appearance);
            }

            StateHasChanged();
        });

    private static ThemePreferences ReadAppearance(HackSettingsDocument document) => new(
        document.Get<string>(DesktopThemeKey),
        document.Get<string>(MobileThemeKey),
        document.Get<string>(AccentKey),
        document.Get<bool>(AnimationsKey));

    private Task ChangeDesktopTheme(string themeId) => PersistAppearanceAsync(
        _appearanceSettings with { DesktopThemeId = themeId }, DesktopThemeKey, themeId);

    private Task ChangeMobileTheme(string themeId) => PersistAppearanceAsync(
        _appearanceSettings with { MobileThemeId = themeId }, MobileThemeKey, themeId);

    private Task ChangeAccent(string accentId) => PersistAppearanceAsync(
        _appearanceSettings with { AccentId = accentId }, AccentKey, accentId);

    private static string ThemeOptionClass(string themeId, string selectedThemeId) =>
        string.Equals(themeId, selectedThemeId, StringComparison.Ordinal)
            ? "theme-option is-selected"
            : "theme-option";

    private string AccentOptionClass(string accentId) =>
        string.Equals(accentId, _appearanceSettings.AccentId, StringComparison.Ordinal)
            ? $"accent-btn accent-{accentId} is-selected"
            : $"accent-btn accent-{accentId}";

    private string LayerOptionClass(SettingLayer layer) =>
        layer == _targetLayer ? "layer-option is-selected" : "layer-option";

    private void SelectLayer(SettingLayer layer)
    {
        _targetLayer = layer;
        _appearanceMessage = null;
    }

    /// <summary>Gets which layer currently provides <paramref name="key"/> (ADR 0044 section 3).</summary>
    private SettingLayer SourceOf(string key) => _appearance?.GetSource(key) ?? SettingLayer.Default;

    private bool IsLocked(string key) => _appearance?.IsLocked(key) == true;

    /// <summary>A locked key cannot be changed for oneself; the system default stays editable.</summary>
    private bool IsBlocked(string key) => _targetLayer == SettingLayer.User && IsLocked(key);

    private Task ApplyCurrentAsSystemDefaultAsync() => PersistAppearanceAsync(_appearanceSettings, key: null, value: null);

    /// <summary>The settings write in flight, if any (ADR 0069).</summary>
    private Task? _appearanceWrite;

    /// <summary>
    /// A stop request (logout, <c>kill</c>, "End task") waits for the settings write in flight: the
    /// write is one atomic edit of the settings file and runs to its end, so the change the user
    /// just made is either fully saved or reported, never half applied.
    /// </summary>
    protected override Task OnAppStopRequestedAsync(HackerOs.AppSdk.Diagnostics.HackStopRequestedEventArgs e) =>
        Task.WhenAll(_appearanceWrite ?? Task.CompletedTask, _mobileWrite ?? Task.CompletedTask);

    /// <summary>
    /// Writes one changed key to the user layer, or the whole look to the system layer in one edit
    /// (the system default is what every user starts from, and one edit means one administrator
    /// approval).
    /// </summary>
    private async Task PersistAppearanceAsync(ThemePreferences next, string? key, object? value)
    {
        if (_isSavingAppearance || _appearance is null)
        {
            return;
        }

        _appearanceSettings = next;
        _appearanceMessage = null;
        _isSavingAppearance = true;
        StateHasChanged();

        // Not the process token: a stop request must let this one edit finish (see OnAppStopRequestedAsync).
        TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _appearanceWrite = written.Task;
        try
        {
            if (_targetLayer == SettingLayer.System || key is null)
            {
                // /etc/hackeros belongs to root: the write uses this app's own rights, so a denial
                // raises the administrator approval of ADR 0042.
                await _appearance.SetManyAsync(
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [DesktopThemeKey] = next.DesktopThemeId,
                        [MobileThemeKey] = next.MobileThemeId,
                        [AccentKey] = next.AccentId,
                        [AnimationsKey] = next.AnimationsEnabled
                    },
                    SettingLayer.System,
                    CancellationToken.None);
                _appearanceMessage = L["Appearance.SavedAsSystemDefault"];
            }
            else
            {
                await _appearance.SetAsync(key, value!, SettingLayer.User, CancellationToken.None);
            }
        }
        catch (HackSettingLockedException exception)
        {
            _appearanceMessage = exception.Message;
        }
        catch (UnauthorizedAccessException)
        {
            _appearanceMessage = L["Appearance.Error.AdminRequired"];
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            _appearanceMessage = L["Appearance.Error.CannotSave", exception.Message];
        }
        finally
        {
            _isSavingAppearance = false;
            _appearanceWrite = null;
            written.TrySetResult();
        }

        _appearanceSettings = ReadAppearance(_appearance);
        StateHasChanged();
    }

    private async Task RefreshConnectionAsync()
    {
        try
        {
            _connectionState = await HackServerConnection.GetStateAsync(AppContext, AppContext.CancellationToken);
            await RefreshSyncAsync();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException)
        {
            _connectionState = HackServerConnectionState.NotConnected;
            _connectionErrorMessage = L["Server.Error.Unavailable", exception.Message];
        }
    }

    private async Task ConnectAsync(bool createNewAccount)
    {
        if (!CanSubmitConnection || _isConnecting)
        {
            return;
        }

        _isConnecting = true;
        _connectionErrorMessage = null;
        StateHasChanged();

        try
        {
            Uri serverUrl = new(_serverUrl, UriKind.Absolute);
            _connectionState = createNewAccount
                ? await HackServerConnection.CreateAccountAsync(AppContext, serverUrl, _username, _password, AppContext.CancellationToken)
                : await HackServerConnection.ConnectAsync(AppContext, serverUrl, _username, _password, AppContext.CancellationToken);
            _password = string.Empty;
        }
        catch (Exception exception) when (exception is HackServerConnectionException or UnauthorizedAccessException)
        {
            _connectionErrorMessage = exception.Message;
        }
        finally
        {
            _isConnecting = false;
        }

        if (_connectionState.IsConnected)
        {
            // ADR 0029 Decision 7: sync once immediately after a successful connect.
            await SyncNowAsync();
        }
    }

    /// <summary>The Local server panel linked this device with a pairing code (ADR 0191 §6).</summary>
    private async Task OnLocalServerPairedAsync(HackServerConnectionState state)
    {
        _connectionState = state;
        _connectionErrorMessage = null;

        // ADR 0029 Decision 7: sync once immediately after a successful connect.
        await SyncNowAsync();
    }

    private async Task DisconnectAsync()
    {
        if (_isConnecting)
        {
            return;
        }

        await HackServerConnection.DisconnectAsync(AppContext, AppContext.CancellationToken);
        _connectionState = HackServerConnectionState.NotConnected;
        _serverUrl = string.Empty;
        _username = string.Empty;
        _password = string.Empty;
        _connectionErrorMessage = null;
        _syncStatusMessage = null;
        _conflictCount = 0;
    }

    private async Task SyncNowAsync()
    {
        if (_isSyncing || !_connectionState.IsConnected)
        {
            return;
        }

        _isSyncing = true;
        _syncStatusMessage = null;
        StateHasChanged();

        try
        {
            // ADR 0101 section 4: one complete pass in the OS's order (FileSystem push then pull, so a
            // concurrent edit is caught as a conflict first; Grants pull; FileAssociations). User
            // settings are home files (ADR 0044), so they travel with the FileSystem domain.
            _syncStatus = await HackServerConnection.SyncNowAsync(AppContext, AppContext.CancellationToken);
            await RefreshSyncAsync();
            string now = DateTimeOffset.Now.ToString("t", Formats);
            _syncStatusMessage = _conflictCount > 0
                ? L["Sync.LastSyncedWithConflicts", now, _conflictCount]
                : L["Sync.LastSynced", now];
        }
        catch (HackServerConnectionException exception)
        {
            _syncStatusMessage = exception.Message;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    /// <summary>Reads the periodic-sync state and the pending conflicts (ADR 0101).</summary>
    private async Task RefreshSyncAsync()
    {
        _syncStatus = await HackServerConnection.GetSyncStatusAsync(AppContext, AppContext.CancellationToken);
        _conflicts = await HackServerConnection.GetConflictsAsync(AppContext, AppContext.CancellationToken);
        _conflictCount = _conflicts.Count;
    }

    private string SyncStateLabel =>
        !_connectionState.IsConnected ? L["Sync.State.Off"]
        : !_syncStatus.IsActive ? L["Sync.State.Inactive"]
        : _syncStatus.IsPaused ? L["Sync.State.Paused"]
        : _syncStatus.IsOnline ? L["Sync.State.Running"]
        : L["Sync.State.WaitingForServer"];

    /// <summary>The sync period as the description says it: "every minute", "every 5 minutes".</summary>
    private string FormatInterval(TimeSpan interval) =>
        interval.TotalMinutes is 1
            ? L["Sync.Interval.Minute"]
            : L["Sync.Interval.Minutes", interval.TotalMinutes.ToString("0", Formats)];

    private async Task SetSyncPausedAsync(bool paused)
    {
        try
        {
            await HackServerConnection.SetSyncPausedAsync(AppContext, paused, AppContext.CancellationToken);
            await RefreshSyncAsync();
            _syncStatus = _syncStatus with { IsPaused = paused };
        }
        catch (Exception exception) when (exception is HackServerConnectionException or UnauthorizedAccessException or IOException)
        {
            _syncStatusMessage = L["Sync.Error.CannotChange", exception.Message];
        }
    }

    private async Task ShowConflictsAsync()
    {
        await HackServerConnection.ShowConflictsAsync(AppContext, AppContext.CancellationToken);
    }

    private async Task RebootAsync()
    {
        if (_isRebooting)
        {
            return;
        }

        _isRebooting = true;
        _systemMessage = null;
        StateHasChanged();
        try
        {
            // The OS asks an administrator, flushes the logs, ends every process and reloads.
            await HackSystem.RebootAsync(AppContext, AppContext.CancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            _systemMessage = L["Restart.Error.NotConfirmed"];
        }
        catch (OperationCanceledException)
        {
            // The session ended under us: the machine is restarting.
        }
        finally
        {
            _isRebooting = false;
        }
    }

    private void OpenResetDialog()
    {
        _isResetDialogOpen = true;
        _confirmText = string.Empty;
        _resetErrorMessage = null;
    }

    private void CancelReset()
    {
        if (_isResetting)
        {
            return;
        }

        _isResetDialogOpen = false;
        _confirmText = string.Empty;
        _resetErrorMessage = null;
    }

    private async Task ConfirmResetAsync()
    {
        if (!IsConfirmed || _isResetting)
        {
            return;
        }

        _isResetting = true;
        _resetErrorMessage = null;

        try
        {
            // The OS asks an administrator, erases its database and restarts as freshly installed.
            await HackSystem.FactoryResetAsync(AppContext, AppContext.CancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            _resetErrorMessage = L["Reset.Error.NotConfirmed"];
            _isResetting = false;
        }
        catch (OperationCanceledException)
        {
            // The session ended under us: the machine is restarting.
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException)
        {
            _resetErrorMessage = L["Reset.Error.Failed", exception.Message];
            _isResetting = false;
        }
    }
}
