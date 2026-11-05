using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Kazo.Os.AppSdk.Web.Http;

/// <summary>
/// The response of a <see cref="KazoHttpContext"/>, modeled on ASP.NET Core's <c>HttpResponse</c>:
/// status, headers, cookies and a buffered body sent once the pipeline returns.
/// </summary>
public sealed class KazoHttpResponse
{
    private readonly MemoryStream _body = new();

    internal KazoHttpResponse() => Cookies = new KazoResponseCookies(Headers);

    /// <summary>Gets or sets the status code (200 by default).</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>Gets or sets the reason phrase; the standard one of <see cref="StatusCode"/> when <see langword="null"/>.</summary>
    public string? ReasonPhrase { get; set; }

    /// <summary>Gets or sets the body's <c>Content-Type</c>.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets the response headers (<c>Content-Type</c> is <see cref="ContentType"/>; <c>Content-Length</c> is computed).</summary>
    public KazoHeaderDictionary Headers { get; } = new();

    /// <summary>Gets the cookies to set on the client (<c>Set-Cookie</c>).</summary>
    public KazoResponseCookies Cookies { get; }

    /// <summary>Gets the body stream; writes are buffered until the response is sent.</summary>
    public Stream Body => _body;

    /// <summary>Gets the body length written so far.</summary>
    public long ContentLength => _body.Length;

    /// <summary>Writes UTF-8 text to the body.</summary>
    /// <param name="text">Text to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _body.Write(KazoBodyEncoding.Utf8.GetBytes(text ?? string.Empty));
        return Task.CompletedTask;
    }

    /// <summary>Writes <paramref name="value"/> as JSON with source-generated metadata, and sets <c>application/json</c>.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="jsonTypeInfo">Metadata, e.g. <c>MyJsonContext.Default.Note</c>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task WriteAsJsonAsync<T>(T value, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        cancellationToken.ThrowIfCancellationRequested();
        ContentType ??= "application/json; charset=utf-8";
        JsonSerializer.Serialize(_body, value, jsonTypeInfo);
        return Task.CompletedTask;
    }

    /// <summary>Redirects with <c>302 Found</c>, or <c>301 Moved Permanently</c>.</summary>
    /// <param name="location">Target URL (absolute or relative).</param>
    /// <param name="permanent">Whether the redirection is permanent.</param>
    public void Redirect(string location, bool permanent = false)
    {
        ArgumentNullException.ThrowIfNull(location);
        StatusCode = permanent ? 301 : 302;
        Headers["Location"] = location;
    }

    /// <summary>Drops the body written so far (the exception page and error results replace it).</summary>
    public void ClearBody() => _body.SetLength(0);

    /// <summary>Removes the status, headers and body (keeps nothing of a failed handler).</summary>
    public void Clear()
    {
        StatusCode = 200;
        ReasonPhrase = null;
        ContentType = null;
        Headers.Clear();
        ClearBody();
    }

    /// <summary>Gets the body bytes written so far.</summary>
    public byte[] GetBodyBytes() => _body.ToArray();

    /// <summary>Copies the response into the transport-independent shape.</summary>
    /// <param name="withoutBody">Drops the body (a <c>HEAD</c> request).</param>
    internal KazoWebResponseData ToResponseData(bool withoutBody)
    {
        List<KeyValuePair<string, string>> headers = [];
        foreach (KeyValuePair<string, IReadOnlyList<string>> header in Headers)
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string value in header.Value)
            {
                headers.Add(new(header.Key, value));
            }
        }

        byte[] body = withoutBody ? [] : _body.ToArray();
        return new KazoWebResponseData(StatusCode, ReasonPhrase, headers, ContentType ?? Headers["Content-Type"], body);
    }
}

/// <summary>The cookies a response sets, like ASP.NET Core's <c>IResponseCookies</c>.</summary>
public sealed class KazoResponseCookies
{
    private readonly KazoHeaderDictionary _headers;

    internal KazoResponseCookies(KazoHeaderDictionary headers) => _headers = headers;

    /// <summary>Adds a <c>Set-Cookie</c> header.</summary>
    /// <param name="name">Cookie name.</param>
    /// <param name="value">Cookie value (escaped).</param>
    /// <param name="options">Path, expiry and flags; <c>Path=/</c> by default.</param>
    public void Append(string name, string value, KazoCookieOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        options ??= new KazoCookieOptions();
        StringBuilder cookie = new();
        cookie.Append(name).Append('=').Append(Uri.EscapeDataString(value ?? string.Empty));
        cookie.Append("; Path=").Append(options.Path ?? "/");
        if (options.Domain is not null)
        {
            cookie.Append("; Domain=").Append(options.Domain);
        }

        if (options.Expires is DateTimeOffset expires)
        {
            cookie.Append("; Expires=").Append(expires.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        }

        if (options.MaxAge is TimeSpan maxAge)
        {
            cookie.Append("; Max-Age=").Append(((long)maxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        if (options.Secure)
        {
            cookie.Append("; Secure");
        }

        if (options.HttpOnly)
        {
            cookie.Append("; HttpOnly");
        }

        if (options.SameSite is string sameSite)
        {
            cookie.Append("; SameSite=").Append(sameSite);
        }

        _headers.Append("Set-Cookie", cookie.ToString());
    }

    /// <summary>Tells the client to forget a cookie (expired, empty value).</summary>
    /// <param name="name">Cookie name.</param>
    /// <param name="path">Cookie path; <c>/</c> by default.</param>
    public void Delete(string name, string path = "/") =>
        Append(name, string.Empty, new KazoCookieOptions { Path = path, Expires = DateTimeOffset.UnixEpoch, MaxAge = TimeSpan.Zero });
}

/// <summary>Options of a cookie set by <see cref="KazoResponseCookies.Append"/>.</summary>
public sealed class KazoCookieOptions
{
    /// <summary>Gets or sets the path; <c>/</c> by default.</summary>
    public string? Path { get; set; } = "/";

    /// <summary>Gets or sets the domain, or <see langword="null"/> for the request host only.</summary>
    public string? Domain { get; set; }

    /// <summary>Gets or sets the expiry, or <see langword="null"/> for a session cookie.</summary>
    public DateTimeOffset? Expires { get; set; }

    /// <summary>Gets or sets the lifetime (<c>Max-Age</c>).</summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>Gets or sets whether the cookie is sent over HTTPS only.</summary>
    public bool Secure { get; set; }

    /// <summary>Gets or sets whether page scripts cannot read the cookie.</summary>
    public bool HttpOnly { get; set; }

    /// <summary>Gets or sets <c>SameSite</c> (<c>Lax</c>, <c>Strict</c>, <c>None</c>), or <see langword="null"/>.</summary>
    public string? SameSite { get; set; }
}
