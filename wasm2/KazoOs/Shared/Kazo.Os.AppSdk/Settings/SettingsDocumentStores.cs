using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Keyring;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.AppSdk.Settings;

/// <summary>
/// Where a <see cref="HackSettingsDocument"/> reads and writes its two layers: an app's own files
/// through its filesystem gateway (ADR 0044), or an OS document through
/// <see cref="IAppSystemSettingsGateway"/> (ADR 0056, section 8). The document keeps the typed API,
/// the lock rule and the change notifications; the store only moves text.
/// </summary>
internal interface ISettingsDocumentStore : IAsyncDisposable
{
    /// <summary>Gets the system layer path.</summary>
    string SystemPath { get; }

    /// <summary>Gets the user layer path (empty for a system-only document).</summary>
    string UserPath { get; }

    /// <summary>Gets the document id, the file name without <c>.config</c>.</summary>
    string DocumentId { get; }

    /// <summary>
    /// Gets the calling app's keyring, which holds the secrets of its
    /// <see cref="SettingSensitivity.SecretReference"/> keys (ADR 0100 section 2), or
    /// <see langword="null"/> for an OS document.
    /// </summary>
    IAppKeyringGateway? Keyring { get; }

    /// <summary>Reads and merges both layers.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    Task<LayeredConfigSnapshot> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies several edits to one layer in a single file write (one administrator approval for a
    /// system-layer batch): a non-null value sets the key, <see langword="null"/> removes it.
    /// </summary>
    /// <param name="edits">Declared fields and their valid serialized values (or <see langword="null"/>).</param>
    /// <param name="target">Layer to edit.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask WriteAsync(IReadOnlyList<SettingEdit> edits, SettingLayer target, CancellationToken cancellationToken);

    /// <summary>Starts calling <paramref name="onChanged"/> after an external change of either layer.</summary>
    /// <param name="onChanged">Reload callback.</param>
    /// <param name="cancellationToken">Cancels the subscription.</param>
    ValueTask StartWatchingAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken);
}

/// <summary>One edit of a settings layer: set the key to <paramref name="Value"/>, or remove it when <see langword="null"/>.</summary>
/// <param name="Field">Declared field.</param>
/// <param name="Value">Valid serialized value, or <see langword="null"/> to remove the key.</param>
internal sealed record SettingEdit(SettingFieldDeclaration Field, string? Value);

/// <summary>
/// The layers of an app's own document, <c>/etc/&lt;appId&gt;/</c> and <c>~/.config/&lt;appId&gt;/</c>,
/// read and written with the app's own <see cref="IAppFileSystemGateway"/> (ADR 0044).
/// </summary>
internal sealed class AppSettingsDocumentStore : ISettingsDocumentStore
{
    private readonly IAppFileSystemGateway _fileSystem;
    private readonly IAppFileSystemWatchGateway? _watch;
    private readonly IAppLoggingGateway? _logging;
    private readonly SettingsSchema _schema;
    private readonly VirtualPath _systemPath;
    private readonly VirtualPath _userPath;
    private readonly VirtualPath _systemPrivatePath;
    private readonly VirtualPath _userPrivatePath;
    private readonly bool _hasPrivateFields;
    private readonly HashSet<string> _loggedRevisions = new(StringComparer.Ordinal);
    private SettingsFilesWatcher? _watcher;

    /// <summary>Creates the store for one document of the calling app.</summary>
    public AppSettingsDocumentStore(IAppExecutionContext context, SettingsSchema schema, string documentId)
    {
        string appId = context.Manifest.Id;
        string home = context.HomeDirectory.TrimEnd('/');
        _fileSystem = context.FileSystem;
        _watch = TryGet(() => context.Watch);
        _logging = TryGet(() => context.Logging);
        Keyring = TryGet(() => context.Keyring);
        _schema = schema;
        DocumentId = documentId;
        _systemPath = VirtualPath.Parse($"/etc/{appId}/{documentId}.config");
        _userPath = VirtualPath.Parse($"{home}/.config/{appId}/{documentId}.config");
        _systemPrivatePath = VirtualPath.Parse(SettingsPrivateFiles.GetPrivatePath(_systemPath.Value));
        _userPrivatePath = VirtualPath.Parse(SettingsPrivateFiles.GetPrivatePath(_userPath.Value));
        // ADR 0100: a document without sensitive keys never touches (nor pays for) private files.
        _hasPrivateFields = SettingsPrivateFiles.HasPrivateFields(schema);
    }

    /// <inheritdoc />
    public string SystemPath => _systemPath.Value;

    /// <inheritdoc />
    public string UserPath => _userPath.Value;

    /// <inheritdoc />
    public string DocumentId { get; }

