using System.Net.Http;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Commands.Common;
using Kazo.Os.Simulation.Abstractions.Network;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Curl;

/// <summary>
/// Simulated <c>curl</c> terminal command (P4-W4-006) on <see cref="KazoHttpClient"/>
/// (ADR 0056, section 6), writing the raw response body (ADR 0074, section 1).
/// </summary>
/// <remarks>
/// <para>
/// Options: <c>-I</c> (HEAD, headers only), <c>-i</c> (status line and headers before the body),
/// <c>-v</c> (request and response headers), <c>-X</c> (method), <c>-H</c> (header),
/// <c>-A</c> (user agent), <c>-d</c>/<c>--data</c>/<c>--data-raw</c>/<c>--data-binary</c>
/// (request body, <c>@file</c> reads a file), <c>--data-urlencode</c>, <c>--json</c>,
/// <c>-o</c>/<c>-O</c> (save the body), <c>-f</c> (fail on HTTP errors), <c>-L</c> (accepted;
/// redirects are always followed by the OS).
/// </para>
/// <para>
/// As the real curl: <c>-d</c> sends its data as is with
/// <c>Content-Type: application/x-www-form-urlencoded</c> and implies <c>POST</c>, several
/// <c>-d</c> are joined with <c>&amp;</c>, a <c>-H "Content-Type: …"</c> replaces the type;
/// <c>--json</c> sets <c>Content-Type</c> and <c>Accept</c> to JSON. An HTTP error status prints
/// the body and exits 0, unless <c>-f</c> asks for exit code 22 and no body. A binary body is not
/// written to the terminal unless <c>-o</c> names a file (or <c>-</c>).
/// </para>
/// <para>
/// A host outside the simulated network is reached by the OS through the optional server when it
/// can (GET and HEAD, ADR 0028/0034); otherwise curl reports "Could not resolve host", exit code 6.
/// </para>
/// </remarks>
public sealed class CurlCommand : TerminalAppBase
{
    private readonly KazoCookieContainer _cookies = new();

    /// <summary>Initializes the command with its manifest.</summary>
    public CurlCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();

        string? url = null;
        bool headersOnly = false;
        bool includeHeaders = false;
        bool verbose = false;
        bool fail = false;
        bool json = false;
        string? method = null;
        string? outputFile = null;
        bool remoteName = false;
        List<KeyValuePair<string, string>> headers = [];
        List<DataPart> data = [];

        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-I" or "--head": headersOnly = true; break;
                case "-i" or "--include": includeHeaders = true; break;
                case "-v" or "--verbose": verbose = true; break;
                case "-L" or "--location": /* redirects are always followed */ break;
                case "-X" or "--request":
                    if (i + 1 < args.Count) method = args[++i].ToUpperInvariant();
                    break;
                case "-d" or "--data" or "--data-ascii":
                    if (i + 1 < args.Count) data.Add(new DataPart(args[++i], DataKind.Ascii));
                    break;
                case "--data-raw":
                    if (i + 1 < args.Count) data.Add(new DataPart(args[++i], DataKind.Raw));
                    break;
                case "--data-binary":
                    if (i + 1 < args.Count) data.Add(new DataPart(args[++i], DataKind.Binary));
                    break;
                case "--data-urlencode":
                    if (i + 1 < args.Count) data.Add(new DataPart(args[++i], DataKind.UrlEncode));
                    break;
                case "--json":
                    if (i + 1 < args.Count) data.Add(new DataPart(args[++i], DataKind.Binary));
                    json = true;
                    break;
                case "-o" or "--output":
                    // ADR 0064: save the body to a file instead of standard output.
                    if (i + 1 < args.Count) outputFile = args[++i];
                    break;
                case "-O" or "--remote-name": remoteName = true; break;
                case "-s" or "--silent" or "-S" or "--show-error" or "-sS" or "-k" or "--insecure": break;
                case "-f" or "--fail" or "-sf" or "-fsS" or "-sSf": fail = true; break;
                case "-H" or "--header":
                    if (i + 1 < args.Count && args[++i].Split(':', 2) is [{ } name, { } value])
                    {
                        headers.Add(new(name.Trim(), value.Trim()));
                    }

