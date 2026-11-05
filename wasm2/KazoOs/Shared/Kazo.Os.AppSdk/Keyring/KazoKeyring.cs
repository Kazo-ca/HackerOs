namespace Kazo.Os.AppSdk.Keyring;

/// <summary>
/// Secrets of the calling app (site passwords, API tokens, keys) in the session user's encrypted
/// keyring (ADR 0100 section 2), like libsecret on GNOME.
/// </summary>
/// <remarks>
/// <para>
/// The keyring is <c>~/.local/share/keyrings/login.keyring</c> (mode <c>0600</c>), encrypted with
/// AES-GCM under a key derived from the login password (PBKDF2). It is unlocked at login, kept in
/// memory for the session and locked again at logout. No capability is needed for the app's own
/// entries; another app's entries need the dangerous capability <c>keyring.read-all</c>.
/// </para>
/// <code>
/// await KazoKeyring.SetAsync(AppContext, "api-token", token, "Jeton de l'API");
/// string? token = await KazoKeyring.GetAsync(AppContext, "api-token");
/// </code>
/// <para>
/// A settings key declared <c>SecretReference</c> stores only the entry id; use
/// <see cref="Settings.KazoSettingsDocument.GetSecretAsync"/> rather than this class for those.
/// </para>
/// </remarks>
public static class KazoKeyring
{
    /// <summary>Gets whether the session user's keyring is unlocked.</summary>
    /// <param name="context">The calling app's execution context.</param>
    public static bool IsUnlocked(IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Keyring.IsUnlocked;
    }

    /// <summary>Reads one secret of the calling app.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="id">Entry identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The secret, or <see langword="null"/> when there is no such entry.</returns>
    /// <exception cref="KazoKeyringLockedException">The keyring is locked.</exception>
    public static ValueTask<string?> GetAsync(IAppExecutionContext context, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateId(id);
        return context.Keyring.GetAsync(null, id, cancellationToken);
    }

    /// <summary>Reads one secret of another app (password manager; needs <c>keyring.read-all</c>).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="appId">App that owns the entry.</param>
    /// <param name="id">Entry identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="KazoKeyringLockedException">The keyring is locked.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>keyring.read-all</c>.</exception>
    public static ValueTask<string?> GetAsync(
        IAppExecutionContext context, string appId, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ValidateId(id);
        return context.Keyring.GetAsync(appId, id, cancellationToken);
    }

    /// <summary>Creates or replaces one secret of the calling app.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="id">Entry identifier.</param>
    /// <param name="secret">Secret to store.</param>
    /// <param name="label">Optional label shown by a password manager.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="KazoKeyringLockedException">The keyring is locked.</exception>
    public static ValueTask SetAsync(
        IAppExecutionContext context, string id, string secret, string? label = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateId(id);
        ArgumentNullException.ThrowIfNull(secret);
        return context.Keyring.SetAsync(id, secret, label, cancellationToken);
    }

    /// <summary>Deletes one secret of the calling app.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="id">Entry identifier.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when an entry was deleted.</returns>
    public static ValueTask<bool> DeleteAsync(IAppExecutionContext context, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateId(id);
        return context.Keyring.DeleteAsync(id, cancellationToken);
    }

    /// <summary>Lists the calling app's entries, without their secrets.</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static ValueTask<IReadOnlyList<KazoKeyringEntry>> ListAsync(IAppExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Keyring.ListAsync(allApps: false, cancellationToken);
    }

    /// <summary>Lists every app's entries, without their secrets (needs <c>keyring.read-all</c>).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>keyring.read-all</c>.</exception>
    public static ValueTask<IReadOnlyList<KazoKeyringEntry>> ListAllAsync(IAppExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Keyring.ListAsync(allApps: true, cancellationToken);
    }

    /// <summary>Reads one secret of the ambient app (<see cref="KazoExecutionContext.Current"/>).</summary>
    /// <param name="id">Entry identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static ValueTask<string?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        GetAsync(KazoExecutionContext.Current, id, cancellationToken);

    /// <summary>Creates or replaces one secret of the ambient app.</summary>
    /// <param name="id">Entry identifier.</param>
    /// <param name="secret">Secret to store.</param>
    /// <param name="label">Optional label.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static ValueTask SetAsync(string id, string secret, string? label = null, CancellationToken cancellationToken = default) =>
        SetAsync(KazoExecutionContext.Current, id, secret, label, cancellationToken);

    /// <summary>Deletes one secret of the ambient app.</summary>
    /// <param name="id">Entry identifier.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        DeleteAsync(KazoExecutionContext.Current, id, cancellationToken);

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 256 || id.Any(char.IsControl))
        {
            throw new ArgumentException("A keyring entry id is at most 256 characters, without control characters.", nameof(id));
        }
    }
}
