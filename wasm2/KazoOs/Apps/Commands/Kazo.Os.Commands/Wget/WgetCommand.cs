using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Wget;

/// <summary>
/// <c>wget [-O FILE] [-P DIR] [-q] [-S] [--spider] URL...</c> (ADR 0064): downloads each URL with
/// <see cref="HackHttpClient"/> (the simulated network, or the real one through the OS relay with
/// <c>network.real.access</c>) into a file named after the URL (<c>index.html</c> for a
/// directory), never overwriting: <c>file.1</c>, <c>file.2</c>… like GNU wget. <c>-O -</c>
/// writes to standard output. Progress goes to standard error, as wget does.
/// </summary>
public sealed class WgetCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> messages = context.App.GetLocalizer<Strings>();
        string? output = null, prefix = null;
        bool quiet = false, headers = false, spider = false;
        List<string> urls = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-O" or "--output-document" when i + 1 < args.Count: output = args[++i]; break;
                case "-P" or "--directory-prefix" when i + 1 < args.Count: prefix = args[++i]; break;
                case "-q" or "--quiet": quiet = true; break;
                case "-S" or "--server-response": headers = true; break;
                case "--spider": spider = true; break;
                case "-c" or "--continue" or "-nv" or "--no-verbose" or "--no-check-certificate": break;
                default:
                    if (arg.StartsWith("-O", StringComparison.Ordinal) && arg.Length > 2)
                    {
                        output = arg[2..];
                    }
                    else if (arg.StartsWith('-'))
                    {
                        await context.StandardError.WriteLineAsync(messages["UnrecognizedOption", arg]);
                        await context.StandardError.WriteLineAsync(messages["Usage"]);
                        return 2;
                    }
                    else
                    {
                        urls.Add(arg);
                    }

                    break;
            }
        }

        if (urls.Count == 0)
        {
            await context.StandardError.WriteLineAsync(messages["MissingUrl"]);
            await context.StandardError.WriteLineAsync(messages["Usage"]);
            return 1;
        }

        TextWriter log = quiet ? TextWriter.Null : context.StandardError;
        int status = 0;
        using HackHttpClient client = new();
        client.DefaultRequestHeaders["User-Agent"] = "Wget/1.21.4";
        foreach (string text in urls)
        {
            string address = text.Contains("://", StringComparison.Ordinal) ? text : "http://" + text;
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
            {
                await log.WriteLineAsync(messages["InvalidUrl", text]);
                status = 1;
                continue;
            }

            DateTimeOffset started = context.App.Clock.UtcNow;
            await log.WriteLineAsync($"--{started:yyyy-MM-dd HH:mm:ss}--  {uri.AbsoluteUri}");
            await log.WriteLineAsync(messages["Resolving", uri.Host]);
            HackHttpResponse response;
            try
            {
                response = spider
                    ? await client.SendAsync(HttpMethod.Head, uri.AbsoluteUri, null, cancellationToken)
                    : await client.GetAsync(uri.AbsoluteUri, cancellationToken);
            }
            catch (HackHttpRequestException exception)
            {
                await log.WriteLineAsync(exception.NetworkError == HackNetworkError.HostNotFound
                    ? messages["CannotResolve", uri.Host]
                    : messages["ConnectFailed", uri.Host, uri.Port, exception.Message]);
                status = 4;
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                await context.StandardError.WriteLineAsync(messages["Error", exception.Message]);
                status = 4;
                continue;
            }

            await log.WriteLineAsync(messages["Connected", uri.Host, uri.Port]);
            await log.WriteLineAsync(messages["RequestSent", response.StatusCode, response.ReasonPhrase]);
            if (headers)
            {
                await log.WriteLineAsync($"  HTTP/1.1 {response.StatusCode} {response.ReasonPhrase}");
                foreach (KeyValuePair<string, string> header in response.Headers)
                {
                    await log.WriteLineAsync($"  {header.Key}: {header.Value}");
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                await log.WriteLineAsync(messages["HttpError", started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), response.StatusCode, response.ReasonPhrase]);
                await log.WriteLineAsync();
                status = 8;
                continue;
            }

            string type = response.Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? "text/html";
            await log.WriteLineAsync(messages["Length", response.ContentBytes.Length, CommandIO.HumanSize(response.ContentBytes.Length), type]);
            if (spider)
            {
                await log.WriteLineAsync(messages["RemoteExists"]);
                await log.WriteLineAsync();
                continue;
            }

            if (output == "-")
            {
                await context.StandardOutput.WriteAsync(response.ReadAsString());
                continue;
            }

            try
            {
                string target = output ?? await FreeNameAsync(prefix, FileName(response.RequestUri), cancellationToken);
                if (prefix is not null && output is null)
                {
                    await HackDirectory.CreateDirectoryAsync(prefix, cancellationToken);
                }

                await log.WriteLineAsync(messages["SavingTo", target]);
                await log.WriteLineAsync();
                await HackFile.WriteAllBytesAsync(target, response.ContentBytes, cancellationToken);
                string bar = new('=', 38);
                await log.WriteLineAsync($"{HackPath.GetFileName(target),-20} 100%[{bar}>] {CommandIO.HumanSize(response.ContentBytes.Length),7}  --.-KB/s    in 0s");
                await log.WriteLineAsync();
                await log.WriteLineAsync(messages["Saved",
                    context.App.Clock.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    CommandIO.HumanSize(response.ContentBytes.Length), target, response.ContentBytes.Length]);
                await log.WriteLineAsync();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await context.StandardError.WriteLineAsync(messages["Error", CommandText.Describe(context, exception)]);
                status = 3;
            }
        }

        return status;
    }

    /// <summary>The local name of a URL: its last path segment, or <c>index.html</c>.</summary>
    private static string FileName(Uri uri)
    {
        string last = uri.Segments.Length > 0 ? Uri.UnescapeDataString(uri.Segments[^1]).Trim('/') : string.Empty;
        return last.Length == 0 ? "index.html" : last;
    }

    /// <summary>wget never overwrites: <c>name</c>, else <c>name.1</c>, <c>name.2</c>…</summary>
    private static async Task<string> FreeNameAsync(string? prefix, string name, CancellationToken cancellationToken)
    {
        string basePath = prefix is null ? name : HackPath.Combine(prefix, name);
        string candidate = basePath;
        for (int suffix = 1; await HackFile.ExistsAsync(candidate, cancellationToken); suffix++)
        {
            candidate = $"{basePath}.{suffix}";
        }

        return candidate;
    }
}
