using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Time;
using HackerOs.Theming.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Appearance;

/// <summary>
/// The shell's live catalog of user themes (ADR 0103 §2.2): the <c>.theme</c> files of
/// <c>/etc/hackeros/themes/</c>, read as the system, and of the session user's
/// <c>~/.config/hackeros/themes/</c>, read as that user so the files' mode bits decide. Both folders
/// are watched, so a theme saved by the Theme Editor, copied with <c>cp</c> or edited with
/// <c>nano</c> applies at once; a session change re-reads the new user's folder. The
/// <c>themes/</c> folders of the installed theme packs are read too, and the two pack folders are
/// watched, so installing or removing a pack updates the list at once (ADR 0213 §3).
/// </summary>
/// <remarks>
/// Invalid lines and refused files are written once each to the log (ADR 0044 §5): the theme still
/// applies with its valid tokens, or the shell keeps the default theme.
/// </remarks>
public sealed class UserThemeCatalogService : IAsyncDisposable
{
    private readonly IFileSystemService _fileSystem;
    private readonly ISessionService _session;
    private readonly ISimulationClock _clock;
    private readonly IOsSettingsService _settings;
    private readonly ITopicMessageBus? _topicBus;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _loggedDiagnostics = new(StringComparer.Ordinal);
    private SettingsFilesWatcher? _systemWatcher;
    private SettingsFilesWatcher? _userWatcher;
    private string? _watchedHome;
    private bool _initialized;

    /// <summary>Creates the catalog over the filesystem, as the session user.</summary>
    /// <param name="fileSystem">Filesystem service.</param>
    /// <param name="session">Session service (whose home holds the user folder).</param>
    /// <param name="clock">Simulation clock used by the gateways.</param>
    /// <param name="settings">OS settings, whose session-change notification triggers a re-read.</param>
    /// <param name="topicBus">Optional topic bus enabling folder watching.</param>
    /// <param name="logger">Optional logger for theme file warnings.</param>
    public UserThemeCatalogService(
        IFileSystemService fileSystem,
        ISessionService session,
        ISimulationClock clock,
        IOsSettingsService settings,
        ITopicMessageBus? topicBus = null,
        ILogger<UserThemeCatalogService>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _topicBus = topicBus;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _settings.DocumentChanged += OnSettingsChanged;
    }

    /// <summary>Gets the accepted user themes and the diagnostics of the last read.</summary>
    public UserThemeLibraryResult Current { get; private set; } = UserThemeLibraryResult.Empty;

    /// <summary>Raised after a re-read changed the accepted themes.</summary>
    public event Action? Changed;

    /// <summary>Reads both folders and starts watching them, without raising <see cref="Changed"/>.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _initialized = true;
        await RefreshCoreAsync(raiseEvent: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-reads both folders and raises <see cref="Changed"/> when the accepted themes changed.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(raiseEvent: true, cancellationToken);

    /// <summary>Resolves the theme to render for a preference on one form factor.</summary>
    /// <param name="themeId">Id from <c>appearance.config</c>.</param>
    /// <param name="platform">Active form factor.</param>
    public ResolvedTheme Resolve(string? themeId, ThemePlatform platform) =>
        ResolvedTheme.Resolve(themeId, platform, Current.Themes);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _settings.DocumentChanged -= OnSettingsChanged;
        await StopWatchersAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private void OnSettingsChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        // A null document id is the settings service's "session changed" signal: another user's
        // ~/.config/hackeros/themes now applies.
        if (_initialized && args.DocumentId is null)
        {
            _ = RefreshInBackgroundAsync();
        }
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshCoreAsync(raiseEvent: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Keep the last good catalog; the shell keeps drawing what it had.
            _logger.LogWarning(exception, "themes: could not re-read the theme folders");
        }
    }

