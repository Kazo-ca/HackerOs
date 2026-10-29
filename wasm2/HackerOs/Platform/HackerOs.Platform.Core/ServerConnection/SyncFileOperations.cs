using HackerOs.App.Abstractions;
using System.Text;
using HackerOs.AppSdk.IO;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.ServerConnection;

/// <summary>
/// Filesystem operations the FileSystem sync needs (ADR 0101), done as the session user through
/// <see cref="IFileSystemService"/>: read, write, stat, move, recursive delete and "move to the
/// user's trash". The sync runs inside the platform, outside any app scope, so it cannot use
/// <see cref="HackTrash"/>; it writes the same freedesktop.org layout (ADR 0052) itself.
/// </summary>
internal sealed class SyncFileOperations(IFileSystemService fileSystem, FileSystemAuthorizationContext context)
{
    /// <summary>Home-relative directory of the user's trash (ADR 0052).</summary>
    public const string TrashRelativePath = ".local/share/Trash";

    /// <summary>Key added to a <c>.trashinfo</c> written for a deletion received from another device.</summary>
    public const string OriginKey = "X-HackerOS-Origin";

    /// <summary>Value of <see cref="OriginKey"/>: "deleted on another device".</summary>
    public const string OriginDeletedOnAnotherDevice = "deleted-on-another-device";

    /// <summary>Key recording which device the deletion came from.</summary>
    public const string DeviceKey = "X-HackerOS-Device";

    private static readonly FileSystemPermissions PrivateDirectoryMode = FileSystemPermissions.FromMode(0b111_000_000);
    private static readonly FileSystemPermissions PublicDirectoryMode = FileSystemPermissions.FromMode(0b111_101_101);
    private static readonly FileSystemPermissions PrivateFileMode = FileSystemPermissions.FromMode(0b110_000_000);

