using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Simulation.Abstractions;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Settings;

/// <summary>
/// Reads and writes the OS's own settings files (<c>/etc/hackeros</c>, <c>~/.config/hackeros</c>,
/// <c>~/.local/state/hackeros</c>) with the same layering engine as the SDK's <c>HackSettings</c>
/// (ADR 0044 section 8).
/// </summary>
public interface IOsSettingsService
{
    /// <summary>
    /// Raised after this service wrote a document, after a watched document changed on disk, and
    /// (with a <see langword="null"/> id) after a session started or ended, since every user layer
    /// then points at another home.
    /// </summary>
    event EventHandler<OsSettingsChangedEventArgs>? DocumentChanged;

    /// <summary>Gets the home directory whose user layer is read, or <see langword="null"/> before login.</summary>
    string? CurrentHomeDirectory { get; }

    /// <summary>Merges a document's system and session-user layers over its defaults.</summary>
    /// <param name="document">Document to read.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The merged snapshot; never fails on a bad file (problems are logged as warnings).</returns>
    ValueTask<LayeredConfigSnapshot> ReadAsync(OsSettingsDocument document, CancellationToken cancellationToken = default);

    /// <summary>Writes one value to one layer, preserving every other line.</summary>
    /// <param name="document">Document to write.</param>
    /// <param name="qualifiedKey">Declared key.</param>
    /// <param name="value">Serialized value; must be valid for the key's type.</param>
    /// <param name="target">Layer to write.</param>
    /// <param name="writer">
    /// Gateway whose rights decide the write, typically the calling app's own
    /// (<c>AppContext.FileSystem</c>) so a system-layer write goes through the administrator
    /// approval of ADR 0042. When <see langword="null"/>, the session user's own rights are used.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">The key is undeclared or the value invalid.</exception>
    /// <exception cref="InvalidOperationException">The key is locked, or the user layer is targeted with no session or on a system-only document.</exception>
    /// <exception cref="UnauthorizedAccessException">The file's permissions deny the write.</exception>
    ValueTask SetAsync(
        OsSettingsDocument document,
        string qualifiedKey,
        string value,
        SettingLayer target = SettingLayer.User,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes several values to one layer in a single file edit, so a system-layer change needs one
    /// administrator approval rather than one per key. Nothing is written when any key is
    /// undeclared, invalid or (for the user layer) locked.
    /// </summary>
    /// <param name="document">Document to write.</param>
    /// <param name="values">Declared keys and their serialized values.</param>
    /// <param name="target">Layer to write.</param>
    /// <param name="writer">See <see cref="SetAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">A key is undeclared or a value invalid.</exception>
    /// <exception cref="InvalidOperationException">A key is locked, or the user layer is unavailable.</exception>
    /// <exception cref="UnauthorizedAccessException">The file's permissions deny the write.</exception>
    ValueTask SetManyAsync(
        OsSettingsDocument document,
        IReadOnlyList<KeyValuePair<string, string>> values,
        SettingLayer target = SettingLayer.User,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes one key from one layer so the next layer applies again.</summary>
    /// <param name="document">Document to write.</param>
    /// <param name="qualifiedKey">Declared key.</param>
    /// <param name="target">Layer to write.</param>
    /// <param name="writer">See <see cref="SetAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask ResetAsync(
        OsSettingsDocument document,
        string qualifiedKey,
        SettingLayer target = SettingLayer.User,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a whole-file document, or <see langword="null"/> when it does not exist or cannot be read.</summary>
    /// <param name="file">File to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<string?> ReadFileAsync(OsSettingsFile file, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a strict whole-file document (ADR 0044 section 8). A missing file gives
    /// <paramref name="defaultContent"/>; an invalid one also gives it, with one logged warning, and
    /// stays untouched on disk so the administrator can fix it.
    /// </summary>
    /// <param name="file">File to read.</param>
    /// <param name="validator">Strict validator of the whole document.</param>
    /// <param name="defaultContent">Built-in content used when the file is absent or invalid.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<string> ReadValidatedFileAsync(
        OsSettingsFile file,
        ISettingsDocumentValidator validator,
        string defaultContent,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a whole-file document, creating it and its directories when needed.</summary>
    /// <param name="file">File to write.</param>
    /// <param name="content">New content.</param>
    /// <param name="writer">See <see cref="SetAsync"/>; system files default to an audited system operation.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask WriteFileAsync(
        OsSettingsFile file,
        string content,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts watching a document's files so external edits (<c>nano /etc/hackeros/…</c>) raise
    /// <see cref="DocumentChanged"/>. Idempotent per document; watches follow session changes.
    /// </summary>
    /// <param name="document">Document to watch.</param>
    /// <param name="cancellationToken">Cancels the subscriptions.</param>
    ValueTask WatchAsync(OsSettingsDocument document, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IOsSettingsService"/>: system-layer reads run as an audited system operation;
/// user-layer reads and writes run with the session user's own identity, groups and authority, so
/// mode bits apply exactly as for that user's apps.
/// </summary>
public sealed class OsSettingsService : IOsSettingsService, IAsyncDisposable
{
    /// <summary>App identity used for the OS's own settings operations.</summary>
    public const string ShellAppId = "org.hackeros.shell";

    private readonly IFileSystemService _fileSystem;
    private readonly ISessionService _session;
    private readonly ISimulationClock _clock;
    private readonly ITopicMessageBus? _topicBus;
    private readonly ILogger _logger;
    private readonly IDisposable[] _sessionSubscriptions;
    private readonly HashSet<string> _loggedWarnings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (OsSettingsDocument Document, List<SettingsFilesWatcher> Watchers)> _watches =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _watchGate = new(1, 1);

    /// <summary>Creates the service.</summary>
    /// <param name="fileSystem">Filesystem engine; every access is authorized there.</param>
    /// <param name="session">Session service giving the user whose layer applies.</param>
    /// <param name="clock">Simulation clock for authorization timestamps.</param>
    /// <param name="topicBus">Optional topic bus enabling <see cref="WatchAsync"/>.</param>
    /// <param name="eventBus">Optional kernel event bus; session start/end then raises <see cref="DocumentChanged"/>.</param>
    /// <param name="logger">Optional logger receiving one warning per invalid line (written to <c>/var/log/syslog</c> by the logging pipeline).</param>
    public OsSettingsService(
        IFileSystemService fileSystem,
        ISessionService session,
        ISimulationClock clock,
        ITopicMessageBus? topicBus = null,
        IEventBus? eventBus = null,
        ILogger<OsSettingsService>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _topicBus = topicBus;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _sessionSubscriptions = eventBus is null
            ? []
            :
            [
                eventBus.Subscribe<SessionActivatedEvent>(_ => { Task ignored = OnSessionChangedAsync(); }),
                eventBus.Subscribe<SessionLoggedOutEvent>(_ => { Task ignored = OnSessionChangedAsync(); }),
                // ADR 0201: switching user changes the foreground session too.
                eventBus.Subscribe<SessionSwitchedOutEvent>(_ => { Task ignored = OnSessionChangedAsync(); }),
                eventBus.Subscribe<SessionResumedEvent>(_ => { Task ignored = OnSessionChangedAsync(); })
            ];
    }

    /// <inheritdoc />
    public event EventHandler<OsSettingsChangedEventArgs>? DocumentChanged;

    /// <inheritdoc />
    public string? CurrentHomeDirectory => _session.CurrentPrincipal?.HomePath;

    /// <inheritdoc />
    public async ValueTask<LayeredConfigSnapshot> ReadAsync(OsSettingsDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;

        SettingsFileReadResult system = await SettingsTextFile
            .ReadAsync(CreateSystemGateway(), document.SystemPath, cancellationToken)
            .ConfigureAwait(false);
        SettingsFileReadResult user = new(null, null, null);
        string userPath = principal is null ? string.Empty : document.GetUserPath(principal.HomePath).Value;
        if (!document.SystemOnly && principal is not null)
        {
            user = await SettingsTextFile
                .ReadAsync(CreateUserGateway(principal), document.GetUserPath(principal.HomePath), cancellationToken)
                .ConfigureAwait(false);
        }

        LogReadError(system);
        LogReadError(user);
        LayeredConfigSnapshot snapshot = LayeredConfigResolver.Resolve(
            document.Schema, system.Content, user.Content, document.SystemPath.Value, userPath);
        foreach (LayeredConfigWarning warning in snapshot.Warnings)
        {
            long? revision = warning.Path == document.SystemPath.Value ? system.Revision : user.Revision;
            if (_loggedWarnings.Add($"{warning.Path}@{revision}:{warning.LineNumber}:{warning.Message}"))
            {
                _logger.LogWarning("{SettingsWarning}", warning.ToLogLine());
            }
        }

        return snapshot;
    }

    /// <inheritdoc />
    public ValueTask SetAsync(
        OsSettingsDocument document,
        string qualifiedKey,
        string value,
        SettingLayer target = SettingLayer.User,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetManyAsync(document, [new KeyValuePair<string, string>(qualifiedKey, value)], target, writer, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask SetManyAsync(
        OsSettingsDocument document,
        IReadOnlyList<KeyValuePair<string, string>> values,
        SettingLayer target = SettingLayer.User,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return;
        }

        // Validate everything first: a batch is written whole or not at all.
        List<(SettingFieldDeclaration Field, string Value)> normalizedValues = [];
        foreach ((string qualifiedKey, string value) in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            SettingFieldDeclaration field = RequireField(document, qualifiedKey);
            if (!SettingValueCodec.TryNormalize(field, value, out string normalized))
            {
                throw new ArgumentException(
                    $"Value '{value}' is invalid for setting '{qualifiedKey}' (expected {SettingValueCodec.DescribeExpected(field)}).",
                    nameof(values));
            }

            normalizedValues.Add((field, normalized));
        }

        if (target == SettingLayer.User)
        {
            LayeredConfigSnapshot current = await ReadAsync(document, cancellationToken).ConfigureAwait(false);
            foreach ((SettingFieldDeclaration field, _) in normalizedValues)
            {
                if (current.LockedKeys.Contains(field.QualifiedKey))
                {
                    throw new InvalidOperationException(
                        $"Setting '{field.QualifiedKey}' is locked by the administrator in {document.SystemPath}.");
                }
            }
        }

        await EditAsync(
            document,
            target,
            writer,
            content =>
            {
                string edited = content ?? string.Empty;
                foreach ((SettingFieldDeclaration field, string normalized) in normalizedValues)
                {
                    edited = ConfigDocumentEditor.SetValue(edited, field.Group, field.Key, normalized);
                }

                return edited;
            },
            createIfMissing: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask ResetAsync(
        OsSettingsDocument document,
        string qualifiedKey,
        SettingLayer target = SettingLayer.User,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        SettingFieldDeclaration field = RequireField(document, qualifiedKey);
        return EditAsync(
            document,
            target,
            writer,
            content => ConfigDocumentEditor.RemoveValue(content, field.Group, field.Key),
            createIfMissing: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<string?> ReadFileAsync(OsSettingsFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        if (file.Location != OsSettingsLocation.System && principal is null)
        {
            return null;
        }

        IAppFileSystemGateway gateway = file.Location == OsSettingsLocation.System
            ? CreateSystemGateway()
            : CreateUserGateway(principal!);
        SettingsFileReadResult read = await SettingsTextFile
            .ReadAsync(gateway, file.Resolve(principal?.HomePath), cancellationToken)
            .ConfigureAwait(false);
        LogReadError(read);
        return read.Content;
    }

    /// <inheritdoc />
    public async ValueTask<string> ReadValidatedFileAsync(
        OsSettingsFile file,
        ISettingsDocumentValidator validator,
        string defaultContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(defaultContent);
        string? content = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return defaultContent;
        }

        IReadOnlyList<string> errors = validator.Validate(content);
        if (errors.Count == 0)
        {
            return content;
        }

        string path = file.Resolve(_session.CurrentPrincipal?.HomePath).Value;
        if (_loggedWarnings.Add($"{path}:{content.GetHashCode(StringComparison.Ordinal)}"))
        {
            _logger.LogWarning(
                "settings: {Path}:1: invalid document ({Errors}), using built-in defaults",
                path,
                string.Join(", ", errors));
        }

        return defaultContent;
    }

    /// <inheritdoc />
    public async ValueTask WriteFileAsync(
        OsSettingsFile file,
        string content,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(content);
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        IAppFileSystemGateway gateway = writer
            ?? (file.Location == OsSettingsLocation.System
                ? CreateSystemGateway()
                : CreateUserGateway(principal ?? throw new InvalidOperationException("A user settings file needs an active session.")));
        FileSystemError? error = await SettingsTextFile
            .WriteAllAsync(gateway, file.Resolve(principal?.HomePath), content, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            throw ToException(error);
        }

        DocumentChanged?.Invoke(this, new OsSettingsChangedEventArgs(file.RelativePath));
    }

    /// <inheritdoc />
    public async ValueTask WatchAsync(OsSettingsDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_topicBus is null)
        {
            return;
        }

        await _watchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_watches.ContainsKey(document.DocumentId))
            {
                return;
            }

            List<SettingsFilesWatcher> watchers = await StartWatchersAsync(document, cancellationToken).ConfigureAwait(false);
            _watches[document.DocumentId] = (document, watchers);
        }
        finally
        {
            _watchGate.Release();
        }
    }

    /// <summary>Builds an audited system-operation gateway (used for <c>/etc</c> reads, the migration and system files).</summary>
    public IAppFileSystemGateway CreateSystemGateway() => new AppFileSystemGateway(
        _fileSystem,
        _clock,
        new AppOperationContext
        {
            AppId = ShellAppId,
            UserId = "system",
            UserName = "system",
            UserAuthority = AppAuthority.System,
            GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
            IsSystemOperation = true
        },
        ["system"]);

    /// <summary>
    /// Builds a gateway acting as <paramref name="principal"/> itself: ordinary user authority, the
    /// user's groups, and only home and system-read filesystem capabilities, so mode bits decide.
    /// </summary>
    /// <param name="principal">Session principal.</param>
    public IAppFileSystemGateway CreateUserGateway(AuthenticatedPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new AppFileSystemGateway(_fileSystem, _clock, CreateUserOperation(principal), [.. principal.GroupIds.Select(static id => id.ToString())]);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (IDisposable subscription in _sessionSubscriptions)
        {
            subscription.Dispose();
        }

        await _watchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach ((_, List<SettingsFilesWatcher> watchers) in _watches.Values)
            {
                foreach (SettingsFilesWatcher watcher in watchers)
                {
                    await watcher.DisposeAsync().ConfigureAwait(false);
                }
            }

            _watches.Clear();
        }
        finally
        {
            _watchGate.Release();
        }
    }

    private async ValueTask EditAsync(
        OsSettingsDocument document,
        SettingLayer target,
        IAppFileSystemGateway? writer,
        Func<string?, string> edit,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        VirtualPath path;
        IAppFileSystemGateway gateway;
        switch (target)
        {
            case SettingLayer.User:
                if (document.SystemOnly)
                {
                    throw new InvalidOperationException($"OS settings document '{document.DocumentId}' has no user layer.");
                }

                if (principal is null)
                {
                    throw new InvalidOperationException("The user settings layer needs an active session.");
                }

                path = document.GetUserPath(principal.HomePath);
                gateway = writer ?? CreateUserGateway(principal);
                break;
            case SettingLayer.System:
                path = document.SystemPath;
                gateway = writer ?? (principal is null ? CreateSystemGateway() : CreateUserGateway(principal));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Only the User and System layers can be written.");
        }

        FileSystemError? error = await SettingsTextFile
            .EditAsync(gateway, path, edit, createIfMissing, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null && !(error.Code == FileSystemErrorCode.NotFound && !createIfMissing))
        {
            throw ToException(error);
        }

        DocumentChanged?.Invoke(this, new OsSettingsChangedEventArgs(document.DocumentId));
    }

    private async ValueTask<List<SettingsFilesWatcher>> StartWatchersAsync(OsSettingsDocument document, CancellationToken cancellationToken)
    {
        List<SettingsFilesWatcher> watchers = [];
        ValueTask Raise()
        {
            DocumentChanged?.Invoke(this, new OsSettingsChangedEventArgs(document.DocumentId));
            return ValueTask.CompletedTask;
        }

        SettingsFilesWatcher system = new(CreateWatchGateway(CreateSystemOperation(), ["system"]), [document.SystemPath], Raise);
        await system.StartAsync(cancellationToken).ConfigureAwait(false);
        watchers.Add(system);

        if (!document.SystemOnly && _session.CurrentPrincipal is { } principal)
        {
            SettingsFilesWatcher user = new(
                CreateWatchGateway(CreateUserOperation(principal), [.. principal.GroupIds.Select(static id => id.ToString())]),
                [document.GetUserPath(principal.HomePath)],
                Raise);
            await user.StartAsync(cancellationToken).ConfigureAwait(false);
            watchers.Add(user);
        }

        return watchers;
    }

    private async Task OnSessionChangedAsync()
    {
        try
        {
            // User-layer watches point at the previous home: rebuild them for the new session.
            await _watchGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (string documentId in _watches.Keys.ToArray())
                {
                    (OsSettingsDocument document, List<SettingsFilesWatcher> watchers) = _watches[documentId];
                    foreach (SettingsFilesWatcher watcher in watchers)
                    {
                        await watcher.DisposeAsync().ConfigureAwait(false);
                    }

                    _watches[documentId] = (document, await StartWatchersAsync(document, CancellationToken.None).ConfigureAwait(false));
                }
            }
            finally
            {
                _watchGate.Release();
            }

            DocumentChanged?.Invoke(this, new OsSettingsChangedEventArgs(null));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "settings: could not refresh OS settings after a session change");
        }
    }

    private IAppFileSystemWatchGateway CreateWatchGateway(AppOperationContext operation, IReadOnlyList<string> groups) =>
        new AppFileSystemWatchGateway(_fileSystem, _topicBus!, _clock, operation, groups, "os-settings");

    internal static AppOperationContext CreateSystemOperation() => new()
    {
        AppId = ShellAppId,
        UserId = "system",
        UserName = "system",
        UserAuthority = AppAuthority.System,
        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
        IsSystemOperation = true
    };

    internal static AppOperationContext CreateUserOperation(AuthenticatedPrincipal principal) => new()
    {
        AppId = ShellAppId,
        UserId = principal.UserId.ToString(),
        UserName = principal.LoginName.Value,
        UserAuthority = principal.Authority,
        GrantedCapabilities = new HashSet<string>(
            [
                AppCapabilities.FileSystemUserHomeRead,
                AppCapabilities.FileSystemUserHomeWrite,
                AppCapabilities.FileSystemSystemRead,
                AppCapabilities.FileSystemSystemWrite
            ],
            StringComparer.Ordinal),
        IsSystemOperation = false
    };

    private void LogReadError(SettingsFileReadResult read)
    {
        if (read.Error is { } error)
        {
            _logger.LogWarning("settings: cannot read {Path}: {Code}, layer ignored", error.Path?.Value, error.Code);
        }
    }

    private static SettingFieldDeclaration RequireField(OsSettingsDocument document, string qualifiedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedKey);
        return document.Schema.Fields.TryGetValue(qualifiedKey, out SettingFieldDeclaration? field)
            ? field
            : throw new ArgumentException(
                $"Setting '{qualifiedKey}' is not declared in OS settings document '{document.DocumentId}'.",
                nameof(qualifiedKey));
    }

    private static Exception ToException(FileSystemError error) => error.Code switch
    {
        FileSystemErrorCode.PermissionDenied
            or FileSystemErrorCode.CapabilityDenied
            or FileSystemErrorCode.AuthorityDenied
            or FileSystemErrorCode.ProtectedEntry => new UnauthorizedAccessException(
                $"Access to settings file '{error.Path?.Value}' is denied ({error.Code})."),
        FileSystemErrorCode.Cancelled => new OperationCanceledException(),
        _ => new IOException($"Settings operation on '{error.Path?.Value}' failed: {error.Code}.")
    };
}
