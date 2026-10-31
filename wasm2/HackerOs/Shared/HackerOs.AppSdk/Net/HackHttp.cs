using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HackerOs.Simulation.Abstractions.Network;

namespace HackerOs.AppSdk.Net;

/// <summary>A cookie of a <see cref="HackCookieContainer"/>.</summary>
/// <param name="Domain">Host the cookie belongs to, e.g. <c>hackmail.com</c>.</param>
/// <param name="Name">Cookie name.</param>
/// <param name="Value">Cookie value.</param>
public sealed record HackCookie(string Domain, string Name, string Value);

/// <summary>One HTTP request as the gateway receives it; apps build it through <see cref="HackHttpClient"/>.</summary>
/// <param name="Method">HTTP method, upper case (<c>GET</c>, <c>HEAD</c>, <c>POST</c>…).</param>
/// <param name="RequestUri">Absolute <c>http</c> or <c>https</c> URI.</param>
/// <param name="Headers">Request headers; <c>Content-Type</c> and <c>Content-Length</c> come from <paramref name="Content"/>.</param>
/// <param name="Content">The body (ADR 0074, section 1): a form, text, JSON or bytes; <see langword="null"/> for none.</param>
/// <param name="Cookies">Every cookie of the caller's container; the OS sends those of the target host.</param>
public sealed record HackHttpRequest(
    string Method,
    Uri RequestUri,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    HackHttpContent? Content,
    IReadOnlyList<HackCookie> Cookies);