    /// <summary>Gets an entry without following a final symbolic link, or <see langword="null"/> when absent.</summary>
    public async ValueTask<FileSystemEntrySnapshot?> StatAsync(VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> result = await fileSystem.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow), context, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.Value : null;
    }

    /// <summary>Creates one entry (a directory, or a symbolic link to <paramref name="symbolicLinkTarget"/>).</summary>
    /// <exception cref="IOException">The entry could not be created.</exception>
    public async ValueTask CreateAsync(
        VirtualPath path, FileSystemEntryKind kind, FileSystemPermissions permissions, string? symbolicLinkTarget)
    {
        FileSystemMutationResult created = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(path, kind, permissions, symbolicLinkTarget: symbolicLinkTarget), context, CancellationToken.None).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            throw Failure("create", path, created);
        }
    }

    /// <summary>Lists the immediate children of a directory; an unreadable directory gives none.</summary>
    public async ValueTask<IReadOnlyList<VirtualPath>> ListAsync(VirtualPath directory, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemDirectorySnapshot> listed = await fileSystem.EnumerateAsync(
            new FileSystemEnumerateRequest(directory, FileSystemLinkBehavior.NoFollow), context, cancellationToken).ConfigureAwait(false);
        return listed.Succeeded
            ? [.. listed.Value!.Entries.Select(entry => VirtualPath.Parse($"{directory.Value.TrimEnd('/')}/{entry.Name.Value}"))]
            : [];
    }

    /// <summary>Reads a whole file; an unreadable file gives <see langword="null"/>.</summary>
    public async ValueTask<byte[]?> TryReadAllBytesAsync(VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> result = await fileSystem.ReadAsync(
            new FileSystemReadRequest(path), context, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        await using FileSystemContentReadHandle handle = result.Value!;
        using MemoryStream buffer = new();
        await handle.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Creates the file if needed (with <paramref name="permissions"/>) and replaces its content.</summary>
    /// <returns>The entry after the write.</returns>
    /// <exception cref="IOException">The file could not be created or written.</exception>
    public async ValueTask<FileSystemEntrySnapshot> WriteAllBytesAsync(
        VirtualPath path, byte[] content, FileSystemPermissions permissions, CancellationToken cancellationToken)
    {
        FileSystemMutationResult created = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(path, FileSystemEntryKind.File, permissions), context, cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            throw Failure("create", path, created);
        }

        FileSystemMutationResult written = await fileSystem.WriteAsync(
            new FileSystemWriteRequest(path), new ByteContentSource(content), context, cancellationToken).ConfigureAwait(false);
        return written.Succeeded ? written.Entry! : throw Failure("write", path, written);
    }

    /// <summary>Creates a directory and its missing parents; an existing directory is fine.</summary>
    public async ValueTask EnsureDirectoryAsync(VirtualPath path, FileSystemPermissions permissions, CancellationToken cancellationToken)
    {
        if (await StatAsync(path, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        if (Parent(path) is { } parent)
        {
            await EnsureDirectoryAsync(parent, PublicDirectoryMode, cancellationToken).ConfigureAwait(false);
        }

        FileSystemMutationResult created = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(path, FileSystemEntryKind.Directory, permissions), context, cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            throw Failure("create", path, created);
        }
    }

    /// <summary>Renames or moves an entry, creating the destination's parents.</summary>
    /// <returns>The moved entry.</returns>
    public async ValueTask<FileSystemEntrySnapshot> MoveAsync(VirtualPath source, VirtualPath destination, CancellationToken cancellationToken)
    {
        if (Parent(destination) is { } destinationParentPath)
        {
            await EnsureDirectoryAsync(destinationParentPath, PublicDirectoryMode, cancellationToken).ConfigureAwait(false);
        }

        for (int attempt = 1; ; attempt++)
        {
            FileSystemEntrySnapshot entry = await StatAsync(source, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"'{source.Value}' does not exist.", source.Value);
            FileSystemEntrySnapshot sourceParent = (await StatAsync(Parent(source)!.Value, cancellationToken).ConfigureAwait(false))!;
            FileSystemEntrySnapshot destinationParent = (await StatAsync(Parent(destination)!.Value, cancellationToken).ConfigureAwait(false))!;

            FileSystemMutationResult moved = await fileSystem.MoveAsync(
                new FileSystemMoveRequest(
                    source, destination, entry.Metadata.Revision, sourceParent.Metadata.Revision, destinationParent.Metadata.Revision),
                context, cancellationToken).ConfigureAwait(false);
            if (moved.Succeeded)
            {
                return moved.Entry!;
            }

            if (moved.Transaction.Error?.Code != FileSystemErrorCode.RevisionConflict || attempt >= 3)
            {
                throw Failure("move", source, moved);
            }
        }
    }

    /// <summary>Deletes an entry and everything below it; an absent entry is not an error.</summary>
    public async ValueTask DeleteRecursiveAsync(VirtualPath path, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            FileSystemEntrySnapshot? entry = await StatAsync(path, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return;
            }

            FileSystemEntrySnapshot parent = (await StatAsync(Parent(path)!.Value, cancellationToken).ConfigureAwait(false))!;
            FileSystemMutationResult deleted = await fileSystem.DeleteAsync(
                new FileSystemDeleteRequest(path, entry.Metadata.Revision, parent.Metadata.Revision, recursive: true),
                context, cancellationToken).ConfigureAwait(false);
            if (deleted.Succeeded)
            {
                return;
            }

            if (deleted.Transaction.Error?.Code != FileSystemErrorCode.RevisionConflict || attempt >= 3)
            {
                throw Failure("delete", path, deleted);
            }
        }
    }

    /// <summary>
    /// Moves an entry of <paramref name="homePath"/> to the user's trash, with a <c>.trashinfo</c>
    /// saying it was deleted on another device (ADR 0101 section 1). An entry already inside the
    /// trash is deleted for good instead, exactly like <see cref="HackTrash.MoveToTrashAsync"/>.
    /// </summary>
    /// <param name="homePath">The user's home directory.</param>
    /// <param name="source">Entry to trash.</param>
    /// <param name="originDeviceId">Device the deletion or the winning version came from, recorded in the <c>.trashinfo</c>.</param>
    /// <param name="deletedAtLocal">Deletion time written to <c>DeletionDate</c> (local time).</param>
    /// <param name="cancellationToken">Cancels before the move.</param>
    /// <returns>
    /// The home-relative paths of the trashed element and of its <c>.trashinfo</c>, or
    /// <see langword="null"/> when the element was already in the trash and is now gone.
    /// </returns>
    public async ValueTask<(string ElementPath, string InfoPath)?> MoveToTrashAsync(
        VirtualPath homePath, VirtualPath source, Guid originDeviceId, DateTimeOffset deletedAtLocal, CancellationToken cancellationToken)
    {
        string trashRoot = $"{homePath.Value}/{TrashRelativePath}";
        if (source.Value.StartsWith(trashRoot + "/", StringComparison.Ordinal))
        {
            await DeleteRecursiveAsync(source, cancellationToken).ConfigureAwait(false);
            return null;
        }

        VirtualPath filesDirectory = VirtualPath.Parse(trashRoot + "/files");
        VirtualPath infoDirectory = VirtualPath.Parse(trashRoot + "/info");
        await EnsureDirectoryAsync(VirtualPath.Parse(trashRoot), PrivateDirectoryMode, cancellationToken).ConfigureAwait(false);
        await EnsureDirectoryAsync(filesDirectory, PrivateDirectoryMode, cancellationToken).ConfigureAwait(false);
        await EnsureDirectoryAsync(infoDirectory, PrivateDirectoryMode, cancellationToken).ConfigureAwait(false);

        string originalName = source.Value[(source.Value.LastIndexOf('/') + 1)..];
        string infoContent = HackTrashInfo.Format(source.Value, deletedAtLocal)
            + $"{OriginKey}={OriginDeletedOnAnotherDevice}\n{DeviceKey}={originDeviceId:D}\n";

        for (int attempt = 1; attempt <= 10_000; attempt++)
        {
            string candidate = HackTrashInfo.CandidateName(originalName, attempt);
            VirtualPath element = VirtualPath.Parse($"{filesDirectory.Value}/{candidate}");
            VirtualPath info = VirtualPath.Parse($"{infoDirectory.Value}/{candidate}{HackTrash.InfoExtension}");
            if (await StatAsync(element, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            // Reserve the name exclusively with the .trashinfo first, like the specification asks.
            FileSystemMutationResult reserved = await fileSystem.CreateAsync(
                new FileSystemCreateRequest(info, FileSystemEntryKind.File, PrivateFileMode), context, cancellationToken).ConfigureAwait(false);
            if (!reserved.Succeeded)
            {
                if (reserved.Transaction.Error?.Code == FileSystemErrorCode.AlreadyExists)
                {
                    continue;
                }

                throw Failure("create", info, reserved);
            }

            await WriteAllBytesAsync(info, Encoding.UTF8.GetBytes(infoContent), PrivateFileMode, cancellationToken).ConfigureAwait(false);
            try
            {
                await MoveAsync(source, element, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await DeleteRecursiveAsync(info, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            int prefix = homePath.Value.Length + 1;
            return (element.Value[prefix..], info.Value[prefix..]);
        }

        throw new IOException($"Cannot move '{source.Value}' to trash: no free name left.");
    }

    private static VirtualPath? Parent(VirtualPath path)
    {
        int slash = path.Value.LastIndexOf('/');
        return slash <= 0 ? (path.Value == "/" ? null : VirtualPath.Parse("/")) : VirtualPath.Parse(path.Value[..slash]);
    }

    private static IOException Failure(string operation, VirtualPath path, FileSystemMutationResult result) =>
        new($"sync: cannot {operation} '{path.Value}' ({result.Transaction.Error?.Code}).");

    /// <summary>Content source over a byte array.</summary>
    internal sealed class ByteContentSource(byte[] bytes) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Binary();

        public long? Length => bytes.Length;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
