using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Interop;

/// <summary>
/// Provides hardware-accelerated password hashing using the browser's native Web Crypto API (<c>crypto.subtle</c>).
/// </summary>
public sealed class WebCryptoPasswordHasher : IAsyncDisposable
{
    internal const string ModulePath = "./_content/Kazo.Os.Infrastructure.Browser/cryptoHasher.js";

    private readonly IJSRuntime _runtime;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebCryptoPasswordHasher"/> class.
    /// </summary>
    /// <param name="runtime">The JavaScript runtime instance.</param>
    public WebCryptoPasswordHasher(IJSRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <summary>
    /// Derives a PBKDF2-HMAC-SHA256 key using native browser Web Crypto API execution.
    /// </summary>
    /// <param name="password">Plaintext password.</param>
    /// <param name="salt">Salt bytes.</param>
    /// <param name="iterations">Work factor iterations.</param>
    /// <param name="keyLengthBytes">Output key length in bytes.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>Derived key bytes.</returns>
    public async ValueTask<byte[]> DeriveKeyAsync(
        string password,
        byte[] salt,
        int iterations,
        int keyLengthBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(salt);

        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        byte[] derived = await module.InvokeAsync<byte[]>(
            "derivePbkdf2Key",
            cancellationToken,
            password,
            salt,
            iterations,
            keyLengthBytes).ConfigureAwait(false);

        return derived;
    }

    /// <inheritdoc />
    /// <summary>Seals bytes with AES-256-GCM through Web Crypto (ADR 0100, user keyring).</summary>
    /// <param name="key">32-byte key.</param>
    /// <param name="nonce">12-byte nonce.</param>
    /// <param name="plaintext">Bytes to seal.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Ciphertext followed by the 16-byte tag.</returns>
    public async ValueTask<byte[]> EncryptAesGcmAsync(byte[] key, byte[] nonce, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        return await module.InvokeAsync<byte[]>("aesGcmEncrypt", cancellationToken, key, nonce, plaintext).ConfigureAwait(false);
    }

    /// <summary>Opens bytes sealed by <see cref="EncryptAesGcmAsync"/>; <see langword="null"/> on a wrong key or altered data.</summary>
    /// <param name="key">32-byte key.</param>
    /// <param name="nonce">12-byte nonce.</param>
    /// <param name="sealedData">Ciphertext followed by the tag.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async ValueTask<byte[]?> DecryptAesGcmAsync(byte[] key, byte[] nonce, byte[] sealedData, CancellationToken cancellationToken = default)
    {
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        return await module.InvokeAsync<byte[]?>("aesGcmDecrypt", cancellationToken, key, nonce, sealedData).ConfigureAwait(false);
    }

    /// <summary>Disposes the imported crypto module once; later calls do nothing.</summary>
    /// <returns>A task that completes when the module is released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // Browser shutdown can disconnect JS before DI disposal.
            }
        }

        _moduleGate.Dispose();
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_module is not null)
        {
            return _module;
        }

        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _module ??= await _runtime.InvokeAsync<IJSObjectReference>(
                "import", cancellationToken, ModulePath).ConfigureAwait(false);
            return _module;
        }
        finally
        {
            _moduleGate.Release();
        }
    }
}
