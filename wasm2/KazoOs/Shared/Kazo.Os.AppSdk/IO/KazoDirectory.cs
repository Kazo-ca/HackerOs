using System.Runtime.CompilerServices;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.IO;

/// <summary>
/// Static directory operations on the Kazo OS virtual filesystem, shaped like <see cref="Directory"/>
/// (ADR 0043). Runs as the ambient app (<see cref="KazoExecutionContext.Current"/>).
/// </summary>
/// <remarks>
/// Listing methods return canonical absolute paths (not paths combined with the caller's relative
/// form as <see cref="Directory.GetFiles(string)"/> does), in ordinal name order, depth-first for
/// <see cref="SearchOption.AllDirectories"/>. Recursion does not descend into symbolic links, which
/// rules out link loops. Patterns support <c>*</c> and <c>?</c> and are case-sensitive, like Linux.
/// </remarks>
public static class KazoDirectory
{
    /// <summary>Creates a directory and every missing parent (mode <c>0755</c>); succeeds if it already exists.</summary>
    /// <returns>The directory with a fresh snapshot.</returns>
    /// <exception cref="KazoIOException">A file already occupies the path or one of its parents.</exception>
    public static Task<KazoDirectoryInfo> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        CreateDirectoryCoreAsync(path, KazoUnixFileMode.DefaultDirectory, cancellationToken);

    /// <summary>Creates a directory and every missing parent with <paramref name="unixCreateMode"/> (like the .NET 7+ overload).</summary>
    public static Task<KazoDirectoryInfo> CreateDirectoryAsync(string path, UnixFileMode unixCreateMode, CancellationToken cancellationToken = default) =>
        CreateDirectoryCoreAsync(path, KazoUnixFileMode.ToPermissions(unixCreateMode, nameof(unixCreateMode)), cancellationToken);

    /// <summary>
    /// Returns whether <paramref name="path"/> is an existing directory (following links). Like
    /// <see cref="Directory.Exists(string)"/>, an invalid path or a denied lookup answers <see langword="false"/>.
    /// </summary>
    public static async Task<bool> ExistsAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        IAppFileSystemGateway gateway = KazoFileSystemCore.Gateway;
        try
        {
            FileSystemEntrySnapshot? snapshot = await KazoFileSystemCore.TryStatAsync(
                gateway, KazoPath.Resolve(path, nameof(path)), FileSystemLinkBehavior.Follow, KazoEntryTarget.Directory, cancellationToken)
                .ConfigureAwait(false);
            return snapshot?.Metadata.Kind == FileSystemEntryKind.Directory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the total size of a folder's whole content, or of a file (ADR 0185 §5): the sum of the files'
    /// lengths, as the OS reports them, without reading any content (<c>du -sb</c>). Symbolic links below
    /// are neither followed nor counted; a subfolder that cannot be read is skipped and
    /// <see cref="KazoDirectorySize.IsComplete"/> is then <see langword="false"/>.
    /// </summary>
    /// <exception cref="KazoDirectoryNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    /// <exception cref="KazoUnauthorizedAccessException">The folder itself cannot be listed.</exception>
    public static async Task<KazoDirectorySize> GetTotalSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath target = KazoPath.Resolve(path, nameof(path));
        FileSystemResult<FileSystemTreeSize> result = await KazoFileSystemCore.Gateway.MeasureAsync(target, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw KazoFileSystemErrorTranslator.Translate(result.Error!, KazoEntryTarget.Directory, target.Value);
        }

        FileSystemTreeSize size = result.Value!;
        return new KazoDirectorySize(size.Length, size.FileCount, size.DirectoryCount, size.IsComplete);
    }

        /// <summary>Lists the files of a directory.</summary>
    public static Task<string[]> GetFilesAsync(
        string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly, CancellationToken cancellationToken = default) =>
        CollectAsync(path, searchPattern, searchOption, includeFiles: true, includeDirectories: false, cancellationToken);

    /// <summary>Lists the subdirectories of a directory.</summary>
    public static Task<string[]> GetDirectoriesAsync(
        string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly, CancellationToken cancellationToken = default) =>
        CollectAsync(path, searchPattern, searchOption, includeFiles: false, includeDirectories: true, cancellationToken);

    /// <summary>Lists the files and subdirectories of a directory.</summary>
    public static Task<string[]> GetFileSystemEntriesAsync(
        string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly, CancellationToken cancellationToken = default) =>
        CollectAsync(path, searchPattern, searchOption, includeFiles: true, includeDirectories: true, cancellationToken);

