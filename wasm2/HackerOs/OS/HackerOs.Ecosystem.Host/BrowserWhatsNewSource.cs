using HackerOs.Platform.Core.WhatsNew;

namespace HackerOs.Ecosystem;

/// <summary>
/// Fetches the <c>whats-new.json</c> the build publishes next to the page (ADR 0186 §4.1). Unlike
/// <c>version.json</c>, the service worker's copy is fine: it belongs to the running build, and a
/// new deployment reloads the page.
/// </summary>
internal sealed class BrowserWhatsNewSource(HttpClient http) : IWhatsNewSource
{
    /// <inheritdoc />
    public async ValueTask<string?> GetDocumentAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync("whats-new.json", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (HttpRequestException)
        {
            // Offline with no cached copy, or a host that serves no list: nothing is new.
            return null;
        }
    }
}
