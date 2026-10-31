using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Diagnostics;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Apps.FileExplorer.Archives;

/// <summary>Where a path falls inside a <c>.zip</c>: the archive file and the path inside it.</summary>
/// <param name="Archive">The <c>.zip</c> file in the file system.</param>
/// <param name="EntryPath">The path inside the archive, without a leading slash; empty for its root.</param>
internal readonly record struct ZipArchiveLocation(VirtualPath Archive, string EntryPath)
{
    /// <summary>Gets whether the location is the archive itself, shown as its root folder.</summary>
    public bool IsRoot => EntryPath.Length == 0;
}

/// <summary>
/// Reads <c>.zip</c> files for the File Explorer, which shows them as read-only folders (ADR 0185 §6-9).
/// Nothing is mounted: a path such as <c>/home/u/Documents/photos.zip/2024/a.jpg</c> means "the entry
/// <c>2024/a.jpg</c> of the file <c>photos.zip</c>" only for the explorer, which resolves it here; the
/// file system, the other apps and the terminal never see inside the archive (§7).
/// </summary>
/// <remarks>
/// An archive is read whole once, then kept with its table of contents while its revision does not
/// change, for the last <see cref="CacheSize"/> archives: browsing its folders reads nothing more.
/// </remarks>
internal sealed class ZipArchiveBrowser
{
    /// <summary>How many archives stay in memory at once.</summary>
    internal const int CacheSize = 4;

    /// <summary>The extension of the archives the explorer opens (§8: <c>.zip</c> only).</summary>
    internal const string Extension = ".zip";

    private readonly IAppFileSystemGateway _fileSystem;
    private readonly LinkedList<CachedArchive> _cache = new();
    private readonly HashSet<string> _knownArchives = new(StringComparer.Ordinal);

    /// <summary>Creates a reader of the archives <paramref name="fileSystem"/> can read.</summary>
    /// <param name="fileSystem">The explorer's own file system, which reads the <c>.zip</c> files.</param>
    public ZipArchiveBrowser(IAppFileSystemGateway fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    /// <summary>Gets the file system the archives are read from.</summary>
    public IAppFileSystemGateway FileSystem => _fileSystem;

    /// <summary>Whether a file name is one of an archive the explorer opens as a folder.</summary>
    public static bool IsArchiveName(string name) =>
        name.Length > Extension.Length && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="path"/> is an archive already opened by this browser or a path inside one.
    /// Synchronous, for menus and toolbars: the folder shown was located when it was listed.
    /// </summary>
    public bool IsInsideArchive(VirtualPath path) =>
        _knownArchives.Contains(path.Value) || IsArchiveEntry(path);

    /// <summary>
    /// Whether <paramref name="path"/> is an entry strictly inside an archive already opened by this
    /// browser; the archive file itself is not (it can still be renamed, moved or deleted).
    /// </summary>
    public bool IsArchiveEntry(VirtualPath path) =>
        _knownArchives.Any(archive => path.Value.StartsWith(archive + "/", StringComparison.Ordinal));

    /// <summary>
    /// Finds the archive <paramref name="path"/> lies in: the first segment named <c>*.zip</c> that is a
    /// file (a folder named <c>x.zip</c> stays a folder). A path with no such segment costs no read.
    /// </summary>
    /// <returns>The location, or <see langword="null"/> for an ordinary path.</returns>
    public async ValueTask<ZipArchiveLocation?> LocateAsync(VirtualPath path, CancellationToken cancellationToken = default)
    {
        string[] segments = path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length; index++)
        {
            if (!IsArchiveName(segments[index]))
            {
                continue;
            }

            string archive = "/" + string.Join('/', segments, 0, index + 1);
            if (!_knownArchives.Contains(archive))
            {
                FileSystemResult<FileSystemEntrySnapshot> stat = await _fileSystem.StatAsync(
                    new FileSystemStatRequest(VirtualPath.Parse(archive)), cancellationToken);
                if (!stat.Succeeded || stat.Value!.Metadata.Kind != FileSystemEntryKind.File)
                {
                    continue;
                }

                _knownArchives.Add(archive);
            }

            return new ZipArchiveLocation(VirtualPath.Parse(archive), string.Join('/', segments, index + 1, segments.Length - index - 1));
        }

        return null;
    }

