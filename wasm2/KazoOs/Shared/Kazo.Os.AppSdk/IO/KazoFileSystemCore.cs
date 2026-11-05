using System.Runtime.CompilerServices;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.IO;

/// <summary>
/// Shared plumbing behind <see cref="KazoFile"/>, <see cref="KazoDirectory"/>, the info classes and
/// <see cref="KazoFileStream"/>. Every call goes through the ambient app's
/// <see cref="IAppFileSystemGateway"/> only, so capabilities, authority, mode bits, elevation
/// tickets and permission-error notifications apply exactly as for direct gateway calls (ADR 0043,
/// section 1). This class never sees <c>IFileSystemService</c>.
/// </summary>
internal static class KazoFileSystemCore
{
    /// <summary>Gets the ambient app's filesystem gateway, or throws <see cref="KazoExecutionContextMissingException"/>.</summary>
    public static IAppFileSystemGateway Gateway => KazoExecutionContext.Current.FileSystem;

    /// <summary>Returns the parent of a canonical path, or <see langword="null"/> for the root.</summary>
    public static VirtualPath? ParentOf(VirtualPath path)
    {
        string? parent = KazoPath.GetDirectoryName(path.Value);
        return parent is null ? null : VirtualPath.Parse(parent);
    }

    /// <summary>Reads one entry snapshot; <see langword="null"/> when nothing exists there, any other failure throws.</summary>
    public static async ValueTask<FileSystemEntrySnapshot?> TryStatAsync(
        IAppFileSystemGateway gateway,
        VirtualPath path,
        FileSystemLinkBehavior linkBehavior,
        KazoEntryTarget target,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> result =
            await gateway.StatAsync(new FileSystemStatRequest(path, linkBehavior), cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return result.Value!;
        }

        if (result.Error!.Code == FileSystemErrorCode.NotFound)
        {
            return null;
        }

        throw KazoFileSystemErrorTranslator.Translate(result.Error, target, path.Value);
    }

    /// <summary>Reads one entry snapshot and throws the "not found" exception matching <paramref name="target"/> when missing.</summary>
    public static async ValueTask<FileSystemEntrySnapshot> StatAsync(
        IAppFileSystemGateway gateway,
        VirtualPath path,
        FileSystemLinkBehavior linkBehavior,
        KazoEntryTarget target,
        CancellationToken cancellationToken) =>
        await TryStatAsync(gateway, path, linkBehavior, target, cancellationToken).ConfigureAwait(false)
        ?? throw (target == KazoEntryTarget.File
            ? new KazoFileNotFoundException(path.Value)
            : new KazoDirectoryNotFoundException(path.Value));

    /// <summary>Requires <paramref name="path"/> to be an existing directory (following links).</summary>
    public static async ValueTask<FileSystemEntrySnapshot> RequireDirectoryAsync(
        IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemEntrySnapshot snapshot = await StatAsync(
            gateway, path, FileSystemLinkBehavior.Follow, KazoEntryTarget.Directory, cancellationToken).ConfigureAwait(false);
        if (snapshot.Metadata.Kind != FileSystemEntryKind.Directory)
        {
            throw new KazoIOException($"The path '{path.Value}' is not a directory.", FileSystemErrorCode.NotDirectory, path.Value);
        }

        return snapshot;
    }

