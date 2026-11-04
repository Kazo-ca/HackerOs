using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.AppSdk.Net;

/// <summary>
/// The body of an HTTP request, modeled on <see cref="HttpContent"/> (ADR 0074, section 1): bytes
/// and the <c>Content-Type</c> that says how to read them. Build one with
/// <see cref="HackStringContent"/>, <see cref="HackByteArrayContent"/>,
/// <see cref="HackFormUrlEncodedContent"/> or <see cref="HackJsonContent"/>, then pass it to
/// <see cref="HackHttpClient.PostAsync(string, HackHttpContent, CancellationToken)"/>,
/// <c>PutAsync</c>, <c>PatchAsync</c> or <c>SendAsync</c>.
/// </summary>
/// <remarks>Immutable: the bytes are copied in and every read returns a copy or a decoded value.</remarks>
public class HackHttpContent
{
    private readonly byte[] _content;

    /// <summary>Initializes a content from bytes, copying them.</summary>
    /// <param name="content">The bytes.</param>
    /// <param name="contentType">Full <c>Content-Type</c> value; empty means <c>application/octet-stream</c>.</param>
    public HackHttpContent(ReadOnlySpan<byte> content, string contentType)
    {
        _content = content.ToArray();
        ContentType = string.IsNullOrWhiteSpace(contentType) ? SimulatedMediaTypes.OctetStream : contentType.Trim();
    }

    /// <summary>Gets the full <c>Content-Type</c> header value, parameters included.</summary>
    public string ContentType { get; }

    /// <summary>Gets the <c>type/subtype</c> part of <see cref="ContentType"/>, lower case.</summary>
    public string MediaType => SimulatedMediaTypes.GetMediaType(ContentType);

    /// <summary>Gets the <c>charset</c> parameter of <see cref="ContentType"/>, or <see langword="null"/>.</summary>
    public string? CharSet => SimulatedMediaTypes.GetCharset(ContentType);

    /// <summary>Gets the size in bytes, the <c>Content-Length</c> header.</summary>
    public int Length => _content.Length;

    /// <summary>Gets the bytes, read-only.</summary>
    public ReadOnlyMemory<byte> Bytes => _content;

    /// <summary>Returns a copy of the bytes.</summary>
    public byte[] ReadAsByteArray() => (byte[])_content.Clone();

    /// <summary>Decodes the bytes with the declared charset, UTF-8 when none.</summary>
    public string ReadAsString() => SimulatedMediaTypes.GetEncoding(ContentType).GetString(_content);
}

/// <summary>A text body, modeled on <see cref="StringContent"/>; <c>text/plain; charset=utf-8</c> by default.</summary>
public sealed class HackStringContent : HackHttpContent
{
    /// <summary>Initializes a text body.</summary>
    /// <param name="content">The text.</param>
    /// <param name="encoding">Encoding of the bytes, UTF-8 when <see langword="null"/>.</param>
    /// <param name="mediaType">Media type, <c>text/plain</c> when <see langword="null"/>.</param>
    public HackStringContent(string content, Encoding? encoding = null, string? mediaType = null)
        : base(
            (encoding ?? Encoding.UTF8).GetBytes(content ?? throw new ArgumentNullException(nameof(content))),
            $"{mediaType ?? SimulatedMediaTypes.TextPlain}; charset={(encoding ?? Encoding.UTF8).WebName}")
    {
    }

    /// <summary>Initializes a UTF-8 text body of the given media type, like <c>new StringContent(text, mediaType)</c>.</summary>
    public HackStringContent(string content, string mediaType)
        : this(content, Encoding.UTF8, mediaType)
    {
    }
}

/// <summary>A binary body, modeled on <see cref="ByteArrayContent"/>; <c>application/octet-stream</c> by default.</summary>
public sealed class HackByteArrayContent : HackHttpContent
{
    /// <summary>Initializes a binary body, copying <paramref name="content"/>.</summary>
    public HackByteArrayContent(byte[] content, string contentType = SimulatedMediaTypes.OctetStream)
        : base(content ?? throw new ArgumentNullException(nameof(content)), contentType)
    {
    }
}

/// <summary>An HTML form body, modeled on <see cref="FormUrlEncodedContent"/> (<c>application/x-www-form-urlencoded</c>).</summary>
public sealed class HackFormUrlEncodedContent : HackHttpContent
{
    /// <summary>Initializes a form body; names and values are encoded as a browser does (space as <c>+</c>).</summary>
    public HackFormUrlEncodedContent(IEnumerable<KeyValuePair<string, string>> nameValueCollection)
        : base(SimulatedHttpBody.FormUrlEncoded(nameValueCollection).Content.Span, SimulatedMediaTypes.FormUrlEncoded)
    {
    }
}

/// <summary>A JSON body, modeled on <c>System.Net.Http.Json.JsonContent</c> (<c>application/json; charset=utf-8</c>).</summary>
public sealed class HackJsonContent : HackHttpContent
{
    private HackJsonContent(byte[] utf8Json)
        : base(utf8Json, $"{SimulatedMediaTypes.ApplicationJson}; charset=utf-8")
    {
    }

    /// <summary>Serializes <paramref name="value"/> with source-generated metadata (trim-safe).</summary>
    public static HackJsonContent Create<T>(T value, JsonTypeInfo<T> jsonTypeInfo) =>
        new(JsonSerializer.SerializeToUtf8Bytes(value, jsonTypeInfo));

    /// <summary>Serializes <paramref name="value"/> by reflection, with the web defaults (camelCase) unless <paramref name="options"/> says otherwise.</summary>
    [RequiresUnreferencedCode("JSON serialization by reflection may need types the trimmer removes; use the JsonTypeInfo overload.")]
    [RequiresDynamicCode("JSON serialization by reflection may need runtime code generation; use the JsonTypeInfo overload.")]
    public static HackJsonContent Create<T>(T value, JsonSerializerOptions? options = null) =>
        new(JsonSerializer.SerializeToUtf8Bytes(value, options ?? HackJson.WebOptions));

    /// <summary>Wraps already serialized JSON text.</summary>
    public static HackJsonContent FromJson(string json) =>
        new(Encoding.UTF8.GetBytes(json ?? throw new ArgumentNullException(nameof(json))));
}

/// <summary>Shared JSON settings of the network SDK.</summary>
internal static class HackJson
{
    /// <summary>The defaults of <c>System.Net.Http.Json</c>: camelCase, case-insensitive reads, numbers from strings.</summary>
    public static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
}