    /// <summary>Reads (or takes from the cache) the table of contents of an archive.</summary>
    /// <param name="archive">The <c>.zip</c> file.</param>
    /// <param name="operation">The operation reported in a failure.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The opened archive, or the reason it cannot be read.</returns>
    public async ValueTask<FileSystemResult<OpenedZipArchive>> OpenAsync(
        VirtualPath archive, FileSystemOperation operation, CancellationToken cancellationToken = default)
    {
        FileSystemResult<FileSystemEntrySnapshot> stat = await _fileSystem.StatAsync(new FileSystemStatRequest(archive), cancellationToken);
        if (!stat.Succeeded)
        {
            _knownArchives.Remove(archive.Value);
            return FileSystemResult<OpenedZipArchive>.Failure(stat.Error!);
        }

        FileSystemEntrySnapshot file = stat.Value!;
        if (_cache.FirstOrDefault(cached => cached.Archive.Path == archive && cached.Archive.Snapshot.Metadata.Revision == file.Metadata.Revision) is { } hit)
        {
            _cache.Remove(hit);
            _cache.AddFirst(hit);
            return FileSystemResult<OpenedZipArchive>.Success(hit.Archive);
        }

        FileSystemResult<FileSystemContentReadHandle> read = await _fileSystem.ReadAsync(new FileSystemReadRequest(archive), cancellationToken);
        if (!read.Succeeded)
        {
            return FileSystemResult<OpenedZipArchive>.Failure(read.Error!);
        }

        byte[] content;
        await using (FileSystemContentReadHandle handle = read.Value!)
        {
            using MemoryStream buffer = new();
            await handle.Content.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        // Reading the table of contents of a large archive is CPU work on the window's thread (ADR 0069).
        await HackProcess.CheckpointAsync(cancellationToken);
        if (ZipArchiveIndex.TryRead(content) is not { } index)
        {
            return FileSystemResult<OpenedZipArchive>.Failure(new FileSystemError(operation, FileSystemErrorCode.InvalidContent, archive));
        }

        OpenedZipArchive opened = new(archive, file, index);
        _cache.RemoveAll(cached => cached.Archive.Path == archive);
        _cache.AddFirst(new CachedArchive(opened));
        while (_cache.Count > CacheSize)
        {
            _cache.RemoveLast();
        }

        return FileSystemResult<OpenedZipArchive>.Success(opened);
    }

    /// <summary>
    /// Extracts one file or folder of an archive to <paramref name="destination"/> (§7 and §9: opening a
    /// file of the archive, dragging or pasting it out). The destination must not exist yet.
    /// </summary>
    /// <param name="source">The file or folder inside the archive.</param>
    /// <param name="destination">The path of the copy in the file system.</param>
    /// <param name="cancellationToken">Cancels between two files.</param>
    /// <returns><see langword="null"/>, or the first error met.</returns>
    public async Task<FileSystemError?> ExtractAsync(ZipArchiveLocation source, VirtualPath destination, CancellationToken cancellationToken = default)
    {
        FileSystemResult<OpenedZipArchive> opened = await OpenAsync(source.Archive, FileSystemOperation.Copy, cancellationToken);
        if (!opened.Succeeded)
        {
            return opened.Error;
        }

        ZipArchiveIndex index = opened.Value!.Index;
        if (!index.TryGet(source.EntryPath, out ZipArchiveNode node))
        {
            return new FileSystemError(FileSystemOperation.Copy, FileSystemErrorCode.NotFound, source.Archive);
        }

        return await ExtractNodeAsync(index, node, destination, cancellationToken);
    }

    private async Task<FileSystemError?> ExtractNodeAsync(ZipArchiveIndex index, ZipArchiveNode node, VirtualPath destination, CancellationToken cancellationToken)
    {
        await HackProcess.CheckpointAsync(cancellationToken);
        if (node.IsDirectory)
        {
            FileSystemMutationResult folder = await _fileSystem.CreateAsync(
                new FileSystemCreateRequest(destination, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0b111_101_101)), cancellationToken);
            if (!folder.Succeeded)
            {
                return folder.Transaction.Error;
            }

            foreach (ZipArchiveNode child in index.ChildrenOf(node))
            {
                if (await ExtractNodeAsync(index, child, VirtualPath.Parse(destination.Value + "/" + child.Name), cancellationToken) is { } error)
                {
                    return error;
                }
            }

            return null;
        }

        FileSystemMutationResult file = await _fileSystem.CreateAsync(
            new FileSystemCreateRequest(destination, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)), cancellationToken);
        if (!file.Succeeded)
        {
            return file.Transaction.Error;
        }

