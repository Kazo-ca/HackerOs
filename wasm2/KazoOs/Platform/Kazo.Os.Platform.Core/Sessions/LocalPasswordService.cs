using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Platform.Core.Keyring;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Sessions;

/// <summary>
/// Changes and resets local passwords (ADR 0120), the work of <c>passwd</c> and <c>/etc/shadow</c>:
/// the new credential is hashed like at account creation (<see cref="LocalPasswordHasher"/>), the
/// user's login keyring follows (re-sealed under the new password, or set aside when the old one is
/// lost), and every attempt is written to <c>auth.log</c>. It does not decide who may reset whom: the
/// app gateway checks <c>users.manage</c> and asks for an administrator first.
/// </summary>
public sealed class LocalPasswordService
{
    private readonly ILocalUserRepository _users;
    private readonly IAuditLog _audit;
    private readonly UserKeyringService? _keyring;
    private readonly KeyDerivationAsyncDelegate? _asyncHasher;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _iterations;

    /// <summary>Initializes the service.</summary>
    /// <param name="users">The account store.</param>
    /// <param name="audit">The authentication log (<c>auth.log</c>).</param>
    /// <param name="keyring">The login keyring of ADR 0100, if the host has one.</param>
    /// <param name="asyncHasher">Optional Web Crypto key derivation, as used at login.</param>
    /// <param name="clock">Clock of the audit entries; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="iterations">PBKDF2 work factor of new credentials (lower only in tests).</param>
    public LocalPasswordService(
        ILocalUserRepository users,
        IAuditLog audit,
        UserKeyringService? keyring = null,
        KeyDerivationAsyncDelegate? asyncHasher = null,
        Func<DateTimeOffset>? clock = null,
        int iterations = 210_000)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        _keyring = keyring;
        _asyncHasher = asyncHasher;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _iterations = iterations;
    }

    /// <summary>Lists every account, ordered by login name.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<IReadOnlyList<LocalUser>> ListAsync(CancellationToken cancellationToken = default) =>
        await _users.GetAllAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Changes the session user's own password after checking the current one, then re-seals their
    /// keyring under the new password.
    /// </summary>
    /// <param name="principal">The session user.</param>
    /// <param name="currentPassword">The current password; empty when the account has none.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <exception cref="UnauthorizedAccessException">The current password is wrong.</exception>
    /// <exception cref="ArgumentException">The new password breaks the rules or is unchanged.</exception>
    public async ValueTask ChangeOwnPasswordAsync(
        AuthenticatedPrincipal principal,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(currentPassword);
        ThrowIfInvalid(newPassword);
        LocalUser user = await _users.FindByIdAsync(principal.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"passwd: user '{principal.LoginName.Value}' does not exist.", nameof(principal));
        string login = user.LoginName.Value;

        bool currentMatches = user.Credential is { } credential
            ? await LocalPasswordHasher.VerifyAsync(currentPassword, credential, _asyncHasher, cancellationToken).ConfigureAwait(false)
            : currentPassword.Length == 0;
        if (!currentMatches)
        {
            Record(login, "passwd.change", login, AuditOutcome.Denied);
            throw new UnauthorizedAccessException("passwd: Authentication token manipulation error");
        }

        if (user.Credential is not null && string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            throw new ArgumentException("passwd: Password unchanged", nameof(newPassword));
        }

        await StoreAsync(user, newPassword, cancellationToken).ConfigureAwait(false);

        if (_keyring is not null)
        {
            // pam_gnome_keyring re-seals the login keyring with the new password. A keyring that is
            // not open yet is opened with the old one first, so its secrets survive the change.
            if (!_keyring.IsUnlockedFor(user.Id) && user.Credential is not null)
            {
                await _keyring.UnlockAsync(principal, currentPassword, cancellationToken).ConfigureAwait(false);
            }

            if (_keyring.IsUnlockedFor(user.Id))
            {
                await _keyring.ChangePasswordAsync(user.Id, newPassword, cancellationToken).ConfigureAwait(false);
            }
        }

        Record(login, "passwd.change", login, AuditOutcome.Success);
    }

    /// <summary>
    /// Sets or removes another account's password for an administrator who already confirmed. The
    /// account's keyring cannot be opened with the new password, so it is set aside
    /// (<c>login.keyring.old</c>); when the account is the open session's, its open keyring is
    /// re-sealed instead (or set aside and locked when the password is removed).
    /// </summary>
    /// <param name="caller">The session that asked.</param>
    /// <param name="administratorLogin">The administrator who confirmed (or <c>root</c> under <c>sudo</c>).</param>
    /// <param name="loginName">The account to change.</param>
    /// <param name="newPassword">The new password, or <see langword="null"/> to remove it.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <exception cref="ArgumentException">The account does not exist, or the new password breaks the rules.</exception>
    /// <exception cref="InvalidOperationException">Removing the last enabled administrator's password.</exception>
    public async ValueTask ResetPasswordAsync(
        AuthenticatedPrincipal caller,
        string administratorLogin,
        string loginName,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorLogin);
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        if (newPassword is not null)
        {
            ThrowIfInvalid(newPassword);
        }

        string operation = newPassword is null ? "passwd.delete" : "passwd.reset";
        LocalUser user = await FindAsync(loginName, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"passwd: user '{loginName}' does not exist", nameof(loginName));

        if (newPassword is null && user.Enabled && user.Authority == AppAuthority.Administrator)
        {
            IReadOnlyList<LocalUser> all = await _users.GetAllAsync(cancellationToken).ConfigureAwait(false);
            if (all.Count(other => other.Enabled && other.Authority == AppAuthority.Administrator && other.Credential is not null) <= 1)
            {
                Record(administratorLogin, operation, user.LoginName.Value, AuditOutcome.Denied);
                throw new InvalidOperationException(
                    $"passwd: refusing to remove the password of '{user.LoginName.Value}', the last administrator with a password");
            }
        }

        if (newPassword is null)
        {
            await _users.SetCredentialAsync(user.Id, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StoreAsync(user, newPassword, cancellationToken).ConfigureAwait(false);
        }

        if (_keyring is not null)
        {
            if (_keyring.IsUnlockedFor(user.Id) && newPassword is not null)
            {
                await _keyring.ChangePasswordAsync(user.Id, newPassword, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                bool open = _keyring.IsUnlockedFor(user.Id);
                await _keyring.SetAsideAsync(user.HomePath, cancellationToken).ConfigureAwait(false);
                if (open)
                {
                    _keyring.Lock();
                }
            }
        }

        Record(administratorLogin, operation, user.LoginName.Value, AuditOutcome.Success);
    }

    private async ValueTask<LocalUser?> FindAsync(string loginName, CancellationToken cancellationToken)
    {
        LocalLoginName name;
        try
        {
            name = LocalLoginName.Parse(loginName);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return await _users.FindByLoginNameAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask StoreAsync(LocalUser user, string newPassword, CancellationToken cancellationToken)
    {
        LocalPasswordCredential credential = await LocalPasswordHasher
            .CreateAsync(newPassword, _iterations, _asyncHasher, cancellationToken)
            .ConfigureAwait(false);
        await _users.SetCredentialAsync(user.Id, credential, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfInvalid(string newPassword)
    {
        if (HackUserAccounts.ValidateNewPassword(newPassword) is { } reason)
        {
            throw new ArgumentException($"BAD PASSWORD: {reason}", nameof(newPassword));
        }
    }

    private void Record(string actor, string operation, string subject, AuditOutcome outcome) =>
        _audit.Record(new AuditEntry(_clock(), Guid.NewGuid(), actor, operation, subject, outcome));
}