/// <summary>
/// An HTTP response, the parts of <see cref="HttpResponseMessage"/> HackerOS apps use, plus the
/// structured page a historical simulated site returns.
/// </summary>
/// <remarks>
/// ADR 0074, section 1: <see cref="Content"/> gives the body as text (decoded with the charset of
/// <see cref="ContentType"/>), <see cref="ContentBytes"/> the raw bytes.
/// </remarks>
/// <param name="StatusCode">Status code, e.g. 200.</param>
/// <param name="ReasonPhrase">Reason phrase, e.g. <c>OK</c>.</param>
/// <param name="RequestUri">Final URI, after the redirects.</param>
/// <param name="Headers">Response headers.</param>
/// <param name="ContentBytes">Body bytes; empty for <c>HEAD</c>. A simulated page's body is its text rendering.</param>
/// <param name="Page">Structured page of a simulated site, which the browser renders; <see langword="null"/> otherwise.</param>
/// <param name="RedirectCount">Number of redirects followed.</param>
/// <param name="SetCookies">Cookies the exchange set, already stored in the client's container.</param>
public sealed record HackHttpResponse(
    int StatusCode,
    string ReasonPhrase,
    Uri RequestUri,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] ContentBytes,
    SimulatedPage? Page,
    int RedirectCount,
    IReadOnlyList<HackCookie> SetCookies)
{
    /// <summary>
    /// Gets where the final response came from (ADR 0210 §4): the simulated network, a site of this
    /// machine, or the real Internet through the relay. The browser shows it in its address bar; it
    /// never names the server nor carries its token (ADR 0056 §6).
    /// </summary>
    public HackHttpResponseSource Source { get; init; }

    /// <summary>Gets whether <see cref="StatusCode"/> is in the 200–299 range.</summary>
    public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;

    /// <summary>Gets the <c>Content-Type</c> header, or <see langword="null"/> when the response has none.</summary>
    public string? ContentType => GetHeader("Content-Type");

    /// <summary>Gets the <c>type/subtype</c> part of <see cref="ContentType"/>, lower case; empty when there is none.</summary>
    public string MediaType => SimulatedMediaTypes.GetMediaType(ContentType);

    /// <summary>Gets the body as text, decoded with the charset of <see cref="ContentType"/> (UTF-8 when none).</summary>
    public string Content => ReadAsString();

    /// <summary>Decodes <see cref="ContentBytes"/> with the charset of <see cref="ContentType"/> (UTF-8 when none).</summary>
    public string ReadAsString() => SimulatedMediaTypes.GetEncoding(ContentType).GetString(ContentBytes);

    /// <summary>Gets the first value of a header, by case-insensitive name, or <see langword="null"/>.</summary>
    public string? GetHeader(string name) =>
        Headers.FirstOrDefault(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found
            ? found.Value
            : null;

    /// <summary>Deserializes the JSON body with source-generated metadata (trim-safe).</summary>
    /// <exception cref="JsonException">The body is not valid JSON for <typeparamref name="T"/>.</exception>
    public T? ReadFromJson<T>(JsonTypeInfo<T> jsonTypeInfo) => JsonSerializer.Deserialize(ContentBytes, jsonTypeInfo);

    /// <summary>Deserializes the JSON body by reflection, with the web defaults unless <paramref name="options"/> says otherwise.</summary>
    /// <exception cref="JsonException">The body is not valid JSON for <typeparamref name="T"/>.</exception>
    [RequiresUnreferencedCode("JSON deserialization by reflection may need types the trimmer removes; use the JsonTypeInfo overload.")]
    [RequiresDynamicCode("JSON deserialization by reflection may need runtime code generation; use the JsonTypeInfo overload.")]
    public T? ReadFromJson<T>(JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(ContentBytes, options ?? HackJson.WebOptions);

    /// <summary>Throws when <see cref="IsSuccessStatusCode"/> is <see langword="false"/>, like <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>.</summary>
    /// <exception cref="HttpRequestException">The status code is not a success.</exception>
    public HackHttpResponse EnsureSuccessStatusCode() =>
        IsSuccessStatusCode
            ? this
            : throw new HttpRequestException(
                $"Response status code does not indicate success: {StatusCode} ({ReasonPhrase}).",
                null,
                (System.Net.HttpStatusCode)StatusCode);

    /// <summary>Gets the standard reason phrase of a status code.</summary>
    public static string GetReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        304 => "Not Modified",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        415 => "Unsupported Media Type",
        422 => "Unprocessable Content",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => string.Empty
    };
}

/// <summary>Where a <see cref="HackHttpResponse"/> came from (ADR 0210 §4).</summary>
public enum HackHttpResponseSource
{
    /// <summary>A machine of the simulated network (<c>hackdns</c>, simulated sites).</summary>
    Simulated,

    /// <summary>A site served by a process of this machine (a <c>.hack</c> site, <c>localhost</c>, ADR 0074).</summary>
    LocalSite,

    /// <summary>The real Internet, relayed by the optional HackerOS server (ADR 0102, ADR 0210).</summary>
    RealNetwork
}

/// <summary>
/// Thrown by <see cref="HackHttpClient"/> when no response came back: unknown host, unreachable
/// host, too many redirects. A response with an error status (404, 500) is not an exception.
/// </summary>
public sealed class HackHttpRequestException : HttpRequestException
{
    /// <summary>Initializes the exception.</summary>
    public HackHttpRequestException(HackNetworkError networkError, HttpRequestError httpRequestError, string message)
        : base(httpRequestError, message)
    {
        NetworkError = networkError;
    }

    /// <summary>Gets why the request failed.</summary>
    public HackNetworkError NetworkError { get; }

    /// <summary>Gets the code a browser prints for this failure, e.g. <c>ERR_NAME_NOT_RESOLVED</c>.</summary>
    public string ErrorCode => HackNetworkErrorCodes.ToCode(NetworkError);
}

/// <summary>
/// Cookie store of an HTTP client, modeled on <see cref="System.Net.CookieContainer"/>: the app
/// keeps it (a browser has one per tab) and the OS reads and fills it on each exchange.
/// </summary>
/// <remarks>Thread-safe. Cookies are kept by host and name, for the life of the container.</remarks>
public sealed class HackCookieContainer
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Domain, string Name), string> _cookies = [];

    /// <summary>Gets the number of cookies.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _cookies.Count;
            }
        }
    }

    /// <summary>Adds or replaces a cookie.</summary>
    public void Add(HackCookie cookie)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        lock (_gate)
        {
            _cookies[(cookie.Domain.ToLowerInvariant(), cookie.Name)] = cookie.Value;
        }
    }

    /// <summary>
    /// Removes a cookie, as a browser does when a cookie expires (<c>Max-Age=0</c> or an
    /// <c>Expires</c> date in the past).
    /// </summary>
    /// <param name="domain">Host the cookie belongs to.</param>
    /// <param name="name">Cookie name.</param>
    /// <returns>Whether a cookie was removed.</returns>
    public bool Remove(string domain, string name)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            return _cookies.Remove((domain.ToLowerInvariant(), name));
        }
    }

    /// <summary>
    /// Gets the cookies sent to <paramref name="uri"/>'s host: the host's own cookies, and the domain
    /// cookies a real site set with a <c>Domain</c> attribute, stored as <c>.example.com</c>
    /// (ADR 0210 §2), when the host is that domain or one of its subdomains.
    /// </summary>
    public IReadOnlyList<HackCookie> GetCookies(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string host = uri.Host.ToLowerInvariant();
        return [.. GetAllCookies().Where(cookie => cookie.Domain == host || DomainMatches(host, cookie.Domain))];
    }

    /// <summary>
    /// Whether <paramref name="host"/> matches a domain cookie key (<c>.example.com</c>): the host is
    /// <c>example.com</c> or ends with <c>.example.com</c> (RFC 6265 §5.1.3).
    /// </summary>
    /// <param name="host">The request's host, lower case.</param>
    /// <param name="domain">The cookie's domain; only a key starting with a dot is a domain cookie.</param>
    public static bool DomainMatches(string host, string domain)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(domain);
        return domain.Length > 1 && domain[0] == '.'
            && (host.Equals(domain[1..], StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gets every cookie, ordered by host then name.</summary>
    public IReadOnlyList<HackCookie> GetAllCookies()
    {
        lock (_gate)
        {
            return [.. _cookies
                .OrderBy(pair => pair.Key.Domain, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Name, StringComparer.Ordinal)
                .Select(pair => new HackCookie(pair.Key.Domain, pair.Key.Name, pair.Value))];
        }
    }

    /// <summary>Removes every cookie.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cookies.Clear();
        }
    }
}

