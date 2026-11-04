using System.Collections.Immutable;

namespace Kazo.Os.Simulation.Abstractions.Network;

// ---------------------------------------------------------------------------
// Simulated HTTP request / response contracts.
// ---------------------------------------------------------------------------

/// <summary>
/// Immutable HTTP method constants used by the simulation.
/// </summary>
public static class SimulatedHttpMethods
{
    /// <summary><c>GET</c>: reads a resource.</summary>
    public const string Get    = "GET";
    /// <summary><c>POST</c>: submits data, e.g. a form.</summary>
    public const string Post   = "POST";
    /// <summary><c>PUT</c>: replaces a resource.</summary>
    public const string Put    = "PUT";
    /// <summary><c>DELETE</c>: removes a resource.</summary>
    public const string Delete = "DELETE";
    /// <summary><c>HEAD</c>: like GET, without the response body.</summary>
    public const string Head   = "HEAD";
    /// <summary><c>PATCH</c>: partially updates a resource.</summary>
    public const string Patch  = "PATCH";
    /// <summary><c>OPTIONS</c>: asks which methods a resource supports.</summary>
    public const string Options = "OPTIONS";

    /// <summary>
    /// Gets whether a method only reads (RFC 9110 "safe" methods: GET, HEAD, OPTIONS). The OS asks
    /// <c>network.simulated.read</c> for those and <c>network.simulated.write</c> for the others.
    /// </summary>
    public static bool IsSafe(string method) =>
        method.ToUpperInvariant() is Get or Head or Options;
}

/// <summary>
/// An immutable inbound HTTP-like request directed at a simulated website.
/// The simulation never makes real network calls; all fields are populated
/// by the in-memory network service before dispatch to the controller.
/// </summary>
/// <param name="Method">HTTP method, upper case.</param>
/// <param name="Host">Target host name, without port.</param>
/// <param name="Path">Absolute path, <c>/</c> at least.</param>
/// <param name="Query">Query-string parameters (last value wins for a repeated name).</param>
/// <param name="Headers">Request headers, case-insensitive names; repeated headers are joined with <c>, </c>.</param>
/// <param name="Cookies">Cookies the client holds for <paramref name="Host"/>.</param>
/// <param name="RouteParams">Values of <c>{param}</c> route segments, filled by the controller's router.</param>
/// <param name="FormBody">
/// Fields of a URL-encoded <paramref name="Body"/> (an HTML form post), parsed for convenience;
/// <see langword="null"/> for any other body.
/// </param>
/// <param name="Body">The request body with its <c>Content-Type</c> (ADR 0074, section 1), or <see langword="null"/>.</param>
public sealed record SimulatedHttpRequest(
    string Method,
    string Host,
    string Path,
    ImmutableDictionary<string, string> Query,
    ImmutableDictionary<string, string> Headers,
    ImmutableDictionary<string, string> Cookies,
    ImmutableDictionary<string, string> RouteParams,
    ImmutableDictionary<string, string>? FormBody = null,
    SimulatedHttpBody? Body = null)
{
    /// <summary>Returns the canonical URL string for this request.</summary>
    public string Url =>
        $"https://{Host}{Path}" +
        (Query.Count > 0
            ? "?" + string.Join("&", Query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))
            : string.Empty);

    /// <summary>Gets the <c>Content-Type</c> of <see cref="Body"/>, or <see langword="null"/> when there is no body.</summary>
    public string? ContentType => Body?.ContentType;

    /// <summary>Decodes <see cref="Body"/> as text with its charset; empty when there is no body.</summary>
    public string ReadBodyAsString() => Body?.ReadAsString() ?? string.Empty;

    /// <summary>
    /// Creates a request with any method and body. <see cref="FormBody"/> is filled when the body
    /// is URL-encoded, so form handlers read fields the same way for every method.
    /// </summary>
    public static SimulatedHttpRequest Create(
        string method,
        string host,
        string path,
        ImmutableDictionary<string, string>? query = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        ImmutableDictionary<string, string>? cookies = null,
        SimulatedHttpBody? body = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ImmutableDictionary<string, string>.Builder headerBuilder =
            ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> header in headers ?? [])
        {
            headerBuilder[header.Key] = headerBuilder.TryGetValue(header.Key, out string? existing)
                ? $"{existing}, {header.Value}"
                : header.Value;
        }

        if (body is not null)
        {
            headerBuilder["Content-Type"] = body.ContentType;
            headerBuilder["Content-Length"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new(
            Method: method.ToUpperInvariant(),
            Host: host,
            Path: string.IsNullOrEmpty(path) ? "/" : path,
            Query: query ?? ImmutableDictionary<string, string>.Empty,
            Headers: headerBuilder.ToImmutable(),
            Cookies: cookies ?? ImmutableDictionary<string, string>.Empty,
            RouteParams: ImmutableDictionary<string, string>.Empty,
            FormBody: body is { IsFormUrlEncoded: true } ? body.ReadAsForm() : null,
            Body: body);
    }

    /// <summary>Convenience factory for a simple GET request.</summary>
    public static SimulatedHttpRequest Get(string host, string path,
        ImmutableDictionary<string, string>? query = null,
        ImmutableDictionary<string, string>? cookies = null) =>
        Create(SimulatedHttpMethods.Get, host, path, query, cookies: cookies);

    /// <summary>Convenience factory for a POST request with a URL-encoded form body.</summary>
    public static SimulatedHttpRequest Post(string host, string path,
        ImmutableDictionary<string, string> form,
        ImmutableDictionary<string, string>? cookies = null) =>
        Create(SimulatedHttpMethods.Post, host, path, cookies: cookies, body: SimulatedHttpBody.FormUrlEncoded(form));
}

