using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.JSInterop;

namespace HackerOs.Ecosystem.Passkeys;

/// <summary>
/// <see cref="IAppPackageSignatureVerifier"/> on Web Crypto (ADR 0214 §2): .NET in WebAssembly has
/// no ECDSA, so the signature of an app package is checked by the same <c>verify</c> function of
/// <c>webAuthn.js</c> that checks passkey signatures (ES256, IEEE P1363 signature, SPKI key).
/// </summary>
internal sealed class BrowserAppPackageSignatureVerifier(IJSRuntime runtime) : IAppPackageSignatureVerifier, IAsyncDisposable
{
    private const string ModulePath = "./_content/HackerOs.Infrastructure.Browser/webAuthn.js";

    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;

    /// <inheritdoc />
    public async ValueTask<bool> VerifyAsync(byte[] publicKey, byte[] data, byte[] signature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        if (!OperatingSystem.IsBrowser())
        {
            return await ManagedAppPackageSignatureVerifier.Instance.VerifyAsync(publicKey, data, signature, cancellationToken).ConfigureAwait(false);
        }

        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        return await module.InvokeAsync<bool>(
                "verify",
                cancellationToken,
                LocalPasskeyCredential.Es256,
                LocalPasskeyCredential.ToBase64Url(publicKey),
                LocalPasskeyCredential.ToBase64Url(data),
                LocalPasskeyCredential.ToBase64Url(signature))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
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
        if (_module is not null)
        {
            return _module;
        }

        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _module ??= await runtime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).ConfigureAwait(false);
        }
        finally
        {
            _moduleGate.Release();
        }
    }
}
