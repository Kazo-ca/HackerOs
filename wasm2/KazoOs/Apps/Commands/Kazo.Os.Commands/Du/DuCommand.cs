using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Du;

/// <summary>
/// <c>du [-a] [-s] [-h] [-c] [-b] [-d N] [PATH...]</c> (ADR 0064): disk usage of each directory,
/// counted as an ext4-like disk would allocate it: every file rounded up to whole 4 KiB blocks,
/// every directory one block. Sizes print in 1 KiB units (<c>-h</c> human readable, <c>-b</c>
/// apparent bytes). A directory it cannot read is reported and skipped.
/// </summary>
public sealed class DuCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    private const long BlockSize = 4096;

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool all = false, summarize = false, human = false, total = false, bytes = false;
        int maxDepth = int.MaxValue;
        List<string> paths = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg is "-d" or "--max-depth" && i + 1 < args.Count && int.TryParse(args[i + 1], out int depth))
            {
                maxDepth = depth;
                i++;
                continue;
            }

            if (arg.StartsWith("--max-depth=", StringComparison.Ordinal) && int.TryParse(arg[12..], out int inline))
            {
                maxDepth = inline;
                continue;
            }

            if (arg.StartsWith('-') && arg.Length > 1 && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                foreach (char flag in arg[1..])
                {
                    switch (flag)
                    {
                        case 'a': all = true; break;
                        case 's': summarize = true; break;
                        case 'h': human = true; break;
                        case 'c': total = true; break;
                        case 'b': bytes = true; break;
                        case 'k' or 'x' or 'L' or 'P': break;
                        default:
                            await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["InvalidOption", flag]);
                            return 1;
                    }
                }

                continue;
            }

            paths.Add(arg);
        }

        if (paths.Count == 0)
        {
            paths.Add(".");
        }

        if (summarize)
        {
            maxDepth = 0;
        }

        Walker walker = new(context, all, maxDepth, bytes, human, cancellationToken);
        long grandTotal = 0;
        foreach (string path in paths)
        {
            grandTotal += await walker.MeasureAsync(path, path, 0);
        }

        if (total)
        {
            await context.StandardOutput.WriteLineAsync($"{walker.Format(grandTotal)}\ttotal");
        }

        return walker.Failed ? 1 : 0;
    }

    private sealed class Walker(TerminalExecutionContext context, bool all, int maxDepth, bool bytes, bool human, CancellationToken cancellationToken)
    {
        public bool Failed { get; private set; }

        public string Format(long size) =>
            human ? CommandIO.HumanSize(size)
            : bytes ? size.ToString(CultureInfo.InvariantCulture)
            : ((size + 1023) / 1024).ToString(CultureInfo.InvariantCulture);

        private long Allocated(long length) => bytes ? length : (length + BlockSize - 1) / BlockSize * BlockSize;

        /// <summary>Returns the usage of <paramref name="path"/>, printing it (and its children within the depth).</summary>
        public async Task<long> MeasureAsync(string path, string display, int depth)
        {
            // ADR 0069: one checkpoint per directory level, and per child below.
            await KazoProcess.CheckpointAsync(cancellationToken);
            try
            {
                if (!await KazoDirectory.ExistsAsync(path, cancellationToken))
                {
                    KazoFileInfo file = await KazoFileInfo.GetAsync(path, cancellationToken);
                    if (!file.Exists)
                    {
                        await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["CannotAccess", display, CommandText.Reason(context, Errno.NotFound)]);
                        Failed = true;
                        return 0;
                    }

                    long size = Allocated(file.Length);
                    if (depth == 0 || (all && depth <= maxDepth))
                    {
                        await context.StandardOutput.WriteLineAsync($"{Format(size)}\t{display}");
                    }

                    return size;
                }

                long sum = bytes ? 0 : BlockSize;
                KazoDirectoryInfo directory = await KazoDirectoryInfo.GetAsync(path, cancellationToken);
                foreach (KazoFileSystemInfo child in (await directory.GetFileSystemInfosAsync(cancellationToken: cancellationToken)).OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    await KazoProcess.CheckpointAsync(cancellationToken);
                    string childDisplay = display.TrimEnd('/') + "/" + child.Name;
                    if (child.IsSymbolicLink)
                    {
                        sum += bytes ? (child.LinkTarget?.Length ?? 0) : 0;
                        continue;
                    }

                    if (child is KazoDirectoryInfo)
                    {
                        sum += await MeasureAsync(child.FullName, childDisplay, depth + 1);
                    }
                    else if (child is KazoFileInfo file)
                    {
                        long size = Allocated(file.Length);
                        sum += size;
                        if (all && depth + 1 <= maxDepth)
                        {
                            await context.StandardOutput.WriteLineAsync($"{Format(size)}\t{childDisplay}");
                        }
                    }
                }

                if (depth <= maxDepth)
                {
                    await context.StandardOutput.WriteLineAsync($"{Format(sum)}\t{display}");
                }

                return sum;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["CannotReadDirectory", display, CommandText.Describe(context, exception)]);
                Failed = true;
                return 0;
            }
        }
    }
}