    /// <inheritdoc />
    public IAppKeyringGateway? Keyring { get; }

    /// <inheritdoc />
    public async Task<LayeredConfigSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        SettingsFileReadResult system = await SettingsTextFile.ReadAsync(_fileSystem, _systemPath, cancellationToken).ConfigureAwait(false);
        SettingsFileReadResult user = await SettingsTextFile.ReadAsync(_fileSystem, _userPath, cancellationToken).ConfigureAwait(false);
        if (system.Error is not null)
        {
            throw SettingsErrorTranslator.ToException(system.Error);
        }

        if (user.Error is not null)
        {
            throw SettingsErrorTranslator.ToException(user.Error);
        }

        SettingsFileReadResult systemPrivate = Absent;
        SettingsFileReadResult userPrivate = Absent;
        if (_hasPrivateFields)
        {
            // A private file the caller may not read is not an error: its keys come back masked.
            systemPrivate = await ReadPrivateAsync(_systemPrivatePath, cancellationToken).ConfigureAwait(false);
            userPrivate = await ReadPrivateAsync(_userPrivatePath, cancellationToken).ConfigureAwait(false);
        }

        LayeredConfigSnapshot snapshot = LayeredConfigResolver.Resolve(
            _schema,
            new LayeredConfigFiles(system.Content, user.Content, SystemPath, UserPath)
            {
                SystemPrivateContent = systemPrivate.Content,
                UserPrivateContent = userPrivate.Content,
                SystemPrivateDenied = systemPrivate.IsDenied,
                UserPrivateDenied = userPrivate.IsDenied
            });
        LogNewWarnings(snapshot, new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            [SystemPath] = system.Revision,
            [UserPath] = user.Revision,
            [_systemPrivatePath.Value] = systemPrivate.Revision,
            [_userPrivatePath.Value] = userPrivate.Revision
        });
        return snapshot;
    }

    private static readonly SettingsFileReadResult Absent = new(null, null, null);

    private async Task<SettingsFileReadResult> ReadPrivateAsync(VirtualPath path, CancellationToken cancellationToken)
    {
        SettingsFileReadResult read = await SettingsTextFile.ReadAsync(_fileSystem, path, cancellationToken).ConfigureAwait(false);
        return read.Error is null || read.IsDenied ? read : throw SettingsErrorTranslator.ToException(read.Error);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(IReadOnlyList<SettingEdit> edits, SettingLayer target, CancellationToken cancellationToken)
    {
        VirtualPath path = target switch
        {
            SettingLayer.User => _userPath,
            SettingLayer.System => _systemPath,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Only the User and System layers can be written.")
        };

        if (_hasPrivateFields)
        {
            VirtualPath privatePath = target == SettingLayer.User ? _userPrivatePath : _systemPrivatePath;
            await WriteSplitAsync(edits, path, privatePath, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Removing keys from a missing file is a no-op; setting one creates it.
        bool createIfMissing = edits.Any(static edit => edit.Value is not null);
        FileSystemError? error = await SettingsTextFile
            .EditAsync(
                _fileSystem,
                path,
                content =>
                {
                    string? edited = content;
                    foreach (SettingEdit edit in edits)
                    {
                        edited = edit.Value is null
                            ? ConfigDocumentEditor.RemoveValue(edited, edit.Field.Group, edit.Field.Key)
                            : ConfigDocumentEditor.SetValue(edited, edit.Field.Group, edit.Field.Key, edit.Value);
                    }

                    return edited ?? string.Empty;
                },
                createIfMissing,
                cancellationToken)
            .ConfigureAwait(false);
        if (error is not null && !(error.Code == FileSystemErrorCode.NotFound && !createIfMissing))
        {
            throw SettingsErrorTranslator.ToException(error);
        }
    }

    /// <inheritdoc />
    public async ValueTask StartWatchingAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken)
    {
        if (_watch is null)
        {
            return;
        }

        _watcher = new SettingsFilesWatcher(
            _watch,
            _hasPrivateFields ? [_systemPath, _userPath, _systemPrivatePath, _userPrivatePath] : [_systemPath, _userPath],
            onChanged);
        await _watcher.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_watcher is not null)
        {
            await _watcher.DisposeAsync().ConfigureAwait(false);
            _watcher = null;
        }
    }

    /// <summary>
    /// Writes one layer whose document has sensitive keys (ADR 0100 section 1): non-public keys go to
    /// <c>&lt;doc&gt;.private.config</c> (created <c>0600</c>), public keys to <c>&lt;doc&gt;.config</c>, and
    /// a key found in the wrong file is moved. The private file is written first, so a moved
    /// secret is never missing from both files at once.
    /// </summary>
    private async ValueTask WriteSplitAsync(
        IReadOnlyList<SettingEdit> edits, VirtualPath publicPath, VirtualPath privatePath, CancellationToken cancellationToken)
    {
        List<(SettingFieldDeclaration Field, string? Value)> changes = [.. edits.Select(static edit => (edit.Field, edit.Value))];
        SettingsFileReadResult publicFile = await SettingsTextFile.ReadAsync(_fileSystem, publicPath, cancellationToken).ConfigureAwait(false);
        SettingsFileReadResult privateFile = await SettingsTextFile.ReadAsync(_fileSystem, privatePath, cancellationToken).ConfigureAwait(false);
        if (publicFile.Error is not null)
        {
            throw SettingsErrorTranslator.ToException(publicFile.Error);
        }

        if (privateFile.Error is not null)
        {
            throw SettingsErrorTranslator.ToException(privateFile.Error);
        }

        (string? nextPublic, string? nextPrivate) = SettingsPrivateFiles.Apply(_schema, publicFile.Content, privateFile.Content, changes);
        if (nextPrivate is not null)
        {
            // Re-applied to fresh content on a revision conflict, against the public text read above.
            FileSystemError? error = await SettingsTextFile
                .EditAsync(
                    _fileSystem,
                    privatePath,
                    content => SettingsPrivateFiles.Apply(_schema, publicFile.Content, content, changes).Private ?? content ?? string.Empty,
                    createIfMissing: true,
                    SettingsPrivateFiles.PrivateFileMode,
                    cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                throw SettingsErrorTranslator.ToException(error);
            }
        }

        if (nextPublic is not null)
        {
            FileSystemError? error = await SettingsTextFile
                .EditAsync(
                    _fileSystem,
                    publicPath,
                    content => SettingsPrivateFiles.Apply(_schema, content, privateFile.Content, changes).Public ?? content ?? string.Empty,
                    createIfMissing: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                throw SettingsErrorTranslator.ToException(error);
            }
        }
    }

    private void LogNewWarnings(LayeredConfigSnapshot snapshot, IReadOnlyDictionary<string, long?> revisions)
    {
        if (_logging is null || snapshot.Warnings.Count == 0)
        {
            return;
        }

        // One warning per problem, per file and per revision: re-reading an unchanged bad file
        // (every watch event, every write to the other layer) must not flood the log.
        foreach (LayeredConfigWarning warning in snapshot.Warnings)
        {
            long? revision = revisions.GetValueOrDefault(warning.Path);
            if (_loggedRevisions.Add($"{warning.Path}@{revision}:{warning.LineNumber}:{warning.Message}"))
            {
                _logging.Log(DiagnosticSeverity.Warning, warning.ToLogLine());
            }
        }
    }

    private static T? TryGet<T>(Func<T> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// The layers of an OS document, <c>/etc/hackeros/</c> and <c>~/.config/hackeros/</c>, through
/// <see cref="IAppSystemSettingsGateway"/> (ADR 0056, section 8). The OS logs file problems itself.
/// </summary>
internal sealed class SystemSettingsDocumentStore(IAppSystemSettingsGateway gateway, AppSystemSettingsDocumentInfo info)
    : ISettingsDocumentStore
{
    private IAsyncDisposable? _watch;

    /// <inheritdoc />
    public string SystemPath => info.SystemPath;

    /// <inheritdoc />
    public string UserPath => info.UserPath;

    /// <inheritdoc />
    public string DocumentId => info.DocumentId;

    /// <inheritdoc />
    public IAppKeyringGateway? Keyring => null;

    /// <inheritdoc />
    public async Task<LayeredConfigSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        await gateway.ReadAsync(info.DocumentId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask WriteAsync(IReadOnlyList<SettingEdit> edits, SettingLayer target, CancellationToken cancellationToken)
    {
        if (target == SettingLayer.User && info.IsSystemOnly)
        {
            throw new InvalidOperationException($"OS settings document '{info.DocumentId}' has no user layer.");
        }

        KeyValuePair<string, string>[] values =
        [
            .. edits
                .Where(static edit => edit.Value is not null)
                .Select(static edit => new KeyValuePair<string, string>(edit.Field.QualifiedKey, edit.Value!))
        ];
        if (values.Length > 0)
        {
            await gateway.SetAsync(info.DocumentId, values, target, cancellationToken).ConfigureAwait(false);
        }

        foreach (SettingEdit removal in edits.Where(static edit => edit.Value is null))
        {
            await gateway.ResetAsync(info.DocumentId, removal.Field.QualifiedKey, target, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask StartWatchingAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken) =>
        _watch = await gateway.WatchAsync(info.DocumentId, onChanged, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_watch is not null)
        {
            await _watch.DisposeAsync().ConfigureAwait(false);
            _watch = null;
        }
    }
}
