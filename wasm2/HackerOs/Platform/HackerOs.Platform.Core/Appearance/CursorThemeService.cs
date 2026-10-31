using HackerOs.App.Abstractions;
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
/// The shell's live cursor theme (ADR 0205 §3, §4): the id of the layered <c>cursors.config</c>
/// (the session user's <c>~/.config/hackeros/</c> over the machine's <c>/etc/hackeros/</c>), resolved
/// against the built-in themes and the theme folders of <c>/etc/hackeros/cursors/</c> (read as the
/// system) and <c>~/.config/hackeros/cursors/</c> (read as the user, so mode bits decide).
/// </summary>
/// <remarks>
/// The folders are re-read when <c>cursors.config</c> changes and when the session changes, not on
/// every file change: a theme copied into a folder shows in Settings at once (Settings reads the
/// folders itself) and applies when it is chosen. Refused lines and images are written once each
/// to the log (ADR 0044 §5).
/// </remarks>
public sealed class CursorThemeService : IDisposable
{
    private readonly IFileSystemService _fileSystem;
    private readonly ISessionService _session;
    private readonly ISimulationClock _clock;
    private readonly IOsSettingsService _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _loggedDiagnostics = new(StringComparer.Ordinal);
    private bool _initialized;

    /// <summary>Creates the service.</summary>
    /// <param name="fileSystem">Filesystem service.</param>
    /// <param name="session">Session service (whose home holds the user folder).</param>
    /// <param name="clock">Simulation clock used by the gateways.</param>
    /// <param name="settings">OS settings service.</param>
    /// <param name="logger">Optional logger for refused theme files.</param>
    public CursorThemeService(
        IFileSystemService fileSystem,
        ISessionService session,
        ISimulationClock clock,
        IOsSettingsService settings,
        ILogger<CursorThemeService>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _settings.DocumentChanged += OnDocumentChanged;
    }

    /// <summary>Gets the theme to draw: <c>system</c> until initialization completes.</summary>
    public CursorTheme Current { get; private set; } = CursorThemeCatalog.System;

    /// <summary>Raised after <see cref="Current"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Reads the file and the folders, and starts watching the file, without raising <see cref="Changed"/>.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _initialized = true;
        await RefreshCoreAsync(raiseEvent: false, cancellationToken).ConfigureAwait(false);
        await _settings.WatchAsync(CursorSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-reads the file and the folders; raises <see cref="Changed"/> when the theme changed.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(raiseEvent: true, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.DocumentChanged -= OnDocumentChanged;
        _gate.Dispose();
    }

    private void OnDocumentChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        // A null document id is the settings service's "session changed" signal.
        if (_initialized && (args.DocumentId is null || args.Concerns(CursorSettingsDocuments.Document.DocumentId)))
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
            // Keep the last good theme: the pointer never disappears because a file is unreadable.
            _logger.LogWarning(exception, "cursors: could not re-read the cursor theme");
        }
    }

    private async Task RefreshCoreAsync(bool raiseEvent, CancellationToken cancellationToken)
    {
        // Serialized like ThemePreferenceService: a stale read finishing last would win otherwise.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CursorTheme theme;
        try
        {
            LayeredConfigSnapshot snapshot = await _settings
                .ReadAsync(CursorSettingsDocuments.Document, cancellationToken)
                .ConfigureAwait(false);
            string id = CursorSettingsDocuments.ToThemeId(snapshot);
            theme = CursorThemeCatalog.TryGet(id, out CursorTheme builtIn)
                ? builtIn
                : (await LoadLibraryAsync(cancellationToken).ConfigureAwait(false)).Resolve(id);
        }
        finally
        {
            _gate.Release();
        }

        bool changed = !ReferenceEquals(theme, Current) && !SameTheme(theme, Current);
        Current = theme;
        if (raiseEvent && changed)
        {
            Changed?.Invoke();
        }
    }

    private async Task<CursorThemeLibraryResult> LoadLibraryAsync(CancellationToken cancellationToken)
    {
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        IAppFileSystemGateway system = CreateGateway(OsSettingsService.CreateSystemOperation(), ["system"]);
        IAppFileSystemGateway? user = principal is null
            ? null
            : CreateGateway(OsSettingsService.CreateUserOperation(principal), [.. principal.GroupIds.Select(static id => id.ToString())]);
        IAppFileSystemGateway GatewayFor(string path) =>
            user is not null && path.StartsWith(principal!.HomePath.TrimEnd('/') + "/", StringComparison.Ordinal) ? user : system;

        CursorThemeLibraryResult result = await CursorThemeLibrary.LoadAsync(
            (directory, token) => ListDirectoriesAsync(GatewayFor(directory), directory, token),
            async (path, token) => System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(GatewayFor(path), path, CursorThemeFile.MaxIndexBytes, token).ConfigureAwait(false)),
            (path, token) => ReadBytesAsync(GatewayFor(path), path, CursorThemeFile.MaxImageBytes, token),
            principal?.HomePath,
            cancellationToken).ConfigureAwait(false);

        foreach (ThemeFileDiagnostic diagnostic in result.Diagnostics)
        {
            string line = diagnostic.ToString();
            if (_loggedDiagnostics.Add(line))
            {
                _logger.LogWarning("cursors: {Diagnostic}", line);
            }
        }

        return result;
    }

    private static async Task<IReadOnlyList<string>> ListDirectoriesAsync(IAppFileSystemGateway gateway, string directory, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemDirectorySnapshot> result = await gateway
            .EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse(directory)), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            throw new IOException($"Cannot list {directory}: {result.Error?.Code}");
        }

        return [.. result.Value.Entries
            .Where(static entry => entry.Metadata.Kind == FileSystemEntryKind.Directory)
            .Select(entry => $"{directory}/{entry.Name.Value}")];
    }

    // Reads at most one byte more than the limit: the library refuses an image over the limit, and
    // a huge file is never copied whole into memory.
    private static async Task<byte[]> ReadBytesAsync(IAppFileSystemGateway gateway, string path, int limit, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> read = await gateway
            .ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(path)), cancellationToken)
            .ConfigureAwait(false);
        if (!read.Succeeded || read.Value is null)
        {
            throw new IOException(read.Error?.Code.ToString() ?? "NotFound");
        }

        await using FileSystemContentReadHandle handle = read.Value;
        using MemoryStream copy = new();
        byte[] buffer = new byte[8192];
        int count;
        while (copy.Length <= limit && (count = await handle.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            copy.Write(buffer, 0, count);
        }

        return copy.ToArray();
    }

    private IAppFileSystemGateway CreateGateway(AppOperationContext operation, IReadOnlyList<string> groups) =>
        new AppFileSystemGateway(_fileSystem, _clock, operation, groups);

    private static bool SameTheme(CursorTheme left, CursorTheme right) =>
        left.Id == right.Id
        && left.SourcePath == right.SourcePath
        && left.Images.Count == right.Images.Count
        && left.Images.All(pair => right.Images.TryGetValue(pair.Key, out CursorImage? other) && other == pair.Value);
}
