using HackerOs.Simulation.Abstractions.Network;

namespace HackerOs.Apps.Browser.Net;

/// <summary>
/// One request the browser sends: the page itself, a sub-resource, or a <c>fetch</c>/XHR relayed
/// by the page bridge (ADR 0075 §2 and §3).
/// </summary>
/// <param name="Method">HTTP method, upper case.</param>
/// <param name="Url">Absolute <c>http</c>, <c>https</c> or <c>file</c> URL.</param>
/// <param name="Headers">Request headers set by the page or the browser (<c>Accept</c>, <c>Origin</c>…).</param>
/// <param name="Body">Body bytes, or <see langword="null"/> for none.</param>
/// <param name="ContentType">The body's <c>Content-Type</c>, or <see langword="null"/>.</param>
/// <param name="WithCredentials">
/// Whether the tab's cookies are sent and stored, as fetch's credentials mode decides; a
/// cross-origin <c>fetch</c> without <c>credentials: "include"</c> goes without them.
/// </param>
public sealed record BrowserRequest(
    string Method,
    Uri Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[]? Body = null,
    string? ContentType = null,
    bool WithCredentials = true)
{
    /// <summary>Creates a <c>GET</c> request with no header.</summary>
    public static BrowserRequest Get(Uri url) => new("GET", url, []);
}

/// <summary>A response the browser received, from the network or from a local file.</summary>
/// <param name="StatusCode">Status code; <c>200</c> for a file that was read.</param>
/// <param name="ReasonPhrase">Reason phrase.</param>
/// <param name="Url">Final URL, after redirects.</param>
/// <param name="Headers">Response headers.</param>
/// <param name="Body">Body bytes.</param>
/// <param name="Page">Structured page of a historical simulated site, or <see langword="null"/>.</param>
/// <param name="RedirectCount">Redirects followed to reach <paramref name="Url"/>.</param>
/// <param name="Source">Where the response came from, shown in the address bar (ADR 0210 §4).</param>
public sealed record BrowserResponse(
    int StatusCode,
    string ReasonPhrase,
    Uri Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    SimulatedPage? Page = null,
    int RedirectCount = 0,
    BrowserResponseSource Source = BrowserResponseSource.Simulated)
{
    /// <summary>Gets the <c>Content-Type</c> header, or <see langword="null"/>.</summary>
    public string? ContentType => GetHeader("Content-Type");

    /// <summary>Gets the <c>type/subtype</c> of <see cref="ContentType"/>, lower case.</summary>
    public string MediaType => SimulatedMediaTypes.GetMediaType(ContentType);

    /// <summary>Gets whether the status is in the 200–299 range.</summary>
    public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;

    /// <summary>Gets the first header named <paramref name="name"/>, ignoring case.</summary>
    public string? GetHeader(string name) =>
        Headers.FirstOrDefault(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Decodes the body with the charset of <see cref="ContentType"/> (UTF-8 by default).</summary>
    public string ReadAsString() => SimulatedMediaTypes.GetEncoding(ContentType).GetString(Body);
}

/// <summary>Where a page came from (ADR 0210 §4), shown by the icon at the start of the address bar.</summary>
public enum BrowserResponseSource
{
    /// <summary>A machine of the simulated network.</summary>
    Simulated,

    /// <summary>A site of this machine (<c>.hack</c>, <c>localhost</c>).</summary>
    LocalSite,

    /// <summary>A local file (<c>file://</c>).</summary>
    File,

    /// <summary>The real Internet, through the HackerOS server's relay.</summary>
    RealNetwork,
}

/// <summary>
/// The outcome of one request: a response, or the Chromium-style <c>net::ERR_*</c> code of a
/// request that got none (name not resolved, connection refused, file not found…).
/// </summary>
/// <param name="Response">The response, or <see langword="null"/> on a network error.</param>
/// <param name="ErrorCode">The <c>ERR_*</c> code, or <see langword="null"/> when a response came back.</param>
public sealed record BrowserFetchResult(BrowserResponse? Response, string? ErrorCode)
{
    /// <summary>Creates a successful result.</summary>
    public static BrowserFetchResult Ok(BrowserResponse response) => new(response, null);

    /// <summary>Creates a network error result.</summary>
    public static BrowserFetchResult Fail(string errorCode) => new(null, errorCode);
}

/// <summary>
/// How the browser reaches content: the simulated network through the SDK for <c>http(s)</c>, the
/// OS file system for <c>file://</c>. An interface so the page composer and the bridge are tested
/// without an OS (ADR 0075).
/// </summary>
public interface IBrowserTransport
{
    /// <summary>Sends <paramref name="request"/>, following redirects, and never throws for a network error.</summary>
    Task<BrowserFetchResult> SendAsync(BrowserRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Chromium's <c>net::ERR_*</c> codes the browser produces itself.</summary>
public static class BrowserErrorCodes
{
    /// <summary>A <c>file://</c> path does not exist.</summary>
    public const string FileNotFound = "ERR_FILE_NOT_FOUND";

    /// <summary>The user may not read a <c>file://</c> path.</summary>
    public const string AccessDenied = "ERR_ACCESS_DENIED";

    /// <summary>The address is not a URL the browser can load.</summary>
    public const string InvalidUrl = "ERR_INVALID_URL";

    /// <summary>A scheme the browser does not load (<c>ftp:</c>, <c>javascript:</c>…).</summary>
    public const string UnknownUrlScheme = "ERR_UNKNOWN_URL_SCHEME";

    /// <summary>A web page asked for a <c>file://</c> resource, which browsers refuse.</summary>
    public const string BlockedLocalResource = "ERR_BLOCKED_LOCAL_RESOURCE";

    /// <summary>A request the browser blocked (CORS, Content Security Policy…).</summary>
    public const string BlockedByClient = "ERR_BLOCKED_BY_CLIENT";

    /// <summary>Generic failure.</summary>
    public const string Failed = "ERR_FAILED";
}
