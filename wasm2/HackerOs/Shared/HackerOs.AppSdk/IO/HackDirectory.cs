using System.Runtime.CompilerServices;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.AppSdk.IO;

/// <summary>
/// Static directory operations on the HackerOS virtual filesystem, shaped like <see cref="Directory"/>
/// (ADR 0043). Runs as the ambient app (<see cref="HackExecutionContext.Current"/>).
/// </summary>
/// <remarks>
/// Listing methods return canonical absolute paths (not paths combined with the caller's relative
/// form as <see cref="Directory.GetFiles(string)"/> does), in ordinal name order, depth-first for
/// <see cref="SearchOption.AllDirectories"/>. Recursion does not descend into symbolic links, which
/// rules out link loops. Patterns support <c>*</c> and <c>?</c> and are case-sensitive, like Linux.
/// </remarks>
public static class HackDirectory
{
    /// <summary>Creates a directory and every missing parent (mode <c>0755</c>); succeeds if it already exists.</summary>
    /// <returns>The directory with a fresh snapshot.</returns>
    /// <exception cref="HackIOException">A file already occupies the path or one of its parents.</exception>
    public static Task<HackDirectoryInfo> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        CreateDirectoryCoreAsync(path, HackUnixFileMode.DefaultDirectory, cancellationToken);

