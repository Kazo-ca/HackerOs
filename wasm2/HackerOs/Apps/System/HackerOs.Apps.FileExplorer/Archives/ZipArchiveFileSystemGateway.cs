using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Processes;

namespace HackerOs.Apps.FileExplorer.Archives;

/// <summary>
/// The File Explorer's file system as its own view sees it (ADR 0185 §6-9): the explorer's gateway,
/// plus the inside of every <c>.zip</c> file, listed as read-only folders. A path that does not go
/// through an archive is handed to the explorer's gateway untouched, with its rights.
/// </summary>
/// <remarks>
/// <para>
/// Only the explorer's windows wrap their file control in it: the gateway the other apps, the file
/// dialogs and the desktop use never shows the inside of an archive (§7).
/// </para>
/// <para>
/// Every change inside an archive is refused with <see cref="FileSystemErrorCode.ProtectedEntry"/> (§9);
/// the explorer itself turns a drag or a paste out of an archive into an extraction
/// (<see cref="ZipArchiveBrowser.ExtractAsync"/>) and explains the refusals before reaching this point.
/// </para>
/// </remarks>
internal sealed class ZipArchiveFileSystemGateway : IAppFileSystemGateway
{
    private const ushort FileMode = 0b100_100_100;
    private const ushort FolderMode = 0b101_101_101;

    private readonly IAppFileSystemGateway _inner;

    /// <summary>Wraps the explorer's gateway.</summary>
    /// <param name="inner">The explorer's own gateway, which keeps every ordinary path.</param>
    public ZipArchiveFileSystemGateway(IAppFileSystemGateway inner)
        : this(inner, new ZipArchiveBrowser(inner))
    {
    }