    private async Task RefreshCoreAsync(bool raiseEvent, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        try
        {
            AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
            IAppFileSystemGateway system = CreateGateway(OsSettingsService.CreateSystemOperation(), ["system"]);
            IAppFileSystemGateway? user = principal is null
                ? null
                : CreateGateway(OsSettingsService.CreateUserOperation(principal), [.. principal.GroupIds.Select(static id => id.ToString())]);

            UserThemeLibraryResult result = await UserThemeLibrary.LoadAsync(
                (directory, token) => ListAsync(GatewayFor(directory, system, user, principal), directory, FileSystemEntryKind.File, token),
                (directory, token) => ListAsync(GatewayFor(directory, system, user, principal), directory, FileSystemEntryKind.Directory, token),
                (path, token) => ReadAsync(GatewayFor(path, system, user, principal), path, token),
                principal?.HomePath,
                cancellationToken).ConfigureAwait(false);

            foreach (ThemeFileDiagnostic diagnostic in result.Diagnostics)
            {
                string line = diagnostic.ToString();
                if (_loggedDiagnostics.Add(line))
                {
                    _logger.LogWarning("themes: {Diagnostic}", line);
                }
            }

            changed = !SameThemes(Current.Themes, result.Themes);
            Current = result;
            await EnsureWatchersAsync(principal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (raiseEvent && changed)
        {
            Changed?.Invoke();
        }
    }

    private static IAppFileSystemGateway GatewayFor(
        string path, IAppFileSystemGateway system, IAppFileSystemGateway? user, AuthenticatedPrincipal? principal) =>
        user is not null && principal is not null && path.StartsWith(principal.HomePath.TrimEnd('/') + "/", StringComparison.Ordinal)
            ? user
            : system;

    private static async Task<IReadOnlyList<string>> ListAsync(
        IAppFileSystemGateway gateway, string directory, FileSystemEntryKind kind, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemDirectorySnapshot> result = await gateway
            .EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse(directory)), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            throw new IOException($"Cannot list {directory}: {result.Error?.Code}");
        }

        return [.. result.Value.Entries
            .Where(entry => entry.Metadata.Kind == kind)
            .Select(entry => $"{directory}/{entry.Name.Value}")];
    }

    private static async Task<string> ReadAsync(IAppFileSystemGateway gateway, string path, CancellationToken cancellationToken)
    {
        SettingsFileReadResult read = await SettingsTextFile.ReadAsync(gateway, VirtualPath.Parse(path), cancellationToken).ConfigureAwait(false);
        return read.Content ?? throw new IOException($"{read.Error?.Code.ToString() ?? "NotFound"}");
    }

    private async Task EnsureWatchersAsync(AuthenticatedPrincipal? principal, CancellationToken cancellationToken)
    {
        if (_topicBus is null)
        {
            return;
        }

        if (_systemWatcher is null)
        {
            _systemWatcher = SettingsFilesWatcher.ForDirectories(
                CreateWatchGateway(OsSettingsService.CreateSystemOperation(), ["system"]),
                [VirtualPath.Parse(UserThemeLibrary.SystemDirectory), VirtualPath.Parse(ThemePackLayout.SystemDirectory)],
                OnFolderChangedAsync);
            await _systemWatcher.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (principal?.HomePath == _watchedHome)
        {
            return;
        }

        if (_userWatcher is not null)
        {
            await _userWatcher.DisposeAsync().ConfigureAwait(false);
            _userWatcher = null;
        }

        _watchedHome = principal?.HomePath;
        if (principal is not null)
        {
            _userWatcher = SettingsFilesWatcher.ForDirectories(
                CreateWatchGateway(OsSettingsService.CreateUserOperation(principal), [.. principal.GroupIds.Select(static id => id.ToString())]),
                [VirtualPath.Parse(UserThemeLibrary.GetUserDirectory(principal.HomePath)), VirtualPath.Parse(ThemePackLayout.GetUserDirectory(principal.HomePath))],
                OnFolderChangedAsync);
            await _userWatcher.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask OnFolderChangedAsync()
    {
        _ = RefreshInBackgroundAsync();
        return ValueTask.CompletedTask;
    }

    private async Task StopWatchersAsync()
    {
        if (_systemWatcher is not null)
        {
            await _systemWatcher.DisposeAsync().ConfigureAwait(false);
        }

        if (_userWatcher is not null)
        {
            await _userWatcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IAppFileSystemGateway CreateGateway(AppOperationContext operation, IReadOnlyList<string> groups) =>
        new AppFileSystemGateway(_fileSystem, _clock, operation, groups);

    private IAppFileSystemWatchGateway CreateWatchGateway(AppOperationContext operation, IReadOnlyList<string> groups) =>
        new AppFileSystemWatchGateway(_fileSystem, _topicBus!, _clock, operation, groups, "themes");

    private static bool SameThemes(IReadOnlyList<UserTheme> left, IReadOnlyList<UserTheme> right) =>
        left.Count == right.Count
        && left.Zip(right).All(static pair =>
            pair.First.Id == pair.Second.Id
            && pair.First.Name == pair.Second.Name
            && pair.First.BaseThemeId == pair.Second.BaseThemeId
            && pair.First.SourcePath == pair.Second.SourcePath
            && pair.First.Platforms.SequenceEqual(pair.Second.Platforms)
            && pair.First.Tokens.SequenceEqual(pair.Second.Tokens));
}
