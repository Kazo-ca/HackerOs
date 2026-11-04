using System.Net;
using System.Text;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.AppSdk.Web.Http;

/// <summary>
/// A request handed to <see cref="Hosting.HackWebApplication.ProcessAsync"/>, independent of the
/// transport: <see cref="Hosting.HackWebHost"/> builds one from each <c>HackHttpListener</c> request,
/// tests build them directly (like ASP.NET Core's <c>TestServer</c>).
/// </summary>
/// <param name="Method">HTTP method (upper-cased on use).</param>
/// <param name="Url">Absolute URL requested.</param>
/// <param name="Headers">Request headers, in order; a name may repeat.</param>
/// <param name="ContentType">The body's <c>Content-Type</c>, or <see langword="null"/>.</param>
/// <param name="Body">The body bytes.</param>
public sealed record HackWebRequestData(
    string Method,
    Uri Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? ContentType,
    ReadOnlyMemory<byte> Body)
{
    /// <summary>Gets the client's address and port (<c>127.0.0.1:0</c> when unknown).</summary>
    public IPEndPoint RemoteEndPoint { get; init; } = new(IPAddress.Loopback, 0);

    /// <summary>Gets the address and port the request arrived on (<c>127.0.0.1:</c> URL port when unknown).</summary>
    public IPEndPoint? LocalEndPoint { get; init; }

    /// <summary>Builds a request without body.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="url">Absolute URL.</param>
    /// <param name="headers">Optional headers.</param>
    public static HackWebRequestData Create(string method, string url, params KeyValuePair<string, string>[] headers) =>
        new(method, new Uri(url, UriKind.Absolute), headers, null, ReadOnlyMemory<byte>.Empty);

    /// <summary>Builds a request with a text body.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="url">Absolute URL.</param>
    /// <param name="contentType">Body type, e.g. <c>application/json</c>.</param>
    /// <param name="body">Body text, sent as UTF-8.</param>
    /// <param name="headers">Optional headers.</param>
    public static HackWebRequestData Create(string method, string url, string contentType, string body, params KeyValuePair<string, string>[] headers) =>
        new(method, new Uri(url, UriKind.Absolute), headers, contentType, Encoding.UTF8.GetBytes(body));
}

/// <summary>The response <see cref="Hosting.HackWebApplication.ProcessAsync"/> produced for a <see cref="HackWebRequestData"/>.</summary>
/// <param name="StatusCode">Status code.</param>
/// <param name="ReasonPhrase">Reason phrase, or <see langword="null"/> for the standard one.</param>
/// <param name="Headers">Response headers (without <c>Content-Type</c> and <c>Content-Length</c>).</param>
/// <param name="ContentType">The body's <c>Content-Type</c>, or <see langword="null"/>.</param>
/// <param name="Body">The body bytes.</param>
public sealed record HackWebResponseData(
    int StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? ContentType,
    byte[] Body)
{
    /// <summary>Decodes the body with the charset of <see cref="ContentType"/> (UTF-8 by default).</summary>
    public string ReadAsString() => SimulatedMediaTypes.GetEncoding(ContentType).GetString(Body);

    /// <summary>Gets the values of a header, joined with <c>", "</c>, or <see langword="null"/>.</summary>
    /// <param name="name">Header name, compared without case.</param>
    public string? GetHeader(string name)
    {
        string[] values = [.. Headers.Where(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(header => header.Value)];
        return values.Length == 0 ? null : string.Join(", ", values);
    }
}
