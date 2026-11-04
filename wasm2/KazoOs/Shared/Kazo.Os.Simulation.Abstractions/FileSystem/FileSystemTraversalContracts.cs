using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>Defines stable virtual path traversal limits.</summary>
public static class FileSystemTraversalPolicy
{
    /// <summary>Maximum symbolic links followed during one resolution.</summary>
    public const int MaximumSymbolicLinkHops = 40;
}

/// <summary>Contains the final canonical path and followed symbolic-link count.</summary>
/// <param name="Path">Resolved canonical path.</param>
/// <param name="FollowedSymbolicLinks">Number of followed symbolic links.</param>
public sealed record FileSystemPathResolution(
    VirtualPath Path,
    int FollowedSymbolicLinks);

/// <summary>Resolves symbolic links through the global mount namespace.</summary>
public interface IFileSystemPathResolver
{
    /// <summary>Resolves one canonical path with explicit final-link behavior.</summary>
    ValueTask<FileSystemResult<FileSystemPathResolution>> ResolveAsync(
        VirtualPath path,
        FileSystemLinkBehavior finalLinkBehavior,
        FileSystemOperation operation,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);
}
/// <summary>
/// Optional provider capability: looks up every component of a path in one storage request, the
/// way a kernel walks a path once (namei) instead of starting again from <c>/</c> for each
/// prefix. <see cref="IFileSystemPathResolver"/> uses it when every prefix of a path belongs to
/// the same mount; other providers keep being asked one <c>lstat</c> per prefix.
/// </summary>
public interface IFileSystemPathWalkProvider
{
    /// <summary>
    /// Returns the metadata of each component of <paramref name="path"/>, without following any
    /// symbolic link: element <c>i</c> is the prefix made of the first <c>i + 1</c> names below
    /// <c>/</c>. The list stops after the deepest component that exists, so a list shorter than
    /// the number of names means the next component does not exist (<see cref="FileSystemErrorCode.NotFound"/>).
    /// </summary>
    /// <param name="path">Canonical absolute path other than <c>/</c>.</param>
    /// <param name="context">Caller identity, as for <see cref="IFileSystemProvider.StatAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The component snapshots, or the storage failure that stopped the lookup.</returns>
    ValueTask<FileSystemResult<IReadOnlyList<FileSystemEntrySnapshot>>> StatComponentsAsync(
        VirtualPath path,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);
}
