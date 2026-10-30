using System.Runtime.Versioning;
using HackerOs.Infrastructure.Browser.Apps;

namespace HackerOs.Ecosystem;

/// <summary>
/// The browser's <see cref="IInstalledAppAssetCache"/> (ADR 0206): Cache Storage, served by the
/// host's service worker, through <see cref="BrowserInstalledAppAssets"/>.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserInstalledAppAssetCache : IInstalledAppAssetCache
{
    /// <summary>How long a launch waits for a newly registered service worker to take control.</summary>
    public static readonly TimeSpan ServiceWorkerTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async ValueTask<bool> PrepareAsync(CancellationToken cancellationToken) =>
        await BrowserInstalledAppAssets.PrepareAsync(ServiceWorkerTimeout).WaitAsync(cancellationToken).ConfigureAwait(true);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) =>
        await BrowserInstalledAppAssets.ListAsync().WaitAsync(cancellationToken).ConfigureAwait(true);

    /// <inheritdoc />
    public async ValueTask PutAsync(string url, byte[] bytes, string contentType, CancellationToken cancellationToken) =>
        await BrowserInstalledAppAssets.PutAsync(url, bytes, contentType).WaitAsync(cancellationToken).ConfigureAwait(true);

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string url, CancellationToken cancellationToken) =>
        await BrowserInstalledAppAssets.RemoveAsync(url).WaitAsync(cancellationToken).ConfigureAwait(true);
}
