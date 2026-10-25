namespace HackerOs.Simulation.Abstractions.Sessions;

/// <summary>
/// One WebAuthn passkey registered for a local account (ADR 0168): what the OS needs to verify an
/// assertion, never a secret. The private key stays in the authenticator.
/// </summary>
public sealed record LocalPasskeyCredential
{
    /// <summary>COSE algorithm identifier of ECDSA with P-256 and SHA-256.</summary>
    public const int Es256 = -7;

    /// <summary>COSE algorithm identifier of RSASSA-PKCS1-v1_5 with SHA-256.</summary>
    public const int Rs256 = -257;

    /// <summary>Longest name a user may give a passkey.</summary>
    public const int MaximumNameLength = 64;

    /// <summary>Initializes a validated passkey record.</summary>
    /// <param name="credentialId">Credential id returned by the authenticator (raw bytes).</param>
    /// <param name="publicKey">Public key in SubjectPublicKeyInfo (DER) form.</param>
    /// <param name="algorithm">COSE algorithm: <see cref="Es256"/> or <see cref="Rs256"/>.</param>
    /// <param name="name">Name given by the user, e.g. "Laptop".</param>
    /// <param name="signCount">Last signature counter seen (0 when the authenticator keeps none).</param>
    /// <param name="createdAtUtc">When the passkey was registered.</param>
    /// <param name="lastUsedAtUtc">Last successful use, or <see langword="null"/>.</param>
    /// <param name="prfSalt">32-byte salt of the PRF extension, or <see langword="null"/> when the authenticator has no PRF.</param>
    /// <exception cref="ArgumentException">A field is missing or out of range.</exception>
    public LocalPasskeyCredential(
        byte[] credentialId,
        byte[] publicKey,
        int algorithm,
        string name,
        uint signCount,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? lastUsedAtUtc,
        byte[]? prfSalt)
    {
        if (credentialId is not { Length: > 0 })
        {
            throw new ArgumentException("A credential id is required.", nameof(credentialId));
        }

        if (publicKey is not { Length: > 0 })
        {
            throw new ArgumentException("A public key is required.", nameof(publicKey));
        }

        if (algorithm is not (Es256 or Rs256))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm), "Only ES256 (-7) and RS256 (-257) are supported.");
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaximumNameLength)
        {
            throw new ArgumentException($"A passkey name of 1 to {MaximumNameLength} characters is required.", nameof(name));
        }

        if (prfSalt is { Length: not 32 })
        {
            throw new ArgumentException("The PRF salt must be 32 bytes.", nameof(prfSalt));
        }

        CredentialId = credentialId;
        PublicKey = publicKey;
        Algorithm = algorithm;
        Name = name.Trim();
        SignCount = signCount;
        CreatedAtUtc = createdAtUtc;
        LastUsedAtUtc = lastUsedAtUtc;
        PrfSalt = prfSalt;
    }

    /// <summary>Gets the credential id returned by the authenticator.</summary>
    public byte[] CredentialId { get; }

    /// <summary>Gets the public key (SubjectPublicKeyInfo, DER).</summary>
    public byte[] PublicKey { get; }

    /// <summary>Gets the COSE algorithm of the key.</summary>
    public int Algorithm { get; }

    /// <summary>Gets the name given by the user.</summary>
    public string Name { get; }

    /// <summary>Gets the last signature counter seen.</summary>
    public uint SignCount { get; init; }

    /// <summary>Gets when the passkey was registered.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets the last successful use, or <see langword="null"/>.</summary>
    public DateTimeOffset? LastUsedAtUtc { get; init; }

    /// <summary>Gets the salt of the PRF extension, or <see langword="null"/> when the authenticator has none.</summary>
    public byte[]? PrfSalt { get; }

    /// <summary>Gets the credential id in base64url, as WebAuthn and the SDK show it.</summary>
    public string Id => ToBase64Url(CredentialId);

    /// <summary>Encodes bytes in base64url without padding (RFC 4648 section 5).</summary>
    /// <param name="bytes">Bytes to encode.</param>
    /// <returns>The base64url text.</returns>
    public static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes base64url, with or without padding.</summary>
    /// <param name="text">Text to decode.</param>
    /// <returns>The bytes, or <see langword="null"/> when the text is empty or malformed.</returns>
    public static byte[]? FromBase64Url(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
