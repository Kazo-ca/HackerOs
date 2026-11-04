using System.Text.RegularExpressions;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Common;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Grep;

/// <summary>
/// Implements the <c>grep</c> terminal command (`P4-W5-CMD-003`).
/// Searches stdin or virtual files for matching lines.
/// Supports -i (case-insensitive), -n (line numbers), -v (invert match), -r/-R (search directories
/// recursively, the working directory when no file is given).
/// </summary>
/// <remarks>
/// ADR 0069: every line read and every file visited is a <see cref="KazoProcess.CheckpointAsync"/>,
/// so <c>grep -r</c> over a large tree keeps the OS responsive and stops at once on <c>kill</c>.
/// </remarks>
public sealed class GrepCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public GrepCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        bool ignoreCase = false;
        bool lineNumbers = false;
        bool invertMatch = false;
        bool recursive = false;

        string? pattern = null;
        List<string> files = [];

        var args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-i" or "--ignore-case") ignoreCase = true;
            else if (a is "-n" or "--line-number") lineNumbers = true;
            else if (a is "-v" or "--invert-match") invertMatch = true;
            else if (a is "-r" or "-R" or "--recursive") recursive = true;
            else if (a.Length > 2 && a[0] == '-' && a[1] != '-' && a.Skip(1).All(static c => c is 'i' or 'n' or 'v' or 'r' or 'R'))
            {
                // Bundled short options: -rn, -inv...
                ignoreCase |= a.Contains('i');
                lineNumbers |= a.Contains('n');
                invertMatch |= a.Contains('v');
                recursive |= a.Contains('r') || a.Contains('R');
            }
            else if (pattern is null && !a.StartsWith('-')) pattern = a;
            else if (!a.StartsWith('-')) files.Add(a);
        }

        if (pattern is null)
        {
            context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["MissingPattern"]);
            return 2;
        }

        var regexOpts = RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        Regex regex;
        try { regex = new Regex(pattern, regexOpts); }
        catch
        {
            context.StandardError.WriteLine(context.App.GetLocalizer<Strings>()["InvalidRegex", pattern]);
            return 2;
        }

        int matchesCount = 0;
        if (recursive && files.Count == 0)
        {
            files.Add(".");
        }

        if (files.Count == 0)
        {
            int lineNo = 0;
            string? line;
            while ((line = await context.StandardInput.ReadLineAsync(cancellationToken)) != null)
            {
                await KazoProcess.CheckpointAsync(cancellationToken);
                lineNo++;
                bool matches = regex.IsMatch(line);
                if (invertMatch ? !matches : matches)
                {
                    matchesCount++;
                    if (lineNumbers) context.StandardOutput.WriteLine($"{lineNo}:{line}");
                    else context.StandardOutput.WriteLine(line);
                }
            }
        }
        else
        {
            List<(string Display, string Path)> targets = [];
            foreach (var file in files)
            {
                var resolved = ResolvePath(context.WorkingDirectory, file);
                if (recursive && await KazoDirectory.ExistsAsync(resolved, cancellationToken))
                {
                    await CollectFilesAsync(file, resolved, targets, cancellationToken);
                }
                else
                {
                    targets.Add((file, resolved));
                }
            }

            bool showNames = targets.Count > 1 || recursive;
            foreach ((string file, string resolved) in targets)
            {
                await KazoProcess.CheckpointAsync(cancellationToken);
                var req = new FileSystemReadRequest(VirtualPath.Parse(resolved));
                var res = await context.App.FileSystem.ReadAsync(req, cancellationToken);
                if (!res.Succeeded || res.Value is null)
                {
                    context.StandardError.WriteLine(CommandText.FileError(context, "grep", file, CommandText.Reason(context, CommandIO.ReadErrno(res.Error))));
                    continue;
                }

                using var reader = new StreamReader(res.Value.Content);
                int lineNo = 0;
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    await KazoProcess.CheckpointAsync(cancellationToken);
                    lineNo++;
                    bool matches = regex.IsMatch(line);
                    if (invertMatch ? !matches : matches)
                    {
                        matchesCount++;
                        string prefix = showNames ? $"{file}:" : "";
                        string lineNoStr = lineNumbers ? $"{lineNo}:" : "";
                        context.StandardOutput.WriteLine($"{prefix}{lineNoStr}{line}");
                    }
                }
            }
        }

        return matchesCount > 0 ? 0 : 1;
    }

    /// <summary>
    /// Lists the regular files under <paramref name="directory"/> in ordinal order, like
    /// <c>grep -r</c>; symbolic links met during the walk are not followed.
    /// </summary>
    private static async Task CollectFilesAsync(
        string display, string directory, List<(string Display, string Path)> targets, CancellationToken cancellationToken)
    {
        KazoDirectoryInfo info = await KazoDirectoryInfo.GetAsync(directory, cancellationToken);
        foreach (KazoFileSystemInfo child in (await info.GetFileSystemInfosAsync(cancellationToken: cancellationToken)).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            await KazoProcess.CheckpointAsync(cancellationToken);
            if (child.IsSymbolicLink)
            {
                continue;
            }

            string childDisplay = display == "." ? child.Name : display.TrimEnd('/') + "/" + child.Name;
            if (child is KazoDirectoryInfo)
            {
                await CollectFilesAsync(childDisplay, child.FullName, targets, cancellationToken);
            }
            else
            {
                targets.Add((childDisplay, child.FullName));
            }
        }
    }

    private static string ResolvePath(string cwd, string path) =>
        path.StartsWith('/') ? path : (cwd.TrimEnd('/') + "/" + path).Replace("//", "/");
}
