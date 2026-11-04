using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Commands.Common;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Cat;

/// <summary>
/// Implements the <c>cat</c> terminal command (`P2-CMD-004`). An argument starting with
/// <c>http://</c> or <c>https://</c> is read as a URL instead of a virtual-filesystem path
/// (ADR 0023 named `cat` as a network command alongside <c>ping</c>/<c>curl</c>): it is fetched with
/// <see cref="HackHttpClient"/> (ADR 0056, section 6) and its body printed, exactly as <c>curl</c>
/// prints it. Whether the URL is served by the simulated network or, through the optional server,
/// by the real one is the OS's decision. Every other argument keeps its VFS-only behavior.
/// </summary>
public sealed class CatCommand : TerminalAppBase
{
    private readonly HackCookieContainer _cookies = new();

    /// <summary>Initializes the command with its manifest.</summary>
    public CatCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Arguments.Count == 0)
        {
            context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["MissingFile"]);
            return 1;
        }

        int exitCode = 0;
        foreach (string fileArg in context.Arguments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsUrl(fileArg))
            {
                int urlExitCode = await CatUrlAsync(context, fileArg, cancellationToken).ConfigureAwait(false);
                if (urlExitCode != 0)
                {
                    exitCode = urlExitCode;
                }
                continue;
            }

            string resolvedPath = ResolvePath(context.WorkingDirectory, fileArg);
            FileSystemResult<FileSystemContentReadHandle> readResult = await context.App.FileSystem.ReadAsync(
                new FileSystemReadRequest(VirtualPath.Parse(resolvedPath)), cancellationToken);

            if (!readResult.Succeeded || readResult.Value is null)
            {
                context.StandardError.WriteLine(CommandText.FileError(context, "cat", fileArg, CommandText.Reason(context, CommandIO.ReadErrno(readResult.Error))));
                exitCode = 1;
            }
            else
            {
                await using FileSystemContentReadHandle handle = readResult.Value;
                using StreamReader reader = new(handle.Content, Encoding.UTF8, leaveOpen: true);
                string text = await reader.ReadToEndAsync(cancellationToken);
                context.StandardOutput.Write(text);
                if (!text.EndsWith('\n'))
                {
                    context.StandardOutput.WriteLine();
                }
            }
        }

        return exitCode;
    }

    private static bool IsUrl(string arg) =>
        arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads <paramref name="url"/> as content and prints its body.</summary>
    private async ValueTask<int> CatUrlAsync(TerminalExecutionContext context, string url, CancellationToken cancellationToken)
    {
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        HackHttpResponse response;
        try
        {
            using HackHttpClient client = new(_cookies);
            response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (HackHttpRequestException exception)
        {
            context.StandardError.WriteLine(exception.NetworkError == HackNetworkError.HostNotFound
                ? text["ResolveFailed", url]
                : $"cat: {url}: {exception.Message}");
            return 1;
        }
        catch (ArgumentException)
        {
            context.StandardError.WriteLine(text["InvalidUrl", url]);
            return 1;
        }

        if (response.StatusCode >= 400)
        {
            context.StandardError.WriteLine($"cat: {url}: {response.StatusCode} {response.ReasonPhrase}".TrimEnd());
            return 1;
        }

        string body = response.ReadAsString();
        context.StandardOutput.Write(body);
        if (body.Length > 0 && !body.EndsWith('\n'))
        {
            context.StandardOutput.WriteLine();
        }

        return 0;
    }

    private static string ResolvePath(string cwd, string target)
    {
        if (target.StartsWith('/'))
        {
            return NormalizePath(target);
        }
        return NormalizePath($"{cwd.TrimEnd('/')}/{target}");
    }

    private static string NormalizePath(string rawPath)
    {
        string[] segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Stack<string> stack = new();

        foreach (string segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.Pop();
                }
            }
            else
            {
                stack.Push(segment);
            }
        }

        return "/" + string.Join('/', stack.ToArray().Reverse());
    }
}
