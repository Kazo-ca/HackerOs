using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Ln;

/// <summary>
/// <c>ln -s [-f] [-n] [-v] TARGET [LINK]</c>, <c>ln -s TARGET... DIRECTORY</c> (ADR 0064):
/// creates symbolic links through <see cref="KazoFile.CreateSymbolicLinkAsync"/>; the target text
/// is stored as given and may dangle. Without <c>-s</c> coreutils makes a hard link, which the
/// Kazo OS filesystem does not support (one entry per inode, like FAT): <c>ln</c> then fails
/// with <c>Operation not supported</c> and suggests <c>-s</c>.
/// </summary>
public sealed class LnCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        (List<string> options, List<string> operands) = CommandIO.SplitArguments(context.Arguments);
        bool symbolic = false, force = false, verbose = false, noDereference = false;
        foreach (string option in options)
        {
            if (option is "--symbolic" or "--force" or "--verbose" or "--no-dereference")
            {
                symbolic |= option == "--symbolic";
                force |= option == "--force";
                verbose |= option == "--verbose";
                noDereference |= option == "--no-dereference";
                continue;
            }

            foreach (char flag in option.TrimStart('-'))
            {
                switch (flag)
                {
                    case 's': symbolic = true; break;
                    case 'f': force = true; break;
                    case 'v': verbose = true; break;
                    case 'n': noDereference = true; break;
                    case 'r' or 'T' or 'i': break;
                    default:
                        await context.StandardError.WriteLineAsync(text["InvalidOption", flag]);
                        return 1;
                }
            }
        }

        if (operands.Count == 0)
        {
            await context.StandardError.WriteLineAsync(text["MissingOperand"]);
            return 1;
        }

        // `ln -s target` links into the current directory; with several operands the last one
        // must be a directory receiving one link per target.
        List<(string Target, string Link)> links = [];
        string last = operands[^1];
        bool intoDirectory = operands.Count > 1 && !noDereference && await KazoDirectory.ExistsAsync(last, cancellationToken);
        if (operands.Count == 1)
        {
            links.Add((operands[0], BaseName(operands[0])));
        }
        else if (intoDirectory)
        {
            links.AddRange(operands.Take(operands.Count - 1).Select(target => (target, KazoPath.Combine(last, BaseName(target)))));
        }
        else if (operands.Count == 2)
        {
            links.Add((operands[0], last));
        }
        else
        {
            await context.StandardError.WriteLineAsync(text["TargetNotDirectory", last]);
            return 1;
        }

        int status = 0;
        foreach ((string target, string link) in links)
        {
            if (!symbolic)
            {
                await context.StandardError.WriteLineAsync(text["HardLinkFailed", link, target, CommandText.Reason(context, Errno.NotSupported)]);
                await context.StandardError.WriteLineAsync(text["HardLinkHint"]);
                status = 1;
                continue;
            }

            try
            {
                if (force && (await KazoFile.ExistsAsync(link, cancellationToken) || (await KazoFileInfo.GetAsync(link, cancellationToken)).IsSymbolicLink))
                {
                    await KazoFile.DeleteAsync(link, cancellationToken);
                }

                await KazoFile.CreateSymbolicLinkAsync(link, target, cancellationToken);
                if (verbose)
                {
                    await context.StandardOutput.WriteLineAsync($"'{link}' -> '{target}'");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                string reason = exception is KazoIOException && !force && await KazoFile.ExistsAsync(link, cancellationToken)
                    ? CommandText.Reason(context, Errno.FileExists)
                    : CommandText.Describe(context, exception);
                await context.StandardError.WriteLineAsync(text["SymbolicLinkFailed", link, reason]);
                status = 1;
            }
        }

        return status;
    }

    private static string BaseName(string path)
    {
        string trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
