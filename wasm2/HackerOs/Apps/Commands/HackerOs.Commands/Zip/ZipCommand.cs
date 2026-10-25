using System.IO.Compression;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Diagnostics;
using HackerOs.AppSdk.IO;
using HackerOs.Commands.Common;
using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Zip;

/// <summary>
/// <c>zip [-r] [-q] [-j] [-d] [-0..-9] ARCHIVE.zip FILE...</c> (ADR 0064), Info-ZIP style: adds
/// (or updates) files in a real deflate-compressed zip archive that any zip tool can open,
/// recursing into directories with <c>-r</c>, junking paths with <c>-j</c>, deleting entries with
/// <c>-d</c>. Unix permission bits are stored in the entries' external attributes, so
/// <c>unzip</c> restores an executable script as executable. <c>.zip</c> is added to a name
/// without extension.
/// </summary>
public sealed class ZipCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Errors and warnings follow the process language; the "adding:" progress lines stay as Info-ZIP prints them.
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        bool recursive = false, quiet = false, junkPaths = false, delete = false;
        CompressionLevel level = CompressionLevel.Optimal;
        List<string> operands = [];
        foreach (string arg in context.Arguments)
        {
            if (arg.Length > 1 && arg[0] == '-' && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                foreach (char flag in arg[1..])
                {
                    switch (flag)
                    {
                        case 'r' or 'R': recursive = true; break;
                        case 'q': quiet = true; break;
                        case 'j': junkPaths = true; break;
                        case 'd': delete = true; break;
                        case '0': level = CompressionLevel.NoCompression; break;
                        case >= '1' and <= '3': level = CompressionLevel.Fastest; break;
                        case >= '4' and <= '9': level = flag == '9' ? CompressionLevel.SmallestSize : CompressionLevel.Optimal; break;
                        case 'v' or 'y' or 'u' or 'f' or 'm' or 'T': break;
                        default:
                            await context.StandardError.WriteLineAsync(text["ShortOption", flag]);
                            return 16;
                    }
                }

                continue;
            }

            operands.Add(arg);
        }

        if (operands.Count < 2)
        {
            await context.StandardError.WriteLineAsync(operands.Count == 0 ? text["NothingNoArchive"] : text["Nothing", operands[0]]);
            return 12;
        }

        string archive = HackPath.HasExtension(operands[0]) ? operands[0] : operands[0] + ".zip";
        using MemoryStream buffer = new();
        bool existed = await HackFile.ExistsAsync(archive, cancellationToken);
        try
        {
            if (existed)
            {
                byte[] previous = await HackFile.ReadAllBytesAsync(archive, cancellationToken);
                await buffer.WriteAsync(previous, cancellationToken);
                buffer.Position = 0;
            }

            int status = 0;
            using (ZipArchive zip = new(buffer, existed ? ZipArchiveMode.Update : ZipArchiveMode.Create, leaveOpen: true))
            {
                if (delete)
                {
                    foreach (string name in operands.Skip(1))
                    {
                        List<ZipArchiveEntry> matches = [.. zip.Entries.Where(e => e.FullName == name || e.FullName.StartsWith(name.TrimEnd('/') + "/", StringComparison.Ordinal))];
                        if (matches.Count == 0)
                        {
                            await context.StandardError.WriteLineAsync(text["NotMatched", name]);
                            status = 12;
                        }

                        foreach (ZipArchiveEntry entry in matches)
                        {
                            await HackProcess.CheckpointAsync(cancellationToken);
                            if (!quiet)
                            {
                                await context.StandardOutput.WriteLineAsync($"deleting: {entry.FullName}");
                            }

                            entry.Delete();
                        }
                    }
                }
                else
                {
                    foreach (string source in operands.Skip(1))
                    {
                        status |= await AddAsync(context, zip, source, recursive, junkPaths, quiet, level, cancellationToken);
                    }
                }
            }

            await HackFile.WriteAllBytesAsync(archive, buffer.ToArray(), cancellationToken);
            return status;
        }
        catch (InvalidDataException)
        {
            await context.StandardError.WriteLineAsync(text["InvalidStructure", archive]);
            return 3;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await context.StandardError.WriteLineAsync(text["FileError", archive, CommandText.Describe(context, exception)]);
            return 15;
        }
    }

    private static async Task<int> AddAsync(
        TerminalExecutionContext context, ZipArchive zip, string source, bool recursive, bool junkPaths, bool quiet,
        CompressionLevel level, CancellationToken cancellationToken)
    {
        string entryName = EntryName(source, junkPaths);
        if (await HackDirectory.ExistsAsync(source, cancellationToken))
        {
            if (!junkPaths)
            {
                Replace(zip, entryName + "/");
                ZipArchiveEntry directory = zip.CreateEntry(entryName + "/");
                StampModified(directory, (await HackDirectoryInfo.GetAsync(source, cancellationToken)).LastWriteTimeUtc);
                directory.ExternalAttributes = UnixAttributes(await HackFile.GetUnixFileModeAsync(source, cancellationToken), isDirectory: true);
                if (!quiet)
                {
                    await context.StandardOutput.WriteLineAsync($"  adding: {entryName}/ (stored 0%)");
                }
            }

            if (!recursive)
            {
                return 0;
            }

            int status = 0;
            foreach (string child in (await HackDirectory.GetFileSystemEntriesAsync(source, cancellationToken: cancellationToken)).Order(StringComparer.Ordinal))
            {
                // ADR 0069: compressing a tree is long work; give the thread back between entries.
                await HackProcess.CheckpointAsync(cancellationToken);
                string childPath = source.TrimEnd('/') + "/" + HackPath.GetFileName(child);
                status |= await AddAsync(context, zip, childPath, recursive, junkPaths, quiet, level, cancellationToken);
            }

            return status;
        }

        if (!await HackFile.ExistsAsync(source, cancellationToken))
        {
            await context.StandardError.WriteLineAsync(context.App.GetLocalizer<Strings>()["NotMatched", source]);
            return 12;
        }

        byte[] content = await HackFile.ReadAllBytesAsync(source, cancellationToken);
        bool updating = Replace(zip, entryName);
        ZipArchiveEntry entry = zip.CreateEntry(entryName, level);
        StampModified(entry, (await HackFileInfo.GetAsync(source, cancellationToken)).LastWriteTimeUtc);
        entry.ExternalAttributes = UnixAttributes(await HackFile.GetUnixFileModeAsync(source, cancellationToken), isDirectory: false);
        await using (Stream stream = entry.Open())
        {
            await stream.WriteAsync(content, cancellationToken);
        }

        if (!quiet)
        {
            // Info-ZIP reports the space saved; the compressed size is only known once written.
            string method = level == CompressionLevel.NoCompression || content.Length == 0 ? "stored 0%" : "deflated";
            await context.StandardOutput.WriteLineAsync($"{(updating ? "updating" : "  adding")}: {entryName} ({method})");
        }

        return 0;
    }

    /// <summary>
    /// Stamps an entry with its file's modification time, as Info-ZIP does: <see cref="ZipArchive.CreateEntry(string)"/>
    /// would take the host's wall clock, not the OS's simulated one. A time a zip header cannot hold
    /// (before 1980) keeps the default.
    /// </summary>
    private static void StampModified(ZipArchiveEntry entry, DateTime modifiedUtc)
    {
        if (modifiedUtc.Year is >= 1980 and <= 2107)
        {
            entry.LastWriteTime = new DateTimeOffset(modifiedUtc, TimeSpan.Zero);
        }
    }

    /// <summary>Removes an existing entry of that name (an update); true when there was one.</summary>
    private static bool Replace(ZipArchive zip, string name)
    {
        ZipArchiveEntry? existing = zip.Mode == ZipArchiveMode.Create ? null : zip.GetEntry(name);
        existing?.Delete();
        return existing is not null;
    }

    /// <summary>The name inside the archive: relative, without <c>./</c> or a leading <c>/</c>, or the bare name with <c>-j</c>.</summary>
    private static string EntryName(string path, bool junkPaths)
    {
        string trimmed = path.TrimEnd('/');
        if (junkPaths)
        {
            return HackPath.GetFileName(trimmed) ?? trimmed;
        }

        while (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.TrimStart('/');
    }

    /// <summary>Unix mode in the high 16 bits of the external attributes (Info-ZIP "made by Unix" convention).</summary>
    internal static int UnixAttributes(UnixFileMode mode, bool isDirectory) =>
        (((isDirectory ? 0x4000 : 0x8000) | ((int)mode & 0x1FF)) << 16) | (isDirectory ? 0x10 : 0);
}
