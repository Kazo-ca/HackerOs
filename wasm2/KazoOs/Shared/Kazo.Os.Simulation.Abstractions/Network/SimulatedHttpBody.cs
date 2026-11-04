using System.Collections.Immutable;
using System.Text;

namespace Kazo.Os.Simulation.Abstractions.Network;

/// <summary>
/// Media types the simulation names often (ADR 0074, section 1). Any other <c>type/subtype</c>
/// string is valid too: a body carries whatever its sender declares.
/// </summary>
public static class SimulatedMediaTypes
{
    /// <summary><c>text/plain</c>.</summary>
    public const string TextPlain = "text/plain";

    /// <summary><c>text/html</c>.</summary>
    public const string TextHtml = "text/html";

    /// <summary><c>text/css</c>.</summary>
    public const string TextCss = "text/css";

    /// <summary><c>text/javascript</c> (RFC 9239 name of JavaScript).</summary>
    public const string TextJavaScript = "text/javascript";

    /// <summary><c>application/json</c>.</summary>
    public const string ApplicationJson = "application/json";

    /// <summary><c>application/xml</c>.</summary>
    public const string ApplicationXml = "application/xml";

    /// <summary><c>application/x-www-form-urlencoded</c>, the body of an HTML form post.</summary>
    public const string FormUrlEncoded = "application/x-www-form-urlencoded";

    /// <summary><c>application/octet-stream</c>, bytes of unknown type.</summary>
    public const string OctetStream = "application/octet-stream";