/// <summary>
/// Stable HTTP status code constants used by the simulation.
/// </summary>
public static class SimulatedHttpStatus
{
    /// <summary>200 OK.</summary>
    public const int Ok                = 200;
    /// <summary>201 Created.</summary>
    public const int Created           = 201;
    /// <summary>204 No Content.</summary>
    public const int NoContent         = 204;
    /// <summary>302 Found: temporary redirect; browsers follow it with a GET.</summary>
    public const int Found             = 302;
    /// <summary>303 See Other: redirect after a POST, always followed with a GET.</summary>
    public const int SeeOther          = 303;
    /// <summary>301 Moved Permanently (historical name; see <see cref="MovedPermanently"/>).</summary>
    public const int PermanentRedirect = 301;
    /// <summary>301 Moved Permanently.</summary>
    public const int MovedPermanently  = 301;
    /// <summary>307: redirect that keeps the method and the body.</summary>
    public const int TemporaryRedirect = 307;
    /// <summary>308: permanent redirect that keeps the method and the body.</summary>
    public const int PermanentRedirectKeepMethod = 308;
    /// <summary>400 Bad Request.</summary>
    public const int BadRequest        = 400;
    /// <summary>401 Unauthorized: authentication is required.</summary>
    public const int Unauthorized      = 401;
    /// <summary>403 Forbidden: authenticated but not allowed.</summary>
    public const int Forbidden         = 403;
    /// <summary>404 Not Found.</summary>
    public const int NotFound          = 404;
    /// <summary>405 Method Not Allowed: no route for this method on the path.</summary>
    public const int MethodNotAllowed  = 405;
    /// <summary>500 Internal Server Error.</summary>
    public const int InternalError     = 500;
}

