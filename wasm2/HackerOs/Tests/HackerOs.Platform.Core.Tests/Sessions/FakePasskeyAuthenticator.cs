using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Sessions;

/// <summary>What a test makes the fake authenticator get wrong.</summary>
public enum PasskeyTamper
{
    None,
    Challenge,
    Origin,
    RpId,
    Signature,
    ClonedCounter,
    NoUserPresence
}

/// <summary>
/// A browser and authenticator for ADR 0168 tests: a real P-256 key, client data and authenticator
/// data shaped as WebAuthn Level 3 describes them, a DER signature, and a PRF output computed as
/// HMAC-SHA-256 of the salt, like the CTAP <c>hmac-secret</c> extension behind PRF.
/// </summary>
internal sealed class FakePasskeyAuthenticator : IPasskeyAuthenticator, IDisposable
{
    internal const string Origin = "https://hackeros.test";
    internal const string RpId = "hackeros.test";

    private const string CreateType = "webauthn.create";
    private const string AssertionType = "webauthn.get";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _prfSecret = RandomNumberGenerator.GetBytes(32);
    private uint _counter;

    /// <summary>Gets or sets whether the page has WebAuthn.</summary>
    internal bool IsSupported { get; set; } = true;

    /// <summary>Gets or sets whether the authenticator has the PRF extension.</summary>
    internal bool SupportsPrf { get; set; } = true;

    /// <summary>Gets or sets whether the PRF output already comes with the creation (otherwise only with an assertion).</summary>
    internal bool PrfOnCreate { get; set; }

    /// <summary>Gets or sets whether the user closes the browser prompt.</summary>
    internal bool Cancels { get; set; }

    /// <summary>Gets or sets what the next answers get wrong.</summary>
    internal PasskeyTamper Tamper { get; set; }

    /// <summary>Gets the credential id of this authenticator's passkey.</summary>
    internal byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    /// <summary>Gets how many assertions were asked.</summary>
    internal int Assertions { get; private set; }

    public ValueTask<PasskeyEnvironment> GetEnvironmentAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new PasskeyEnvironment(IsSupported, Origin, RpId));

    public ValueTask<PasskeyRegistrationResponse> CreateAsync(PasskeyCreationOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfCancelled();
        return ValueTask.FromResult(new PasskeyRegistrationResponse(
            CredentialId,
            ClientData(CreateType, options.Challenge),
            AuthenticatorData(_counter),
            _key.ExportSubjectPublicKeyInfo(),
            LocalPasskeyCredential.Es256,
            SupportsPrf,
            SupportsPrf && PrfOnCreate ? Prf(options.PrfSalt) : null));
    }

    public ValueTask<PasskeyAssertionResponse> GetAsync(PasskeyRequestOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfCancelled();
        Assertions++;
        PasskeyAllowedCredential allowed = options.AllowCredentials.First(candidate => candidate.Id.AsSpan().SequenceEqual(CredentialId));
        uint counter = Tamper == PasskeyTamper.ClonedCounter ? _counter : ++_counter;
        byte[] clientData = ClientData(AssertionType, options.Challenge);
        byte[] authenticatorData = AuthenticatorData(counter);
        byte[] signed = [.. authenticatorData, .. SHA256.HashData(clientData)];
        byte[] signature = _key.SignData(
            Tamper == PasskeyTamper.Signature ? [.. signed, 0] : signed,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        byte[]? prfOutput = SupportsPrf && allowed.PrfSalt is { } salt ? Prf(salt) : null;
        return ValueTask.FromResult(new PasskeyAssertionResponse(CredentialId, clientData, authenticatorData, signature, prfOutput));
    }

    /// <summary>Verifies as the page does: DER converted to <c>r‖s</c>, then the stored public key.</summary>
    public ValueTask<bool> VerifySignatureAsync(int algorithm, byte[] publicKey, byte[] data, byte[] signature, CancellationToken cancellationToken = default)
    {
        if (algorithm != LocalPasskeyCredential.Es256 || PasskeySignatureFormat.DerToP1363(signature) is not { } p1363)
        {
            return ValueTask.FromResult(false);
        }

        using ECDsa verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
        return ValueTask.FromResult(verifier.VerifyData(data, p1363, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public void Dispose() => _key.Dispose();

    private void ThrowIfCancelled()
    {
        if (Cancels)
        {
            throw new OperationCanceledException("NotAllowedError");
        }
    }

    private byte[] Prf(byte[] salt) => HMACSHA256.HashData(_prfSecret, salt);

    private byte[] ClientData(string type, byte[] challenge) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["type"] = type,
            ["challenge"] = LocalPasskeyCredential.ToBase64Url(
                Tamper == PasskeyTamper.Challenge ? RandomNumberGenerator.GetBytes(challenge.Length) : challenge),
            ["origin"] = Tamper == PasskeyTamper.Origin ? "https://evil.test" : Origin,
            ["crossOrigin"] = false
        });

    private byte[] AuthenticatorData(uint counter)
    {
        byte[] data = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(Tamper == PasskeyTamper.RpId ? "evil.test" : RpId)).CopyTo(data, 0);
        data[32] = Tamper == PasskeyTamper.NoUserPresence ? (byte)0x04 : (byte)0x05;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(33), counter);
        return data;
    }
}
