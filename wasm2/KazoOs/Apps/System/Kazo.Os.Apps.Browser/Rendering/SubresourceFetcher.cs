using System.Collections.Concurrent;
using Kazo.Os.Apps.Browser.DevTools;
using Kazo.Os.Apps.Browser.Net;

namespace Kazo.Os.Apps.Browser.Rendering;

/// <summary>A sub-resource loaded for a page: its bytes and type, ready to become a <c>data:</c> URL.</summary>
/// <param name="Url">Final URL, after redirects (the base for the URLs inside a style sheet).</param>
/// <param name="ContentType">The response's <c>Content-Type</c>.</param>
/// <param name="Body">The bytes.</param>
public sealed record LoadedResource(Uri Url, string ContentType, byte[] Body)
{
    /// <summary>Gets the <c>type/subtype</c> of the resource.</summary>
    public string MediaType => Kazo.Os.Simulation.Abstractions.Network.SimulatedMediaTypes.GetMediaType(ContentType);

    /// <summary>Decodes the bytes as text with the declared charset (UTF-8 by default).</summary>
    public string ReadAsString() =>
        Kazo.Os.Simulation.Abstractions.Network.SimulatedMediaTypes.GetEncoding(ContentType).GetString(Body);

    /// <summary>Gets the bytes as a <c>data:</c> URL of <paramref name="fallbackMediaType"/> when the server sent no type.</summary>
    public string ToDataUrl(string fallbackMediaType)
    {
        string mediaType = MediaType.Length > 0 && MediaType != "application/octet-stream" ? MediaType : fallbackMediaType;
        return DataUrl.FromBytes(mediaType, Body);
    }
}

/// <summary><c>data:</c> URL helpers.</summary>
public static class DataUrl
{
    /// <summary>A valid, empty <c>data:</c> URL: what a failed sub-resource is replaced with, so the frame never reaches out itself.</summary>
    public const string Empty = "data:,";

    /// <summary>Encodes bytes as a base64 <c>data:</c> URL.</summary>
    public static string FromBytes(string mediaType, byte[] bytes) =>
        $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";

    /// <summary>Encodes text as a UTF-8 base64 <c>data:</c> URL.</summary>
    public static string FromText(string mediaType, string text) =>
        FromBytes($"{mediaType};charset=utf-8", System.Text.Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// Loads the sub-resources of one page through the tab's <see cref="BrowserResourceLoader"/>, the
/// way a browser's preload scanner does: each URL once (a page that names the same image twice
/// costs one request), within a budget of requests and bytes, and never a local file for a web page
/// (ADR 0075 §2 and §4).
/// </summary>
public sealed class SubresourceFetcher
{
    /// <summary>Maximum sub-resource requests per page.</summary>
    public const int MaxRequests = 300;

    /// <summary>Maximum total bytes of sub-resources per page (32 MiB).</summary>
    public const long MaxTotalBytes = 32L * 1024 * 1024;

    private readonly BrowserResourceLoader _loader;
    private readonly Uri _documentUrl;
    private readonly CancellationToken _cancellationToken;
    private readonly ConcurrentDictionary<string, Task<LoadedResource?>> _cache = new(StringComparer.Ordinal);
    private int _requests;
    private long _bytes;

    /// <summary>Initializes a fetcher for the page at <paramref name="documentUrl"/>.</summary>
    public SubresourceFetcher(BrowserResourceLoader loader, Uri documentUrl, CancellationToken cancellationToken)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _documentUrl = documentUrl ?? throw new ArgumentNullException(nameof(documentUrl));
        _cancellationToken = cancellationToken;
    }

    /// <summary>Gets the number of requests sent so far.</summary>
    public int RequestCount => _requests;

    /// <summary>Loads <paramref name="url"/> once; <see langword="null"/> when it failed or was blocked.</summary>
    public Task<LoadedResource?> GetAsync(Uri url, NetworkRequestKind kind)
    {
        ArgumentNullException.ThrowIfNull(url);

        // The fragment never reaches the server: "sprite.svg#a" and "sprite.svg#b" are one request.
        string key = url.GetLeftPart(UriPartial.Query);
        return _cache.GetOrAdd(key, _ => LoadAsync(new Uri(key), kind));
    }

    private async Task<LoadedResource?> LoadAsync(Uri url, NetworkRequestKind kind)
    {
        List<KeyValuePair<string, string>> headers = [new("Accept", AcceptFor(kind))];
        if (_documentUrl.Scheme is "http" or "https")
        {
            headers.Add(new("Referer", _documentUrl.GetLeftPart(UriPartial.Query)));
        }

        BrowserRequest request = new("GET", url, headers);
        if (url.Scheme == "file" && _documentUrl.Scheme != "file")
        {
            _loader.LogBlocked(request, kind, BrowserErrorCodes.BlockedLocalResource,
                $"Not allowed to load local resource: {BrowserUrl.ToDisplayString(url)}");
            return null;
        }

        if (Interlocked.Increment(ref _requests) > MaxRequests || Interlocked.Read(ref _bytes) > MaxTotalBytes)
        {
            _loader.LogBlocked(request, kind, BrowserErrorCodes.BlockedByClient,
                $"Resource skipped: the page exceeds {MaxRequests} sub-resources or {MaxTotalBytes / (1024 * 1024)} MB.");
            return null;
        }

        BrowserFetchResult result = await _loader.SendAsync(request, kind, _cancellationToken).ConfigureAwait(false);
        if (result.Response is not { IsSuccessStatusCode: true } response)
        {
            return null;
        }

        Interlocked.Add(ref _bytes, response.Body.Length);
        return new LoadedResource(response.Url, response.ContentType ?? string.Empty, response.Body);
    }

    private static string AcceptFor(NetworkRequestKind kind) => kind switch
    {
        NetworkRequestKind.Stylesheet => "text/css,*/*;q=0.1",
        NetworkRequestKind.Image => "image/avif,image/webp,image/svg+xml,image/*,*/*;q=0.8",
        NetworkRequestKind.Script => "*/*",
        _ => "*/*",
    };
}
