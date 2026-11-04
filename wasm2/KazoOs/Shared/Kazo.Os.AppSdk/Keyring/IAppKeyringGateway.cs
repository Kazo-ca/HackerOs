namespace Kazo.Os.AppSdk.Keyring;

/// <summary>One entry of the session user's keyring, without its secret.</summary>
/// <param name="AppId">App that owns the entry; an app sees only its own unless it holds <c>keyring.read-all</c>.</param>
/// <param name="Id">Entry identifier, unique per app.</param>
/// <param name="Label">Human-readable label shown by a password manager, or <see langword="null"/>.</param>
/// <param name="UpdatedAtUtc">When the secret was last written.</param>
public sealed record HackKeyringEntry(string AppId, string Id, string? Label, DateTimeOffset UpdatedAtUtc);

/// <summary>
/// The session user's keyring as one app sees it (ADR 0100 section 2), behind <see cref="HackKeyring"/>:
/// <c>~/.local/share/keyrings/login.keyring</c>, encrypted with a key derived from the login password
/// and unlocked for the length of the session, like gnome-keyring.
/// </summary>
/// <remarks>
/// An app reads and writes only its own entries. Reading other apps' entries needs the dangerous
/// capability <c>keyring.read-all</c> (a password manager). System services started at boot have
/// no user keyring.
/// </remarks>
public interface IAppKeyringGateway
{
    /// <summary>Gets whether the keyring is unlocked (a user is logged in with a password).</summary>
    bool IsUnlocked { get; }

    /// <summary>Reads one secret of an app.</summary>
    /// <param name="appId">Owning app; <see langword="null"/> for the calling app.</param>
    /// <param name="id">Entry identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The secret, or <see langword="null"/> when there is no such entry.</returns>
    /// <exception cref="HackKeyringLockedException">The keyring is locked.</exception>
    /// <exception cref="UnauthorizedAccessException">Another app's entry without <c>keyring.read-all</c>.</exception>
    ValueTask<string?> GetAsync(string? appId, string id, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces one secret of the calling app and saves the keyring.</summary>
    /// <param name="id">Entry identifier.</param>
    /// <param name="secret">Secret to store.</param>
    /// <param name="label">Optional label.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="HackKeyringLockedException">The keyring is locked.</exception>
    ValueTask SetAsync(string id, string secret, string? label, CancellationToken cancellationToken = default);

    /// <summary>Deletes one secret of the calling app.</summary>
    /// <param name="id">Entry identifier.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when an entry was deleted.</returns>
    /// <exception cref="HackKeyringLockedException">The keyring is locked.</exception>
    ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists entries without their secrets.</summary>
    /// <param name="allApps"><see langword="true"/> for every app's entries (needs <c>keyring.read-all</c>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="HackKeyringLockedException">The keyring is locked.</exception>
    /// <exception cref="UnauthorizedAccessException"><paramref name="allApps"/> without <c>keyring.read-all</c>.</exception>
    ValueTask<IReadOnlyList<HackKeyringEntry>> ListAsync(bool allApps, CancellationToken cancellationToken = default);
}

/// <summary>The keyring is locked: no user session, or a session opened without a password.</summary>
public sealed class HackKeyringLockedException : InvalidOperationException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="message">Human-readable message.</param>
    public HackKeyringLockedException(string message)
        : base(message)
    {
    }
}

/// <summary>Keyring gateway of a context that has none: always locked.</summary>
internal sealed class UnsupportedAppKeyringGateway : IAppKeyringGateway
{
    public static UnsupportedAppKeyringGateway Instance { get; } = new();

    private const string Message = "This execution context has no user keyring.";

    public bool IsUnlocked => false;

    public ValueTask<string?> GetAsync(string? appId, string id, CancellationToken cancellationToken = default) =>
        throw new HackKeyringLockedException(Message);

    public ValueTask SetAsync(string id, string secret, string? label, CancellationToken cancellationToken = default) =>
        throw new HackKeyringLockedException(Message);

    public ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        throw new HackKeyringLockedException(Message);

    public ValueTask<IReadOnlyList<HackKeyringEntry>> ListAsync(bool allApps, CancellationToken cancellationToken = default) =>
        throw new HackKeyringLockedException(Message);
}
