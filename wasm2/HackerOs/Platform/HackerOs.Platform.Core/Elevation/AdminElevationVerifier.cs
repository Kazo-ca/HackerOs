using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Elevation;

/// <summary>
/// Verifies that a login belongs to an enabled Administrator, reusing the same lookup/hash-check
/// sequence as <see cref="HackerOs.Platform.Core.Sessions.LocalSessionService.LoginAsync"/>, but
/// without touching session state — safe to call while a different user's session is active
/// (`ADR 0042`).
/// </summary>
public sealed class AdminElevationVerifier : IAdminElevationVerifier
{
    private readonly ILocalUserRepository _users;
    private readonly IAuditLog _auditLog;
    private readonly Func<DateTimeOffset> _clock;
    private readonly KeyDerivationAsyncDelegate? _asyncHasher;

    /// <summary>Initializes the verifier over the user repository and audit log.</summary>
    public AdminElevationVerifier(
        ILocalUserRepository users,
        IAuditLog auditLog,
        Func<DateTimeOffset>? clock = null,
        KeyDerivationAsyncDelegate? asyncHasher = null)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _asyncHasher = asyncHasher;
    }

    /// <inheritdoc />
    public Task<AdminElevationVerificationResult> VerifyAsync(
        string loginName, string password, CancellationToken cancellationToken = default) =>
        VerifyCoreAsync(
            loginName,
            async (user, token) => user.Credential is { } credential
                && await LocalPasswordHasher.VerifyAsync(password ?? string.Empty, credential, _asyncHasher, token).ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Verifies that a login belongs to an enabled Administrator who confirms with one of their
    /// passkeys (ADR 0168 section 4). Opens no session and no keyring.
    /// </summary>
    /// <param name="loginName">The administrator's login.</param>
    /// <param name="passkeys">The passkey service of this host.</param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    /// <returns>The verification; <c>no-passkey</c> when the account has none.</returns>
    /// <exception cref="OperationCanceledException">The user closed the browser's prompt.</exception>
    /// <exception cref="NotSupportedException">The page has no WebAuthn.</exception>
    public Task<AdminElevationVerificationResult> VerifyWithPasskeyAsync(
        string loginName, PasskeyService passkeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passkeys);
        return VerifyCoreAsync(
            loginName,
            async (user, token) =>
            {
                try
                {
                    await passkeys.AuthenticateAsync(user, token).ConfigureAwait(false);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            },
            cancellationToken,
            requirePasskey: true);
    }

    private async Task<AdminElevationVerificationResult> VerifyCoreAsync(
        string loginName,
        Func<LocalUser, CancellationToken, Task<bool>> proves,
        CancellationToken cancellationToken,
        bool requirePasskey = false)
    {
        ArgumentNullException.ThrowIfNull(loginName);

        LocalLoginName parsed;
        try
        {
            parsed = LocalLoginName.Parse(loginName);
        }
        catch (ArgumentException)
        {
            RecordAudit(loginName, loginName, AuditOutcome.Denied);
            return AdminElevationVerificationResult.Failed("invalid-login-name");
        }

        LocalUser? user = await _users.FindByLoginNameAsync(parsed, cancellationToken);
        if (user is null)
        {
            RecordAudit(loginName, loginName, AuditOutcome.Denied);
            return AdminElevationVerificationResult.Failed("user-not-found");
        }

        if (!user.Enabled)
        {
            RecordAudit(user.Id.ToString(), loginName, AuditOutcome.Denied);
            return AdminElevationVerificationResult.Failed("user-disabled");
        }

        if (!AppAuthorityPolicy.Satisfies(user.Authority, AppAuthority.Administrator))
        {
            RecordAudit(user.Id.ToString(), loginName, AuditOutcome.Denied);
            return AdminElevationVerificationResult.Failed("not-administrator");
        }

        if (requirePasskey && user.Passkeys.Count == 0)
        {
            RecordAudit(user.Id.ToString(), loginName, AuditOutcome.Denied);
            return AdminElevationVerificationResult.Failed("no-passkey");
        }

        if (!await proves(user, cancellationToken).ConfigureAwait(false))
        {
            RecordAudit(user.Id.ToString(), loginName, AuditOutcome.Denied);
            return AdminElevationVerificationResult.Failed("invalid-credentials");
        }

        RecordAudit(user.Id.ToString(), loginName, AuditOutcome.Success);
        return new AdminElevationVerificationResult(true, user.Id.ToString(), user.LoginName.Value, null);
    }

    private void RecordAudit(string actor, string subject, AuditOutcome outcome) =>
        _auditLog.Record(new AuditEntry(_clock(), Guid.NewGuid(), actor, "session.elevate", subject, outcome));
}
