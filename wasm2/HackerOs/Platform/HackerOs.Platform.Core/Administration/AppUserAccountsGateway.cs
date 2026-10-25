using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Administration;

/// <summary>
/// Trusted <see cref="IAppUserAccountsGateway"/> (ADR 0120): lists accounts and changes the session
/// user's own password with no capability (the current password is the proof, as <c>passwd</c>
/// checks it through PAM); resetting or removing another account's password, and adding or deleting
/// an account (ADR 0169), check <c>users.manage</c> and ask the OS authentication agent for an
/// administrator first. The session user manages their own passkeys with no capability (ADR 0168):
/// the browser asks for their presence.
/// </summary>
internal sealed class AppUserAccountsGateway : IAppUserAccountsGateway
{
    private readonly Func<LocalPasswordService>? _passwords;
    private readonly ICapabilityChecker _capabilities;
    private readonly AdministratorGate _gate;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="passwords">Lazily resolved password service; <see langword="null"/> on a host without accounts.</param>
    /// <param name="capabilities">The app's capability checker.</param>
    /// <param name="gate">Administrator authentication for this app.</param>
    public AppUserAccountsGateway(Func<LocalPasswordService>? passwords, ICapabilityChecker capabilities, AdministratorGate gate)
    {
        _passwords = passwords;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>Gets the lazily resolved account service of ADR 0169; <see langword="null"/> leaves adding and deleting unsupported.</summary>
    public Func<LocalAccountService>? Accounts { get; init; }

    /// <summary>Gets the lazily resolved passkey service of ADR 0168; <see langword="null"/> on a host without WebAuthn.</summary>
    public Func<PasskeyService>? Passkeys { get; init; }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackUserAccount>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LocalUser> users = await Service().ListAsync(cancellationToken).ConfigureAwait(false);
        return users
            .OrderBy(user => user.LoginName.Value, StringComparer.Ordinal)
            .Select(ToAccount)
            .ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<HackUserAccount> CreateAsync(HackNewUserAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        LocalAccountService service = Accounts?.Invoke() ?? throw new NotSupportedException("This HackerOS host cannot add accounts.");
        _capabilities.Require(AppCapabilities.UsersManage);
        string login = account.LoginName.Trim();
        string admin = await _gate.AuthenticateAsync(_gate.Text["Admin.AddUser", login], "users.add", login, cancellationToken).ConfigureAwait(false);
        LocalUser created = await service.CreateAsync(admin, account, cancellationToken).ConfigureAwait(false);
        return ToAccount(created);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string loginName, bool removeHome, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        LocalAccountService service = Accounts?.Invoke() ?? throw new NotSupportedException("This HackerOS host cannot delete accounts.");
        _capabilities.Require(AppCapabilities.UsersManage);
        string admin = await _gate.AuthenticateAsync(
            _gate.Text[removeHome ? "Admin.DeleteUserAndHome" : "Admin.DeleteUser", loginName],
            "users.delete",
            loginName,
            cancellationToken).ConfigureAwait(false);
        await service.DeleteAsync(_gate.Principal, admin, loginName, removeHome, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HackPasskey>> ListPasskeysAsync(CancellationToken cancellationToken = default)
    {
        PasskeyService service = Passkeys?.Invoke() ?? throw new NotSupportedException("This HackerOS host has no passkeys.");
        IReadOnlyList<LocalPasskeyCredential> passkeys = await service.ListAsync(_gate.Principal.UserId, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> unlocking = await service.GetKeyringPasskeyIdsAsync(_gate.Principal, cancellationToken).ConfigureAwait(false);
        return [.. passkeys.Select(passkey => ToPasskey(passkey, unlocking))];
    }

    /// <inheritdoc />
    public async ValueTask<HackPasskey> AddPasskeyAsync(string name, CancellationToken cancellationToken = default)
    {
        PasskeyService service = Passkeys?.Invoke() ?? throw new NotSupportedException("This HackerOS host has no passkeys.");
        LocalPasskeyCredential passkey = await service.RegisterAsync(_gate.Principal, name, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> unlocking = await service.GetKeyringPasskeyIdsAsync(_gate.Principal, cancellationToken).ConfigureAwait(false);
        return ToPasskey(passkey, unlocking);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemovePasskeyAsync(string passkeyId, CancellationToken cancellationToken = default)
    {
        PasskeyService service = Passkeys?.Invoke() ?? throw new NotSupportedException("This HackerOS host has no passkeys.");
        return service.RemoveAsync(_gate.Principal, passkeyId, cancellationToken);
    }

    private HackUserAccount ToAccount(LocalUser user) => new(
        user.LoginName.Value,
        user.DisplayName,
        user.Authority,
        user.Enabled,
        user.Credential is not null,
        user.UpdatedAtUtc,
        user.Id == _gate.Principal.UserId)
    {
        PasskeyCount = user.Passkeys.Count
    };

    private static HackPasskey ToPasskey(LocalPasskeyCredential passkey, IReadOnlySet<string> unlocking) =>
        new(passkey.Id, passkey.Name, passkey.CreatedAtUtc, passkey.LastUsedAtUtc, unlocking.Contains(passkey.Id));

    /// <inheritdoc />
    public ValueTask ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
        Service().ChangeOwnPasswordAsync(_gate.Principal, currentPassword, newPassword, cancellationToken);

    /// <inheritdoc />
    public async ValueTask ResetPasswordAsync(string loginName, string? newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        LocalPasswordService service = Service();
        _capabilities.Require(AppCapabilities.UsersManage);
        bool remove = newPassword is null;
        string admin = await _gate.AuthenticateAsync(
            _gate.Text[remove ? "Admin.RemovePassword" : "Admin.ResetPassword", loginName],
            remove ? "users.password-delete" : "users.password-reset",
            loginName,
            cancellationToken).ConfigureAwait(false);
        await service.ResetPasswordAsync(_gate.Principal, admin, loginName, newPassword, cancellationToken).ConfigureAwait(false);
    }

    private LocalPasswordService Service() =>
        _passwords?.Invoke() ?? throw new NotSupportedException("This HackerOS host cannot change passwords.");
}
