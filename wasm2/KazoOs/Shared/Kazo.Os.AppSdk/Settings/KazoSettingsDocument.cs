using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.AppSdk.Settings;

/// <summary>
/// One open, layered settings document: typed reads over the merged view of
/// <c>/etc/&lt;appId&gt;/&lt;document&gt;.config</c> and <c>~/.config/&lt;appId&gt;/&lt;document&gt;.config</c>
/// (an app's own document, ADR 0044 section 7) or of <c>/etc/kazoos/&lt;document&gt;.config</c> and
/// <c>~/.config/kazoos/&lt;document&gt;.config</c> (an OS document, ADR 0056 section 8), typed writes
/// to either layer, and change notifications.
/// </summary>
/// <remarks>
/// <para>
/// For an app's own document, every read and write goes through the app's own
/// <see cref="IAppFileSystemGateway"/>: the file's permissions decide access. Writing the user layer needs <c>filesystem.private.write</c>; writing
/// the system layer needs the right to write <c>/etc/&lt;appId&gt;/</c> (administrator plus <c>sudo</c>
/// or an approval ticket of ADR 0042), otherwise <see cref="UnauthorizedAccessException"/> is thrown.
/// </para>
/// <para>
/// For an OS document (<see cref="KazoSettings.OpenSystemAsync(string, CancellationToken)"/>), the OS
/// reads both layers and writes the user layer as the session user; the system layer is written
/// with the app's own filesystem rights, so it needs <c>sudo</c> or the approval of ADR 0042.
/// </para>
/// <para>
/// Reads never fail on a bad file: malformed lines, undeclared keys and ill-typed values are
/// ignored, and each problem is written once per file revision to the app's log through
/// <see cref="IAppExecutionContext.Logging"/> (to the OS log for an OS document).
/// </para>
/// </remarks>
public sealed class KazoSettingsDocument : IAsyncDisposable
{
    private readonly ISettingsDocumentStore _store;
    private readonly SettingsSchema _schema;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LayeredConfigSnapshot _snapshot;
    private bool _disposed;

    private KazoSettingsDocument(ISettingsDocumentStore store, SettingsSchema schema)
    {
        _store = store;
        _schema = schema;
        _snapshot = LayeredConfigResolver.Resolve(schema, null, null, store.SystemPath, store.UserPath);
    }

    /// <summary>Gets the system layer path, <c>/etc/&lt;appId&gt;/&lt;document&gt;.config</c> (<c>/etc/kazoos/…</c> for an OS document).</summary>
    public string SystemPath => _store.SystemPath;

    /// <summary>
    /// Gets the user layer path, <c>/home/&lt;user&gt;/.config/&lt;appId&gt;/&lt;document&gt;.config</c>
    /// (<c>~/.config/kazoos/…</c> for an OS document; empty for a system-only OS document).
    /// </summary>
    public string UserPath => _store.UserPath;

    /// <summary>Gets the declared schema of this document.</summary>
    public SettingsSchema Schema => _schema;

    /// <summary>Gets every problem found in the two files at the last read.</summary>
    public IReadOnlyList<LayeredConfigWarning> Warnings => _snapshot.Warnings;

    /// <summary>
    /// Raised after a write by this document or an external change of either file (seen through a
    /// filesystem watch) modified at least one effective value, source layer or lock.
    /// </summary>
    public event EventHandler<KazoSettingsChangedEventArgs>? Changed;

    /// <summary>Opens one of the calling app's documents, reads both layers and starts watching them.</summary>
    internal static ValueTask<KazoSettingsDocument> OpenAsync(
        IAppExecutionContext context,
        SettingsSchema schema,
        string documentId,
        CancellationToken cancellationToken) =>
        OpenAsync(new AppSettingsDocumentStore(context, schema, documentId), schema, cancellationToken);

    /// <summary>Opens one OS document through the system settings gateway (ADR 0056, section 8).</summary>
    internal static ValueTask<KazoSettingsDocument> OpenSystemAsync(
        IAppSystemSettingsGateway gateway,
        string documentId,
        CancellationToken cancellationToken)
    {
        AppSystemSettingsDocumentInfo info = gateway.Describe(documentId);
        return OpenAsync(new SystemSettingsDocumentStore(gateway, info), info.Schema, cancellationToken);
    }

