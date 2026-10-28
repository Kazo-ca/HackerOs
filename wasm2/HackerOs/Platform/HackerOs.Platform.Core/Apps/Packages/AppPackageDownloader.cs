namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// Downloads the files of an app package source (ADR 0182 §2): <c>index.json</c> and the
/// <c>.hackapp</c> package, from a host listed in <c>[hosts]</c> of <c>app-sources.config</c>.
/// </summary>
public interface IAppPackageDownloader
{
    /// <summary>Downloads one file.</summary>
    /// <param name="uri">Absolute <c>https</c> address.</param>
    /// <param name="maximumBytes">Largest accepted body; a larger one is refused.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The body, or <see langword="null"/> when the host answers 404.</returns>
    /// <exception cref="AppPackageException"><c>apps.package.download-failed</c> or <c>apps.package.too-large</c>.</exception>
    ValueTask<byte[]?> DownloadAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IAppPackageDownloader"/> over the browser's <c>fetch</c> (an <see cref="HttpClient"/>
/// in WebAssembly). The OS downloads by itself: neither the simulated network nor the proxy is
/// involved, and no app gets network access (ADR 0182 §3).
/// </summary>
/// <param name="http">Client used with absolute addresses only.</param>
public sealed class HttpAppPackageDownloader(HttpClient http) : IAppPackageDownloader
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <inheritdoc />
    public async ValueTask<byte[]?> DownloadAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new AppPackageException("apps.package.download-failed", $"Only https addresses are downloaded, not '{uri}'.");
        }

        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AppPackageException(
                    "apps.package.download-failed", $"{uri} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            if (response.Content.Headers.ContentLength > maximumBytes)
            {
                throw TooLarge(uri, maximumBytes);
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81_920];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maximumBytes)
                {
                    throw TooLarge(uri, maximumBytes);
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (HttpRequestException exception)
        {
            // In a browser, a host without CORS headers fails here too, with no status code.
            throw new AppPackageException("apps.package.download-failed", $"{uri} could not be downloaded ({exception.Message}).");
        }
    }

    private static AppPackageException TooLarge(Uri uri, long maximumBytes) =>
        new("apps.package.too-large", $"{uri} is larger than {maximumBytes} bytes.");
}
