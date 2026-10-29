using HackerOs.App.Abstractions;

namespace HackerOs.Simulation.Abstractions.FileSystem;

/// <summary>
/// The total size of a file or of a folder's whole content (ADR 0185 §5), as the OS reports it from the
/// entries' metadata, without reading any content: what <c>du -sb</c> counts.
/// </summary>
/// <param name="Length">Sum of the lengths of the regular files, in bytes.</param>
/// <param name="FileCount">Number of regular files counted (the entry itself when it is a file).</param>
/// <param name="DirectoryCount">Number of folders below the measured one (not counting it).</param>
/// <param name="IsComplete">
/// <see langword="false"/> when a folder below could not be listed (denied, removed meanwhile): the
/// totals then cover only what could be read.
/// </param>
public sealed record FileSystemTreeSize(long Length, long FileCount, long DirectoryCount, bool IsComplete);

/// <summary>
/// Measures a subtree with only stat and directory listings: the default of
/// <c>IAppFileSystemGateway.MeasureAsync</c>, shared by every gateway. Symbolic links are neither
/// followed nor counted (no loops), like <c>du</c> without <c>-L</c>.
/// </summary>
public static class FileSystemTreeMeasure
{
    /// <summary>Measures <paramref name="path"/>, which may be a file or a folder (a link to either is followed).</summary>
    /// <param name="path">The entry to measure.</param>
    /// <param name="stat">Reads one entry, following a final link.</param>
    /// <param name="enumerate">Lists one folder's immediate children.</param>
    /// <param name="cancellationToken">Cancels the walk, checked before each folder.</param>
    /// <returns>The totals, or the error of the measured entry itself (a folder below that fails only clears <see cref="FileSystemTreeSize.IsComplete"/>).</returns>
    public static async ValueTask<FileSystemResult<FileSystemTreeSize>> MeasureAsync(
        VirtualPath path,
        Func<VirtualPath, CancellationToken, ValueTask<FileSystemResult<FileSystemEntrySnapshot>>> stat,
        Func<VirtualPath, CancellationToken, ValueTask<FileSystemResult<FileSystemDirectorySnapshot>>> enumerate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stat);
        ArgumentNullException.ThrowIfNull(enumerate);

        FileSystemResult<FileSystemEntrySnapshot> root = await stat(path, cancellationToken).ConfigureAwait(false);
        if (!root.Succeeded)
        {
            return FileSystemResult<FileSystemTreeSize>.Failure(root.Error!);
        }

        if (root.Value!.Metadata is FileMetadata file)
        {
            return FileSystemResult<FileSystemTreeSize>.Success(new FileSystemTreeSize(file.Length, 1, 0, true));
        }

        if (root.Value.Metadata.Kind != FileSystemEntryKind.Directory)
        {
            return FileSystemResult<FileSystemTreeSize>.Success(new FileSystemTreeSize(0, 0, 0, true));
        }

        long length = 0, files = 0, directories = 0;
        bool complete = true;
        Stack<VirtualPath> pending = new();
        pending.Push(root.Value.Path);
        bool first = true;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VirtualPath folder = pending.Pop();
            FileSystemResult<FileSystemDirectorySnapshot> listing = await enumerate(folder, cancellationToken).ConfigureAwait(false);
            if (!listing.Succeeded)
            {
                if (first)
                {
                    return FileSystemResult<FileSystemTreeSize>.Failure(listing.Error!);
                }

                complete = false;
                continue;
            }

            first = false;
            foreach (FileSystemDirectoryItem entry in listing.Value!.Entries)
            {
                switch (entry.Metadata)
                {
                    case FileMetadata child:
                        length += child.Length;
                        files++;
                        break;
                    case { Kind: FileSystemEntryKind.Directory }:
                        directories++;
                        pending.Push(Combine(listing.Value.Path, entry.Name.Value));
                        break;
                }
            }
        }

        return FileSystemResult<FileSystemTreeSize>.Success(new FileSystemTreeSize(length, files, directories, complete));
    }

    private static VirtualPath Combine(VirtualPath parent, string name) =>
        VirtualPath.Parse(parent.Value == "/" ? "/" + name : parent.Value + "/" + name);
}
