using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.Notifications;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Sounds;
using HackerOs.Simulation.Abstractions.Time;
using HackerOs.Theming.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Sounds;

/// <summary>
/// The sound server of the session (ADR 0204): plays the sound of an OS event from the chosen sound
/// theme, at the chosen volume, unless sounds are turned off in <c>sound.config</c>.
/// </summary>
/// <remarks>
/// <para>
/// Sources: a posted notification (by severity), a message box with the warning or error icon
/// (<see cref="Play"/>, called by the dialog coordinator), and the session events of the event bus.
/// </para>
/// <para>
/// A sound is a real file. The theme folder is looked up in <c>/usr/share/sounds/&lt;theme&gt;</c>, the
/// <c>sounds/</c> folder of the system theme pack of that id (ADR 0213 §3), then in
/// <c>~/.local/share/sounds/&lt;theme&gt;</c> and the user's theme pack (a user theme cannot hide a
/// system one); inside it, each folder
/// of <c>Directories=</c> then the theme folder itself, for <c>&lt;sound-name&gt;.oga</c>, <c>.ogg</c> or
/// <c>.mp3</c> of at most 1 MiB. An event the theme lacks plays the default theme's sound, like the
/// freedesktop.org <c>Inherits</c> fallback.
/// </para>
/// <para>
/// Changing the theme or the volume in the session plays the notification sound once, as feedback,
/// like the sound panel of GNOME. Opening another session does not.
/// </para>
/// </remarks>
public sealed class OsSoundService : IDisposable
{
    // The same event twice within this delay plays once (ten notifications posted together).
    private static readonly TimeSpan RepeatGuard = TimeSpan.FromMilliseconds(150);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IOsSettingsService _settings;
    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly ISessionService _session;
    private readonly IOsSoundOutput _output;
    private readonly INotificationFeed? _notifications;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OsSoundService> _logger;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<OsSoundEvent, long> _lastPlayed = [];
    private readonly Lock _playGate = new();
    private readonly Func<CancellationToken, ValueTask>? _installBuiltInThemes;
    private readonly Lock _installGate = new();
    private Task? _installation;
    private string? _preferencesHome;
    private bool _initialized;

    /// <summary>Creates the service.</summary>
    /// <param name="settings">OS settings service (<c>sound.config</c>).</param>
    /// <param name="fileSystem">File system holding the themes.</param>
    /// <param name="clock">Simulation clock of the file reads.</param>
    /// <param name="session">Session service: whose home holds the user themes.</param>
    /// <param name="output">The sound card.</param>
    /// <param name="eventBus">Session events (sign-in and sign-out sounds); optional.</param>
    /// <param name="notifications">Posted notifications; optional.</param>
    /// <param name="timeProvider">Real time of the repeat guard; <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="logger">Logger of unreadable themes.</param>
    /// <param name="installBuiltInThemes">
    /// Writes the built-in themes to <c>/usr/share/sounds</c> when they are missing (the seeder); run
    /// once, in the background after the first sign-in, so the first login does not wait for it.
    /// </param>
    public OsSoundService(
        IOsSettingsService settings,
        IFileSystemService fileSystem,
        ISimulationClock clock,
        ISessionService session,
        IOsSoundOutput output,
        IEventBus? eventBus = null,
        INotificationFeed? notifications = null,
        TimeProvider? timeProvider = null,
        ILogger<OsSoundService>? logger = null,
        Func<CancellationToken, ValueTask>? installBuiltInThemes = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _notifications = notifications;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<OsSoundService>.Instance;
        _installBuiltInThemes = installBuiltInThemes;
        _settings.DocumentChanged += OnDocumentChanged;
        if (eventBus is not null)
        {
            _subscriptions.Add(eventBus.Subscribe<SessionActivatedEvent>(_ => SignInInBackground()));
            _subscriptions.Add(eventBus.Subscribe<SessionLoggedOutEvent>(_ => Play(OsSoundEvent.SignOut)));
        }

        if (_notifications is not null)
        {
            _notifications.Posted += OnNotificationPosted;
        }
    }

    /// <summary>Gets the effective preferences (all layers of <c>sound.config</c> merged).</summary>
    public SoundPreferences Preferences { get; private set; } = SoundPreferences.Default;

    /// <summary>Raised after <see cref="Preferences"/> changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the "Do not disturb" mode (ADR 0216 §1): while it is on, a posted notification plays no
    /// sound; the other events still do. <see langword="null"/> when the host has no such mode.
    /// </summary>
    public DoNotDisturbService? DoNotDisturb { get; init; }

