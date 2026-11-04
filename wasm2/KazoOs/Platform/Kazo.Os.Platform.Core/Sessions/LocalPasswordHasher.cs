using System.Security.Cryptography;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Sessions;

/// <summary>
/// Delegate used to execute asynchronous key derivation (e.g. Web Crypto API native interop).
/// </summary>
/// <param name="password">Plaintext password.</param>
/// <param name="salt">Salt byte array.</param>
/// <param name="iterations">PBKDF2 iteration count.</param>
/// <param name="lengthBytes">Key length in bytes.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Derived key verifier bytes.</returns>
public delegate ValueTask<byte[]> KeyDerivationAsyncDelegate(
    string password,
    byte[] salt,
    int iterations,
    int lengthBytes,
    CancellationToken cancellationToken);

/// <summary>
/// Creates and verifies <see cref="LocalPasswordCredential"/> records using PBKDF2-HMAC-SHA256,
/// per ADR 0013. Comparison is constant-time to avoid timing side channels.
/// Hardware acceleration (Web Crypto API) is supported asynchronously with automatic C# fallback.
/// </summary>
public static class LocalPasswordHasher
{
    /// <summary>Identifier for the key-derivation function and parameters produced by this version.</summary>
    public const string KdfIdentifier = "pbkdf2-sha256-v1";

    private const int SaltLengthBytes = 16;
    private const int VerifierLengthBytes = 32;
    private const int DefaultIterations = 210_000;

    /// <summary>Creates a new salted credential for a plaintext password synchronously.</summary>
    /// <param name="password">Plaintext password; never stored.</param>
    /// <param name="iterations">PBKDF2 work factor; higher is slower but more resistant to brute force.</param>
    /// <exception cref="ArgumentException"><paramref name="password"/> is empty.</exception>
    public static LocalPasswordCredential Create(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLengthBytes);
        byte[] verifier = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, VerifierLengthBytes);

        return new LocalPasswordCredential(KdfIdentifier, salt, iterations, verifier);
    }

    /// <summary>
    /// Creates a new salted credential for a plaintext password asynchronously, leveraging native Web Crypto API if available.
    /// </summary>
    /// <param name="password">Plaintext password; never stored.</param>
    /// <param name="iterations">PBKDF2 work factor.</param>
    /// <param name="asyncHasher">Optional asynchronous key derivation delegate (e.g. Web Crypto API interop).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new <see cref="LocalPasswordCredential"/>.</returns>
    public static async ValueTask<LocalPasswordCredential> CreateAsync(
        string password,
        int iterations = DefaultIterations,
        KeyDerivationAsyncDelegate? asyncHasher = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLengthBytes);
        byte[] verifier;

        if (asyncHasher is not null)
        {
            try
            {
                verifier = await asyncHasher(
                    password, salt, iterations, VerifierLengthBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Graceful fallback to C# managed PBKDF2 if Web Crypto interop is unavailable or fails.
                verifier = Rfc2898DeriveBytes.Pbkdf2(
                    password, salt, iterations, HashAlgorithmName.SHA256, VerifierLengthBytes);
            }
        }
        else
        {
            verifier = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA256, VerifierLengthBytes);
        }

        return new LocalPasswordCredential(KdfIdentifier, salt, iterations, verifier);
    }

    /// <summary>Verifies a plaintext password against a stored credential in constant time synchronously.</summary>
    /// <param name="password">Plaintext password to verify.</param>
    /// <param name="credential">Stored credential to verify against.</param>
    /// <returns><see langword="true"/> if the password matches; <see langword="false"/> for a mismatch or an unrecognized KDF version.</returns>
    public static bool Verify(string password, LocalPasswordCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        if (credential.KdfIdentifier != KdfIdentifier)
        {
            // Fail closed: never attempt to verify against a KDF version we no longer trust.
            return false;
        }

        byte[] computed = Rfc2898DeriveBytes.Pbkdf2(
            password, credential.Salt, credential.Iterations, HashAlgorithmName.SHA256, credential.Verifier.Length);

        return CryptographicOperations.FixedTimeEquals(computed, credential.Verifier);
    }

    /// <summary>
    /// Verifies a plaintext password against a stored credential in constant time asynchronously, using Web Crypto API hardware acceleration if available.
    /// </summary>
    /// <param name="password">Plaintext password to verify.</param>
    /// <param name="credential">Stored credential to verify against.</param>
    /// <param name="asyncHasher">Optional asynchronous key derivation delegate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if the password matches; <see langword="false"/> otherwise.</returns>
    public static async ValueTask<bool> VerifyAsync(
        string password,
        LocalPasswordCredential credential,
        KeyDerivationAsyncDelegate? asyncHasher = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        if (credential.KdfIdentifier != KdfIdentifier)
        {
            return false;
        }

        byte[] computed;
        if (asyncHasher is not null)
        {
            try
            {
                computed = await asyncHasher(
                    password, credential.Salt, credential.Iterations, credential.Verifier.Length, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Graceful fallback to C# managed PBKDF2 if Web Crypto interop fails.
                computed = Rfc2898DeriveBytes.Pbkdf2(
                    password, credential.Salt, credential.Iterations, HashAlgorithmName.SHA256, credential.Verifier.Length);
            }
        }
        else
        {
            computed = Rfc2898DeriveBytes.Pbkdf2(
                password, credential.Salt, credential.Iterations, HashAlgorithmName.SHA256, credential.Verifier.Length);
        }

        return CryptographicOperations.FixedTimeEquals(computed, credential.Verifier);
    }
}