    /// <summary>
    /// Gets the <c>type/subtype</c> part of a <c>Content-Type</c> value, lower case and without
    /// parameters: <c>text/html; charset=utf-8</c> gives <c>text/html</c>.
    /// </summary>
    public static string GetMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        int separator = contentType.IndexOf(';');
        return (separator < 0 ? contentType : contentType[..separator]).Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Gets the <c>charset</c> parameter of a <c>Content-Type</c> value, without quotes, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    public static string? GetCharset(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        foreach (string parameter in contentType.Split(';').Skip(1))
        {
            int equals = parameter.IndexOf('=');
            if (equals > 0 && parameter[..equals].Trim().Equals("charset", StringComparison.OrdinalIgnoreCase))
            {
                return parameter[(equals + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the encoding named by the <c>charset</c> of <paramref name="contentType"/>; UTF-8 when
    /// there is none or the name is unknown, as browsers default for modern content.
    /// </summary>
    public static Encoding GetEncoding(string? contentType)
    {
        string? charset = GetCharset(contentType);
        if (charset is null)
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Gets whether a body of this type reads as text: <c>text/*</c>, JSON, XML, JavaScript and
    /// form data, including the structured suffixes (<c>application/problem+json</c>,
    /// <c>image/svg+xml</c>). <c>curl</c> uses it to refuse binary output on a terminal, the
    /// browser to choose between a text view and a download.
    /// </summary>
    public static bool IsText(string? contentType)
    {
        string mediaType = GetMediaType(contentType);
        return mediaType.StartsWith("text/", StringComparison.Ordinal)
            || mediaType is ApplicationJson or ApplicationXml or FormUrlEncoded
                or "application/javascript" or "application/ecmascript"
            || mediaType.EndsWith("+json", StringComparison.Ordinal)
            || mediaType.EndsWith("+xml", StringComparison.Ordinal);
    }

    /// <summary>Gets whether the type is JSON: <c>application/json</c> or a <c>+json</c> suffix.</summary>
    public static bool IsJson(string? contentType)
    {
        string mediaType = GetMediaType(contentType);
        return mediaType == ApplicationJson || mediaType.EndsWith("+json", StringComparison.Ordinal);
    }
}

/// <summary>
/// The body of a simulated HTTP request or response (ADR 0074, section 1): the bytes on the wire and
/// the <c>Content-Type</c> that says how to read them. Immutable: the bytes are copied in and only
/// exposed read-only.
/// </summary>
/// <remarks>
/// A response of an existing site may still carry a structured <see cref="SimulatedPage"/> instead
/// (or besides); a body is what every other kind of content uses: HTML, CSS, JavaScript, JSON,
/// images, files.
/// </remarks>
public sealed class SimulatedHttpBody
{
    private readonly byte[] _content;

    /// <summary>Initializes a body from bytes, copying them.</summary>
    /// <param name="contentType">
    /// Full <c>Content-Type</c> value, parameters included (<c>text/html; charset=utf-8</c>);
    /// empty means <see cref="SimulatedMediaTypes.OctetStream"/>.
    /// </param>
    /// <param name="content">The bytes.</param>
    public SimulatedHttpBody(string contentType, ReadOnlySpan<byte> content)
    {
        ContentType = string.IsNullOrWhiteSpace(contentType) ? SimulatedMediaTypes.OctetStream : contentType.Trim();
        _content = content.ToArray();
    }

    /// <summary>Gets the full <c>Content-Type</c> value, parameters included.</summary>
    public string ContentType { get; }

    /// <summary>Gets the <c>type/subtype</c> part of <see cref="ContentType"/>, lower case.</summary>
    public string MediaType => SimulatedMediaTypes.GetMediaType(ContentType);

    /// <summary>Gets the <c>charset</c> parameter, or <see langword="null"/>.</summary>
    public string? Charset => SimulatedMediaTypes.GetCharset(ContentType);

    /// <summary>Gets the bytes, read-only.</summary>
    public ReadOnlyMemory<byte> Content => _content;

    /// <summary>Gets the size of the body in bytes, the <c>Content-Length</c>.</summary>
    public int Length => _content.Length;

    /// <summary>Gets whether this is an HTML form post (<see cref="SimulatedMediaTypes.FormUrlEncoded"/>).</summary>
    public bool IsFormUrlEncoded => MediaType == SimulatedMediaTypes.FormUrlEncoded;

    /// <summary>Returns a copy of the bytes.</summary>
    public byte[] ToArray() => (byte[])_content.Clone();

    /// <summary>Decodes the bytes with the declared charset (UTF-8 when none).</summary>
    public string ReadAsString() => SimulatedMediaTypes.GetEncoding(ContentType).GetString(_content);

    /// <summary>
    /// Parses a URL-encoded body into fields (last value wins for a repeated name), whatever its
    /// declared type. <c>+</c> stands for a space, as in an HTML form.
    /// </summary>
    public ImmutableDictionary<string, string> ReadAsForm() => ParseForm(ReadAsString());

    /// <summary>Creates a text body, encoded as UTF-8, of type <c><paramref name="mediaType"/>; charset=utf-8</c>.</summary>
    public static SimulatedHttpBody FromText(string text, string mediaType = SimulatedMediaTypes.TextPlain)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        return new SimulatedHttpBody($"{mediaType}; charset=utf-8", Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Creates an HTML body (<c>text/html; charset=utf-8</c>).</summary>
    public static SimulatedHttpBody Html(string html) => FromText(html, SimulatedMediaTypes.TextHtml);

    /// <summary>Creates a JSON body (<c>application/json; charset=utf-8</c>) from already serialized JSON.</summary>
    public static SimulatedHttpBody Json(string json) => FromText(json, SimulatedMediaTypes.ApplicationJson);

    /// <summary>Creates a body from bytes of the given type.</summary>
    public static SimulatedHttpBody FromBytes(ReadOnlySpan<byte> content, string contentType) => new(contentType, content);

    /// <summary>Creates an HTML form body (<c>application/x-www-form-urlencoded</c>).</summary>
    public static SimulatedHttpBody FormUrlEncoded(IEnumerable<KeyValuePair<string, string>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        string encoded = string.Join('&', fields.Select(static field =>
            $"{EncodeFormComponent(field.Key)}={EncodeFormComponent(field.Value)}"));
        return new SimulatedHttpBody(SimulatedMediaTypes.FormUrlEncoded, Encoding.UTF8.GetBytes(encoded));
    }

    /// <summary>Encodes one name or value of a form like a browser does: space as <c>+</c>.</summary>
    public static string EncodeFormComponent(string value) =>
        Uri.EscapeDataString(value ?? string.Empty).Replace("%20", "+", StringComparison.Ordinal);

    /// <summary>Parses <c>a=1&amp;b=two+words</c> into fields; last value wins for a repeated name.</summary>
    public static ImmutableDictionary<string, string> ParseForm(string encoded)
    {
        ImmutableDictionary<string, string>.Builder fields = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (string pair in (encoded ?? string.Empty).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            string name = separator < 0 ? pair : pair[..separator];
            string value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            if (name.Length > 0)
            {
                fields[DecodeFormComponent(name)] = DecodeFormComponent(value);
            }
        }

        return fields.ToImmutable();
    }

    private static string DecodeFormComponent(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));
}