    private ZipArchiveFileSystemGateway(IAppFileSystemGateway inner, ZipArchiveBrowser archives)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        Archives = archives;
    }

    /// <summary>Gets the archive reader shared by this gateway and the explorer's commands.</summary>
    public ZipArchiveBrowser Archives { get; }

    /// <inheritdoc />
    public KernelMemoryCharge ChargeStreamBuffer() => _inner.ChargeStreamBuffer();

    /// <inheritdoc />
    public ValueTask<FileSystemResult<IDisposable>> OpenDescriptorAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
        _inner.OpenDescriptorAsync(path, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request, CancellationToken cancellationToken = default)
    {
        if (await LocateEntryAsync(request.Path, cancellationToken) is not { } location)
        {
            return await _inner.ReadAsync(request, cancellationToken);
        }

        FileSystemResult<OpenedZipArchive> opened = await Archives.OpenAsync(location.Archive, FileSystemOperation.Read, cancellationToken);
        if (!opened.Succeeded)
        {
            return FileSystemResult<FileSystemContentReadHandle>.Failure(opened.Error!);
        }

        if (!opened.Value!.Index.TryGet(location.EntryPath, out ZipArchiveNode node))
        {
            return FileSystemResult<FileSystemContentReadHandle>.Failure(Error(FileSystemOperation.Read, FileSystemErrorCode.NotFound, request.Path));
        }

        if (node.IsDirectory)
        {
            return FileSystemResult<FileSystemContentReadHandle>.Failure(Error(FileSystemOperation.Read, FileSystemErrorCode.NotFile, request.Path));
        }

        MemoryStream content = await opened.Value.Index.OpenFileAsync(node, cancellationToken);
        return FileSystemResult<FileSystemContentReadHandle>.Success(new FileSystemContentReadHandle(
            new FileSystemEntrySnapshot(request.Path, MetadataOf(opened.Value, node)),
            FileSystemContentDescriptor.Binary(ZipArchiveBrowser.MediaTypeOf(node.Name)),
            content));
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request, CancellationToken cancellationToken = default)
    {
        if (await Archives.LocateAsync(request.Path, cancellationToken) is not { } location)
        {
            return await _inner.EnumerateAsync(request, cancellationToken);
        }

        FileSystemResult<OpenedZipArchive> opened = await Archives.OpenAsync(location.Archive, FileSystemOperation.Enumerate, cancellationToken);
        if (!opened.Succeeded)
        {
            return FileSystemResult<FileSystemDirectorySnapshot>.Failure(opened.Error!);
        }

        OpenedZipArchive archive = opened.Value!;
        if (!archive.Index.TryGet(location.EntryPath, out ZipArchiveNode folder))
        {
            return FileSystemResult<FileSystemDirectorySnapshot>.Failure(Error(FileSystemOperation.Enumerate, FileSystemErrorCode.NotFound, request.Path));
        }

        if (!folder.IsDirectory)
        {
            return FileSystemResult<FileSystemDirectorySnapshot>.Failure(Error(FileSystemOperation.Enumerate, FileSystemErrorCode.NotDirectory, request.Path));
        }

        IEnumerable<FileSystemDirectoryItem> entries = archive.Index.ChildrenOf(folder)
            .Select(child => new FileSystemDirectoryItem(FileSystemEntryName.Parse(child.Name), MetadataOf(archive, child)));
        return FileSystemResult<FileSystemDirectorySnapshot>.Success(
            new FileSystemDirectorySnapshot(request.Path, archive.Snapshot.Metadata.Revision, entries));
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request, CancellationToken cancellationToken = default)
    {
        if (await LocateEntryAsync(request.Path, cancellationToken) is not { } location)
        {
            // The archive itself stays the file it is: only what is inside is shown as folders.
            return await _inner.StatAsync(request, cancellationToken);
        }

        FileSystemResult<OpenedZipArchive> opened = await Archives.OpenAsync(location.Archive, FileSystemOperation.Stat, cancellationToken);
        if (!opened.Succeeded)
        {
            return FileSystemResult<FileSystemEntrySnapshot>.Failure(opened.Error!);
        }

        return opened.Value!.Index.TryGet(location.EntryPath, out ZipArchiveNode node)
            ? FileSystemResult<FileSystemEntrySnapshot>.Success(new FileSystemEntrySnapshot(request.Path, MetadataOf(opened.Value, node)))
            : FileSystemResult<FileSystemEntrySnapshot>.Failure(Error(FileSystemOperation.Stat, FileSystemErrorCode.NotFound, request.Path));
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> CreateAsync(FileSystemCreateRequest request, CancellationToken cancellationToken = default) =>
        await IsInArchiveAsync(request.Path, cancellationToken)
            ? Refused(FileSystemOperation.Create, request.Path)
            : await _inner.CreateAsync(request, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> WriteAsync(
        FileSystemWriteRequest request, IFileSystemContentSource content, CancellationToken cancellationToken = default) =>
        await IsInArchiveAsync(request.Path, cancellationToken)
            ? Refused(FileSystemOperation.Write, request.Path)
            : await _inner.WriteAsync(request, content, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> MoveAsync(FileSystemMoveRequest request, CancellationToken cancellationToken = default) =>
        await IsInArchiveAsync(request.SourcePath, cancellationToken) || await IsInArchiveAsync(request.DestinationPath, cancellationToken)
            ? Refused(FileSystemOperation.Move, request.SourcePath)
            : await _inner.MoveAsync(request, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> CopyAsync(FileSystemCopyRequest request, CancellationToken cancellationToken = default) =>
        await IsInArchiveAsync(request.SourcePath, cancellationToken) || await IsInArchiveAsync(request.DestinationPath, cancellationToken)
            ? Refused(FileSystemOperation.Copy, request.SourcePath)
            : await _inner.CopyAsync(request, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> DeleteAsync(FileSystemDeleteRequest request, CancellationToken cancellationToken = default) =>
        await IsInArchiveAsync(request.Path, cancellationToken)
            ? Refused(FileSystemOperation.Delete, request.Path)
            : await _inner.DeleteAsync(request, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> SetPermissionsAsync(
        FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default) =>
        await IsInArchiveAsync(request.Path, cancellationToken)
            ? Refused(FileSystemOperation.SetPermissions, request.Path)
            : await _inner.SetPermissionsAsync(request, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemTreeSize>> MeasureAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
        await Archives.LocateAsync(path, cancellationToken) is { IsRoot: false }
            ? await FileSystemTreeMeasure.MeasureAsync(
                path,
                (entry, token) => StatAsync(new FileSystemStatRequest(entry), token),
                (folder, token) => EnumerateAsync(new FileSystemEnumerateRequest(folder), token),
                cancellationToken)
            : await _inner.MeasureAsync(path, cancellationToken);

    /// <inheritdoc />
    public IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle) =>
        new ZipArchiveFileSystemGateway(_inner.WithSelectedHandle(handle));

    /// <summary>The location of an entry strictly inside an archive; <see langword="null"/> for ordinary paths and for the archive file itself.</summary>
    private async ValueTask<ZipArchiveLocation?> LocateEntryAsync(VirtualPath path, CancellationToken cancellationToken) =>
        await Archives.LocateAsync(path, cancellationToken) is { IsRoot: false } location ? location : null;

    private async ValueTask<bool> IsInArchiveAsync(VirtualPath path, CancellationToken cancellationToken) =>
        await LocateEntryAsync(path, cancellationToken) is not null;

    /// <summary>
    /// The metadata of an entry, built from the archive file's: its owner and group, read-only
    /// permissions, the entry's date (the archive's for a folder the zip only implies) and the
    /// archive's revision, so a changed archive is listed again.
    /// </summary>
    internal static FileSystemEntryMetadata MetadataOf(OpenedZipArchive archive, ZipArchiveNode node)
    {
        FileSystemEntryMetadata file = archive.Snapshot.Metadata;
        DateTimeOffset modified = node.ModifiedUtc ?? file.Timestamps.ContentModifiedAtUtc;
        FileSystemTimestamps timestamps = new(modified, modified, modified);
        FileSystemEntryId id = ZipArchiveBrowser.IdOf(archive.Path, node.Path);
        return node.IsDirectory
            ? new DirectoryMetadata(id, file.OwnerId, file.GroupId, FileSystemPermissions.FromMode(FolderMode), timestamps, file.Revision)
            : new FileMetadata(id, file.OwnerId, file.GroupId, FileSystemPermissions.FromMode(FileMode), timestamps, file.Revision, node.Length, ZipArchiveBrowser.MediaTypeOf(node.Name));
    }

    private static FileSystemError Error(FileSystemOperation operation, FileSystemErrorCode code, VirtualPath path) => new(operation, code, path);

    private static FileSystemMutationResult Refused(FileSystemOperation operation, VirtualPath path) =>
        new(FileSystemTransactionResult.Rejected(Guid.NewGuid(), Error(operation, FileSystemErrorCode.ProtectedEntry, path)));
}

/// <summary>
/// The explorer's folder watch as its view sees it: a folder inside an archive is watched through the
/// folder that holds the <c>.zip</c>, so the listing is read again when the archive changes.
/// </summary>
internal sealed class ZipArchiveWatchGateway : IAppFileSystemWatchGateway
{
    private readonly IAppFileSystemWatchGateway _inner;
    private readonly ZipArchiveBrowser _archives;

    /// <summary>Wraps the explorer's watch gateway.</summary>
    public ZipArchiveWatchGateway(IAppFileSystemWatchGateway inner, ZipArchiveBrowser archives)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(archives);
        _inner = inner;
        _archives = archives;
    }

    /// <inheritdoc />
    public async ValueTask<ITopicChannelSubscription<FileSystemChangeEvent>> WatchAsync(
        VirtualPath path, FileSystemWatchScope scope, CancellationToken cancellationToken = default)
    {
        if (await _archives.LocateAsync(path, cancellationToken) is { } location)
        {
            string archive = location.Archive.Value;
            int separator = archive.LastIndexOf('/');
            path = VirtualPath.Parse(separator <= 0 ? "/" : archive[..separator]);
            scope = FileSystemWatchScope.ImmediateChildren;
        }

        return await _inner.WatchAsync(path, scope, cancellationToken);
    }
}
