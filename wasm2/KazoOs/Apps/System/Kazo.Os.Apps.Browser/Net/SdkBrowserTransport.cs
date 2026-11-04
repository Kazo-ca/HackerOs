using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Apps.Browser.Net;

/// <summary>
/// The browser's real transport: <c>http(s)</c> through <see cref="KazoHttpClient"/> (the OS picks
/// the network, ADR 0056 §6), <c>file://</c> through <see cref="KazoFile"/> with the user's rights
/// (ADR 0075 §4). Every request shares the tab's <see cref="KazoCookieContainer"/>.
/// </summary>
public sealed class SdkBrowserTransport(KazoCookieContainer cookies) : IBrowserTransport
{
    // Headers the client or the OS owns: a page cannot forge them, as with fetch's forbidden headers.
    private static readonly HashSet<string> ForbiddenRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Cookie", "Host", "Connection", "Transfer-Encoding",
    };

    private readonly KazoCookieContainer _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));

    /// <inheritdoc />
    public async Task<BrowserFetchResult> SendAsync(BrowserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Url.Scheme switch
        {
            "http" or "https" => await SendHttpAsync(request, cancellationToken).ConfigureAwait(false),
            "file" => await ReadFileAsync(request, cancellationToken).ConfigureAwait(false),
            _ => BrowserFetchResult.Fail(BrowserErrorCodes.UnknownUrlScheme),
        };
    }

    private async Task<BrowserFetchResult> SendHttpAsync(BrowserRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using KazoHttpClient client = new(request.WithCredentials ? _cookies : new KazoCookieContainer());
            foreach ((string name, string value) in request.Headers)
            {
                if (!ForbiddenRequestHeaders.Contains(name))
                {
                    client.DefaultRequestHeaders[name] = value;
                }
            }

            KazoHttpContent? content = request.Body is null
                ? null
                : new KazoByteArrayContent(request.Body, request.ContentType ?? SimulatedMediaTypes.OctetStream);
            KazoHttpResponse response = await client
                .SendAsync(new HttpMethod(request.Method), request.Url.AbsoluteUri, content, cancellationToken)
                .ConfigureAwait(false);

            return BrowserFetchResult.Ok(new BrowserResponse(
                response.StatusCode,
                response.ReasonPhrase,
                response.RequestUri,
                response.Headers,
                response.ContentBytes,
                response.Page,
                response.RedirectCount,
                response.Source switch
                {
                    KazoHttpResponseSource.LocalSite => BrowserResponseSource.LocalSite,
                    KazoHttpResponseSource.RealNetwork => BrowserResponseSource.RealNetwork,
                    _ => BrowserResponseSource.Simulated,
                }));
        }
        catch (KazoHttpRequestException exception)
        {
            return BrowserFetchResult.Fail(exception.ErrorCode);
        }
        catch (UnauthorizedAccessException)
        {
            return BrowserFetchResult.Fail(BrowserErrorCodes.AccessDenied);
        }
        catch (ArgumentException)
        {
            return BrowserFetchResult.Fail(BrowserErrorCodes.InvalidUrl);
        }
    }

    private static async Task<BrowserFetchResult> ReadFileAsync(BrowserRequest request, CancellationToken cancellationToken)
    {
        if (request.Method is not ("GET" or "HEAD"))
        {
            // Chromium refuses anything but reading a local file.
            return BrowserFetchResult.Fail(BrowserErrorCodes.Failed);
        }

        string path = BrowserUrl.ToLocalPath(request.Url);
        try
        {
            KazoDirectoryInfo directory = await KazoDirectoryInfo.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (directory.Exists)
            {
                return BrowserFetchResult.Ok(await ListDirectoryAsync(request.Url, directory, cancellationToken).ConfigureAwait(false));
            }

            byte[] bytes = await KazoFile.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return BrowserFetchResult.Ok(new BrowserResponse(
                200,
                "OK",
                request.Url,
                [new("Content-Type", SimulatedFileTypes.GetContentType(path)), new("Content-Length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                request.Method == "HEAD" ? [] : bytes,
                Source: BrowserResponseSource.File));
        }
        catch (Exception exception) when (exception is KazoFileNotFoundException or KazoDirectoryNotFoundException or FileNotFoundException or DirectoryNotFoundException)
        {
            return BrowserFetchResult.Fail(BrowserErrorCodes.FileNotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return BrowserFetchResult.Fail(BrowserErrorCodes.AccessDenied);
        }
        catch (IOException)
        {
            return BrowserFetchResult.Fail(BrowserErrorCodes.Failed);
        }
    }

    private static async Task<BrowserResponse> ListDirectoryAsync(Uri url, KazoDirectoryInfo directory, CancellationToken cancellationToken)
    {
        // The listing speaks the language of the browser's process (ADR 0104), ambient like KazoFile's rights.
        IAppExecutionContext? process = KazoExecutionContext.CurrentOrNull;
        KazoFileSystemInfo[] entries = await directory.GetFileSystemInfosAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        string html = DirectoryListingPage.Build(
            directory.FullName,
            entries.Select(entry => new DirectoryListingEntry(
                entry.Name,
                entry is KazoDirectoryInfo,
                entry is KazoFileInfo file ? file.Length : null,
                entry.LastWriteTimeUtc)),
            process?.Culture,
            process?.FormatCulture);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(html);

        // Like Chromium, a directory URL ends with "/" so the listing's relative links resolve inside it.
        Uri finalUrl = url.AbsolutePath.EndsWith('/') ? url : BrowserUrl.FromLocalPath(directory.FullName.TrimEnd('/') + "/");
        return new BrowserResponse(200, "OK", finalUrl, [new("Content-Type", "text/html; charset=utf-8")], bytes, Source: BrowserResponseSource.File);
    }
}