                    break;
                case "-A" or "--user-agent":
                    if (i + 1 < args.Count) headers.Add(new("User-Agent", args[++i]));
                    break;
                default:
                    if (!a.StartsWith('-')) url = a;
                    break;
            }
        }

        if (url is null)
        {
            context.StandardError.WriteLine(text["NoUrl"]);
            context.StandardError.WriteLine(text["TryHelp"]);
            return 1;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            context.StandardError.WriteLine(text["BadUrl"]);
            return 3;
        }

        KazoHttpContent? content = null;
        if (data.Count > 0)
        {
            byte[] bytes;
            try
            {
                bytes = await BuildDataAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                context.StandardError.WriteLine(text["ReadDataFailed", CommandText.Describe(context, exception)]);
                return 26;
            }

            // An explicit -H "Content-Type: …" wins over the type the data option implies.
            string contentType = TakeHeader(headers, "Content-Type")
                ?? (json ? SimulatedMediaTypes.ApplicationJson : SimulatedMediaTypes.FormUrlEncoded);
            content = new KazoByteArrayContent(bytes, contentType);
        }

        if (json && !headers.Any(static header => header.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add(new("Accept", SimulatedMediaTypes.ApplicationJson));
        }

        method ??= content is not null ? "POST" : headersOnly ? "HEAD" : "GET";
        if (verbose)
        {
            context.StandardOutput.WriteLine(text["Verbose.Trying", uri.Host, uri.Port]);
            context.StandardOutput.WriteLine($"> {method} {uri.PathAndQuery} HTTP/1.1");
            context.StandardOutput.WriteLine($"> Host: {uri.Host}");
            context.StandardOutput.WriteLine("> User-Agent: curl/7.88.1");
            foreach (KeyValuePair<string, string> header in headers)
            {
                context.StandardOutput.WriteLine($"> {header.Key}: {header.Value}");
            }

            if (content is not null)
            {
                context.StandardOutput.WriteLine($"> Content-Type: {content.ContentType}");
                context.StandardOutput.WriteLine($"> Content-Length: {content.Length}");
            }

            context.StandardOutput.WriteLine(">");
        }

        KazoHttpResponse response;
        try
        {
            using KazoHttpClient client = new(_cookies);
            client.DefaultRequestHeaders["User-Agent"] = "curl/7.88.1";
            foreach (KeyValuePair<string, string> header in headers)
            {
                client.DefaultRequestHeaders[header.Key] = header.Value;
            }

            response = await client.SendAsync(new HttpMethod(method), uri.AbsoluteUri, content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KazoHttpRequestException exception)
        {
            (int code, string message) = exception.NetworkError switch
            {
                KazoNetworkError.HostNotFound => (6, text["ResolveFailed", uri.Host].Value),
                KazoNetworkError.TooManyRedirects => (47, text["TooManyRedirects"].Value),
                _ => (7, text["ConnectFailed", uri.Host, uri.Port, exception.Message].Value)
            };
            context.StandardError.WriteLine($"curl: ({code}) {message}");
            return code;
        }

        if (verbose)
        {
            if (response.RedirectCount > 0)
                context.StandardOutput.WriteLine(text["Verbose.Followed", response.RedirectCount, response.RequestUri]);
            context.StandardOutput.WriteLine($"< HTTP/1.1 {response.StatusCode} {response.ReasonPhrase}");
            foreach (KeyValuePair<string, string> header in response.Headers)
            {
                context.StandardOutput.WriteLine($"< {header.Key}: {header.Value}");
            }

            context.StandardOutput.WriteLine("<");
        }

        if (headersOnly || includeHeaders)
        {
            context.StandardOutput.WriteLine($"HTTP/1.1 {response.StatusCode} {response.ReasonPhrase}".TrimEnd());
            foreach (KeyValuePair<string, string> header in response.Headers)
            {
                context.StandardOutput.WriteLine($"{header.Key}: {header.Value}");
            }

            if (headersOnly)
            {
                return 0;
            }

            context.StandardOutput.WriteLine();
        }

        if (fail && response.StatusCode >= 400)
        {
            context.StandardError.WriteLine(text["HttpError", response.StatusCode]);
            return 22;
        }

        if (remoteName)
        {
            outputFile = uri.Segments.Length > 0 && uri.Segments[^1].Trim('/') is { Length: > 0 } last ? Uri.UnescapeDataString(last) : "index.html";
        }

        if (outputFile is not null and not "-")
        {
            try
            {
                await KazoFile.WriteAllBytesAsync(outputFile, response.ContentBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                context.StandardError.WriteLine(text["WriteFailed", outputFile, CommandText.Describe(context, exception)]);
                return 23;
            }

            return 0;
        }

        // The real curl refuses to spray binary onto a terminal unless told to with "-o -".
        if (outputFile is null && response.ContentBytes.Length > 0 && response.ContentType is { } type && !SimulatedMediaTypes.IsText(type))
        {
            context.StandardError.WriteLine(text["BinaryWarning.Line1"]);
            context.StandardError.WriteLine(text["BinaryWarning.Line2"]);
            context.StandardError.WriteLine(text["BinaryWarning.Line3"]);
            return 23;
        }

        string body = response.ReadAsString();
        context.StandardOutput.Write(body);
        if (body.Length > 0 && !body.EndsWith('\n'))
        {
            context.StandardOutput.WriteLine();
        }

        return 0;
    }

    /// <summary>How one data option turns its argument into body bytes, as the real curl does.</summary>
    private enum DataKind
    {
        /// <summary><c>-d</c>: <c>@file</c> reads a file and drops its line breaks.</summary>
        Ascii,

        /// <summary><c>--data-raw</c>: the argument as is, <c>@</c> included.</summary>
        Raw,

        /// <summary><c>--data-binary</c>, <c>--json</c>: <c>@file</c> reads a file unchanged.</summary>
        Binary,

        /// <summary><c>--data-urlencode</c>: <c>name=value</c> with the value URL-encoded.</summary>
        UrlEncode
    }

    private sealed record DataPart(string Value, DataKind Kind);

    /// <summary>Builds the request body from every data option, joined with <c>&amp;</c> (curl's behavior).</summary>
    private static async Task<byte[]> BuildDataAsync(List<DataPart> parts, CancellationToken cancellationToken)
    {
        List<byte> body = [];
        foreach (DataPart part in parts)
        {
            if (body.Count > 0)
            {
                body.Add((byte)'&');
            }

            byte[] bytes = part.Kind switch
            {
                DataKind.Ascii when part.Value.StartsWith('@') =>
                    [.. (await KazoFile.ReadAllBytesAsync(part.Value[1..], cancellationToken).ConfigureAwait(false))
                        .Where(static b => b is not (byte)'\r' and not (byte)'\n')],
                DataKind.Binary when part.Value.StartsWith('@') =>
                    await KazoFile.ReadAllBytesAsync(part.Value[1..], cancellationToken).ConfigureAwait(false),
                DataKind.UrlEncode => Encoding.UTF8.GetBytes(UrlEncodePart(part.Value)),
                _ => Encoding.UTF8.GetBytes(part.Value)
            };
            body.AddRange(bytes);
        }

        return [.. body];
    }

    /// <summary><c>name=value</c> keeps the name and encodes the value; a bare value is encoded whole.</summary>
    private static string UrlEncodePart(string value)
    {
        int separator = value.IndexOf('=');
        return separator < 0
            ? SimulatedHttpBody.EncodeFormComponent(value)
            : $"{value[..separator]}={SimulatedHttpBody.EncodeFormComponent(value[(separator + 1)..])}";
    }

    /// <summary>Removes a header from the list and returns its value, or <see langword="null"/>.</summary>
    private static string? TakeHeader(List<KeyValuePair<string, string>> headers, string name)
    {
        int index = headers.FindIndex(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        string value = headers[index].Value;
        headers.RemoveAt(index);
        return value;
    }
}