    /// <summary>Creates a directory and every missing parent with <paramref name="unixCreateMode"/> (like the .NET 7+ overload).</summary>
    public static Task<HackDirectoryInfo> CreateDirectoryAsync(string path, UnixFileMode unixCreateMode, CancellationToken cancellationToken = default) =>
        CreateDirectoryCoreAsync(path, HackUnixFileMode.ToPermissions(unixCreateMode, nameof(unixCreateMode)), cancellationToken);

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

        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;
        try
        {
            FileSystemEntrySnapshot? snapshot = await HackFileSystemCore.TryStatAsync(
                gateway, HackPath.Resolve(path, nameof(path)), FileSystemLinkBehavior.Follow, HackEntryTarget.Directory, cancellationToken)
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
    /// <see cref="HackDirectorySize.IsComplete"/> is then <see langword="false"/>.
    /// </summary>
    /// <exception cref="HackDirectoryNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    /// <exception cref="HackUnauthorizedAccessException">The folder itself cannot be listed.</exception>
    public static async Task<HackDirectorySize> GetTotalSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        VirtualPath target = HackPath.Resolve(path, nameof(path));
        FileSystemResult<FileSystemTreeSize> result = await HackFileSystemCore.Gateway.MeasureAsync(target, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw HackFileSystemErrorTranslator.Translate(result.Error!, HackEntryTarget.Directory, target.Value);
        }

        FileSystemTreeSize size = result.Value!;
        return new HackDirectorySize(size.Length, size.FileCount, size.DirectoryCount, size.IsComplete);
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
        await foreach (HackEntry entry in EnumerateCore(path, searchPattern, searchOption, includeFiles: true, includeDirectories: true, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return entry.FullPath;
        }
    }

    /// <summary>
    /// Moves (renames) a directory, or a file, to a destination that must not exist. Across two
    /// mounts the facade copies the subtree then deletes the source.
    /// </summary>
    /// <exception cref="HackDirectoryNotFoundException">The source does not exist.</exception>
    /// <exception cref="HackIOException">The destination exists, or is inside the source.</exception>
    public static async Task MoveAsync(string sourceDirName, string destDirName, CancellationToken cancellationToken = default)
    {
        VirtualPath source = HackPath.Resolve(sourceDirName, nameof(sourceDirName));
        VirtualPath destination = HackPath.Resolve(destDirName, nameof(destDirName));
        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;

        await HackFileSystemCore.StatAsync(gateway, source, FileSystemLinkBehavior.NoFollow, HackEntryTarget.Directory, cancellationToken)
            .ConfigureAwait(false);
        if (source == destination || destination.Value.StartsWith(source.Value + "/", StringComparison.Ordinal))
        {
            throw new HackIOException(
                $"Cannot move '{source.Value}' to '{destination.Value}': the destination is the source or inside it.",
                FileSystemErrorCode.InvalidPath,
                source.Value);
        }

        if (await HackFileSystemCore.TryStatAsync(gateway, destination, FileSystemLinkBehavior.NoFollow, HackEntryTarget.Directory, cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            throw new HackIOException($"The destination '{destination.Value}' already exists.", FileSystemErrorCode.AlreadyExists, destination.Value);
        }

        await HackFileSystemCore.MoveEntryAsync(gateway, source, destination, HackEntryTarget.Directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a directory and everything under it (like <c>cp -r</c>) to a destination that must not
    /// exist; permission bits are kept, symbolic links are copied as links. The engine copies the whole
    /// subtree in one transaction when it can; across two mounts the facade recreates it entry by entry.
    /// .NET has no <c>Directory.Copy</c>: this member is a HackerOS addition (ADR 0058, section 1).
    /// </summary>
    /// <exception cref="HackDirectoryNotFoundException">The source does not exist.</exception>
    /// <exception cref="HackIOException">The destination exists, or is the source or inside it.</exception>
    public static async Task CopyAsync(string sourceDirName, string destDirName, CancellationToken cancellationToken = default)
    {
        VirtualPath source = HackPath.Resolve(sourceDirName, nameof(sourceDirName));
        VirtualPath destination = HackPath.Resolve(destDirName, nameof(destDirName));
        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;

        if (source == destination || destination.Value.StartsWith(source.Value + "/", StringComparison.Ordinal))
        {
            throw new HackIOException(
                $"Cannot copy '{source.Value}' to '{destination.Value}': the destination is the source or inside it.",
                FileSystemErrorCode.InvalidPath,
                source.Value);
        }

        for (int attempt = 1; ; attempt++)
        {
            FileSystemEntrySnapshot sourceEntry = await HackFileSystemCore.StatAsync(
                gateway, source, FileSystemLinkBehavior.NoFollow, HackEntryTarget.Directory, cancellationToken).ConfigureAwait(false);
            if (await HackFileSystemCore.TryStatAsync(gateway, destination, FileSystemLinkBehavior.NoFollow, HackEntryTarget.Directory, cancellationToken)
                    .ConfigureAwait(false) is not null)
            {
                throw new HackIOException($"The destination '{destination.Value}' already exists.", FileSystemErrorCode.AlreadyExists, destination.Value);
            }

            FileSystemEntrySnapshot destinationParent = await HackFileSystemCore.RequireParentAsync(gateway, destination, cancellationToken).ConfigureAwait(false);
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
                await HackFileSystemCore.CopyTreeAsync(gateway, source, destination, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (error.Code == FileSystemErrorCode.RevisionConflict && attempt < 2)
            {
                continue;
            }

            throw HackFileSystemErrorTranslator.Translate(error, HackEntryTarget.Directory, source.Value);
        }
    }

    /// <summary>Deletes a directory permanently; a non-empty one requires <paramref name="recursive"/>.</summary>
    /// <exception cref="HackDirectoryNotFoundException">The directory does not exist.</exception>
    /// <exception cref="HackIOException">The path is a file, or the directory is not empty and <paramref name="recursive"/> is false.</exception>
    public static async Task DeleteAsync(string path, bool recursive = false, CancellationToken cancellationToken = default)
    {
        VirtualPath resolved = HackPath.Resolve(path, nameof(path));
        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;

        FileSystemEntrySnapshot entry = await HackFileSystemCore.StatAsync(
            gateway, resolved, FileSystemLinkBehavior.NoFollow, HackEntryTarget.Directory, cancellationToken).ConfigureAwait(false);
        if (entry.Metadata.Kind != FileSystemEntryKind.Directory)
        {
            throw new HackIOException($"The path '{resolved.Value}' is not a directory.", FileSystemErrorCode.NotDirectory, resolved.Value);
        }

        if (!await HackFileSystemCore.DeleteEntryAsync(gateway, resolved, recursive, HackEntryTarget.Directory, cancellationToken).ConfigureAwait(false))
        {
            throw new HackDirectoryNotFoundException(resolved.Value);
        }
    }

    private static async Task<HackDirectoryInfo> CreateDirectoryCoreAsync(
        string path, FileSystemPermissions permissions, CancellationToken cancellationToken)
    {
        VirtualPath target = HackPath.Resolve(path, nameof(path));
        IAppFileSystemGateway gateway = HackFileSystemCore.Gateway;

        // Walk up to the deepest existing ancestor, then create downwards (like mkdir -p).
        Stack<VirtualPath> missing = new();
        for (VirtualPath? current = target; current is { } candidate; current = HackFileSystemCore.ParentOf(candidate))
        {
            FileSystemEntrySnapshot? snapshot = await HackFileSystemCore.TryStatAsync(
                gateway, candidate, FileSystemLinkBehavior.Follow, HackEntryTarget.Directory, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                missing.Push(candidate);
                continue;
            }

            if (snapshot.Metadata.Kind != FileSystemEntryKind.Directory)
            {
                throw new HackIOException(
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
                await HackFileSystemCore.TryStatAsync(gateway, directory, FileSystemLinkBehavior.Follow, HackEntryTarget.Directory, cancellationToken)
                    .ConfigureAwait(false) is { Metadata.Kind: FileSystemEntryKind.Directory })
            {
                continue;
            }

            throw HackFileSystemErrorTranslator.Translate(created.Transaction.Error!, HackEntryTarget.Directory, directory.Value);
        }

        return await HackDirectoryInfo.GetAsync(target.Value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string[]> CollectAsync(
        string path, string searchPattern, SearchOption searchOption, bool includeFiles, bool includeDirectories, CancellationToken cancellationToken)
    {
        List<string> paths = [];
        await foreach (HackEntry entry in EnumerateCore(path, searchPattern, searchOption, includeFiles, includeDirectories, cancellationToken)
                           .ConfigureAwait(false))
        {
            paths.Add(entry.FullPath);
        }

        return [.. paths];
    }

    private static IAsyncEnumerable<HackEntry> EnumerateCore(
        string path, string searchPattern, SearchOption searchOption, bool includeFiles, bool includeDirectories, CancellationToken cancellationToken)
    {
        VirtualPath directory = HackPath.Resolve(path, nameof(path));
        HackPathPattern.Validate(searchPattern, nameof(searchPattern));
        if (!Enum.IsDefined(searchOption))
        {
            throw new ArgumentOutOfRangeException(nameof(searchOption), searchOption, "Unknown search option.");
        }

        return HackFileSystemCore.EnumerateAsync(
            HackFileSystemCore.Gateway, directory, searchPattern, searchOption, includeFiles, includeDirectories, cancellationToken);
    }
}
