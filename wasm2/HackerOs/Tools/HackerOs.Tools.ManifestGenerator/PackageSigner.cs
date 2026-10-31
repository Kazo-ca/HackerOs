using System.Security.Cryptography;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Tools.ManifestGenerator;

/// <summary>
/// Signatures of an app author's packages (ADR 0219, format of ADR 0214 §2): ECDSA P-256 with SHA-256
/// over the bytes of the <c>.hackapp</c>, IEEE P1363 (<c>r||s</c>, 64 bytes) as base64 in the
/// <c>signature</c> field of <c>index.json</c>. The OS checks it against the keys that
/// <c>[signing-keys]</c> lists for <c>github.com/&lt;owner&gt;</c> or <c>github.com/&lt;owner&gt;/&lt;repo&gt;</c>.
/// </summary>
/// <remarks>
/// Same format as <c>PackageSigner</c> of <c>HackerOs.MarketPlace.Tool</c> (official catalog). The private
/// key is a PKCS#8 PEM file kept by the author; a workflow receives it through the
/// <see cref="KeyVariable"/> secret, never on a command line.
/// </remarks>
public static class PackageSigner
{
    /// <summary>Environment variable that holds the author's private key (PEM) during <c>pack</c>.</summary>
    public const string KeyVariable = "HACKEROS_APP_SIGNING_KEY";

    /// <summary>Creates a new key pair.</summary>
    /// <returns>The private key as PKCS#8 PEM and the public key as base64 SubjectPublicKeyInfo.</returns>
    public static (string PrivateKeyPem, string PublicKey) CreateKeyPair()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Gets the public key of a private key.</summary>
    /// <param name="privateKeyPem">PKCS#8 PEM of a P-256 key.</param>
    /// <exception cref="ArgumentException">The key is not a P-256 private key.</exception>
    public static string GetPublicKey(string privateKeyPem)
    {
        using ECDsa key = Import(privateKeyPem);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Signs a package and checks the result with the public key before returning it.</summary>
    /// <param name="package">Bytes of the <c>.hackapp</c>.</param>
    /// <param name="privateKeyPem">PKCS#8 PEM of a P-256 key.</param>
    /// <returns>The 64-byte signature.</returns>
    /// <exception cref="ArgumentException">The key is not a P-256 private key.</exception>
    public static byte[] Sign(byte[] package, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(package);
        using ECDsa key = Import(privateKeyPem);
        byte[] signature = key.SignData(package, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!Verify(package, signature, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())))
        {
            throw new CryptographicException("The signature does not verify with the key's own public key.");
        }

        return signature;
    }

    /// <summary>Checks a signature the way the OS does (shape of the key included).</summary>
    /// <param name="package">Bytes of the <c>.hackapp</c>.</param>
    /// <param name="signature">The 64-byte signature.</param>
    /// <param name="publicKey">The base64 SubjectPublicKeyInfo.</param>
    public static bool Verify(byte[] package, byte[] signature, string publicKey)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Length != AppPackageSignature.SignatureLength
            || !AppPackageSignature.TryParsePublicKey(publicKey, out byte[]? der))
        {
            return false;
        }

        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(der, out _);
            return key.VerifyData(package, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Reads the private key from a file, or from <see cref="KeyVariable"/> when no file is named.</summary>
    /// <param name="path">PEM file, or <see langword="null"/>.</param>
    /// <returns>The PEM text, or <see langword="null"/> when there is no key at all.</returns>
    public static string? ReadKey(string? path) =>
        !string.IsNullOrWhiteSpace(path) ? File.ReadAllText(Path.GetFullPath(path))
        : Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 0 } pem ? pem
        : null;

    /// <summary>
    /// Writes a new private key file, readable by its owner only; an existing file is never replaced.
    /// </summary>
    /// <param name="path">Destination of the PEM.</param>
    /// <param name="privateKeyPem">The PEM text.</param>
    /// <exception cref="IOException">The file already exists.</exception>
    public static void WriteNewKeyFile(string path, string privateKeyPem)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using StreamWriter writer = new(file);
        writer.Write(privateKeyPem);
    }

    private static ECDsa Import(string privateKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportFromPem(privateKeyPem);
            if (key.KeySize != 256)
            {
                throw new ArgumentException("The signing key must be an ECDSA P-256 key.");
            }

            return key;
        }
        catch (CryptographicException exception)
        {
            key.Dispose();
            throw new ArgumentException($"The signing key is not a PKCS#8 PEM private key: {exception.Message}");
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