    private static async ValueTask<KazoSettingsDocument> OpenAsync(
        ISettingsDocumentStore store, SettingsSchema schema, CancellationToken cancellationToken)
    {
        KazoSettingsDocument document = new(store, schema);
        try
        {
            await document.ReloadAsync(raiseChanged: false, cancellationToken).ConfigureAwait(false);
            await store
                .StartWatchingAsync(() => new ValueTask(document.ReloadAsync(raiseChanged: true, CancellationToken.None)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return document;
    }

    /// <summary>Gets the effective value of a declared key.</summary>
    /// <typeparam name="T">
    /// <see cref="string"/> (any field), <see cref="long"/> or <see cref="int"/> (integer field),
    /// <see cref="bool"/> (boolean field), or a C# enum whose member names match the field's allowed
    /// values ignoring case, <c>-</c> and <c>_</c> (enum field).
    /// </typeparam>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <exception cref="ArgumentException">The key is not declared in the schema.</exception>
    /// <exception cref="InvalidCastException"><typeparamref name="T"/> does not fit the field's type.</exception>
    /// <exception cref="KazoSettingMaskedException">The value lives in a private file the caller may not read (ADR 0100).</exception>
    public T Get<T>(string qualifiedKey)
    {
        SettingFieldDeclaration field = RequireField(qualifiedKey);
        LayeredConfigSnapshot snapshot = _snapshot;
        if (snapshot.MaskedKeys.Contains(field.QualifiedKey))
        {
            throw new KazoSettingMaskedException(field.QualifiedKey, PrivatePathOf(snapshot.Sources[field.QualifiedKey]));
        }

        return SettingValueConverter.FromSetting<T>(field, snapshot.Values[field.QualifiedKey]);
    }

    /// <summary>
    /// Gets a declared key as the caller may see it, never failing on a masked value
    /// (ADR 0100 section 1): a key held in a private file the caller may not read comes back with
    /// <see cref="KazoSettingValue.IsMasked"/> set and no value, which a UI shows as
    /// <see cref="KazoSettingValue.MaskText"/>.
    /// </summary>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <exception cref="ArgumentException">The key is not declared in the schema.</exception>
    public KazoSettingValue GetValue(string qualifiedKey)
    {
        SettingFieldDeclaration field = RequireField(qualifiedKey);
        LayeredConfigSnapshot snapshot = _snapshot;
        string key = field.QualifiedKey;
        bool masked = snapshot.MaskedKeys.Contains(key);
        return new KazoSettingValue(
            key,
            masked ? null : snapshot.Values[key],
            field.Sensitivity,
            snapshot.Sources[key],
            snapshot.LockedKeys.Contains(key),
            masked);
    }

    /// <summary>Gets whether a declared key's value is hidden from the caller (ADR 0100 section 1).</summary>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <exception cref="ArgumentException">The key is not declared in the schema.</exception>
    public bool IsMasked(string qualifiedKey) => _snapshot.MaskedKeys.Contains(RequireField(qualifiedKey).QualifiedKey);

    /// <summary>
    /// Reads the secret of a <see cref="SettingSensitivity.SecretReference"/> key (ADR 0100 section 2):
    /// the private file holds a keyring entry id, the secret itself is in the user's keyring.
    /// </summary>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, declared <c>SecretReference</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The secret, or <see langword="null"/> when none is set.</returns>
    /// <exception cref="ArgumentException">The key is undeclared or not a <c>SecretReference</c>.</exception>
    /// <exception cref="KazoSettingMaskedException">The reference itself is hidden from the caller.</exception>
    /// <exception cref="Kazo.Os.AppSdk.Keyring.KazoKeyringLockedException">The keyring is locked.</exception>
    /// <exception cref="NotSupportedException">An OS document: it has no keyring.</exception>
    public async ValueTask<string?> GetSecretAsync(string qualifiedKey, CancellationToken cancellationToken = default)
    {
        SettingFieldDeclaration field = RequireSecretField(qualifiedKey);
        string reference = Get<string>(field.QualifiedKey);
        return reference.Length == 0
            ? null
            : await RequireKeyring().GetAsync(null, reference, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the secret of a <see cref="SettingSensitivity.SecretReference"/> key in the user's keyring
    /// and its entry id in the user's private settings file; <see langword="null"/> deletes both.
    /// Secrets are per user, so only the user layer holds a reference.
    /// </summary>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, declared <c>SecretReference</c>.</param>
    /// <param name="secret">New secret, or <see langword="null"/> to forget it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">The key is undeclared or not a <c>SecretReference</c>.</exception>
    /// <exception cref="KazoSettingLockedException">The key is locked by the administrator.</exception>
    /// <exception cref="Kazo.Os.AppSdk.Keyring.KazoKeyringLockedException">The keyring is locked.</exception>
    public async ValueTask SetSecretAsync(string qualifiedKey, string? secret, CancellationToken cancellationToken = default)
    {
        SettingFieldDeclaration field = RequireSecretField(qualifiedKey);
        Kazo.Os.AppSdk.Keyring.IAppKeyringGateway keyring = RequireKeyring();
        string reference = $"settings:{_store.DocumentId}:{field.QualifiedKey}";
        if (secret is null)
        {
            await keyring.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            await ResetAsync(field.QualifiedKey, SettingLayer.User, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_snapshot.LockedKeys.Contains(field.QualifiedKey))
        {
            throw new KazoSettingLockedException(field.QualifiedKey, SystemPath);
        }

        await keyring.SetAsync(reference, secret, field.QualifiedKey, cancellationToken).ConfigureAwait(false);
        await SetAsync(field.QualifiedKey, reference, SettingLayer.User, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the layer that supplied the effective value of a declared key.</summary>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <exception cref="ArgumentException">The key is not declared in the schema.</exception>
    public SettingLayer GetSource(string qualifiedKey) => _snapshot.Sources[RequireField(qualifiedKey).QualifiedKey];

    /// <summary>Gets whether the system layer locks a declared key.</summary>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <exception cref="ArgumentException">The key is not declared in the schema.</exception>
    public bool IsLocked(string qualifiedKey) => _snapshot.LockedKeys.Contains(RequireField(qualifiedKey).QualifiedKey);

    /// <summary>Writes a typed value to one layer, preserving every other line of the file.</summary>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <param name="value">New value; see <see cref="Get{T}"/> for the accepted types.</param>
    /// <param name="target"><see cref="SettingLayer.User"/> (default) or <see cref="SettingLayer.System"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">The key is undeclared or the value is invalid for its type.</exception>
    /// <exception cref="KazoSettingLockedException">The user layer is targeted and the key is locked.</exception>
    /// <exception cref="UnauthorizedAccessException">The file's permissions deny the write.</exception>
    /// <exception cref="IOException">The write failed for another filesystem reason.</exception>
    public async ValueTask SetAsync<T>(
        string qualifiedKey,
        T value,
        SettingLayer target = SettingLayer.User,
        CancellationToken cancellationToken = default)
    {
        SettingFieldDeclaration field = RequireField(qualifiedKey);
        await EditLayerAsync([new SettingEdit(field, Normalize(field, value))], target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes several typed values to one layer in a single file edit, so a system-layer change asks
    /// for one administrator approval rather than one per key. Nothing is written when any key is
    /// undeclared, any value invalid or, for the user layer, any key locked.
    /// </summary>
    /// <param name="values">Declared keys and their new values; see <see cref="Get{T}"/> for the accepted types.</param>
    /// <param name="target"><see cref="SettingLayer.User"/> (default) or <see cref="SettingLayer.System"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">A key is undeclared or a value is invalid for its type.</exception>
    /// <exception cref="KazoSettingLockedException">The user layer is targeted and a key is locked.</exception>
    /// <exception cref="UnauthorizedAccessException">The file's permissions deny the write.</exception>
    public async ValueTask SetManyAsync(
        IReadOnlyDictionary<string, object> values,
        SettingLayer target = SettingLayer.User,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        List<SettingEdit> edits = [];
        foreach ((string qualifiedKey, object value) in values)
        {
            SettingFieldDeclaration field = RequireField(qualifiedKey);
            edits.Add(new SettingEdit(field, Normalize(field, value)));
        }

        if (edits.Count > 0)
        {
            await EditLayerAsync(edits, target, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Removes a key from one layer, so the next layer (or the default) applies again.</summary>
    /// <param name="qualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
    /// <param name="target"><see cref="SettingLayer.User"/> (default) or <see cref="SettingLayer.System"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">The key is not declared in the schema.</exception>
    /// <exception cref="UnauthorizedAccessException">The file's permissions deny the write.</exception>
    public async ValueTask ResetAsync(
        string qualifiedKey,
        SettingLayer target = SettingLayer.User,
        CancellationToken cancellationToken = default)
    {
        SettingFieldDeclaration field = RequireField(qualifiedKey);
        await EditLayerAsync([new SettingEdit(field, null)], target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-reads both files now, raising <see cref="Changed"/> when something changed.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) =>
        new(ReloadAsync(raiseChanged: true, cancellationToken));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _store.DisposeAsync().ConfigureAwait(false);
    }

    private static string Normalize<T>(SettingFieldDeclaration field, T value)
    {
        string serialized = SettingValueConverter.ToSetting(field, value);
        return SettingValueCodec.TryNormalize(field, serialized, out string normalized)
            ? normalized
            : throw new ArgumentException(
                $"Value '{serialized}' is invalid for setting '{field.QualifiedKey}' (expected {SettingValueCodec.DescribeExpected(field)}).",
                nameof(value));
    }

    private async ValueTask EditLayerAsync(IReadOnlyList<SettingEdit> edits, SettingLayer target, CancellationToken cancellationToken)
    {
        if (target is not (SettingLayer.User or SettingLayer.System))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "Only the User and System layers can be written.");
        }

        // ADR 0100: a Restricted key exists only in the system private file (root only).
        if (target == SettingLayer.User
            && edits.FirstOrDefault(static edit => edit.Value is not null && edit.Field.Sensitivity == SettingSensitivity.Restricted) is { } restricted)
        {
            throw new InvalidOperationException(
                $"Setting '{restricted.Field.QualifiedKey}' is Restricted: only the system layer can hold it.");
        }

        await ReloadAsync(raiseChanged: true, cancellationToken).ConfigureAwait(false);
        // Only a write that sets a value is pointless on a locked key; removing a stale user value is not.
        if (target == SettingLayer.User
            && edits.FirstOrDefault(edit => edit.Value is not null && _snapshot.LockedKeys.Contains(edit.Field.QualifiedKey)) is { } locked)
        {
            throw new KazoSettingLockedException(locked.Field.QualifiedKey, SystemPath);
        }

        await _store.WriteAsync(edits, target, cancellationToken).ConfigureAwait(false);
        await ReloadAsync(raiseChanged: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReloadAsync(bool raiseChanged, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LayeredConfigSnapshot previous;
        LayeredConfigSnapshot next;
        try
        {
            next = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            previous = _snapshot;
            _snapshot = next;
        }
        finally
        {
            _gate.Release();
        }

        if (raiseChanged)
        {
            IReadOnlyList<string> changed = Diff(previous, next);
            if (changed.Count > 0)
            {
                Changed?.Invoke(this, new KazoSettingsChangedEventArgs(changed));
            }
        }
    }

    private static IReadOnlyList<string> Diff(LayeredConfigSnapshot previous, LayeredConfigSnapshot next)
    {
        List<string> changed = [];
        foreach ((string key, string value) in next.Values)
        {
            if (!previous.Values.TryGetValue(key, out string? before)
                || before != value
                || previous.Sources[key] != next.Sources[key]
                || previous.LockedKeys.Contains(key) != next.LockedKeys.Contains(key)
                || previous.MaskedKeys.Contains(key) != next.MaskedKeys.Contains(key))
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    private string PrivatePathOf(SettingLayer layer) =>
        SettingsPrivateFiles.GetPrivatePath(layer == SettingLayer.User ? UserPath : SystemPath);

    private SettingFieldDeclaration RequireSecretField(string qualifiedKey)
    {
        SettingFieldDeclaration field = RequireField(qualifiedKey);
        return field.Sensitivity == SettingSensitivity.SecretReference
            ? field
            : throw new ArgumentException($"Setting '{field.QualifiedKey}' is not declared SecretReference.", nameof(qualifiedKey));
    }

    private Kazo.Os.AppSdk.Keyring.IAppKeyringGateway RequireKeyring() =>
        _store.Keyring ?? throw new NotSupportedException("OS settings documents have no keyring.");

    private SettingFieldDeclaration RequireField(string qualifiedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedKey);
        return _schema.Fields.TryGetValue(qualifiedKey, out SettingFieldDeclaration? field)
            ? field
            : throw new ArgumentException($"Setting '{qualifiedKey}' is not declared in this settings schema.", nameof(qualifiedKey));
    }
}
