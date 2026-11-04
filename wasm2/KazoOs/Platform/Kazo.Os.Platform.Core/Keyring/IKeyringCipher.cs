using System.Security.Cryptography;

namespace Kazo.Os.Platform.Core.Keyring;

/// <summary>
/// The cryptography behind the user keyring (ADR 0100 section 2): PBKDF2-SHA256 to derive the key
/// from the login password, AES-256-GCM to seal the entries. The browser hosts use Web Crypto
/// (<c>WebCryptoKeyringCipher</c>); <see cref="ManagedKeyringCipher"/> serves tests and hosts where
/// .NET cryptography runs natively.
/// </summary>
/// <remarks>Both implementations lay out a sealed blob as ciphertext followed by the 16-byte tag, as Web Crypto does.</remarks>
public interface IKeyringCipher
{
    /// <summary>Derives a 32-byte AES key from a password.</summary>
    /// <param name="password">Login password; never empty.</param>
    /// <param name="salt">Keyring salt.</param>
    /// <param name="iterations">PBKDF2 iteration count.</param>
    /// <param name="cancellationToken">Cancels the derivation.</param>
    ValueTask<byte[]> DeriveKeyAsync(string password, byte[] salt, int iterations, CancellationToken cancellationToken = default);

    /// <summary>Seals <paramref name="plaintext"/>: returns ciphertext then tag.</summary>
    /// <param name="key">32-byte key.</param>
    /// <param name="nonce">12-byte nonce, never reused with the same key.</param>
    /// <param name="plaintext">Bytes to seal.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<byte[]> EncryptAsync(byte[] key, byte[] nonce, byte[] plaintext, CancellationToken cancellationToken = default);

    /// <summary>Opens a sealed blob, or returns <see langword="null"/> when the key is wrong or the blob was altered.</summary>
    /// <param name="key">32-byte key.</param>
    /// <param name="nonce">12-byte nonce used to seal.</param>
    /// <param name="sealedData">Ciphertext then tag.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<byte[]?> DecryptAsync(byte[] key, byte[] nonce, byte[] sealedData, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IKeyringCipher"/> on .NET's own <see cref="AesGcm"/> and <see cref="Rfc2898DeriveBytes"/>,
/// for tests and server-side hosts. In the browser, .NET has no AES-GCM: use the Web Crypto cipher.
/// </summary>
public sealed class ManagedKeyringCipher : IKeyringCipher
{
    /// <summary>Length of the GCM authentication tag appended to the ciphertext.</summary>
    public const int TagLength = 16;

    /// <inheritdoc />
    public ValueTask<byte[]> DeriveKeyAsync(string password, byte[] salt, int iterations, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(salt);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32));
    }

    /// <inheritdoc />
    public ValueTask<byte[]> EncryptAsync(byte[] key, byte[] nonce, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] sealedData = new byte[plaintext.Length + TagLength];
        using AesGcm aes = new(key, TagLength);
        aes.Encrypt(nonce, plaintext, sealedData.AsSpan(0, plaintext.Length), sealedData.AsSpan(plaintext.Length));
        return ValueTask.FromResult(sealedData);
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> DecryptAsync(byte[] key, byte[] nonce, byte[] sealedData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sealedData);
        cancellationToken.ThrowIfCancellationRequested();
        if (sealedData.Length < TagLength)
        {
            return ValueTask.FromResult<byte[]?>(null);
        }

        byte[] plaintext = new byte[sealedData.Length - TagLength];
        try
        {
            using AesGcm aes = new(key, TagLength);
            aes.Decrypt(nonce, sealedData.AsSpan(0, plaintext.Length), sealedData.AsSpan(plaintext.Length), plaintext);
            return ValueTask.FromResult<byte[]?>(plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            return ValueTask.FromResult<byte[]?>(null);
        }
    }
}
