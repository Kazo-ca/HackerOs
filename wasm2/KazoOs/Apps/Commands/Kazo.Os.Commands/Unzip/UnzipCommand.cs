using System.Globalization;
using System.IO.Compression;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Unzip;

/// <summary>
/// <c>unzip [-l] [-t] [-o|-n] [-q] [-p] [-d DIR] ARCHIVE[.zip] [FILE...]</c> (ADR 0064),
/// Info-ZIP style: lists (<c>-l</c>), tests (<c>-t</c>), prints (<c>-p</c>) or extracts the
/// entries (all, or those matching the FILE patterns) under the current directory or
/// <c>-d DIR</c>, restoring Unix permission bits stored by <c>zip</c>. An existing file is kept
/// unless <c>-o</c>; entries escaping the destination (<c>../</c>) are refused ("zip slip").
/// </summary>
public sealed class UnzipCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Messages follow the process language; the progress lines and the listing stay as Info-ZIP prints them.
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        bool list = false, test = false, overwrite = false, never = false, quiet = false, pipe = false, junk = false;
        string? destination = null;
        List<string> operands = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "-d" && i + 1 < args.Count)
            {
                destination = args[++i];
                continue;
            }

            if (arg.Length > 1 && arg[0] == '-')
            {
                foreach (char flag in arg[1..])
                {
                    switch (flag)
                    {
                        case 'l' or 'v' or 'Z': list = true; break;
                        case 't': test = true; break;
                        case 'o': overwrite = true; break;
                        case 'n': never = true; break;
                        case 'q': quiet = true; break;
                        case 'p' or 'c': pipe = true; quiet = true; break;
                        case 'j': junk = true; break;
                        case 'u' or 'f' or 'a' or 'X': break;
                        default:
                            await context.StandardError.WriteLineAsync(text["UnknownOption", flag]);
                            return 10;
                    }
                }

                continue;
            }

            operands.Add(arg);
        }

        if (operands.Count == 0)
        {
            await context.StandardError.WriteLineAsync(text["Usage"]);
            return 10;
        }

        string archive = operands[0];
        if (!await KazoFile.ExistsAsync(archive, cancellationToken) && await KazoFile.ExistsAsync(archive + ".zip", cancellationToken))
        {
            archive += ".zip";
        }

        byte[] bytes;
        try
        {
            bytes = await KazoFile.ReadAllBytesAsync(archive, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await context.StandardError.WriteLineAsync(text["CannotOpen", operands[0], CommandText.Describe(context, exception)]);
            return 9;
        }

        List<System.Text.RegularExpressions.Regex> filters = [.. operands.Skip(1).Select(pattern => Wildcard.ToRegex(pattern, ignoreCase: false))];
        try
        {
            using MemoryStream stream = new(bytes);
            using ZipArchive zip = new(stream, ZipArchiveMode.Read);
            List<ZipArchiveEntry> entries = [.. zip.Entries.Where(entry => filters.Count == 0 || filters.Any(filter => filter.IsMatch(entry.FullName)))];
            if (list)
            {
                return await ListAsync(context, archive, entries);
            }

            if (!quiet && !pipe)
            {
                await context.StandardOutput.WriteLineAsync($"Archive:  {archive}");
            }

            if (test)
            {
                foreach (ZipArchiveEntry entry in entries)
                {
                    await KazoProcess.CheckpointAsync(cancellationToken);
                    await using Stream content = entry.Open();
                    await content.CopyToAsync(Stream.Null, cancellationToken);
                    if (!quiet)
                    {
                        await context.StandardOutput.WriteLineAsync($"    testing: {entry.FullName,-24} OK");
                    }
                }

                await context.StandardOutput.WriteLineAsync(text["NoErrors", archive]);
                return 0;
            }

            string root = KazoPath.GetFullPath(destination ?? ".");
            if (destination is not null && !pipe)
            {
                await KazoDirectory.CreateDirectoryAsync(root, cancellationToken);
            }

            int status = 0;
            foreach (ZipArchiveEntry entry in entries)
            {
                // ADR 0069: extracting is long work; give the thread back between entries.
                await KazoProcess.CheckpointAsync(cancellationToken);
                bool isDirectory = entry.FullName.EndsWith('/');
                string relative = junk ? KazoPath.GetFileName(entry.FullName.TrimEnd('/')) ?? entry.FullName : entry.FullName.TrimEnd('/');
                if (pipe)
                {
                    if (!isDirectory)
                    {
                        using StreamReader reader = new(entry.Open());
                        await context.StandardOutput.WriteAsync(await reader.ReadToEndAsync(cancellationToken));
                    }

                    continue;
                }

                if (junk && isDirectory)
                {
                    continue;
                }

                string target = KazoPath.GetFullPath(relative, root);
                if (!target.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal))
                {
                    await context.StandardError.WriteLineAsync(text["PathEscapes", entry.FullName]);
                    status = 1;
                    continue;
                }

                if (isDirectory)
                {
                    await KazoDirectory.CreateDirectoryAsync(target, cancellationToken);
                    if (!quiet)
                    {
                        await context.StandardOutput.WriteLineAsync($"   creating: {Display(target)}/");
                    }

                    continue;
                }

                if (KazoPath.GetDirectoryName(target) is { } parent)
                {
                    await KazoDirectory.CreateDirectoryAsync(parent, cancellationToken);
                }

                if (await KazoFile.ExistsAsync(target, cancellationToken) && !overwrite)
                {
                    if (!quiet || never)
                    {
                        await context.StandardError.WriteLineAsync(text["ReplaceSkipped", Display(target)]);
                    }

                    continue;
                }

                using MemoryStream content = new();
                await using (Stream input = entry.Open())
                {
                    await input.CopyToAsync(content, cancellationToken);
                }

                await KazoFile.WriteAllBytesAsync(target, content.ToArray(), cancellationToken);
                int mode = (entry.ExternalAttributes >> 16) & 0x1FF;
                if (mode != 0)
                {
                    await KazoFile.SetUnixFileModeAsync(target, (UnixFileMode)mode, cancellationToken);
                }

                if (!quiet)
                {
                    await context.StandardOutput.WriteLineAsync($"  inflating: {Display(target)}");
                }
            }

            return status;
        }
        catch (InvalidDataException)
        {
            await context.StandardError.WriteLineAsync(text["NotZip1"]);
            await context.StandardError.WriteLineAsync(text["NotZip2"]);
            await context.StandardError.WriteLineAsync(text["NoDirectory", archive]);
            return 9;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await context.StandardError.WriteLineAsync(text["Error", CommandText.Describe(context, exception)]);
            return 50;
        }

        string Display(string path)
        {
            string cwd = KazoEnvironment.CurrentDirectory.TrimEnd('/') + "/";
            return path.StartsWith(cwd, StringComparison.Ordinal) ? path[cwd.Length..] : path;
        }
    }

    private static async Task<int> ListAsync(TerminalExecutionContext context, string archive, List<ZipArchiveEntry> entries)
    {
        await context.StandardOutput.WriteLineAsync($"Archive:  {archive}");
        await context.StandardOutput.WriteLineAsync("  Length      Date    Time    Name");
        await context.StandardOutput.WriteLineAsync("---------  ---------- -----   ----");
        long total = 0;
        foreach (ZipArchiveEntry entry in entries)
        {
            total += entry.Length;
            await context.StandardOutput.WriteLineAsync(
                $"{entry.Length,9}  {entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}   {entry.FullName}");
        }

        await context.StandardOutput.WriteLineAsync("---------                     -------");
        await context.StandardOutput.WriteLineAsync($"{total,9}                     {entries.Count} file{(entries.Count == 1 ? string.Empty : "s")}");
        return 0;
    }
}
