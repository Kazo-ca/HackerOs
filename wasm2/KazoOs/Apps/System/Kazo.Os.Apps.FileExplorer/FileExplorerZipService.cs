using System.IO.Compression;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The one implementation of zip compress/extract against a virtual filesystem — shared between
/// <c>FileExplorerWindow</c>'s toolbar (Compress/Extract) and the "UnZip Here…" entry of its file menu,
/// so there is exactly one unzip implementation, not two.
/// </summary>
internal static class FileExplorerZipService
{
    public static async Task AddFileToZipAsync(
        IAppFileSystemGateway fileSystem, ZipArchive archive, VirtualPath filePath, string entryName, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> result = await fileSystem.ReadAsync(
            new FileSystemReadRequest(filePath), cancellationToken);

        if (result.Succeeded && result.Value is not null)
        {
            await using FileSystemContentReadHandle handle = result.Value;
            ZipArchiveEntry zipEntry = archive.CreateEntry(entryName);
            using Stream entryStream = zipEntry.Open();
            await handle.Content.CopyToAsync(entryStream, cancellationToken);
        }
    }

    public static async Task AddDirectoryToZipAsync(
        IAppFileSystemGateway fileSystem, ZipArchive archive, VirtualPath dirPath, string currentZipPath, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemDirectorySnapshot> result = await fileSystem.EnumerateAsync(
            new FileSystemEnumerateRequest(dirPath), cancellationToken);

        if (result.Succeeded && result.Value is not null)
        {
            foreach (FileSystemDirectoryItem item in result.Value.Entries)
            {
                // ADR 0069: compressing a tree is long work on the UI thread.
                await HackProcess.CheckpointAsync(cancellationToken);
                VirtualPath childPath = VirtualPath.Parse(dirPath.Value.TrimEnd('/') + "/" + item.Name.Value);
                string zipEntryPath = currentZipPath + "/" + item.Name.Value;

                if (item.Metadata.Kind == FileSystemEntryKind.File)
                {
                    await AddFileToZipAsync(fileSystem, archive, childPath, zipEntryPath, cancellationToken);
                }
                else if (item.Metadata.Kind == FileSystemEntryKind.Directory)
                {
                    await AddDirectoryToZipAsync(fileSystem, archive, childPath, zipEntryPath, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="destinationDirectory"/> (created fresh —
    /// fails if it already exists). Returns a human-readable error message in the explorer's language
    /// (<paramref name="text"/>), or <see langword="null"/> on success.
    /// </summary>
    public static async Task<string?> ExtractAsync(
        IAppFileSystemGateway fileSystem, VirtualPath zipPath, VirtualPath destinationDirectory, IStringLocalizer text, CancellationToken cancellationToken) =>
        Localize(await ExtractCoreAsync(fileSystem, zipPath, destinationDirectory, cancellationToken), text);

    /// <summary>
    /// Extracts the archive in a background task (ADR 0128, ADR 0140): reading, inflating and writing
    /// every entry happen in a child process of the explorer (<c>file-explorer:extract</c>), in a Web
    /// Worker when the host has some, so a large archive never freezes the desktop.
    /// </summary>
    /// <returns>A message in the explorer's language, or <see langword="null"/> on success.</returns>
    public static async Task<string?> ExtractInBackgroundAsync(
        VirtualPath zipPath, VirtualPath destinationDirectory, IStringLocalizer text, CancellationToken cancellationToken)
    {
        ZipFailure? failure = await HackBackground.RunAsync<ZipExtractJobInput, ZipFailure?>(
            FileExplorerZipJobs.Extract,
            new ZipExtractJobInput(zipPath.Value, destinationDirectory.Value),
            options: new HackBackgroundOptions { Name = "extract" },
            cancellationToken: cancellationToken);
        return Localize(failure, text);
    }

    /// <summary>
    /// Compresses <paramref name="sources"/> into a new archive at <paramref name="destination"/>, in a
    /// background task (<c>file-explorer:compress</c>): deflating is CPU work the window must not do.
    /// </summary>
    /// <returns>A message in the explorer's language, or <see langword="null"/> on success.</returns>
    public static async Task<string?> CompressInBackgroundAsync(
        IReadOnlyList<ZipSource> sources, VirtualPath destination, IStringLocalizer text, CancellationToken cancellationToken)
    {
        ZipFailure? failure = await HackBackground.RunAsync<ZipCompressJobInput, ZipFailure?>(
            FileExplorerZipJobs.Compress,
            new ZipCompressJobInput([.. sources], destination.Value),
            options: new HackBackgroundOptions { Name = "compress" },
            cancellationToken: cancellationToken);
        return Localize(failure, text);
    }

    /// <summary>Writes a new archive of <paramref name="sources"/> at <paramref name="destination"/>.</summary>
    /// <returns>Why it failed, or <see langword="null"/>.</returns>
    internal static async Task<ZipFailure?> CompressCoreAsync(
        IAppFileSystemGateway fileSystem, IReadOnlyList<ZipSource> sources, VirtualPath destination, CancellationToken cancellationToken)
    {
        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, true))
        {
            foreach (ZipSource source in sources)
            {
                VirtualPath path = VirtualPath.Parse(source.Path);
                if (source.IsDirectory)
                {
                    await AddDirectoryToZipAsync(fileSystem, archive, path, source.EntryName, cancellationToken);
                }
                else
                {
                    await AddFileToZipAsync(fileSystem, archive, path, source.EntryName, cancellationToken);
                }
            }
        }

        ms.Position = 0;
        FileSystemMutationResult createResult = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(destination, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)),
            cancellationToken);
        if (!createResult.Succeeded)
        {
            return new ZipFailure("Error.CreateArchive");
        }

        MemoryStreamContentSource content = new(FileSystemContentDescriptor.Binary("application/zip"), ms);
        FileSystemMutationResult writeResult = await fileSystem.WriteAsync(new FileSystemWriteRequest(destination), content, cancellationToken);
        return writeResult.Succeeded ? null : new ZipFailure("Error.WriteArchive");
    }

    /// <summary>Extracts <paramref name="zipPath"/> into a new <paramref name="destinationDirectory"/>.</summary>
    /// <returns>Why it failed (a text key of the explorer and its argument), or <see langword="null"/>.</returns>
    internal static async Task<ZipFailure?> ExtractCoreAsync(
        IAppFileSystemGateway fileSystem, VirtualPath zipPath, VirtualPath destinationDirectory, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> readResult = await fileSystem.ReadAsync(
            new FileSystemReadRequest(zipPath), cancellationToken);
        if (!readResult.Succeeded || readResult.Value is null)
        {
            return new ZipFailure("Error.ReadArchive");
        }

        using MemoryStream ms = new();
        await using (FileSystemContentReadHandle handle = readResult.Value)
        {
            await handle.Content.CopyToAsync(ms, cancellationToken);
        }
        ms.Position = 0;

        FileSystemResult<FileSystemEntrySnapshot> destStat = await fileSystem.StatAsync(
            new FileSystemStatRequest(destinationDirectory), cancellationToken);
        if (destStat.Succeeded)
        {
            string folderName = destinationDirectory.Value[(destinationDirectory.Value.LastIndexOf('/') + 1)..];
            return new ZipFailure("Error.AlreadyExists", folderName);
        }

        FileSystemMutationResult dirCreate = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(destinationDirectory, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0b111_101_101)),
            cancellationToken);
        if (!dirCreate.Succeeded)
        {
            return new ZipFailure("Error.CreateDestination");
        }

        HashSet<string> ensuredDirs = new(StringComparer.Ordinal) { destinationDirectory.Value };

        try
        {
            using ZipArchive archive = new(ms, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                await HackProcess.CheckpointAsync(cancellationToken);
                string entryName = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrEmpty(entryName))
                {
                    continue;
                }

                string entryFullPath = destinationDirectory.Value + "/" + entryName.TrimEnd('/');

                if (entryName.EndsWith('/'))
                {
                    await EnsureDirectoryAsync(fileSystem, entryFullPath, ensuredDirs, cancellationToken);
                    continue;
                }

                int lastSlash = entryFullPath.LastIndexOf('/');
                string parentPath = lastSlash > 0 ? entryFullPath[..lastSlash] : destinationDirectory.Value;
                await EnsureDirectoryAsync(fileSystem, parentPath, ensuredDirs, cancellationToken);

                VirtualPath entryPath = VirtualPath.Parse(entryFullPath);
                FileSystemMutationResult fileCreate = await fileSystem.CreateAsync(
                    new FileSystemCreateRequest(entryPath, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)),
                    cancellationToken);
                if (!fileCreate.Succeeded)
                {
                    continue;
                }

                using MemoryStream entryBuffer = new();
                await using (Stream entryStream = entry.Open())
                {
                    await entryStream.CopyToAsync(entryBuffer, cancellationToken);
                }
                entryBuffer.Position = 0;

                MemoryStreamContentSource entrySource = new(FileSystemContentDescriptor.Binary(), entryBuffer);
                await fileSystem.WriteAsync(new FileSystemWriteRequest(entryPath), entrySource, cancellationToken);
            }
        }
        catch (InvalidDataException)
        {
            return new ZipFailure("Error.InvalidZip");
        }

        return null;
    }

    private static string? Localize(ZipFailure? failure, IStringLocalizer text)
    {
        if (failure is null)
        {
            return null;
        }

        LocalizedString message = failure.Argument is { } argument ? text[failure.Key, argument] : text[failure.Key];
        return message.Value;
    }

    private static async Task EnsureDirectoryAsync(
        IAppFileSystemGateway fileSystem, string dirPath, HashSet<string> ensuredDirs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(dirPath) || ensuredDirs.Contains(dirPath))
        {
            return;
        }

        int lastSlash = dirPath.LastIndexOf('/');
        string parent = lastSlash > 0 ? dirPath[..lastSlash] : string.Empty;
        if (!string.IsNullOrEmpty(parent))
        {
            await EnsureDirectoryAsync(fileSystem, parent, ensuredDirs, cancellationToken);
        }

        VirtualPath path = VirtualPath.Parse(dirPath);
        FileSystemResult<FileSystemEntrySnapshot> stat = await fileSystem.StatAsync(new FileSystemStatRequest(path), cancellationToken);
        if (!stat.Succeeded)
        {
            await fileSystem.CreateAsync(
                new FileSystemCreateRequest(path, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0b111_101_101)), cancellationToken);
        }

        ensuredDirs.Add(dirPath);
    }
}

/// <summary>Wraps an in-memory buffer as an <see cref="IFileSystemContentSource"/> for a single write.</summary>
internal sealed class MemoryStreamContentSource : IFileSystemContentSource
{
    private readonly MemoryStream _stream;

    public MemoryStreamContentSource(FileSystemContentDescriptor descriptor, MemoryStream stream)
    {
        Descriptor = descriptor;
        _stream = stream;
        Length = stream.Length;
    }

    public FileSystemContentDescriptor Descriptor { get; }
    public long? Length { get; }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        _stream.Position = 0;
        return ValueTask.FromResult<Stream>(_stream);
    }
}