    /// <summary>Requires the parent of <paramref name="path"/> to exist as a directory.</summary>
    public static async ValueTask<FileSystemEntrySnapshot> RequireParentAsync(
        IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        VirtualPath parent = ParentOf(path)
            ?? throw new KazoUnauthorizedAccessException("The root directory cannot be replaced.", FileSystemErrorCode.ProtectedEntry, path.Value);
        return await RequireDirectoryAsync(gateway, parent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the whole content of a regular file.</summary>
    public static async ValueTask<KazoFileContent> ReadAllAsync(
        IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken) =>
        await TryReadAllAsync(gateway, path, cancellationToken).ConfigureAwait(false)
        ?? throw new KazoFileNotFoundException(path.Value);

    /// <summary>Reads the whole content of a regular file; <see langword="null"/> when nothing exists there.</summary>
    public static async ValueTask<KazoFileContent?> TryReadAllAsync(
        IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> result =
            await gateway.ReadAsync(new FileSystemReadRequest(path), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            if (result.Error!.Code == FileSystemErrorCode.NotFound)
            {
                return null;
            }

            throw KazoFileSystemErrorTranslator.Translate(result.Error, KazoEntryTarget.File, path.Value);
        }

        await using FileSystemContentReadHandle handle = result.Value!;
        using MemoryStream buffer = new();
        await handle.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new KazoFileContent(buffer.ToArray(), handle.Descriptor, handle.Entry);
    }

    /// <summary>
    /// Publishes new content for <paramref name="path"/> with one atomic engine write (ADR 0043,
    /// section 4): creates the file with the umask (or <see cref="KazoWriteOptions.CreateMode"/>)
    /// when missing, writes at the revision just observed, and on a revision conflict re-observes and
    /// retries exactly once. With <see cref="KazoWriteOptions.ExpectedRevision"/> there is no retry.
    /// </summary>
    /// <param name="gateway">Gateway of the app performing the write.</param>
    /// <param name="path">Canonical file path.</param>
    /// <param name="compose">
    /// Builds the bytes to publish from the current content (only read when
    /// <paramref name="needsExistingContent"/> is set, e.g. for an append; otherwise <see langword="null"/>).
    /// Called again on retry so an append never loses a concurrent change.
    /// </param>
    /// <param name="needsExistingContent">Whether <paramref name="compose"/> needs the current bytes.</param>
    /// <param name="descriptor">Content interpretation persisted with the file.</param>
    /// <param name="options">Advanced options, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels before commit.</param>
    /// <returns>The committed entry snapshot.</returns>
    public static async ValueTask<FileSystemEntrySnapshot> PublishAsync(
        IAppFileSystemGateway gateway,
        VirtualPath path,
        Func<byte[]?, byte[]> compose,
        bool needsExistingContent,
        FileSystemContentDescriptor descriptor,
        KazoWriteOptions? options,
        CancellationToken cancellationToken)
    {
        long? expectedRevision = options?.ExpectedRevision;
        FileSystemPermissions createPermissions = options?.CreateMode is { } createMode
            ? KazoUnixFileMode.ToPermissions(createMode, nameof(options))
            : KazoUnixFileMode.DefaultFile;
        int maximumAttempts = expectedRevision is null ? 2 : 1;

        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[]? existingContent = null;
            long? revision;
            if (needsExistingContent)
            {
                KazoFileContent? current = await TryReadAllAsync(gateway, path, cancellationToken).ConfigureAwait(false);
                existingContent = current?.Bytes;
                revision = current?.Entry.Metadata.Revision;
            }
            else if (expectedRevision is null)
            {
                FileSystemEntrySnapshot? snapshot = await TryStatAsync(
                    gateway, path, FileSystemLinkBehavior.Follow, KazoEntryTarget.File, cancellationToken).ConfigureAwait(false);
                if (snapshot?.Metadata.Kind == FileSystemEntryKind.Directory)
                {
                    throw IsDirectory(path);
                }

                revision = snapshot?.Metadata.Revision;
            }
            else
            {
                // Strict write: go straight to the engine with the caller's revision; it rejects a stale one.
                revision = expectedRevision;
            }

            if (expectedRevision is { } expected && revision is { } observed && observed != expected)
            {
                throw new KazoFileConflictException(path.Value, observed);
            }

            if (revision is null)
            {
                if (expectedRevision is not null)
                {
                    throw new KazoFileNotFoundException(path.Value);
                }

                FileSystemMutationResult created = await gateway.CreateAsync(
                    new FileSystemCreateRequest(path, FileSystemEntryKind.File, createPermissions),
                    cancellationToken).ConfigureAwait(false);
                if (!created.Succeeded)
                {
                    // Someone created it between our stat and our create: observe it and retry once.
                    if (created.Transaction.Error!.Code == FileSystemErrorCode.AlreadyExists && attempt < maximumAttempts)
                    {
                        continue;
                    }

                    throw KazoFileSystemErrorTranslator.Translate(created.Transaction.Error!, KazoEntryTarget.File, path.Value);
                }

                revision = created.Entry!.Metadata.Revision;
            }

            byte[] payload = compose(existingContent);
            FileSystemMutationResult written = await gateway.WriteAsync(
                new FileSystemWriteRequest(path, revision),
                new KazoMemoryContentSource(payload, descriptor),
                cancellationToken).ConfigureAwait(false);
            if (written.Succeeded)
            {
                return written.Entry
                    ?? await StatAsync(gateway, path, FileSystemLinkBehavior.Follow, KazoEntryTarget.File, cancellationToken).ConfigureAwait(false);
            }

            FileSystemError error = written.Transaction.Error!;
            if (error.Code == FileSystemErrorCode.RevisionConflict)
            {
                if (attempt < maximumAttempts)
                {
                    continue;
                }

                throw new KazoFileConflictException(path.Value, await TryReadRevisionAsync(gateway, path, cancellationToken).ConfigureAwait(false));
            }

            throw KazoFileSystemErrorTranslator.Translate(error, KazoEntryTarget.File, path.Value);
        }
    }

    /// <summary>
    /// Copies one regular file's bytes into <paramref name="destination"/> through a read and a
    /// publish — the facade's answer to a <see cref="FileSystemErrorCode.CrossMountOperation"/>
    /// and to an overwriting copy.
    /// </summary>
    public static async ValueTask<FileSystemEntrySnapshot> CopyFileContentAsync(
        IAppFileSystemGateway gateway, VirtualPath source, VirtualPath destination, CancellationToken cancellationToken)
    {
        KazoFileContent content = await ReadAllAsync(gateway, source, cancellationToken).ConfigureAwait(false);
        return await PublishAsync(
            gateway,
            destination,
            _ => content.Bytes,
            needsExistingContent: false,
            content.Descriptor,
            new KazoWriteOptions { CreateMode = KazoUnixFileMode.FromPermissions(content.Entry.Metadata.Permissions) },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recursively recreates <paramref name="source"/> (file, directory or symbolic link) at
    /// <paramref name="destination"/>, keeping permission bits. Used when a directory move spans two mounts.
    /// </summary>
    public static async ValueTask CopyTreeAsync(
        IAppFileSystemGateway gateway, VirtualPath source, VirtualPath destination, CancellationToken cancellationToken)
    {
        FileSystemEntrySnapshot snapshot = await StatAsync(
            gateway, source, FileSystemLinkBehavior.NoFollow, KazoEntryTarget.Directory, cancellationToken).ConfigureAwait(false);

        switch (snapshot.Metadata)
        {
            case DirectoryMetadata directory:
                await CreateOrThrowAsync(
                    gateway,
                    new FileSystemCreateRequest(destination, FileSystemEntryKind.Directory, directory.Permissions),
                    KazoEntryTarget.Directory,
                    cancellationToken).ConfigureAwait(false);
                FileSystemDirectorySnapshot children = await EnumerateOrThrowAsync(gateway, source, cancellationToken).ConfigureAwait(false);
                foreach (FileSystemDirectoryItem child in children.Entries)
                {
                    await CopyTreeAsync(
                        gateway,
                        VirtualPath.Parse(KazoPath.Combine(source.Value, child.Name.Value)),
                        VirtualPath.Parse(KazoPath.Combine(destination.Value, child.Name.Value)),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case SymbolicLinkMetadata link:
                await CreateOrThrowAsync(
                    gateway,
                    new FileSystemCreateRequest(destination, FileSystemEntryKind.SymbolicLink, link.Permissions, symbolicLinkTarget: link.Target),
                    KazoEntryTarget.File,
                    cancellationToken).ConfigureAwait(false);
                break;

            default:
                await CopyFileContentAsync(gateway, source, destination, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Deletes one entry at its current revisions, re-observing and retrying once on a concurrent
    /// change. Returns <see langword="false"/> when the entry no longer exists.
    /// </summary>
    public static async ValueTask<bool> DeleteEntryAsync(
        IAppFileSystemGateway gateway,
        VirtualPath path,
        bool recursive,
        KazoEntryTarget target,
        CancellationToken cancellationToken)
    {
        VirtualPath parent = ParentOf(path)
            ?? throw new KazoUnauthorizedAccessException("The root directory cannot be deleted.", FileSystemErrorCode.ProtectedEntry, path.Value);

        for (int attempt = 1; ; attempt++)
        {
            FileSystemEntrySnapshot? entry = await TryStatAsync(
                gateway, path, FileSystemLinkBehavior.NoFollow, target, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return false;
            }

            FileSystemEntrySnapshot parentEntry = await RequireDirectoryAsync(gateway, parent, cancellationToken).ConfigureAwait(false);
            FileSystemMutationResult result = await gateway.DeleteAsync(
                new FileSystemDeleteRequest(path, entry.Metadata.Revision, parentEntry.Metadata.Revision, recursive),
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return true;
            }

            FileSystemError error = result.Transaction.Error!;
            if (error.Code == FileSystemErrorCode.RevisionConflict && attempt < 2)
            {
                continue;
            }

            throw KazoFileSystemErrorTranslator.Translate(error, target, path.Value);
        }
    }

    /// <summary>
    /// Moves one entry to a destination that must not exist, falling back to copy + delete when the
    /// engine reports a move across two mounts (like <c>File.Move</c> between volumes).
    /// </summary>
    public static async ValueTask MoveEntryAsync(
        IAppFileSystemGateway gateway,
        VirtualPath source,
        VirtualPath destination,
        KazoEntryTarget target,
        CancellationToken cancellationToken)
    {
        VirtualPath sourceParent = ParentOf(source)
            ?? throw new KazoUnauthorizedAccessException("The root directory cannot be moved.", FileSystemErrorCode.ProtectedEntry, source.Value);

        for (int attempt = 1; ; attempt++)
        {
            FileSystemEntrySnapshot entry = await StatAsync(
                gateway, source, FileSystemLinkBehavior.NoFollow, target, cancellationToken).ConfigureAwait(false);
            FileSystemEntrySnapshot sourceParentEntry = await RequireDirectoryAsync(gateway, sourceParent, cancellationToken).ConfigureAwait(false);
            FileSystemEntrySnapshot destinationParentEntry = await RequireParentAsync(gateway, destination, cancellationToken).ConfigureAwait(false);

            FileSystemMutationResult result = await gateway.MoveAsync(
                new FileSystemMoveRequest(
                    source,
                    destination,
                    entry.Metadata.Revision,
                    sourceParentEntry.Metadata.Revision,
                    destinationParentEntry.Metadata.Revision),
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return;
            }

            FileSystemError error = result.Transaction.Error!;
            if (error.Code == FileSystemErrorCode.CrossMountOperation)
            {
                await CopyTreeAsync(gateway, source, destination, cancellationToken).ConfigureAwait(false);
                await DeleteEntryAsync(gateway, source, recursive: true, target, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (error.Code == FileSystemErrorCode.RevisionConflict && attempt < 2)
            {
                continue;
            }

            throw KazoFileSystemErrorTranslator.Translate(error, target, source.Value);
        }
    }

    /// <summary>
    /// Enumerates <paramref name="directory"/> (and, for <see cref="SearchOption.AllDirectories"/>,
    /// its subdirectories depth-first, without descending into symbolic links), yielding entries
    /// whose name matches <paramref name="searchPattern"/>.
    /// </summary>
    public static async IAsyncEnumerable<KazoEntry> EnumerateAsync(
        IAppFileSystemGateway gateway,
        VirtualPath directory,
        string searchPattern,
        SearchOption searchOption,
        bool includeFiles,
        bool includeDirectories,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FileSystemDirectorySnapshot snapshot = await EnumerateOrThrowAsync(gateway, directory, cancellationToken).ConfigureAwait(false);
        List<VirtualPath> subdirectories = [];

        foreach (FileSystemDirectoryItem item in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fullPath = KazoPath.Combine(directory.Value, item.Name.Value);
            FileSystemEntryMetadata? target = null;
            if (item.Metadata.Kind == FileSystemEntryKind.SymbolicLink)
            {
                // Classify a link by what it points to, like System.IO does; a dangling link counts as a file.
                FileSystemResult<FileSystemEntrySnapshot> followed = await gateway.StatAsync(
                    new FileSystemStatRequest(VirtualPath.Parse(fullPath), FileSystemLinkBehavior.Follow),
                    cancellationToken).ConfigureAwait(false);
                target = followed.Succeeded ? followed.Value!.Metadata : null;
            }

            KazoEntry entry = new(fullPath, item.Name.Value, item.Metadata, target);
            if ((entry.IsDirectory ? includeDirectories : includeFiles) && KazoPathPattern.IsMatch(entry.Name, searchPattern))
            {
                yield return entry;
            }

            if (searchOption == SearchOption.AllDirectories && item.Metadata.Kind == FileSystemEntryKind.Directory)
            {
                subdirectories.Add(VirtualPath.Parse(fullPath));
            }
        }

        foreach (VirtualPath subdirectory in subdirectories)
        {
            await foreach (KazoEntry nested in EnumerateAsync(
                gateway, subdirectory, searchPattern, searchOption, includeFiles, includeDirectories, cancellationToken).ConfigureAwait(false))
            {
                yield return nested;
            }
        }
    }

    /// <summary>The exception .NET throws when a file API targets a directory.</summary>
    public static KazoUnauthorizedAccessException IsDirectory(VirtualPath path) =>
        new($"Access to the path '{path.Value}' is denied: it is a directory.", FileSystemErrorCode.NotFile, path.Value);

    private static async ValueTask<FileSystemDirectorySnapshot> EnumerateOrThrowAsync(
        IAppFileSystemGateway gateway, VirtualPath directory, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemDirectorySnapshot> result =
            await gateway.EnumerateAsync(new FileSystemEnumerateRequest(directory), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? result.Value!
            : throw KazoFileSystemErrorTranslator.Translate(result.Error!, KazoEntryTarget.Directory, directory.Value);
    }

    private static async ValueTask CreateOrThrowAsync(
        IAppFileSystemGateway gateway, FileSystemCreateRequest request, KazoEntryTarget target, CancellationToken cancellationToken)
    {
        FileSystemMutationResult result = await gateway.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw KazoFileSystemErrorTranslator.Translate(result.Transaction.Error!, target, request.Path.Value);
        }
    }

    private static async ValueTask<long?> TryReadRevisionAsync(
        IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> result =
            await gateway.StatAsync(new FileSystemStatRequest(path), cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.Value!.Metadata.Revision : null;
    }
}

/// <summary>The whole content of a file read in one go, with the revision it was read at.</summary>
internal sealed record KazoFileContent(byte[] Bytes, FileSystemContentDescriptor Descriptor, FileSystemEntrySnapshot Entry);

/// <summary>One enumerated directory child.</summary>
/// <param name="FullPath">Canonical absolute path of the child.</param>
/// <param name="Name">Child name.</param>
/// <param name="Metadata">The child's own metadata (a link's own metadata for a symbolic link).</param>
/// <param name="LinkTarget">For a symbolic link, the metadata of what it points to; <see langword="null"/> when dangling or not a link.</param>
internal sealed record KazoEntry(string FullPath, string Name, FileSystemEntryMetadata Metadata, FileSystemEntryMetadata? LinkTarget)
{
    /// <summary>Gets whether the child is, or points to, a directory.</summary>
    public bool IsDirectory => (Metadata.Kind == FileSystemEntryKind.SymbolicLink ? LinkTarget?.Kind : Metadata.Kind) == FileSystemEntryKind.Directory;
}

/// <summary>An in-memory <see cref="IFileSystemContentSource"/> publishing a fixed byte array.</summary>
internal sealed class KazoMemoryContentSource(byte[] content, FileSystemContentDescriptor descriptor) : IFileSystemContentSource
{
    /// <inheritdoc />
    public FileSystemContentDescriptor Descriptor { get; } = descriptor;

    /// <inheritdoc />
    public long? Length => content.LongLength;

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
    }
}

/// <summary>Matches entry names against <c>System.IO</c>-style search patterns (<c>*</c> and <c>?</c>, case-sensitive).</summary>
internal static class KazoPathPattern
{
    /// <summary>Validates a search pattern the way <c>Directory.GetFiles</c> does.</summary>
    public static void Validate(string searchPattern, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(searchPattern, parameterName);
        if (searchPattern.Contains(KazoPath.DirectorySeparatorChar))
        {
            throw new ArgumentException("Search patterns cannot contain a directory separator.", parameterName);
        }
    }

    /// <summary>Returns whether <paramref name="name"/> matches <paramref name="pattern"/>; <c>*</c>, <c>*.*</c> and an empty pattern match everything.</summary>
    public static bool IsMatch(string name, string pattern)
    {
        if (pattern.Length == 0 || pattern == "*" || pattern == "*.*")
        {
            return true;
        }

        // Iterative wildcard matching with single-star backtracking.
        int n = 0, p = 0, starPattern = -1, starName = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n]))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starName = n;
            }
            else if (starPattern >= 0)
            {
                p = starPattern + 1;
                n = ++starName;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
