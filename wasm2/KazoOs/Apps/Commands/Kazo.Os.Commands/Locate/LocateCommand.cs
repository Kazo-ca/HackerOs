using System.Text.RegularExpressions;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Locate;

/// <summary>
/// <c>locate [-i] [-b] [-c] [-l N] [-e] PATTERN...</c> (ADR 0064): prints every path whose name
/// matches. A pattern without wildcards matches anywhere in the path (<c>*PATTERN*</c>), as
/// mlocate does; with wildcards it must match the whole path (or the base name with <c>-b</c>).
/// Instead of reading an <c>updatedb</c> database, it walks the filesystem live with the user's
/// own permissions, skipping <c>/proc</c>, <c>/dev</c> and <c>/sys</c> and every directory it
/// cannot read, so it never reveals a name the user could not list.
/// </summary>
public sealed class LocateCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    private static readonly HashSet<string> Pruned = new(StringComparer.Ordinal) { "/proc", "/dev", "/sys", "/run" };

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool ignoreCase = false, baseName = false, countOnly = false, existing = false;
        long limit = long.MaxValue;
        List<string> patterns = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-i" or "--ignore-case": ignoreCase = true; break;
                case "-b" or "--basename": baseName = true; break;
                case "-c" or "--count": countOnly = true; break;
                case "-e" or "--existing": existing = true; break;
                case "-l" or "-n" or "--limit" when i + 1 < args.Count && long.TryParse(args[i + 1], out long n):
                    limit = n;
                    i++;
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["InvalidOption", arg.TrimStart('-')]);
                        return 1;
                    }

                    patterns.Add(arg);
                    break;
            }
        }

        _ = existing;
        if (patterns.Count == 0)
        {
            await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["NoPattern"]);
            return 1;
        }

        List<Func<string, bool>> matchers = [.. patterns.Select(pattern => BuildMatcher(pattern, ignoreCase, baseName))];
        long found = 0;
        HashSet<string> visited = new(StringComparer.Ordinal);
        Stack<string> directories = new();
        directories.Push("/");
        while (directories.Count > 0 && found < limit)
        {
            // ADR 0069: locate walks the whole disk; give the thread back between directories.
            await HackProcess.CheckpointAsync(cancellationToken);
            string directory = directories.Pop();
            if (!visited.Add(directory) || Pruned.Contains(directory))
            {
                continue;
            }

            string[] entries;
            string[] subdirectories;
            try
            {
                entries = await HackDirectory.GetFileSystemEntriesAsync(directory, cancellationToken: cancellationToken);
                subdirectories = await HackDirectory.GetDirectoriesAsync(directory, cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (string entry in entries.Order(StringComparer.Ordinal))
            {
                if (found >= limit)
                {
                    break;
                }

                if (matchers.Any(match => match(entry)))
                {
                    found++;
                    if (!countOnly)
                    {
                        await context.StandardOutput.WriteLineAsync(entry);
                    }
                }
            }

            foreach (string subdirectory in subdirectories.OrderDescending(StringComparer.Ordinal))
            {
                if (visited.Count < 100_000)
                {
                    directories.Push(subdirectory);
                }
            }
        }

        if (countOnly)
        {
            await context.StandardOutput.WriteLineAsync(found.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return found > 0 ? 0 : 1;
    }

    private static Func<string, bool> BuildMatcher(string pattern, bool ignoreCase, bool baseName)
    {
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Wildcard.HasWildcards(pattern))
        {
            return path => (baseName ? HackPath.GetFileName(path) ?? path : path).Contains(pattern, comparison);
        }

        Regex regex = Wildcard.ToRegex(pattern, ignoreCase);
        return path => regex.IsMatch(baseName || !pattern.Contains('/') ? HackPath.GetFileName(path) ?? path : path);
    }
}