    /// <summary>Streams the files and subdirectories of a directory, one engine listing at a time.</summary>
    public static async IAsyncEnumerable<string> EnumerateFileSystemEntriesAsync(
        string path,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (KazoEntry entry in EnumerateCore(path, searchPattern, searchOption, includeFiles: true, includeDirectories: true, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return entry.FullPath;
        }
    }

    /// <summary>
    /// Moves (renames) a directory, or a file, to a destination that must not exist. Across two
    /// mounts the facade copies the subtree then deletes the source.
    /// </summary>
    /// <exception cref="KazoDirectoryNotFoundException">The source does not exist.</exception>
    /// <exception cref="KazoIOException">The destination exists, or is inside the source.</exception>
    public static async Task MoveAsync(string sourceDirName, string destDirName, CancellationToken cancellationToken = default)
    {
        VirtualPath source = KazoPath.Resolve(sourceDirName, nameof(sourceDirName));
        VirtualPath destination = KazoPath.Resolve(destDirName, nameof(destDirName));
        IAppFileSystemGateway gateway = KazoFileSystemCore.Gateway;

        await KazoFileSystemCore.StatAsync(gateway, source, FileSystemLinkBehavior.NoFollow, KazoEntryTarget.Directory, cancellationToken)
            .ConfigureAwait(false);
        if (source == destination || destination.Value.StartsWith(source.Value + "/", StringComparison.Ordinal))
        {
            throw new KazoIOException(
                $"Cannot move '{source.Value}' to '{destination.Value}': the destination is the source or inside it.",
                FileSystemErrorCode.InvalidPath,
                source.Value);
        }

        if (await KazoFileSystemCore.TryStatAsync(gateway, destination, FileSystemLinkBehavior.NoFollow, KazoEntryTarget.Directory, cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            throw new KazoIOException($"The destination '{destination.Value}' already exists.", FileSystemErrorCode.AlreadyExists, destination.Value);
        }

        await KazoFileSystemCore.MoveEntryAsync(gateway, source, destination, KazoEntryTarget.Directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a directory and everything under it (like <c>cp -r</c>) to a destination that must not
    /// exist; permission bits are kept, symbolic links are copied as links. The engine copies the whole
    /// subtree in one transaction when it can; across two mounts the facade recreates it entry by entry.
    /// .NET has no <c>Directory.Copy</c>: this member is a Kazo OS addition (ADR 0058, section 1).
    /// </summary>
    /// <exception cref="KazoDirectoryNotFoundException">The source does not exist.</exception>
    /// <exception cref="KazoIOException">The destination exists, or is the source or inside it.</exception>
    public static async Task CopyAsync(string sourceDirName, string destDirName, CancellationToken cancellationToken = default)
    {
        VirtualPath source = KazoPath.Resolve(sourceDirName, nameof(sourceDirName));
        VirtualPath destination = KazoPath.Resolve(destDirName, nameof(destDirName));
        IAppFileSystemGateway gateway = KazoFileSystemCore.Gateway;

        if (source == destination || destination.Value.StartsWith(source.Value + "/", StringComparison.Ordinal))
        {
            throw new KazoIOException(
                $"Cannot copy '{source.Value}' to '{destination.Value}': the destination is the source or inside it.",
                FileSystemErrorCode.InvalidPath,
                source.Value);
        }

        for (int attempt = 1; ; attempt++)
        {
            FileSystemEntrySnapshot sourceEntry = await KazoFileSystemCore.StatAsync(
                gateway, source, FileSystemLinkBehavior.NoFollow, KazoEntryTarget.Directory, cancellationToken).ConfigureAwait(false);
            if (await KazoFileSystemCore.TryStatAsync(gateway, destination, FileSystemLinkBehavior.NoFollow, KazoEntryTarget.Directory, cancellationToken)
                    .ConfigureAwait(false) is not null)
            {
                throw new KazoIOException($"The destination '{destination.Value}' already exists.", FileSystemErrorCode.AlreadyExists, destination.Value);
            }

            FileSystemEntrySnapshot destinationParent = await KazoFileSystemCore.RequireParentAsync(gateway, destination, cancellationToken).ConfigureAwait(false);
            FileSystemMutationResult copied = await gateway.CopyAsync(
                new FileSystemCopyRequest(source, destination, sourceEntry.Metadata.Revision, destinationParent.Metadata.Revision),
                cancellationToken).ConfigureAwait(false);
            if (copied.Succeeded)
            {
                return;
            }

            FileSystemError error = copied.Transaction.Error!;
            if (error.Code == FileSystemErrorCode.CrossMountOperation)
            {
                await KazoFileSystemCore.CopyTreeAsync(gateway, source, destination, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (error.Code == FileSystemErrorCode.RevisionConflict && attempt < 2)
            {
                continue;
            }

            throw KazoFileSystemErrorTranslator.Translate(error, KazoEntryTarget.Directory, source.Value);
        }
    }

    /// <summary>Deletes a directory permanently; a non-empty one requires <paramref name="recursive"/>.</summary>
    /// <exception cref="KazoDirectoryNotFoundException">The directory does not exist.</exception>
    /// <exception cref="KazoIOException">The path is a file, or the directory is not empty and <paramref name="recursive"/> is false.</exception>
    public static async Task DeleteAsync(string path, bool recursive = false, CancellationToken cancellationToken = default)
    {
        VirtualPath resolved = KazoPath.Resolve(path, nameof(path));
        IAppFileSystemGateway gateway = KazoFileSystemCore.Gateway;

        FileSystemEntrySnapshot entry = await KazoFileSystemCore.StatAsync(
            gateway, resolved, FileSystemLinkBehavior.NoFollow, KazoEntryTarget.Directory, cancellationToken).ConfigureAwait(false);
        if (entry.Metadata.Kind != FileSystemEntryKind.Directory)
        {
            throw new KazoIOException($"The path '{resolved.Value}' is not a directory.", FileSystemErrorCode.NotDirectory, resolved.Value);
        }

        if (!await KazoFileSystemCore.DeleteEntryAsync(gateway, resolved, recursive, KazoEntryTarget.Directory, cancellationToken).ConfigureAwait(false))
        {
            throw new KazoDirectoryNotFoundException(resolved.Value);
        }
    }

    private static async Task<KazoDirectoryInfo> CreateDirectoryCoreAsync(
        string path, FileSystemPermissions permissions, CancellationToken cancellationToken)
    {
        VirtualPath target = KazoPath.Resolve(path, nameof(path));
        IAppFileSystemGateway gateway = KazoFileSystemCore.Gateway;

        // Walk up to the deepest existing ancestor, then create downwards (like mkdir -p).
        Stack<VirtualPath> missing = new();
        for (VirtualPath? current = target; current is { } candidate; current = KazoFileSystemCore.ParentOf(candidate))
        {
            FileSystemEntrySnapshot? snapshot = await KazoFileSystemCore.TryStatAsync(
                gateway, candidate, FileSystemLinkBehavior.Follow, KazoEntryTarget.Directory, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                missing.Push(candidate);
                continue;
            }

            if (snapshot.Metadata.Kind != FileSystemEntryKind.Directory)
            {
                throw new KazoIOException(
                    $"Cannot create '{target.Value}' because a file exists at '{candidate.Value}'.",
                    FileSystemErrorCode.AlreadyExists,
                    candidate.Value);
            }

            break;
        }

        while (missing.TryPop(out VirtualPath directory))
        {
            FileSystemMutationResult created = await gateway.CreateAsync(
                new FileSystemCreateRequest(directory, FileSystemEntryKind.Directory, permissions),
                cancellationToken).ConfigureAwait(false);
            if (created.Succeeded)
            {
                continue;
            }

            // Lost a race with someone creating the same directory: fine as long as it is a directory.
            if (created.Transaction.Error!.Code == FileSystemErrorCode.AlreadyExists &&
                await KazoFileSystemCore.TryStatAsync(gateway, directory, FileSystemLinkBehavior.Follow, KazoEntryTarget.Directory, cancellationToken)
                    .ConfigureAwait(false) is { Metadata.Kind: FileSystemEntryKind.Directory })
            {
                continue;
            }

            throw KazoFileSystemErrorTranslator.Translate(created.Transaction.Error!, KazoEntryTarget.Directory, directory.Value);
        }

        return await KazoDirectoryInfo.GetAsync(target.Value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string[]> CollectAsync(
        string path, string searchPattern, SearchOption searchOption, bool includeFiles, bool includeDirectories, CancellationToken cancellationToken)
    {
        List<string> paths = [];
        await foreach (KazoEntry entry in EnumerateCore(path, searchPattern, searchOption, includeFiles, includeDirectories, cancellationToken)
                           .ConfigureAwait(false))
        {
            paths.Add(entry.FullPath);
        }

        return [.. paths];
    }

    private static IAsyncEnumerable<KazoEntry> EnumerateCore(
        string path, string searchPattern, SearchOption searchOption, bool includeFiles, bool includeDirectories, CancellationToken cancellationToken)
    {
        VirtualPath directory = KazoPath.Resolve(path, nameof(path));
        KazoPathPattern.Validate(searchPattern, nameof(searchPattern));
        if (!Enum.IsDefined(searchOption))
        {
            throw new ArgumentOutOfRangeException(nameof(searchOption), searchOption, "Unknown search option.");
        }

        return KazoFileSystemCore.EnumerateAsync(
            KazoFileSystemCore.Gateway, directory, searchPattern, searchOption, includeFiles, includeDirectories, cancellationToken);
    }
}
