using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// Checks the signature a source puts next to the SHA-256 of a package (ADR 0214 §2): ECDSA P-256 with
/// SHA-256 over the bytes of the <c>.hackapp</c> file, signature in IEEE P1363 form (<c>r||s</c>).
/// </summary>
/// <remarks>
/// .NET in WebAssembly has no ECDSA: the browser hosts verify with Web Crypto
/// (<c>crypto.subtle.verify</c>), every other host with <see cref="ManagedAppPackageSignatureVerifier"/>.
/// </remarks>
public interface IAppPackageSignatureVerifier
{
    /// <summary>Verifies one signature.</summary>
    /// <param name="publicKey">The source's public key, SubjectPublicKeyInfo (DER) of a P-256 key.</param>
    /// <param name="data">The package bytes.</param>
    /// <param name="signature">The signature, 64 bytes <c>r||s</c>.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> when the key signed exactly these bytes.</returns>
    ValueTask<bool> VerifyAsync(byte[] publicKey, byte[] data, byte[] signature, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IAppPackageSignatureVerifier"/> on <see cref="ECDsa"/>, for every host but the browser.</summary>
public sealed class ManagedAppPackageSignatureVerifier : IAppPackageSignatureVerifier
{
    /// <summary>The verifier, stateless.</summary>
    public static ManagedAppPackageSignatureVerifier Instance { get; } = new();

    /// <inheritdoc />
    /// <exception cref="PlatformNotSupportedException">In the browser, where .NET has no ECDSA.</exception>
    public ValueTask<bool> VerifyAsync(byte[] publicKey, byte[] data, byte[] signature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException("ECDSA is not available to .NET in the browser; use Web Crypto.");
        }

        return ValueTask.FromResult(Verify(publicKey, data, signature));
    }

    [UnsupportedOSPlatform("browser")]
    private static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>The formats of ADR 0214 §2: public keys and signatures as they appear in files.</summary>
public static class AppPackageSignature
{
    /// <summary>Length of a P-256 signature in IEEE P1363 form.</summary>
    public const int SignatureLength = 64;

    /// <summary>Name of the field of an <c>index.json</c> or <c>catalog.json</c> version.</summary>
    public const string FieldName = "signature";

    /// <summary>
    /// The 27 first bytes of every SubjectPublicKeyInfo of an uncompressed P-256 key: the DER header
    /// (<c>id-ecPublicKey</c>, <c>prime256v1</c>, bit string) and the <c>0x04</c> point prefix.
    /// </summary>
    private static ReadOnlySpan<byte> P256SpkiPrefix =>
    [
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a,
        0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04,
    ];

    /// <summary>Length of the SubjectPublicKeyInfo of a P-256 key.</summary>
    public const int PublicKeyLength = 91;

    /// <summary>
    /// Reads a public key written in base64 (<c>[signing-keys]</c>, <c>HackerOsAppSigningKeys</c>): the
    /// SubjectPublicKeyInfo of an uncompressed P-256 key, checked by its shape only (the browser
    /// imports it at verification time).
    /// </summary>
    /// <param name="text">The base64 text.</param>
    /// <param name="key">The DER bytes.</param>
    /// <returns><see langword="false"/> for anything else.</returns>
    public static bool TryParsePublicKey(string? text, [NotNullWhen(true)] out byte[]? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        byte[] buffer = new byte[PublicKeyLength + 3];
        if (!Convert.TryFromBase64String(text.Trim(), buffer, out int written)
            || written != PublicKeyLength
            || !buffer.AsSpan(0, P256SpkiPrefix.Length).SequenceEqual(P256SpkiPrefix))
        {
            return false;
        }

        key = buffer[..PublicKeyLength];
        return true;
    }

    /// <summary>Reads the optional <c>signature</c> field of one version.</summary>
    /// <param name="version">The JSON object of the version.</param>
    /// <param name="versionText">The version, for the message.</param>
    /// <returns>The 64 bytes, or <see langword="null"/> when the field is absent or empty.</returns>
    /// <exception cref="AppPackageException"><c>apps.index.invalid</c>: not base64 of 64 bytes.</exception>
    public static byte[]? ReadField(JsonElement version, string versionText)
    {
        if (!version.TryGetProperty(FieldName, out JsonElement field) || field.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        string? text = field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        byte[] buffer = new byte[SignatureLength + 3];
        if (!Convert.TryFromBase64String(text, buffer, out int written) || written != SignatureLength)
        {
            throw new AppPackageException(
                "apps.index.invalid", $"The signature of version '{versionText}' is not the base64 of a 64-byte ECDSA P-256 signature.");
        }

        return buffer[..SignatureLength];
    }
}
