using System.Security.Cryptography;
using HackerOs.Platform.Core.Keyring;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Sessions;

/// <summary>
/// Passkeys of the local accounts (ADR 0168): registers one for the session user, verifies a
/// passkey sign-in or administrator confirmation, and removes one. The browser only carries bytes
/// (<see cref="IPasskeyAuthenticator"/>); every check of section 1 is made here, and every change
/// is written to <c>auth.log</c>.
/// </summary>
public sealed class PasskeyService
{
    /// <summary>Relying party name the browser shows.</summary>
    public const string RelyingPartyName = "HackerOS";

    private const int ChallengeLength = 32;
    private const int PrfSaltLength = 32;

    private readonly ILocalUserRepository _users;
    private readonly IPasskeyAuthenticator _authenticator;
    private readonly IAuditLog _audit;
    private readonly UserKeyringService? _keyring;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Initializes the service.</summary>
    /// <param name="users">The account store, which keeps the passkeys.</param>
    /// <param name="authenticator">The page's WebAuthn.</param>
    /// <param name="audit">The authentication log (<c>auth.log</c>).</param>
    /// <param name="keyring">The login keyring, which gets a slot per PRF passkey; <see langword="null"/> on a host without one.</param>
    /// <param name="clock">Clock of the dates and audit entries; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public PasskeyService(
        ILocalUserRepository users,
        IPasskeyAuthenticator authenticator,
        IAuditLog audit,
        UserKeyringService? keyring = null,
        Func<DateTimeOffset>? clock = null)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _keyring = keyring;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Gets whether the page can use passkeys.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when WebAuthn is available.</returns>
    public async ValueTask<bool> IsSupportedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _authenticator.GetEnvironmentAsync(cancellationToken).ConfigureAwait(false)).IsSupported;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Lists the passkeys of one account.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The passkeys, oldest first.</returns>
    public async ValueTask<IReadOnlyList<LocalPasskeyCredential>> ListAsync(LocalUserId userId, CancellationToken cancellationToken = default)
    {
        LocalUser? user = await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        return user is null ? [] : [.. user.Passkeys.OrderBy(static passkey => passkey.CreatedAtUtc)];
    }

    /// <summary>
    /// Gets the ids of the session user's passkeys that open their keyring (ADR 0168 section 3).
    /// </summary>
    /// <param name="principal">The session user.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The base64url ids with a keyring slot.</returns>
    public async ValueTask<IReadOnlySet<string>> GetKeyringPasskeyIdsAsync(AuthenticatedPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return _keyring is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : await _keyring.GetPasskeySlotIdsAsync(principal, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a passkey for the session user and stores it; when their keyring is open and the
    /// authenticator has the PRF extension, adds the passkey's keyring slot (ADR 0168 section 3).
    /// </summary>
    /// <param name="principal">The session user.</param>
    /// <param name="name">Name of the passkey, e.g. "Laptop".</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The stored passkey.</returns>
    /// <exception cref="NotSupportedException">The page has no WebAuthn, or the browser gives no public key.</exception>
    /// <exception cref="OperationCanceledException">The user closed the browser's prompt.</exception>
    /// <exception cref="InvalidOperationException">The browser's answer failed a check.</exception>
    public async ValueTask<LocalPasskeyCredential> RegisterAsync(AuthenticatedPrincipal principal, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > LocalPasskeyCredential.MaximumNameLength)
        {
            throw new ArgumentException($"A passkey name of 1 to {LocalPasskeyCredential.MaximumNameLength} characters is required.", nameof(name));
        }

        PasskeyEnvironment environment = await RequireEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        LocalUser user = await _users.FindByIdAsync(principal.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No local user '{principal.LoginName.Value}'.");
        string login = user.LoginName.Value;

        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeLength);
        byte[] prfSalt = RandomNumberGenerator.GetBytes(PrfSaltLength);
        PasskeyRegistrationResponse response = await _authenticator.CreateAsync(
            new PasskeyCreationOptions(
                challenge,
                environment.RpId,
                RelyingPartyName,
                user.Id.Value.ToByteArray(),
                login,
                user.DisplayName,
                [.. user.Passkeys.Select(static passkey => passkey.CredentialId)],
                prfSalt),
            cancellationToken).ConfigureAwait(false);

        uint signCount = 0;
        string? failure = PasskeyResponseChecks.CheckClientData(response.ClientDataJson, PasskeyResponseChecks.CreateType, challenge, environment.Origin)
            ?? PasskeyResponseChecks.CheckAuthenticatorData(response.AuthenticatorData, environment.RpId, out signCount)
            ?? (response.CredentialId is { Length: > 0 } ? null : "credential-id");
        if (failure is not null)
        {
            Record(login, "passkey.add", login, AuditOutcome.Denied);
            throw new InvalidOperationException($"The passkey could not be verified ({failure}).");
        }

        if (response.PublicKey is not { Length: > 0 } publicKey
            || response.Algorithm is not (LocalPasskeyCredential.Es256 or LocalPasskeyCredential.Rs256))
        {
            Record(login, "passkey.add", login, AuditOutcome.Denied);
            throw new NotSupportedException("This browser or authenticator does not give a usable public key (ES256 or RS256).");
        }

        LocalPasskeyCredential passkey = new(
            response.CredentialId,
            publicKey,
            response.Algorithm,
            name,
            signCount,
            _clock(),
            lastUsedAtUtc: null,
            response.PrfEnabled ? prfSalt : null);

        LocalUser current = await _users.FindByIdAsync(user.Id, cancellationToken).ConfigureAwait(false) ?? user;
        LocalUser stored = await _users
            .SetPasskeysAsync(current.Id, [.. current.Passkeys.Where(existing => existing.Id != passkey.Id), passkey], cancellationToken)
            .ConfigureAwait(false);
        Record(login, "passkey.add", $"{login} {passkey.Id}", AuditOutcome.Success);

        if (response.PrfEnabled && _keyring is not null && _keyring.IsUnlockedFor(user.Id))
        {
            byte[]? prfOutput = response.PrfOutput;
            if (prfOutput is null)
            {
                // The authenticator gives the PRF output only on an assertion: one more confirmation.
                PasskeyAuthentication confirmed = await AuthenticateAsync(stored, [passkey], cancellationToken).ConfigureAwait(false);
                prfOutput = confirmed.PrfOutput;
                passkey = confirmed.Passkey;
            }

            if (prfOutput is not null)
            {
                await _keyring.AddPasskeySlotAsync(user.Id, passkey.Id, prfOutput, cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(prfOutput);
            }
        }

        return passkey;
    }

    /// <summary>
    /// Asks the user for one of the account's passkeys and verifies the answer (ADR 0168 section 1),
    /// then stores the new counter and the date of use. Opens no session and no keyring.
    /// </summary>
    /// <param name="user">The account signing in.</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The verified sign-in, with the PRF output when the authenticator gave one.</returns>
    /// <exception cref="NotSupportedException">The page has no WebAuthn.</exception>
    /// <exception cref="OperationCanceledException">The user closed the browser's prompt.</exception>
    /// <exception cref="InvalidOperationException">The account has no passkey, or the answer failed a check.</exception>
    public ValueTask<PasskeyAuthentication> AuthenticateAsync(LocalUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return AuthenticateAsync(user, user.Passkeys, cancellationToken);
    }

    /// <summary>Removes one of the session user's passkeys, and its keyring slot.</summary>
    /// <param name="principal">The session user.</param>
    /// <param name="passkeyId">Base64url id of the passkey.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> when a passkey was removed.</returns>
    public async ValueTask<bool> RemoveAsync(AuthenticatedPrincipal principal, string passkeyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(passkeyId);
        LocalUser? user = await _users.FindByIdAsync(principal.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.Passkeys.All(passkey => passkey.Id != passkeyId))
        {
            return false;
        }

        await _users.SetPasskeysAsync(user.Id, [.. user.Passkeys.Where(passkey => passkey.Id != passkeyId)], cancellationToken).ConfigureAwait(false);
        if (_keyring is not null)
        {
            await _keyring.RemovePasskeySlotAsync(principal, passkeyId, cancellationToken).ConfigureAwait(false);
        }

        Record(user.LoginName.Value, "passkey.remove", $"{user.LoginName.Value} {passkeyId}", AuditOutcome.Success);
        return true;
    }

    private async ValueTask<PasskeyAuthentication> AuthenticateAsync(
        LocalUser user, IReadOnlyList<LocalPasskeyCredential> allowed, CancellationToken cancellationToken)
    {
        string login = user.LoginName.Value;
        if (allowed.Count == 0)
        {
            throw new InvalidOperationException($"'{login}' has no passkey.");
        }

        PasskeyEnvironment environment = await RequireEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeLength);
        PasskeyAssertionResponse response = await _authenticator.GetAsync(
            new PasskeyRequestOptions(
                challenge,
                environment.RpId,
                [.. allowed.Select(static passkey => new PasskeyAllowedCredential(passkey.CredentialId, passkey.PrfSalt))]),
            cancellationToken).ConfigureAwait(false);

        LocalPasskeyCredential? passkey = allowed.FirstOrDefault(candidate => candidate.CredentialId.AsSpan().SequenceEqual(response.CredentialId));
        uint signCount = 0;
        string? failure = passkey is null
            ? "unknown-credential"
            : PasskeyResponseChecks.CheckClientData(response.ClientDataJson, PasskeyResponseChecks.AssertionType, challenge, environment.Origin)
                ?? PasskeyResponseChecks.CheckAuthenticatorData(response.AuthenticatorData, environment.RpId, out signCount);
        if (failure is null
            && !await _authenticator.VerifySignatureAsync(
                passkey!.Algorithm,
                passkey.PublicKey,
                PasskeyResponseChecks.SignedData(response.AuthenticatorData, response.ClientDataJson),
                response.Signature,
                cancellationToken).ConfigureAwait(false))
        {
            failure = "signature";
        }

        if (failure is null && !PasskeyResponseChecks.IsCounterAcceptable(passkey!.SignCount, signCount))
        {
            failure = "counter";
        }

        if (failure is not null)
        {
            Record(login, "passkey.verify", login, AuditOutcome.Denied);
            throw new InvalidOperationException($"The passkey could not be verified ({failure}).");
        }

        LocalPasskeyCredential used = passkey! with { SignCount = signCount, LastUsedAtUtc = _clock() };
        LocalUser current = await _users.FindByIdAsync(user.Id, cancellationToken).ConfigureAwait(false) ?? user;
        LocalUser updated = await _users
            .SetPasskeysAsync(current.Id, [.. current.Passkeys.Select(existing => existing.Id == used.Id ? used : existing)], cancellationToken)
            .ConfigureAwait(false);
        return new PasskeyAuthentication(updated, used, response.PrfOutput);
    }

    private async ValueTask<PasskeyEnvironment> RequireEnvironmentAsync(CancellationToken cancellationToken)
    {
        PasskeyEnvironment environment = await _authenticator.GetEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        return environment.IsSupported
            ? environment
            : throw new NotSupportedException("Passkeys need a browser with WebAuthn and a secure page (HTTPS or localhost).");
    }

    private void Record(string actor, string operation, string subject, AuditOutcome outcome) =>
        _audit.Record(new AuditEntry(_clock(), Guid.NewGuid(), actor, operation, subject, outcome));
}
