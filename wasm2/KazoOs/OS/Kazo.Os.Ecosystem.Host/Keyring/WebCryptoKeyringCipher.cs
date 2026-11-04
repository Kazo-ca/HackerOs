using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Platform.Core.Keyring;

namespace Kazo.Os.Ecosystem.Keyring;

/// <summary>
/// <see cref="IKeyringCipher"/> on the browser's Web Crypto (ADR 0100 section 2): .NET in
/// WebAssembly has no AES-GCM, so PBKDF2 and AES-256-GCM go through <c>cryptoHasher.js</c>,
/// the module that already derives the password hashes.
/// </summary>
internal sealed class WebCryptoKeyringCipher(WebCryptoPasswordHasher crypto) : IKeyringCipher
{
    /// <inheritdoc />
    public ValueTask<byte[]> DeriveKeyAsync(string password, byte[] salt, int iterations, CancellationToken cancellationToken = default) =>
        crypto.DeriveKeyAsync(password, salt, iterations, 32, cancellationToken);

    /// <inheritdoc />
    public ValueTask<byte[]> EncryptAsync(byte[] key, byte[] nonce, byte[] plaintext, CancellationToken cancellationToken = default) =>
        crypto.EncryptAesGcmAsync(key, nonce, plaintext, cancellationToken);

    /// <inheritdoc />
    public ValueTask<byte[]?> DecryptAsync(byte[] key, byte[] nonce, byte[] sealedData, CancellationToken cancellationToken = default) =>
        crypto.DecryptAesGcmAsync(key, nonce, sealedData, cancellationToken);
}
