using HackerOs.App.Abstractions;
using System.Globalization;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions;
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
/// The wallpaper keys of <c>appearance.config</c> (ADR 0202 §1): the wallpaper of screen 1, used by
/// every screen without its own, how it covers the screen, and each secondary screen's own choice.
/// </summary>
/// <param name="Wallpaper">Value of <c>wallpaper</c>: empty, <c>builtin:&lt;id&gt;</c> or an absolute path.</param>
/// <param name="Fit">Value of <c>wallpaperFit</c>.</param>
/// <param name="Screens">Non-empty <c>wallpaperScreen&lt;n&gt;</c> values, by screen number.</param>
public sealed record WallpaperPreferences(string Wallpaper, WallpaperFit Fit, IReadOnlyDictionary<int, string> Screens)
{
    /// <summary>Gets the value of <c>mobileWallpaper</c> (ADR 0217 §1): empty keeps the mobile theme's background.</summary>
    public string Mobile { get; init; } = string.Empty;

    /// <summary>Gets the value of <c>mobileWallpaperFit</c> (ADR 0217 §1).</summary>
    public WallpaperFit MobileFit { get; init; } = WallpaperFit.Fill;

    /// <summary>Gets the clean-profile preferences: the theme's own background on every screen and on mobile.</summary>
    public static WallpaperPreferences Default { get; } = new(string.Empty, WallpaperFit.Fill, new Dictionary<int, string>());

    /// <summary>Gets the wallpaper value of a screen: its own, otherwise the one of screen 1.</summary>
    /// <param name="screenNumber">Screen number, 1 or more.</param>
    public string ForScreen(int screenNumber) =>
        screenNumber > 1 && Screens.TryGetValue(screenNumber, out string? own) && own.Length > 0 ? own : Wallpaper;

    /// <summary>Reads the preferences from a merged snapshot of <see cref="AppearanceSettingsDocuments.Document"/>.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public static WallpaperPreferences FromSnapshot(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Dictionary<int, string> screens = [];
        for (int number = 2; number <= AppearanceSettingsDocuments.MaxWallpaperScreen; number++)
        {
            if (snapshot.Values.TryGetValue(AppearanceSettingsDocuments.WallpaperScreenKey(number), out string? value)
                && !string.IsNullOrWhiteSpace(value))
            {
                screens[number] = value.Trim();
            }
        }

        return new WallpaperPreferences(
            snapshot.Values.TryGetValue(AppearanceSettingsDocuments.WallpaperKey, out string? wallpaper) ? wallpaper.Trim() : string.Empty,
            WallpaperCatalog.ParseFit(snapshot.Values.TryGetValue(AppearanceSettingsDocuments.WallpaperFitKey, out string? fit) ? fit : null),
            screens)
        {
            Mobile = snapshot.Values.TryGetValue(AppearanceSettingsDocuments.MobileWallpaperKey, out string? mobile) ? mobile.Trim() : string.Empty,
            MobileFit = WallpaperCatalog.ParseFit(snapshot.Values.TryGetValue(AppearanceSettingsDocuments.MobileWallpaperFitKey, out string? mobileFit) ? mobileFit : null),
        };
    }

    /// <summary>Compares the values, including every screen's own choice.</summary>
    /// <param name="other">The other preferences.</param>
    public bool SameAs(WallpaperPreferences? other) =>
        other is not null
        && Wallpaper == other.Wallpaper
        && Fit == other.Fit
        && Mobile == other.Mobile
        && MobileFit == other.MobileFit
        && Screens.Count == other.Screens.Count
        && Screens.All(pair => other.Screens.TryGetValue(pair.Key, out string? value) && value == pair.Value);
}

/// <summary>
/// The image a screen shows (ADR 0202 §4): either the address of a built-in image, or the bytes of a
/// file read from the virtual filesystem with their media type, read from the bytes themselves.
/// </summary>
/// <param name="Value">The wallpaper value it comes from.</param>
/// <param name="Fit">How it covers the screen.</param>
/// <param name="AssetPath">Address of a built-in image, otherwise <see langword="null"/>.</param>
/// <param name="Content">Bytes of a file, otherwise <see langword="null"/>.</param>
/// <param name="MediaType"><c>image/png</c>, <c>image/jpeg</c> or <c>image/webp</c> for a file.</param>
public sealed record WallpaperImage(string Value, WallpaperFit Fit, string? AssetPath, byte[]? Content, string? MediaType);