        using MemoryStream content = await index.OpenFileAsync(node, cancellationToken);
        FileSystemMutationResult write = await _fileSystem.WriteAsync(
            new FileSystemWriteRequest(destination), new MemoryStreamContentSource(FileSystemContentDescriptor.Binary(MediaTypeOf(node.Name)), content), cancellationToken);
        return write.Succeeded ? null : write.Transaction.Error;
    }

    /// <summary>The media type given to a file of an archive: <c>application/zip</c> for an archive, else binary.</summary>
    internal static string MediaTypeOf(string name) => IsArchiveName(name) ? "application/zip" : "application/octet-stream";

    /// <summary>
    /// A stable id for an entry of an archive, so the same entry keeps its id (and the view its
    /// selection) from one listing to the next: a hash of the archive path and the path inside it.
    /// </summary>
    /// <remarks>Two FNV-1a 64-bit hashes with different seeds: the browser runtime has no MD5.</remarks>
    internal static FileSystemEntryId IdOf(VirtualPath archive, string entryPath)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(archive.Value + "\n" + entryPath);
        Span<byte> hash = stackalloc byte[16];
        BitConverter.TryWriteBytes(hash[..8], Fnv1a(bytes, 0xcbf29ce484222325));
        BitConverter.TryWriteBytes(hash[8..], Fnv1a(bytes, 0x84222325cbf29ce4));
        Guid id = new(hash);
        return FileSystemEntryId.FromGuid(id == Guid.Empty ? Guid.NewGuid() : id);
    }

    private static ulong Fnv1a(byte[] bytes, ulong seed)
    {
        ulong hash = seed;
        foreach (byte value in bytes)
        {
            hash = (hash ^ value) * 0x100000001b3;
        }

        return hash;
    }

    private sealed record CachedArchive(OpenedZipArchive Archive);
}

/// <summary>An archive read by <see cref="ZipArchiveBrowser"/>: its file's snapshot and its table of contents.</summary>
/// <param name="Path">The <c>.zip</c> file.</param>
/// <param name="Snapshot">The file's metadata when it was read.</param>
/// <param name="Index">Its table of contents.</param>
internal sealed record OpenedZipArchive(VirtualPath Path, FileSystemEntrySnapshot Snapshot, ZipArchiveIndex Index);

/// <summary>Helpers for <see cref="LinkedList{T}"/>.</summary>
file static class LinkedListExtensions
{
    public static void RemoveAll<T>(this LinkedList<T> list, Func<T, bool> match)
    {
        LinkedListNode<T>? node = list.First;
        while (node is not null)
        {
            LinkedListNode<T>? next = node.Next;
            if (match(node.Value))
            {
                list.Remove(node);
            }

            node = next;
        }
    }
}