/// <summary>
/// HTTP client, modeled on <see cref="HttpClient"/> (ADR 0056, section 6; bodies, ADR 0074
/// section 1). Needs <c>network.simulated.read</c> for <c>GET</c>, <c>HEAD</c> and <c>OPTIONS</c>,
/// <c>network.simulated.write</c> for any other method.
/// </summary>
/// <remarks>
/// <para>
/// Redirects are followed (at most 10), as browsers and <see cref="HttpClient"/> do: after 301 or
/// 302 a <c>POST</c> continues as a <c>GET</c> without its body, after 303 every method but
/// <c>HEAD</c> does, and 307 or 308 repeat the same method with the same body. Cookies set along
/// the way land in <see cref="Cookies"/>.
/// </para>
/// <para>
/// A host outside the simulated network is reached through the optional server when the app holds
/// <c>network.real.access</c> (<c>GET</c> and <c>HEAD</c> only); otherwise it is "not found".
/// The app never learns whether a server exists.
/// </para>
/// </remarks>
public sealed class HackHttpClient : IDisposable
{
    private bool _disposed;

    /// <summary>Initializes a client with its own empty cookie container.</summary>
    public HackHttpClient()
        : this(new HackCookieContainer())
    {
    }

    /// <summary>Initializes a client sharing <paramref name="cookies"/>.</summary>
    public HackHttpClient(HackCookieContainer cookies)
    {
        Cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
    }

    /// <summary>Gets the cookie container read and filled by every exchange.</summary>
    public HackCookieContainer Cookies { get; }