/// <summary>
/// Gives the shell the wallpaper of each screen (ADR 0202 §4). It follows the wallpaper keys of
/// <c>appearance.config</c> like <see cref="ThemePreferenceService"/> follows the theme keys, and
/// reads an image file <b>as the session user</b>, so the file's mode bits decide: a user cannot show
/// a file they could not open. A missing, unreadable, too large or non-image file falls back to the
/// theme's background, with one warning in the log.
/// </summary>
public sealed class WallpaperService : IDisposable
{
    private readonly IOsSettingsService _settings;
    private readonly IFileSystemService _fileSystem;
    private readonly ISessionService _session;
    private readonly ISimulationClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, Task<WallpaperFile?>> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedProblems = new(StringComparer.Ordinal);
    private Task? _initialization;

    /// <summary>Creates the service over the OS settings and the filesystem.</summary>
    /// <param name="settings">OS settings service.</param>
    /// <param name="fileSystem">Filesystem service.</param>
    /// <param name="session">Session service, whose user reads the image files.</param>
    /// <param name="clock">Simulation clock used by the gateways.</param>
    /// <param name="logger">Optional logger for files that cannot be shown.</param>
    public WallpaperService(
        IOsSettingsService settings,
        IFileSystemService fileSystem,
        ISessionService session,
        ISimulationClock clock,
        ILogger<WallpaperService>? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _settings.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>Gets the current preferences, the theme's background until initialization completes.</summary>
    public WallpaperPreferences Current { get; private set; } = WallpaperPreferences.Default;

    /// <summary>Raised after the wallpaper of at least one screen may have changed.</summary>
    public event Action? Changed;

    /// <summary>Reads the preferences once and starts following them; later calls wait for the first one.</summary>
    /// <param name="cancellationToken">Cancels the first read.</param>
    public Task EnsureInitializedAsync(CancellationToken cancellationToken = default) =>
        _initialization ??= InitializeCoreAsync(cancellationToken);

    /// <summary>Re-reads the preferences and raises <see cref="Changed"/> when they changed.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(raiseEvent: true, forget: false, cancellationToken);

    /// <summary>
    /// Gets what a screen shows: <see langword="null"/> for the theme's own background (no wallpaper,
    /// or a file that cannot be shown).
    /// </summary>
    /// <param name="screenNumber">Screen number, 1 or more.</param>
    /// <param name="cancellationToken">Cancels the file read.</param>
    public Task<WallpaperImage?> GetImageAsync(int screenNumber, CancellationToken cancellationToken = default)
    {
        WallpaperPreferences preferences = Current;
        return GetImageCoreAsync(preferences.ForScreen(screenNumber), preferences.Fit);
    }

    /// <summary>
    /// Gets what the mobile home screen shows (ADR 0217 §2): <see langword="null"/> for the mobile
    /// theme's own background (no <c>mobileWallpaper</c>, or a file that cannot be shown).
    /// </summary>
    /// <param name="cancellationToken">Cancels the file read.</param>
    public Task<WallpaperImage?> GetMobileImageAsync(CancellationToken cancellationToken = default)
    {
        WallpaperPreferences preferences = Current;
        return GetImageCoreAsync(preferences.Mobile, preferences.MobileFit);
    }

    /// <summary>Resolves one wallpaper value: a built-in image, or a file shared by every surface that shows it.</summary>
    private async Task<WallpaperImage?> GetImageCoreAsync(string value, WallpaperFit fit)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (WallpaperCatalog.TryGetBuiltIn(value, out BuiltInWallpaper builtIn))
        {
            return new WallpaperImage(value, fit, builtIn.AssetPath, null, null);
        }

        if (!WallpaperCatalog.IsValidValue(value))
        {
            LogOnce(value, "wallpaper: '{Value}' is neither a built-in wallpaper nor the path of a PNG, JPEG or WebP file", value);
            return null;
        }

        Task<WallpaperFile?> load;
        lock (_files)
        {
            if (!_files.TryGetValue(value, out load!))
            {
                // Not the caller's token: the load is shared by every screen that shows this file.
                load = LoadFileAsync(value, CancellationToken.None);
                _files[value] = load;
            }
        }

        WallpaperFile? file = await load.ConfigureAwait(false);
        return file is null ? null : new WallpaperImage(value, fit, null, file.Content, file.MediaType);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.DocumentChanged -= OnDocumentChanged;
        _refreshGate.Dispose();
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await RefreshCoreAsync(raiseEvent: false, forget: false, cancellationToken).ConfigureAwait(false);
        await _settings.WatchAsync(AppearanceSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
    }

    private void OnDocumentChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        // A null document id is the settings service's "session changed" signal: another user's
        // appearance.config applies, and that user's rights decide which files can be shown.
        if (_initialization is not null && (args.DocumentId is null || args.Concerns(AppearanceSettingsDocuments.Document.DocumentId)))
        {
            _ = RefreshInBackgroundAsync(forget: args.DocumentId is null);
        }
    }

