using Kazo.Os.Apps.Browser.DevTools;
using Kazo.Os.Apps.Browser.Net;
using Kazo.Os.Apps.Browser.Rendering;
using Kazo.Os.AppSdk.Net;

namespace Kazo.Os.Apps.Browser.Bridge;

/// <summary>
/// One document shown in the frame: its URL, the token its bridge uses, and the sub-resource
/// cache shared by the composer and the resources the page adds later.
/// </summary>
public sealed class PageSession
{
    /// <summary>Initializes a session for the document at <paramref name="documentUrl"/>.</summary>
    public PageSession(Uri documentUrl, string token, BrowserResourceLoader loader, CancellationToken cancellationToken)
    {
        DocumentUrl = documentUrl ?? throw new ArgumentNullException(nameof(documentUrl));
        Token = token ?? throw new ArgumentNullException(nameof(token));
        CancellationToken = cancellationToken;
        Fetcher = new SubresourceFetcher(loader, documentUrl, cancellationToken);
        Css = new CssResourceRewriter(Fetcher);
    }

    /// <summary>Gets the document's URL (its origin decides CORS and cookies).</summary>
    public Uri DocumentUrl { get; }

    /// <summary>Gets the token the document's bridge sends with each message.</summary>
    public string Token { get; }

    /// <summary>Gets the token cancelled when the tab navigates away.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Gets the sub-resource loader of the document.</summary>
    public SubresourceFetcher Fetcher { get; }

    /// <summary>Gets the style sheet rewriter of the document.</summary>
    public CssResourceRewriter Css { get; }

    /// <summary>Gets the key of the document's Web Storage (<see cref="BrowserUrl.GetStorageKey"/>).</summary>
    public string StorageKey => BrowserUrl.GetStorageKey(DocumentUrl);
}