    /// <summary>Loads <c>sound.config</c> and starts watching it.</summary>
    /// <param name="cancellationToken">Signals cancellation of the reads.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCoreAsync(raiseEvent: false, cancellationToken).ConfigureAwait(false);
        await _settings.WatchAsync(SoundSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    /// <summary>
    /// Installs the built-in themes when they are missing, like a distribution package written
    /// once to the disk. Runs once per session service; later calls return the same task.
    /// </summary>
    /// <returns>The installation, completed at once without an installer.</returns>
    public Task InstallBuiltInThemesAsync()
    {
        if (_installBuiltInThemes is null)
        {
            return Task.CompletedTask;
        }

        lock (_installGate)
        {
            return _installation ??= _installBuiltInThemes(CancellationToken.None).AsTask();
        }
    }

    /// <summary>Plays the sound of an event in the background; never throws.</summary>
    /// <param name="soundEvent">The event.</param>
    public void Play(OsSoundEvent soundEvent) => _ = PlayInBackgroundAsync(soundEvent);

    /// <summary>Plays the sound of an event, when sounds are on.</summary>
    /// <param name="soundEvent">The event.</param>
    /// <param name="cancellationToken">Cancels the file reads.</param>
    /// <returns><see langword="true"/> when a sound started.</returns>
    public async Task<bool> PlayAsync(OsSoundEvent soundEvent, CancellationToken cancellationToken = default)
    {
        SoundPreferences preferences = Preferences;
        if (!preferences.IsAudible || !TakeTurn(soundEvent))
        {
            return false;
        }

        OsSoundClip? clip = await ResolveAsync(preferences.Theme, soundEvent, cancellationToken).ConfigureAwait(false);
        return clip is not null
            && await _output.PlayAsync(clip, preferences.Volume / 100d, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the sound file of an event in a theme, or in the default theme when the theme lacks it.
    /// </summary>
    /// <param name="themeId">Theme identifier.</param>
    /// <param name="soundEvent">The event.</param>
    /// <param name="cancellationToken">Cancels the file reads.</param>
    /// <returns>The clip, or <see langword="null"/> when no readable file exists.</returns>
    public async Task<OsSoundClip?> ResolveAsync(string themeId, OsSoundEvent soundEvent, CancellationToken cancellationToken = default)
    {
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        OsSoundClip? clip = await ResolveInThemeAsync(themeId, soundEvent, principal, cancellationToken).ConfigureAwait(false);
        if (clip is null && themeId != OsSoundEvents.DefaultThemeId)
        {
            clip = await ResolveInThemeAsync(OsSoundEvents.DefaultThemeId, soundEvent, principal, cancellationToken).ConfigureAwait(false);
        }

        return clip;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.DocumentChanged -= OnDocumentChanged;
        if (_notifications is not null)
        {
            _notifications.Posted -= OnNotificationPosted;
        }

        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _refreshGate.Dispose();
    }

    /// <summary>
    /// Maps a notification to its sound; only the session user's notifications sound, and none
    /// while "Do not disturb" is on (ADR 0216).
    /// </summary>
    private void OnNotificationPosted(Notification notification)
    {
        if (_session.CurrentPrincipal?.UserId != notification.UserId || DoNotDisturb?.IsEnabled == true)
        {
            return;
        }

        Play(notification.Severity switch
        {
            NotificationSeverity.Error => OsSoundEvent.Error,
            NotificationSeverity.Warning => OsSoundEvent.Warning,
            _ => OsSoundEvent.Notification
        });
    }

    private async Task PlayInBackgroundAsync(OsSoundEvent soundEvent)
    {
        try
        {
            await PlayAsync(soundEvent).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A sound never breaks what it accompanies.
            _logger.LogWarning(exception, "sound: could not play {Event}", soundEvent);
        }
    }

    // The sign-in sound needs the built-in themes, written after the login rather than during it
    // (writing them made the first login about two seconds slower).
    private void SignInInBackground() => _ = SignInInBackgroundAsync();

    private async Task SignInInBackgroundAsync()
    {
        try
        {
            await InstallBuiltInThemesAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "sound: could not install the built-in sound themes");
        }

        await PlayInBackgroundAsync(OsSoundEvent.SignIn).ConfigureAwait(false);
    }

    private bool TakeTurn(OsSoundEvent soundEvent)
    {
        long now = _timeProvider.GetTimestamp();
        lock (_playGate)
        {
            if (_lastPlayed.TryGetValue(soundEvent, out long last)
                && _timeProvider.GetElapsedTime(last, now) < RepeatGuard)
            {
                return false;
            }

            _lastPlayed[soundEvent] = now;
            return true;
        }
    }

    private async Task<OsSoundClip?> ResolveInThemeAsync(
        string themeId, OsSoundEvent soundEvent, AuthenticatedPrincipal? principal, CancellationToken cancellationToken)
    {
        if (!OsSoundEvents.IsValidThemeId(themeId))
        {
            return null;
        }

        // A theme pack's sounds/ folder is a sound theme named after the pack (ADR 0213 §3).
        List<string> themeDirectories =
        [
            $"{OsSoundEvents.SystemThemesDirectory}/{themeId}",
            $"{ThemePackLayout.SystemDirectory}/{themeId}/{ThemePackLayout.SoundsFolder}",
        ];
        if (principal is not null)
        {
            themeDirectories.Add($"{OsSoundEvents.GetUserThemesDirectory(principal.HomePath)}/{themeId}");
            themeDirectories.Add($"{ThemePackLayout.GetUserDirectory(principal.HomePath)}/{themeId}/{ThemePackLayout.SoundsFolder}");
        }

        foreach (string themeDirectory in themeDirectories)
        {
            IAppFileSystemGateway gateway = GatewayFor(themeDirectory, principal);
            string? index = await ReadIndexAsync(gateway, themeDirectory, cancellationToken).ConfigureAwait(false);
            if (index is null)
            {
                continue;
            }

            // The first theme folder with this name wins, even when it lacks the sound.
            SoundThemeIndex theme = SoundThemeIndex.Parse(themeId, index);
            return await FindSoundAsync(gateway, theme, themeDirectory, soundEvent, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<OsSoundClip?> FindSoundAsync(
        IAppFileSystemGateway gateway,
        SoundThemeIndex theme,
        string themeDirectory,
        OsSoundEvent soundEvent,
        CancellationToken cancellationToken)
    {
        string name = OsSoundEvents.GetSoundName(soundEvent);
        foreach (string directory in theme.GetSearchDirectories(themeDirectory))
        {
            foreach (string extension in OsSoundEvents.Extensions)
            {
                string path = $"{directory}/{name}{extension}";
                FileSystemResult<FileSystemEntrySnapshot> stat = await gateway
                    .StatAsync(new FileSystemStatRequest(VirtualPath.Parse(path)), cancellationToken)
                    .ConfigureAwait(false);
                if (!stat.Succeeded || stat.Value?.Metadata is not FileMetadata file)
                {
                    continue;
                }

                if (file.Length > OsSoundEvents.MaxSoundFileBytes)
                {
                    _logger.LogWarning("sound: {Path} is larger than 1 MiB and is not played", path);
                    continue;
                }

                return new OsSoundClip(
                    $"{path}#{file.Revision}",
                    OsSoundEvents.GetMediaType(path)!,
                    token => ReadBytesAsync(gateway, path, token));
            }
        }

        return null;
    }

    private static async Task<string?> ReadIndexAsync(IAppFileSystemGateway gateway, string themeDirectory, CancellationToken cancellationToken)
    {
        SettingsFileReadResult read = await SettingsTextFile
            .ReadAsync(gateway, VirtualPath.Parse($"{themeDirectory}/{OsSoundEvents.IndexFileName}"), cancellationToken)
            .ConfigureAwait(false);
        return read.Content;
    }

    private static async ValueTask<byte[]> ReadBytesAsync(IAppFileSystemGateway gateway, string path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> read = await gateway
            .ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(path)), cancellationToken)
            .ConfigureAwait(false);
        if (!read.Succeeded || read.Value is null)
        {
            throw new IOException($"Cannot read {path}: {read.Error?.Code}");
        }

        await using FileSystemContentReadHandle handle = read.Value;
        using MemoryStream copy = new();
        await handle.Content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }

    // The user's own files are read as the user (0700 home); the system themes as the system.
    private IAppFileSystemGateway GatewayFor(string path, AuthenticatedPrincipal? principal) =>
        principal is not null && path.StartsWith(principal.HomePath.TrimEnd('/') + "/", StringComparison.Ordinal)
            ? new AppFileSystemGateway(
                _fileSystem, _clock, OsSettingsService.CreateUserOperation(principal), [.. principal.GroupIds.Select(static id => id.ToString())])
            : new AppFileSystemGateway(_fileSystem, _clock, OsSettingsService.CreateSystemOperation(), ["system"]);

    private void OnDocumentChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(SoundSettingsDocuments.DocumentId))
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            // Keep the last good preferences; the read problem is already logged by the settings service.
        }
    }

    private async Task RefreshCoreAsync(bool raiseEvent, CancellationToken cancellationToken)
    {
        // Serialized like SessionTimeZoneService: a stale read finishing last would win.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SoundPreferences previous;
        SoundPreferences current;
        bool sameSession;
        try
        {
            LayeredConfigSnapshot snapshot = await _settings
                .ReadAsync(SoundSettingsDocuments.Document, cancellationToken)
                .ConfigureAwait(false);
            string? home = _session.CurrentPrincipal?.HomePath;
            sameSession = _initialized && home is not null && home == _preferencesHome;
            _preferencesHome = home;
            previous = Preferences;
            current = SoundSettingsDocuments.ToPreferences(snapshot);
            Preferences = current;
        }
        finally
        {
            _refreshGate.Release();
        }

        if (!raiseEvent || current == previous)
        {
            return;
        }

        Changed?.Invoke();
        // Feedback for a choice made in this session (Settings > Sound, or an edited file).
        if (sameSession && current.IsAudible)
        {
            Play(OsSoundEvent.Notification);
        }
    }
}
