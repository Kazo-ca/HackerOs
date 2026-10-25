using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Sessions;

/// <summary>
/// The page's WebAuthn as the OS sees it (ADR 0168): <c>navigator.credentials</c> and the Web
/// Crypto signature check. The OS builds the options and verifies every answer itself
/// (<see cref="PasskeyService"/>); this bridge only carries bytes.
/// </summary>
/// <remarks>
/// An implementation throws <see cref="OperationCanceledException"/> when the user closes the
/// browser's prompt (WebAuthn <c>NotAllowedError</c>) and <see cref="NotSupportedException"/> when
/// the page has no WebAuthn.
/// </remarks>
public interface IPasskeyAuthenticator
{
    /// <summary>Gets whether the page can use passkeys, and its origin and relying party id.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page's passkey environment.</returns>
    ValueTask<PasskeyEnvironment> GetEnvironmentAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs <c>navigator.credentials.create</c>.</summary>
    /// <param name="options">What to create.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The authenticator's answer.</returns>
    ValueTask<PasskeyRegistrationResponse> CreateAsync(PasskeyCreationOptions options, CancellationToken cancellationToken = default);

    /// <summary>Runs <c>navigator.credentials.get</c>.</summary>
    /// <param name="options">The challenge and the allowed credentials.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The authenticator's answer.</returns>
    ValueTask<PasskeyAssertionResponse> GetAsync(PasskeyRequestOptions options, CancellationToken cancellationToken = default);

    /// <summary>Verifies a WebAuthn signature with a stored public key.</summary>
    /// <param name="algorithm">COSE algorithm (ES256 or RS256).</param>
    /// <param name="publicKey">SubjectPublicKeyInfo (DER).</param>
    /// <param name="data">Signed bytes: authenticator data followed by the SHA-256 of the client data.</param>
    /// <param name="signature">The signature as WebAuthn returns it (DER for ES256).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the signature is valid.</returns>
    ValueTask<bool> VerifySignatureAsync(int algorithm, byte[] publicKey, byte[] data, byte[] signature, CancellationToken cancellationToken = default);
}

/// <summary>What the page offers for passkeys.</summary>
/// <param name="IsSupported">Whether <c>navigator.credentials</c> and a secure context are available.</param>
/// <param name="Origin">The page origin, e.g. <c>https://example.github.io</c>.</param>
/// <param name="RpId">The relying party id: the page's host name.</param>
public sealed record PasskeyEnvironment(bool IsSupported, string Origin, string RpId);

/// <summary>Options of <c>navigator.credentials.create</c> built by the OS.</summary>
/// <param name="Challenge">Random challenge (32 bytes).</param>
/// <param name="RpId">Relying party id.</param>
/// <param name="RpName">Relying party name shown by the browser.</param>
/// <param name="UserHandle">Opaque user handle (the account id).</param>
/// <param name="UserName">Login name.</param>
/// <param name="UserDisplayName">Display name.</param>
/// <param name="ExcludeCredentialIds">Credentials the account already has.</param>
/// <param name="PrfSalt">Salt evaluated by the PRF extension, if the authenticator can.</param>
public sealed record PasskeyCreationOptions(
    byte[] Challenge,
    string RpId,
    string RpName,
    byte[] UserHandle,
    string UserName,
    string UserDisplayName,
    IReadOnlyList<byte[]> ExcludeCredentialIds,
    byte[] PrfSalt);

/// <summary>What <c>navigator.credentials.create</c> answered.</summary>
/// <param name="CredentialId">Raw credential id.</param>
/// <param name="ClientDataJson">The client data JSON bytes.</param>
/// <param name="AuthenticatorData">The authenticator data (<c>getAuthenticatorData()</c>).</param>
/// <param name="PublicKey">SubjectPublicKeyInfo (<c>getPublicKey()</c>), or <see langword="null"/> when the browser gives none.</param>
/// <param name="Algorithm">COSE algorithm (<c>getPublicKeyAlgorithm()</c>).</param>
/// <param name="PrfEnabled">Whether the authenticator supports the PRF extension.</param>
/// <param name="PrfOutput">PRF output for the creation salt, when the authenticator returned it at creation.</param>
public sealed record PasskeyRegistrationResponse(
    byte[] CredentialId,
    byte[] ClientDataJson,
    byte[] AuthenticatorData,
    byte[]? PublicKey,
    int Algorithm,
    bool PrfEnabled,
    byte[]? PrfOutput);

/// <summary>One credential allowed in a <c>navigator.credentials.get</c> call.</summary>
/// <param name="Id">Raw credential id.</param>
/// <param name="PrfSalt">PRF salt to evaluate for that credential, or <see langword="null"/>.</param>
public sealed record PasskeyAllowedCredential(byte[] Id, byte[]? PrfSalt);

/// <summary>Options of <c>navigator.credentials.get</c> built by the OS.</summary>
/// <param name="Challenge">Random challenge (32 bytes).</param>
/// <param name="RpId">Relying party id.</param>
/// <param name="AllowCredentials">The account's credentials.</param>
public sealed record PasskeyRequestOptions(byte[] Challenge, string RpId, IReadOnlyList<PasskeyAllowedCredential> AllowCredentials);

/// <summary>What <c>navigator.credentials.get</c> answered.</summary>
/// <param name="CredentialId">Raw id of the credential used.</param>
/// <param name="ClientDataJson">The client data JSON bytes.</param>
/// <param name="AuthenticatorData">The authenticator data.</param>
/// <param name="Signature">The signature.</param>
/// <param name="PrfOutput">PRF output for that credential's salt, or <see langword="null"/>.</param>
public sealed record PasskeyAssertionResponse(
    byte[] CredentialId,
    byte[] ClientDataJson,
    byte[] AuthenticatorData,
    byte[] Signature,
    byte[]? PrfOutput);

/// <summary>A verified passkey sign-in.</summary>
/// <param name="User">The account, with its passkey counter updated.</param>
/// <param name="Passkey">The passkey used, as stored after the sign-in.</param>
/// <param name="PrfOutput">Its PRF output, which opens the keyring slot of that passkey, or <see langword="null"/>.</param>
public sealed record PasskeyAuthentication(LocalUser User, LocalPasskeyCredential Passkey, byte[]? PrfOutput);