    private async Task RefreshInBackgroundAsync(bool forget)
    {
        try
        {
            await RefreshCoreAsync(raiseEvent: true, forget, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Keep the last good wallpaper; the read problem is already logged by the settings service.
        }
    }

    private async Task RefreshCoreAsync(bool raiseEvent, bool forget, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        try
        {
            LayeredConfigSnapshot snapshot = await _settings
                .ReadAsync(AppearanceSettingsDocuments.Document, cancellationToken)
                .ConfigureAwait(false);
            WallpaperPreferences preferences = WallpaperPreferences.FromSnapshot(snapshot);
            changed = forget || !preferences.SameAs(Current);
            Current = preferences;
            ForgetFiles(forget ? null : preferences);
        }
        finally
        {
            _refreshGate.Release();
        }

        if (raiseEvent && changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Drops the loaded files no screen uses any more, or every file: a new choice of the same path
    /// reads the file again, so an image edited in place shows its new content.
    /// </summary>
    private void ForgetFiles(WallpaperPreferences? keepFor)
    {
        lock (_files)
        {
            HashSet<string> used = keepFor is null
                ? []
                : [keepFor.Wallpaper, keepFor.Mobile, .. keepFor.Screens.Values];
            foreach (string path in _files.Keys.Where(path => !used.Contains(path)).ToArray())
            {
                _files.Remove(path);
            }
        }
    }

    private async Task<WallpaperFile?> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            FileSystemResult<FileSystemContentReadHandle> read = await CreateGateway()
                .ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(path)), cancellationToken)
                .ConfigureAwait(false);
            if (!read.Succeeded || read.Value is null)
            {
                LogOnce(path, "wallpaper: cannot read {Path}: {Error}", path, read.Error?.Code.ToString() ?? "unknown");
                return null;
            }

            await using FileSystemContentReadHandle handle = read.Value;
            byte[]? content = await ReadBoundedAsync(handle.Content, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                LogOnce(path, "wallpaper: {Path} is larger than {Limit} bytes", path, WallpaperCatalog.MaxImageBytes.ToString(CultureInfo.InvariantCulture));
                return null;
            }

            if (!WallpaperCatalog.TryDetectMediaType(content, out string mediaType))
            {
                LogOnce(path, "wallpaper: {Path} is not a PNG, JPEG or WebP image", path);
                return null;
            }

            return new WallpaperFile(content, mediaType);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            LogOnce(path, "wallpaper: cannot read {Path}: {Error}", path, exception.Message);
            return null;
        }
    }

    /// <summary>Reads the whole stream, or returns <see langword="null"/> as soon as it passes the size limit.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > WallpaperCatalog.MaxImageBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }

    /// <summary>The session user's gateway, or the system's before anyone signed in.</summary>
    private AppFileSystemGateway CreateGateway()
    {
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        return principal is null
            ? new AppFileSystemGateway(_fileSystem, _clock, OsSettingsService.CreateSystemOperation(), ["system"])
            : new AppFileSystemGateway(
                _fileSystem,
                _clock,
                OsSettingsService.CreateUserOperation(principal),
                [.. principal.GroupIds.Select(static id => id.ToString())]);
    }

    private void LogOnce(string key, string message, params object?[] args)
    {
        if (_loggedProblems.Add(key + "\n" + message))
        {
#pragma warning disable CA2254 // The templates are the constant messages of this class.
            _logger.LogWarning(message, args);
#pragma warning restore CA2254
        }
    }

    private sealed record WallpaperFile(byte[] Content, string MediaType);
}