/// <summary>
/// Immutable response from a simulated website controller. Its content is a <see cref="Body"/>
/// (bytes and <c>Content-Type</c>: HTML, JSON, CSS, files…, ADR 0074 section 1) or, for the
/// historical sites, a structured <see cref="SimulatedPage"/> the browser draws itself. A redirect
/// carries a <see cref="RedirectUrl"/> and usually neither.
/// </summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="Page">Structured page of a historical site, or <see langword="null"/>.</param>
/// <param name="RedirectUrl">Target of a redirect (<c>Location</c>), absolute or relative to the host.</param>
/// <param name="SetCookies">Cookies to store for the host.</param>
/// <param name="Headers">Extra response headers (<c>Cache-Control</c>, <c>Allow</c>…).</param>
/// <param name="Body">The response body, or <see langword="null"/>; it wins over <paramref name="Page"/> on the wire.</param>
public sealed record SimulatedHttpResponse(
    int StatusCode,
    SimulatedPage? Page,
    string? RedirectUrl,
    ImmutableDictionary<string, string> SetCookies,
    ImmutableDictionary<string, string> Headers,
    SimulatedHttpBody? Body = null)
{
    /// <summary>True when this response asks the client to follow a redirect (301, 302, 303, 307, 308 with a target).</summary>
    public bool IsRedirect =>
        StatusCode is SimulatedHttpStatus.Found
            or SimulatedHttpStatus.SeeOther
            or SimulatedHttpStatus.MovedPermanently
            or SimulatedHttpStatus.TemporaryRedirect
            or SimulatedHttpStatus.PermanentRedirectKeepMethod
        && RedirectUrl is not null;

    /// <summary>Convenience factory for a plain-content 200 response.</summary>
    public static SimulatedHttpResponse Ok(SimulatedPage page,
        ImmutableDictionary<string, string>? setCookies = null) =>
        new(
            StatusCode: SimulatedHttpStatus.Ok,
            Page: page,
            RedirectUrl: null,
            SetCookies: setCookies ?? ImmutableDictionary<string, string>.Empty,
            Headers: ImmutableDictionary<string, string>.Empty);

    /// <summary>Creates a response carrying <paramref name="body"/>.</summary>
    public static SimulatedHttpResponse Content(SimulatedHttpBody body, int statusCode = SimulatedHttpStatus.Ok,
        ImmutableDictionary<string, string>? setCookies = null,
        ImmutableDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new(
            StatusCode: statusCode,
            Page: null,
            RedirectUrl: null,
            SetCookies: setCookies ?? ImmutableDictionary<string, string>.Empty,
            Headers: headers ?? ImmutableDictionary<string, string>.Empty,
            Body: body);
    }

    /// <summary>Creates a <c>text/plain; charset=utf-8</c> response.</summary>
    public static SimulatedHttpResponse Text(string text, int statusCode = SimulatedHttpStatus.Ok) =>
        Content(SimulatedHttpBody.FromText(text), statusCode);

    /// <summary>Creates a <c>text/html; charset=utf-8</c> response.</summary>
    public static SimulatedHttpResponse Html(string html, int statusCode = SimulatedHttpStatus.Ok) =>
        Content(SimulatedHttpBody.Html(html), statusCode);

    /// <summary>Creates an <c>application/json; charset=utf-8</c> response from serialized JSON.</summary>
    public static SimulatedHttpResponse Json(string json, int statusCode = SimulatedHttpStatus.Ok) =>
        Content(SimulatedHttpBody.Json(json), statusCode);

    /// <summary>Creates a response carrying bytes of the given type (an image, a download…).</summary>
    public static SimulatedHttpResponse File(ReadOnlySpan<byte> content, string contentType) =>
        Content(SimulatedHttpBody.FromBytes(content, contentType));

    /// <summary>Creates a 204 No Content response, with no body.</summary>
    public static SimulatedHttpResponse NoContent() =>
        new(SimulatedHttpStatus.NoContent, null, null,
            ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, string>.Empty);

    /// <summary>
    /// Creates a 405 Method Not Allowed response with the <c>Allow</c> header listing what the path
    /// accepts, as RFC 9110 requires.
    /// </summary>
    public static SimulatedHttpResponse MethodNotAllowed(IEnumerable<string> allowedMethods)
    {
        string allow = string.Join(", ", allowedMethods.Select(static method => method.ToUpperInvariant()).Distinct());
        return Content(
            SimulatedHttpBody.FromText("405 Method Not Allowed\n"),
            SimulatedHttpStatus.MethodNotAllowed,
            headers: ImmutableDictionary<string, string>.Empty.Add("Allow", allow));
    }

    /// <summary>Convenience factory for a redirect response.</summary>
    public static SimulatedHttpResponse Redirect(string url, bool permanent = false,
        ImmutableDictionary<string, string>? setCookies = null) =>
        new(
            StatusCode: permanent ? SimulatedHttpStatus.PermanentRedirect : SimulatedHttpStatus.Found,
            Page: null,
            RedirectUrl: url,
            SetCookies: setCookies ?? ImmutableDictionary<string, string>.Empty,
            Headers: ImmutableDictionary<string, string>.Empty);

    /// <summary>Convenience factory for an error page response.</summary>
    public static SimulatedHttpResponse Error(int statusCode, string message) =>
        new(
            StatusCode: statusCode,
            Page: SimulatedPage.Error(statusCode, message),
            RedirectUrl: null,
            SetCookies: ImmutableDictionary<string, string>.Empty,
            Headers: ImmutableDictionary<string, string>.Empty);

    /// <summary>Convenience factory for a 404 Not Found response.</summary>
    public static SimulatedHttpResponse NotFound(string path) =>
        Error(SimulatedHttpStatus.NotFound, $"The page '{path}' was not found on this server.");
}
