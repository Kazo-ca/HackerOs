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
using HackerOs.Theming.Abstractions.IconPacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Appearance;

/// <summary>
/// The shell's selected icon pack (ADR 0203 §4): the pack named by <c>icons.config</c> (the session
/// user's layer over the machine's), loaded from <c>/usr/share/icons</c>, <c>~/.local/share/icons</c>
/// or a theme pack's <c>icons/</c> folder. The Start menu, the mobile home, the window chrome and the
/// taskbar ask it for an app's image and draw their built-in icon when it has none.
/// </summary>
/// <remarks>
/// System folders are read as the system, the user's folders as the session user, so the files' mode
/// bits decide. The pack is re-read when <c>icons.config</c> changes and when the session changes (a
/// null document id from the settings service); editing the images of the pack in use applies at the
/// next selection or sign-in.
/// </remarks>
public sealed class IconPackService : IDisposable
{
    private readonly IFileSystemService _fileSystem;
    private readonly ISessionService _session;
    private readonly ISimulationClock _clock;
    private readonly IOsSettingsService _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _loggedDiagnostics = new(StringComparer.Ordinal);
    private bool _initialized;

    /// <summary>Creates the service over the filesystem, as the session user.</summary>
    /// <param name="fileSystem">Filesystem service.</param>
    /// <param name="session">Session service (whose home holds the user folders).</param>
    /// <param name="clock">Simulation clock used by the gateways.</param>
    /// <param name="settings">OS settings, holding <c>icons.config</c>.</param>
    /// <param name="logger">Optional logger for refused packs and images.</param>
    public IconPackService(
        IFileSystemService fileSystem,
        ISessionService session,
        ISimulationClock clock,
        IOsSettingsService settings,
        ILogger<IconPackService>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _settings.DocumentChanged += OnSettingsChanged;
    }

    /// <summary>Gets the id written in <c>icons.config</c> (<see cref="IconPackLibrary.BuiltInId"/> by default).</summary>
    public string SelectedId { get; private set; } = IconPackLibrary.BuiltInId;

    /// <summary>Gets the loaded pack, or <see langword="null"/> for the built-in icons (or a pack that cannot be found).</summary>
    public IconPack? Current { get; private set; }

    /// <summary>Raised after <see cref="Current"/> changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the image of an app for a surface that otherwise draws the app's own declared image
    /// (window chrome, taskbar, mobile home): the pack's <c>apps/&lt;id&gt;.png</c> always wins; its category
    /// image only replaces the generic icon of an app that declares none.
    /// </summary>
    /// <param name="appId">The app id.</param>
    /// <param name="category">Its category.</param>
    /// <param name="hasDeclaredImage">Whether the app declares its own image (ADR 0164).</param>
    /// <returns>A <c>data:</c> URL, or <see langword="null"/> to keep the surface's own icon.</returns>
    public string? FindAppImage(string appId, string? category, bool hasDeclaredImage) =>
        Current is not { } pack ? null : pack.FindApp(appId) ?? (hasDeclaredImage ? null : pack.FindCategory(category));

    /// <summary>Loads the selected pack and starts watching <c>icons.config</c>, without raising <see cref="Changed"/>.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _initialized = true;
        await RefreshCoreAsync(raiseEvent: false, cancellationToken).ConfigureAwait(false);
        await _settings.WatchAsync(IconPackSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-reads <c>icons.config</c> and the pack, and raises <see cref="Changed"/> when the pack changed.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(raiseEvent: true, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.DocumentChanged -= OnSettingsChanged;
        _gate.Dispose();
    }

    private void OnSettingsChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        // A null document id is the "session changed" signal: another user's layer and folders apply.
        if (_initialized && (args.DocumentId is null || args.Concerns(IconPackSettingsDocuments.Document.DocumentId)))
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
            // Keep the last good pack; the shell keeps drawing what it had.
            _logger.LogWarning(exception, "icons: could not re-read the icon pack");
        }
    }

    private async Task RefreshCoreAsync(bool raiseEvent, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        try
        {
            LayeredConfigSnapshot snapshot = await _settings
                .ReadAsync(IconPackSettingsDocuments.Document, cancellationToken)
                .ConfigureAwait(false);
            string selected = IconPackSettingsDocuments.ToPackId(snapshot);
            IconPack? pack = selected == IconPackLibrary.BuiltInId ? null : await LoadAsync(selected, cancellationToken).ConfigureAwait(false);
            changed = selected != SelectedId || !SamePack(Current, pack);
            SelectedId = selected;
            Current = pack;
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

    private async Task<IconPack?> LoadAsync(string packId, CancellationToken cancellationToken)
    {
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        IAppFileSystemGateway system = new AppFileSystemGateway(_fileSystem, _clock, OsSettingsService.CreateSystemOperation(), ["system"]);
        IAppFileSystemGateway? user = principal is null
            ? null
            : new AppFileSystemGateway(
                _fileSystem,
                _clock,
                OsSettingsService.CreateUserOperation(principal),
                [.. principal.GroupIds.Select(static id => id.ToString())]);
        IconPackFileAccess files = CreateFileAccess(path => GatewayFor(path, system, user, principal));

        IconPackListResult packs = await IconPackLibrary.ListAsync(files, principal?.HomePath, cancellationToken).ConfigureAwait(false);
        Log(packs.Diagnostics);
        if (packs.Find(packId) is not { } info)
        {
            Log([$"icons.config: no installed icon pack has the id '{packId}'; the built-in icons are kept."]);
            return null;
        }

        IconPackLoadResult loaded = await IconPackLibrary.LoadAsync(info, files, cancellationToken).ConfigureAwait(false);
        Log(loaded.Diagnostics);
        return loaded.Pack;
    }

    /// <summary>The library's file access over the OS gateways: listings and whole-file reads.</summary>
    /// <param name="gatewayFor">Chooses the gateway (system or user) for a path.</param>
    internal static IconPackFileAccess CreateFileAccess(Func<string, IAppFileSystemGateway> gatewayFor) => new(
        (directory, token) => ListAsync(gatewayFor(directory), directory, FileSystemEntryKind.Directory, token),
        (directory, token) => ListAsync(gatewayFor(directory), directory, FileSystemEntryKind.File, token),
        (path, token) => ReadAllBytesAsync(gatewayFor(path), path, token));

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
            .Select(entry => $"{directory.TrimEnd('/')}/{entry.Name.Value}")];
    }

    private static async Task<byte[]> ReadAllBytesAsync(IAppFileSystemGateway gateway, string path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> result = await gateway
            .ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(path)), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            throw new IOException($"Cannot read {path}: {result.Error?.Code}");
        }

        await using FileSystemContentReadHandle handle = result.Value;
        using MemoryStream buffer = new();
        await handle.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private void Log(IEnumerable<string> diagnostics)
    {
        foreach (string line in diagnostics)
        {
            if (_loggedDiagnostics.Add(line))
            {
                _logger.LogWarning("icons: {Diagnostic}", line);
            }
        }
    }

    private static bool SamePack(IconPack? left, IconPack? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.Info == right.Info
              && left.Images.Count == right.Images.Count
              && left.Images.All(pair => right.Images.TryGetValue(pair.Key, out string? url) && url == pair.Value);
}
