using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kazo.Os.AppSdk.Web.Routing;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.AppSdk.Web.Http;

/// <summary>
/// The request of a <see cref="HackHttpContext"/>, modeled on ASP.NET Core's <c>HttpRequest</c>:
/// method, URL parts, headers, cookies, query, form, body and the route values of the matched endpoint.
/// </summary>
/// <remarks>The body is already in memory: the simulated network delivers whole requests.</remarks>
public sealed class HackHttpRequest
{
    private readonly ReadOnlyMemory<byte> _body;
    private HackValueCollection? _form;
    private Stream? _bodyStream;

    internal HackHttpRequest(HackWebRequestData data)
    {
        Method = data.Method.ToUpperInvariant();
        Url = data.Url;
        Scheme = data.Url.Scheme;
        Host = data.Url.IsDefaultPort ? data.Url.Host : $"{data.Url.Host}:{data.Url.Port}";
        Path = Uri.UnescapeDataString(data.Url.AbsolutePath);
        QueryString = data.Url.Query;
        Query = HackValueCollection.Parse(data.Url.Query);
        foreach (KeyValuePair<string, string> header in data.Headers)
        {
            Headers.Append(header.Key, header.Value);
        }

        ContentType = data.ContentType ?? Headers["Content-Type"];
        if (ContentType is not null && !Headers.ContainsKey("Content-Type"))
        {
            Headers.Append("Content-Type", ContentType);
        }

        if (Headers["Host"] is string hostHeader && hostHeader.Length > 0)
        {
            Host = hostHeader;
        }

        Cookies = ParseCookies(Headers.GetValues("Cookie"));
        _body = data.Body;
    }

    /// <summary>Gets the HTTP method, upper case (<c>GET</c>, <c>POST</c>...).</summary>
    public string Method { get; }

    /// <summary>Gets the absolute URL requested.</summary>
    public Uri Url { get; }

    /// <summary>Gets the scheme (<c>http</c> or <c>https</c>; both are served as HTTP by the simulated network).</summary>
    public string Scheme { get; }

    /// <summary>Gets whether the URL used <c>https</c>.</summary>
    public bool IsHttps => Scheme == Uri.UriSchemeHttps;

    /// <summary>Gets the <c>Host</c> header: host, plus <c>:port</c> when not the default.</summary>
    public string Host { get; }

    /// <summary>Gets the decoded path, starting with <c>/</c>.</summary>
    public string Path { get; }

    /// <summary>Gets the raw query string with its <c>?</c>, or an empty string.</summary>
    public string QueryString { get; }

    /// <summary>Gets the decoded query string fields.</summary>
    public HackValueCollection Query { get; }

    /// <summary>Gets the request headers.</summary>
    public HackHeaderDictionary Headers { get; } = new();

    /// <summary>Gets the cookies sent by the client.</summary>
    public IReadOnlyDictionary<string, string> Cookies { get; }

    /// <summary>Gets the body's <c>Content-Type</c>, or <see langword="null"/>.</summary>
    public string? ContentType { get; }

    /// <summary>Gets the body length in bytes.</summary>
    public long ContentLength => _body.Length;

    /// <summary>Gets the body bytes.</summary>
    public ReadOnlyMemory<byte> BodyBytes => _body;

    /// <summary>Gets the body as a read-only stream.</summary>
    public Stream Body => _bodyStream ??= new MemoryStream(_body.ToArray(), writable: false);

    /// <summary>Gets whether the body is a form (<c>application/x-www-form-urlencoded</c>).</summary>
    public bool HasFormContentType =>
        SimulatedMediaTypes.GetMediaType(ContentType).Equals(SimulatedMediaTypes.FormUrlEncoded, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether the body is JSON (<c>application/json</c>, <c>*+json</c>).</summary>
    public bool HasJsonContentType => SimulatedMediaTypes.IsJson(ContentType);

    /// <summary>Gets the form fields; empty when the body is not a form.</summary>
    public HackValueCollection Form => _form ??= HasFormContentType ? HackValueCollection.Parse(ReadBodyAsString()) : HackValueCollection.Empty;

    /// <summary>Gets the route values of the matched endpoint (empty before routing, or with no match).</summary>
    public HackRouteValueDictionary RouteValues { get; internal set; } = [];

    /// <summary>Gets the path and query, as sent on the request line.</summary>
    public string PathAndQuery => Url.PathAndQuery;

    /// <summary>Decodes the body with the charset of <see cref="ContentType"/> (UTF-8 by default).</summary>
    public string ReadBodyAsString() => SimulatedMediaTypes.GetEncoding(ContentType).GetString(_body.Span);

    /// <summary>Deserializes the JSON body with source-generated metadata (trim-safe).</summary>
    /// <typeparam name="T">Target type.</typeparam>
    /// <param name="jsonTypeInfo">Metadata, e.g. <c>MyJsonContext.Default.Note</c>.</param>
    /// <returns>The value, or <see langword="null"/> for an empty body or <c>null</c>.</returns>
    /// <exception cref="JsonException">The body is not valid JSON for <typeparamref name="T"/>.</exception>
    public T? ReadFromJson<T>(JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return _body.IsEmpty ? default : JsonSerializer.Deserialize(_body.Span, jsonTypeInfo);
    }

    /// <summary>Gets the URL as text (<c>http://intranet.hack/notes?x=1</c>).</summary>
    public override string ToString() => $"{Method} {Url}";

    private static Dictionary<string, string> ParseCookies(IReadOnlyList<string> headers)
    {
        Dictionary<string, string> cookies = new(StringComparer.Ordinal);
        foreach (string header in headers)
        {
            foreach (string part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    // The first cookie of a name wins, as browsers send the most specific path first.
                    cookies.TryAdd(part[..equals], Uri.UnescapeDataString(part[(equals + 1)..].Trim('"')));
                }
            }
        }

        return cookies;
    }
}

/// <summary>Builds the bytes of a response body.</summary>
internal static class HackBodyEncoding
{
    /// <summary>UTF-8 without byte-order mark.</summary>
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
