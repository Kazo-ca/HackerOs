using HackerOs.App.Abstractions;

namespace HackerOs.AppSdk.Administration;

/// <summary>
/// One local account as <see cref="HackUserAccounts.ListAsync(CancellationToken)"/> shows it, like a
/// line of <c>/etc/passwd</c> plus the status column of <c>passwd -S</c>; never the password hash.
/// </summary>
/// <param name="LoginName">Login name, e.g. <c>alice</c>.</param>
/// <param name="DisplayName">Name shown in the interface.</param>
/// <param name="Authority">Authority the account's sessions get (<see cref="AppAuthority.Administrator"/> or user).</param>
/// <param name="IsEnabled">Whether the account can start a session.</param>
/// <param name="HasPassword">Whether the account has a password (<c>P</c>) or none (<c>NP</c>).</param>
/// <param name="UpdatedAtUtc">Last change of the account, password included.</param>
/// <param name="IsCurrentUser">Whether this is the account of the calling session.</param>
public sealed record HackUserAccount(
    string LoginName,
    string DisplayName,
    AppAuthority Authority,
    bool IsEnabled,
    bool HasPassword,
    DateTimeOffset UpdatedAtUtc,
    bool IsCurrentUser)
{
    /// <summary>Gets whether the account's sessions are administrators.</summary>
    public bool IsAdministrator => Authority == AppAuthority.Administrator;

    /// <summary>Gets how many passkeys can sign the account in (ADR 0168).</summary>
    public int PasskeyCount { get; init; }
}

/// <summary>
/// A new local account for <see cref="HackUserAccounts.CreateAsync(HackNewUserAccount, CancellationToken)"/>
/// (ADR 0169), what <c>useradd</c> and a first <c>passwd</c> take.
/// </summary>
/// <param name="LoginName">Login name: lowercase letters, digits, <c>-</c> and <c>_</c>, 32 characters at most.</param>
/// <param name="DisplayName">Name shown in the interface (128 characters at most).</param>
/// <param name="Authority"><see cref="AppAuthority.User"/> or <see cref="AppAuthority.Administrator"/>.</param>
/// <param name="Password">Initial password (rules of <see cref="HackUserAccounts.ValidateNewPassword"/>).</param>
public sealed record HackNewUserAccount(string LoginName, string DisplayName, AppAuthority Authority, string Password)
{
    /// <summary>Hides the password from logs and debugger displays.</summary>
    /// <returns>The account without its password.</returns>
    public override string ToString() => $"HackNewUserAccount {{ LoginName = {LoginName}, DisplayName = {DisplayName}, Authority = {Authority} }}";
}

/// <summary>One passkey of the session user (ADR 0168), as <see cref="HackUserAccounts.ListPasskeysAsync(CancellationToken)"/> shows it.</summary>
/// <param name="Id">Credential id, in base64url.</param>
/// <param name="Name">Name given when it was created.</param>
/// <param name="CreatedAtUtc">When it was created.</param>
/// <param name="LastUsedAtUtc">Its last successful use, or <see langword="null"/>.</param>
/// <param name="UnlocksKeyring">Whether signing in with it also opens the keyring (PRF).</param>
public sealed record HackPasskey(string Id, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastUsedAtUtc, bool UnlocksKeyring);

/// <summary>
/// The local accounts gateway behind <see cref="HackUserAccounts"/> (ADR 0120). Changing one's own
/// password needs no capability (the current password is checked, as <c>passwd</c> does through PAM);
/// resetting or removing another account's password needs <c>users.manage</c> and an administrator.
/// </summary>
public interface IAppUserAccountsGateway
{
    /// <summary>Lists every local account, without any password hash.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<HackUserAccount>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the session user's password, after checking the current one.</summary>
    /// <param name="currentPassword">The current password; empty when the account has none.</param>
    /// <param name="newPassword">The new password (8 to 256 characters, different from the current one).</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <exception cref="UnauthorizedAccessException">The current password is wrong.</exception>
    /// <exception cref="ArgumentException">The new password breaks the rules.</exception>
    ValueTask ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>Sets another account's password, or removes it with <see langword="null"/> (<c>passwd -d</c>).</summary>
    /// <param name="loginName">The account to change.</param>
    /// <param name="newPassword">The new password, or <see langword="null"/> to leave the account without one.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <exception cref="UnauthorizedAccessException">No <c>users.manage</c>, or no administrator confirmed.</exception>
    /// <exception cref="ArgumentException">The new password breaks the rules, or the account does not exist.</exception>
    /// <exception cref="InvalidOperationException">Removing the last enabled administrator's password.</exception>
    ValueTask ResetPasswordAsync(string loginName, string? newPassword, CancellationToken cancellationToken = default);

    /// <summary>Creates an account (ADR 0169); needs <c>users.manage</c> and an administrator.</summary>
    /// <param name="account">The new account.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>The created account.</returns>
    /// <exception cref="UnauthorizedAccessException">No <c>users.manage</c>, or no administrator confirmed.</exception>
    /// <exception cref="ArgumentException">A field breaks the rules, or the login name is taken or reserved.</exception>
    ValueTask<HackUserAccount> CreateAsync(HackNewUserAccount account, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This HackerOS host cannot add accounts.");

    /// <summary>Deletes an account (ADR 0169); needs <c>users.manage</c> and an administrator.</summary>
    /// <param name="loginName">The account to delete.</param>
    /// <param name="removeHome">Whether to delete its home directory too.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <exception cref="UnauthorizedAccessException">No <c>users.manage</c>, or no administrator confirmed.</exception>
    /// <exception cref="ArgumentException">The account does not exist.</exception>
    /// <exception cref="InvalidOperationException">The caller's own account, or the last enabled administrator.</exception>
    ValueTask DeleteAsync(string loginName, bool removeHome, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This HackerOS host cannot delete accounts.");

    /// <summary>Lists the session user's passkeys (ADR 0168).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The passkeys, oldest first.</returns>
    ValueTask<IReadOnlyList<HackPasskey>> ListPasskeysAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<HackPasskey>>([]);

    /// <summary>Creates a passkey for the session user; the browser asks for their presence.</summary>
    /// <param name="name">Name of the passkey.</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The new passkey.</returns>
    /// <exception cref="NotSupportedException">No WebAuthn in this page or process.</exception>
    /// <exception cref="OperationCanceledException">The user closed the browser's prompt.</exception>
    ValueTask<HackPasskey> AddPasskeyAsync(string name, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This HackerOS host has no passkeys.");

    /// <summary>Removes one of the session user's passkeys.</summary>
    /// <param name="passkeyId">Its base64url id.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> when a passkey was removed.</returns>
    ValueTask<bool> RemovePasskeyAsync(string passkeyId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This HackerOS host has no passkeys.");
}