/// <summary>
/// Answers the page bridge (ADR 0075 §3) like a browser's network and cookie layers would:
/// <c>fetch</c>/<c>XMLHttpRequest</c> go through the simulated network with the fetch rules
/// (credentials mode, CORS check, opaque <c>no-cors</c> responses, no <c>file://</c>), resources
/// added by script are loaded and inlined, and <c>document.cookie</c> reads and writes the tab's
/// <see cref="KazoCookieContainer"/>.
/// </summary>
public sealed class PageBridgeHandler
{
    // Response headers a cross-origin page may read without Access-Control-Expose-Headers (Fetch standard).
    private static readonly HashSet<string> SafelistedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache-Control", "Content-Language", "Content-Length", "Content-Type", "Expires", "Last-Modified", "Pragma",
    };

    private readonly BrowserResourceLoader _loader;
    private readonly KazoCookieContainer _cookies;

    /// <summary>Initializes the handler of one tab.</summary>
    public PageBridgeHandler(BrowserResourceLoader loader, KazoCookieContainer cookies)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
    }

    /// <summary>Gets the value of <c>document.cookie</c> for a page: empty for <c>file://</c>.</summary>
    public string GetDocumentCookies(Uri documentUrl)
    {
        ArgumentNullException.ThrowIfNull(documentUrl);
        return documentUrl.Scheme is "http" or "https"
            ? string.Join("; ", _cookies.GetCookies(documentUrl).Select(cookie => $"{cookie.Name}={cookie.Value}"))
            : string.Empty;
    }

    /// <summary>
    /// Applies <c>document.cookie = assignment</c>: <c>name=value</c> stores the cookie for the page's
    /// host, <c>Max-Age=0</c> or a past <c>Expires</c> removes it. Other attributes are accepted and
    /// ignored (cookies live as long as the tab, ADR 0056 §6).
    /// </summary>
    /// <returns>The new value of <c>document.cookie</c>.</returns>
    public string ApplyDocumentCookie(Uri documentUrl, string assignment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(documentUrl);
        if (documentUrl.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(assignment))
        {
            return GetDocumentCookies(documentUrl);
        }

        string[] parts = assignment.Split(';');
        int equals = parts[0].IndexOf('=');
        string name = (equals < 0 ? string.Empty : parts[0][..equals]).Trim();
        string value = (equals < 0 ? parts[0] : parts[0][(equals + 1)..]).Trim();
        bool expired = false;
        foreach (string attribute in parts.Skip(1))
        {
            int at = attribute.IndexOf('=');
            string key = (at < 0 ? attribute : attribute[..at]).Trim();
            string raw = at < 0 ? string.Empty : attribute[(at + 1)..].Trim();
            if (key.Equals("Max-Age", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int maxAge)
                && maxAge <= 0)
            {
                expired = true;
            }
            else if (key.Equals("Expires", StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset expires)
                && expires <= now)
            {
                expired = true;
            }
        }

        string host = documentUrl.Host.ToLowerInvariant();
        if (expired)
        {
            _cookies.Remove(host, name);
        }
        else
        {
            _cookies.Add(new KazoCookie(host, name, value));
        }

        return GetDocumentCookies(documentUrl);
    }

    /// <summary>Carries out a <c>fetch()</c> or <c>XMLHttpRequest</c> of the page.</summary>
    public async Task<BridgeFetchReply> FetchAsync(PageSession session, BridgeFetchRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        Uri document = session.DocumentUrl;
        NetworkRequestKind kind = request.Kind == "xhr" ? NetworkRequestKind.Xhr : NetworkRequestKind.Fetch;
        string api = kind == NetworkRequestKind.Xhr ? "XMLHttpRequest" : "Fetch API";

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? target) || target.Scheme is not ("http" or "https" or "file"))
        {
            return Refuse($"{api} cannot load {request.Url}. URL scheme is not supported.");
        }

        if (target.Scheme == "file")
        {
            // Chromium refuses fetch and XHR on file: URLs, even from a file:// page.
            BrowserRequest blocked = new(request.Method, target, []);
            _loader.LogBlocked(blocked, kind, BrowserErrorCodes.BlockedLocalResource,
                $"{api} cannot load {BrowserUrl.ToDisplayString(target)}. URL scheme \"file\" is not supported.");
            return BridgeFetchReply.Fail("Failed to fetch", GetDocumentCookies(document));
        }

        string origin = BrowserUrl.GetOrigin(document);
        bool sameOrigin = BrowserUrl.IsSameOrigin(document, target);
        string mode = request.Mode ?? "cors";
        if (mode == "same-origin" && !sameOrigin)
        {
            return Refuse($"Fetch API cannot load {target.AbsoluteUri}. Request mode is \"same-origin\" but the URL's origin is not same as the request origin {origin}.");
        }

        string credentials = request.Credentials ?? "same-origin";
        bool withCredentials = credentials == "include" || (credentials == "same-origin" && sameOrigin);

        List<KeyValuePair<string, string>> headers = [];
        string? contentType = null;
        foreach (string[] header in request.Headers ?? [])
        {
            if (header.Length != 2)
            {
                continue;
            }

            if (header[0].Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = header[1];
            }
            else
            {
                headers.Add(new(header[0], header[1]));
            }
        }

        if (!sameOrigin || request.Method is not ("GET" or "HEAD"))
        {
            headers.Add(new("Origin", origin));
        }

        if (document.Scheme is "http" or "https")
        {
            headers.Add(new("Referer", document.GetLeftPart(UriPartial.Query)));
        }

        byte[]? body = request.Body is { Length: > 0 } encoded ? Convert.FromBase64String(encoded) : null;
        BrowserRequest outgoing = new(request.Method, target, headers, body, body is null ? null : contentType ?? "text/plain;charset=UTF-8", withCredentials);
        BrowserFetchResult result = await _loader.SendAsync(outgoing, kind, session.CancellationToken).ConfigureAwait(false);
        string cookies = GetDocumentCookies(document);
        if (result.Response is not { } response)
        {
            return BridgeFetchReply.Fail("Failed to fetch", cookies);
        }

        if (!sameOrigin && mode == "no-cors")
        {
            return new BridgeFetchReply(false, null, 0, null, null, false, "opaque", true, null, null, cookies);
        }

        if (!sameOrigin && !CorsAllows(response, origin, credentials == "include", out string? reason))
        {
            _loader.Log.Add(new ConsoleLogEntry(ConsoleLevel.Error,
                $"Access to {(kind == NetworkRequestKind.Xhr ? "XMLHttpRequest" : "fetch")} at '{target.AbsoluteUri}' from origin '{origin}' has been blocked by CORS policy: {reason}",
                BrowserUrl.ToDisplayString(document)));
            return BridgeFetchReply.Fail("Failed to fetch", cookies);
        }

        return new BridgeFetchReply(
            false,
            null,
            response.StatusCode,
            response.ReasonPhrase,
            response.Url.AbsoluteUri,
            response.RedirectCount > 0,
            sameOrigin ? "basic" : "cors",
            false,
            [.. ExposedHeaders(response, sameOrigin).Select(header => new[] { header.Key, header.Value })],
            Convert.ToBase64String(response.Body),
            cookies);

        BridgeFetchReply Refuse(string message)
        {
            _loader.Log.Add(new ConsoleLogEntry(ConsoleLevel.Error, message, BrowserUrl.ToDisplayString(document)));
            return BridgeFetchReply.Fail("Failed to fetch", GetDocumentCookies(document));
        }
    }

    /// <summary>Loads a resource the page's script added after the load, as a <c>data:</c> URL.</summary>
    public async Task<BridgeResourceReply> LoadResourceAsync(PageSession session, BridgeResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? url) || BrowserUrl.Resolve(session.DocumentUrl, url.AbsoluteUri) is null)
        {
            return new BridgeResourceReply(DataUrl.Empty);
        }

        NetworkRequestKind kind = request.Kind switch
        {
            "script" => NetworkRequestKind.Script,
            "stylesheet" => NetworkRequestKind.Stylesheet,
            "media" => NetworkRequestKind.Media,
            _ => NetworkRequestKind.Image,
        };

        LoadedResource? resource = await session.Fetcher.GetAsync(url, kind).ConfigureAwait(false);
        if (resource is null)
        {
            return new BridgeResourceReply(DataUrl.Empty);
        }

        return kind switch
        {
            NetworkRequestKind.Stylesheet => new BridgeResourceReply(DataUrl.FromText("text/css",
                await session.Css.RewriteAsync(resource.ReadAsString(), resource.Url).ConfigureAwait(false))),
            NetworkRequestKind.Script => new BridgeResourceReply(DataUrl.FromText("text/javascript", resource.ReadAsString())),
            _ => new BridgeResourceReply(resource.ToDataUrl(Simulation.Abstractions.Network.SimulatedMediaTypes.GetMediaType(
                Simulation.Abstractions.Network.SimulatedFileTypes.GetContentType(url.AbsolutePath)))),
        };
    }

    /// <summary>
    /// The CORS check of a cross-origin response (Fetch standard): <c>Access-Control-Allow-Origin</c>
    /// must be <c>*</c> or the page's origin, and with credentials it must be the exact origin plus
    /// <c>Access-Control-Allow-Credentials: true</c>. Preflight requests are not simulated.
    /// </summary>
    public static bool CorsAllows(BrowserResponse response, string origin, bool withCredentials, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(response);
        string? allowOrigin = response.GetHeader("Access-Control-Allow-Origin")?.Trim();
        if (string.IsNullOrEmpty(allowOrigin))
        {
            reason = "No 'Access-Control-Allow-Origin' header is present on the requested resource.";
            return false;
        }

        if (allowOrigin == "*")
        {
            if (withCredentials)
            {
                reason = "The value of the 'Access-Control-Allow-Origin' header in the response must not be the wildcard '*' when the request's credentials mode is 'include'.";
                return false;
            }

            reason = null;
            return true;
        }

        if (!string.Equals(allowOrigin, origin, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"The 'Access-Control-Allow-Origin' header has a value '{allowOrigin}' that is not equal to the supplied origin.";
            return false;
        }

        if (withCredentials && !string.Equals(response.GetHeader("Access-Control-Allow-Credentials")?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            reason = "The value of the 'Access-Control-Allow-Credentials' header in the response is '' which must be 'true' when the request's credentials mode is 'include'.";
            return false;
        }

        reason = null;
        return true;
    }

    private static IEnumerable<KeyValuePair<string, string>> ExposedHeaders(BrowserResponse response, bool sameOrigin)
    {
        HashSet<string> exposed = new(SafelistedResponseHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (string name in (response.GetHeader("Access-Control-Expose-Headers") ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            exposed.Add(name);
        }

        return response.Headers.Where(header =>
            !header.Key.StartsWith("Set-Cookie", StringComparison.OrdinalIgnoreCase)
            && (sameOrigin || exposed.Contains(header.Key) || exposed.Contains("*")));
    }
}
