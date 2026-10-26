using HackerOs.Platform.Blazor.LazyLoading;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace HackerOs.Ecosystem;

/// <summary>
/// Fetches the <c>version.json</c> the origin serves now (ADR 0174). The query string keeps the
/// service worker from answering with its cached copy, and <c>no-store</c> skips the HTTP cache.
/// </summary>
internal sealed class BrowserHostReleaseProbe(HttpClient http) : IHostReleaseProbe
{
    /// <inheritdoc />
    public async ValueTask<string?> GetServedVersionDocumentAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"version.json?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (HttpRequestException)
        {
            // Offline or server down: not an update.
            return null;
        }
    }
}