    /// <summary>
    /// Gets or sets the base address a relative request URI is resolved against, as
    /// <c>HttpClient.BaseAddress</c>: <c>GetAsync("api/notes")</c> with <c>http://intranet.hack/</c>
    /// requests <c>http://intranet.hack/api/notes</c>. <see langword="null"/> (the default) accepts
    /// absolute URIs only. A Blazor site's client (ADR 0084, section 6) is set to the site's address.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Gets the headers added to every request.</summary>
    public IDictionary<string, string> DefaultRequestHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = "HackerOS/1.0" };

    /// <summary>Sends a <c>GET</c> request.</summary>
    /// <exception cref="HackHttpRequestException">No response came back.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.read</c>.</exception>
    public Task<HackHttpResponse> GetAsync(string requestUri, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, requestUri, content: null, cancellationToken);

    /// <summary>Sends a <c>GET</c> request and returns the body as text, throwing on an error status.</summary>
    /// <exception cref="HttpRequestException">No response came back, or its status is not a success.</exception>
    public async Task<string> GetStringAsync(string requestUri, CancellationToken cancellationToken = default)
    {
        HackHttpResponse response = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return response.EnsureSuccessStatusCode().ReadAsString();
    }

    /// <summary>Sends a <c>GET</c> request and returns the body bytes, throwing on an error status.</summary>
    /// <exception cref="HttpRequestException">No response came back, or its status is not a success.</exception>
    public async Task<byte[]> GetByteArrayAsync(string requestUri, CancellationToken cancellationToken = default)
    {
        HackHttpResponse response = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return response.EnsureSuccessStatusCode().ContentBytes;
    }

    /// <summary>Sends a <c>GET</c> request and deserializes the JSON body, throwing on an error status.</summary>
    /// <exception cref="HttpRequestException">No response came back, or its status is not a success.</exception>
    /// <exception cref="JsonException">The body is not valid JSON for <typeparamref name="T"/>.</exception>
    public async Task<T?> GetFromJsonAsync<T>(string requestUri, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken = default)
    {
        HackHttpResponse response = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return response.EnsureSuccessStatusCode().ReadFromJson(jsonTypeInfo);
    }

    /// <summary>Sends a <c>POST</c> request with URL-encoded form fields (<see cref="HackFormUrlEncodedContent"/>).</summary>
    /// <exception cref="HackHttpRequestException">No response came back.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.write</c>.</exception>
    public Task<HackHttpResponse> PostAsync(
        string requestUri, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        return PostAsync(requestUri, new HackFormUrlEncodedContent(form), cancellationToken);
    }

    /// <summary>Sends a <c>POST</c> request with a body.</summary>
    /// <exception cref="HackHttpRequestException">No response came back.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.write</c>.</exception>
    public Task<HackHttpResponse> PostAsync(string requestUri, HackHttpContent? content, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, requestUri, content, cancellationToken);

    /// <summary>Sends a <c>POST</c> request with <paramref name="value"/> serialized as JSON (trim-safe).</summary>
    /// <exception cref="HackHttpRequestException">No response came back.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.write</c>.</exception>
    public Task<HackHttpResponse> PostAsJsonAsync<T>(string requestUri, T value, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken = default) =>
        PostAsync(requestUri, HackJsonContent.Create(value, jsonTypeInfo), cancellationToken);

    /// <summary>Sends a <c>PUT</c> request with a body.</summary>
    /// <exception cref="HackHttpRequestException">No response came back.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.write</c>.</exception>
    public Task<HackHttpResponse> PutAsync(string requestUri, HackHttpContent? content, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, requestUri, content, cancellationToken);

    /// <summary>Sends a <c>PATCH</c> request with a body.</summary>
    /// <exception cref="HackHttpRequestException">No response came back.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.write</c>.</exception>
    public Task<HackHttpResponse> PatchAsync(string requestUri, HackHttpContent? content, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, requestUri, content, cancellationToken);

    /// <summary>Sends a <c>DELETE</c> request.</summary>
    /// <exception cref="HackHttpRequestException">No response came back.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.write</c>.</exception>
    public Task<HackHttpResponse> DeleteAsync(string requestUri, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, requestUri, content: null, cancellationToken);

    /// <summary>Sends a request with any method and an optional body.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="requestUri">Absolute <c>http</c> or <c>https</c> URI.</param>
    /// <param name="content">The body, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <exception cref="ArgumentException"><paramref name="requestUri"/> is not an absolute http(s) URI.</exception>
    /// <exception cref="HackHttpRequestException">No response came back.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks the capability for <paramref name="method"/>.</exception>
    public async Task<HackHttpResponse> SendAsync(
        HttpMethod method,
        string requestUri,
        HackHttpContent? content = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(method);
        Uri uri = ParseUri(requestUri);

        HackHttpRequest request = new(
            method.Method.ToUpperInvariant(),
            uri,
            [.. DefaultRequestHeaders],
            content,
            Cookies.GetAllCookies());

        HackNetworkResult<HackHttpResponse> result = await HackNetworkErrorTranslator
            .CallAsync(gateway => gateway.SendAsync(request, cancellationToken)).ConfigureAwait(false);
        HackHttpResponse response = HackNetworkErrorTranslator.UnwrapHttp(result, uri);
        foreach (HackCookie cookie in response.SetCookies)
        {
            Cookies.Add(cookie);
        }

        return response;
    }

    /// <summary>Marks the client disposed; the cookie container stays usable by its other owners.</summary>
    public void Dispose() => _disposed = true;

    private Uri ParseUri(string requestUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUri);
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out Uri? uri)
            && (BaseAddress is null || !Uri.TryCreate(BaseAddress, requestUri, out uri)))
        {
            throw new ArgumentException($"'{requestUri}' is not an absolute http or https URI.", nameof(requestUri));
        }

        if (uri is null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"'{requestUri}' is not an absolute http or https URI.", nameof(requestUri));
        }

        return uri;
    }
}
