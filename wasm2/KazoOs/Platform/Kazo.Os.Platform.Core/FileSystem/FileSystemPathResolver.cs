using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.FileSystem;

/// <summary>Resolves bounded symbolic-link traversal across provider mounts.</summary>
public sealed class FileSystemPathResolver(IFileSystemMountRouter router) : IFileSystemPathResolver
{
    private readonly IFileSystemMountRouter _router =
        router ?? throw new ArgumentNullException(nameof(router));

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemPathResolution>> ResolveAsync(
        VirtualPath path,
        FileSystemLinkBehavior finalLinkBehavior,
        FileSystemOperation operation,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(path.Value))
        {
            throw new ArgumentException("Path resolution requires a canonical path.", nameof(path));
        }

        if (!Enum.IsDefined(finalLinkBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(finalLinkBehavior));
        }

        HashSet<FileSystemEntryId> followedLinks = [];
        VirtualPath remainingPath = path;
        int followedCount = 0;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Failure(operation, FileSystemErrorCode.Cancelled, remainingPath);
            }

            string[] segments = remainingPath.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            bool restarted = false;
            IReadOnlyList<FileSystemEntrySnapshot>? walked =
                await TryWalkAsync(remainingPath, segments, context, cancellationToken);

            for (int index = 0; index < segments.Length; index++)
            {
                VirtualPath candidate = BuildPath(segments, index + 1);
                FileSystemResult<FileSystemEntrySnapshot> stat;
                if (walked is not null)
                {
                    // The walk stops before the first missing component, exactly where a
                    // per-prefix lstat would have reported NotFound.
                    stat = index < walked.Count
                        ? FileSystemResult<FileSystemEntrySnapshot>.Success(walked[index])
                        : FileSystemResult<FileSystemEntrySnapshot>.Failure(
                            new FileSystemError(FileSystemOperation.Stat, FileSystemErrorCode.NotFound, candidate));
                }
                else
                {
                    FileSystemMountResolution route = _router.Resolve(candidate);
                    try
                    {
                        stat = await route.Mount.Provider.StatAsync(
                            new FileSystemStatRequest(candidate, FileSystemLinkBehavior.NoFollow),
                            context,
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return Failure(operation, FileSystemErrorCode.Cancelled, candidate);
                    }
                }

                if (!stat.Succeeded)
                {
                    return Failure(operation, stat.Error?.Code ?? FileSystemErrorCode.ProviderFailure, candidate);
                }

                FileSystemEntryMetadata metadata = stat.Value!.Metadata;
                bool isFinal = index == segments.Length - 1;
                bool followLink = metadata is SymbolicLinkMetadata
                    && (!isFinal || finalLinkBehavior == FileSystemLinkBehavior.Follow);

                if (!isFinal
                    && metadata.Kind == FileSystemEntryKind.Directory
                    && !CanTraverse(metadata, context))
                {
                    return Failure(operation, FileSystemErrorCode.PermissionDenied, candidate);
                }

                if (followLink)
                {
                    SymbolicLinkMetadata link = (SymbolicLinkMetadata)metadata;
                    if (!followedLinks.Add(link.Id))
                    {
                        return Failure(operation, FileSystemErrorCode.SymbolicLinkLoop, candidate);
                    }

                    if (followedCount >= FileSystemTraversalPolicy.MaximumSymbolicLinkHops)
                    {
                        return Failure(operation, FileSystemErrorCode.SymbolicLinkLimitExceeded, candidate);
                    }

                    followedCount++;
                    string suffix = string.Join('/', segments.Skip(index + 1));
                    try
                    {
                        remainingPath = ResolveTarget(candidate, link.Target, suffix);
                    }
                    catch (FormatException)
                    {
                        return Failure(operation, FileSystemErrorCode.RootContainmentViolation, candidate);
                    }

                    restarted = true;
                    break;
                }

                if (!isFinal && metadata.Kind != FileSystemEntryKind.Directory)
                {
                    return Failure(operation, FileSystemErrorCode.NotDirectory, candidate);
                }
            }

            if (!restarted)
            {
                return FileSystemResult<FileSystemPathResolution>.Success(
                    new FileSystemPathResolution(remainingPath, followedCount));
            }
        }
    }

    /// <summary>
    /// Stats every component of <paramref name="path"/> in one provider request when all its
    /// prefixes live on one mount whose provider can walk a path (<see cref="IFileSystemPathWalkProvider"/>).
    /// Returns <see langword="null"/> when that is not possible or the walk failed: the caller
    /// then stats each prefix, which reports any error exactly as before.
    /// </summary>
    private async ValueTask<IReadOnlyList<FileSystemEntrySnapshot>?> TryWalkAsync(
        VirtualPath path,
        string[] segments,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        // One component is one stat either way.
        if (segments.Length < 2)
        {
            return null;
        }

        FileSystemMount mount = _router.Resolve(path).Mount;
        if (mount.Provider is not IFileSystemPathWalkProvider walker)
        {
            return null;
        }

        for (int count = 1; count < segments.Length; count++)
        {
            if (!ReferenceEquals(_router.Resolve(BuildPath(segments, count)).Mount, mount))
            {
                return null;
            }
        }

        try
        {
            FileSystemResult<IReadOnlyList<FileSystemEntrySnapshot>> walk =
                await walker.StatComponentsAsync(path, context, cancellationToken);
            return walk.Succeeded ? walk.Value : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static VirtualPath ResolveTarget(VirtualPath linkPath, string target, string suffix)
    {
        string basePath = target.StartsWith("/", StringComparison.Ordinal)
            ? target
            : $"{GetParent(linkPath).Value}/{target}";
        string combined = string.IsNullOrEmpty(suffix) ? basePath : $"{basePath}/{suffix}";
        return VirtualPath.Parse(combined);
    }

    private static VirtualPath GetParent(VirtualPath path)
    {
        int separator = path.Value.LastIndexOf('/');
        return separator <= 0
            ? VirtualPath.Parse("/")
            : VirtualPath.Parse(path.Value[..separator]);
    }

    private static VirtualPath BuildPath(string[] segments, int count) =>
        VirtualPath.Parse(count == 0 ? "/" : $"/{string.Join('/', segments.Take(count))}");

    private static bool CanTraverse(
        FileSystemEntryMetadata metadata,
        FileSystemAuthorizationContext context)
    {
        if (context.OperationContext.EffectiveAuthority == AppAuthority.System)
        {
            return true;
        }

        bool isOwner = string.Equals(metadata.OwnerId, context.OperationContext.UserId, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(context.OperationContext.UserName)
                && string.Equals(metadata.OwnerId, context.OperationContext.UserName, StringComparison.Ordinal));

        FileSystemAccess access = isOwner
            ? metadata.Permissions.Owner
            : context.GroupIds.Contains(metadata.GroupId)
                ? metadata.Permissions.Group
                : metadata.Permissions.Other;
        return (access & FileSystemAccess.Execute) == FileSystemAccess.Execute;
    }

    private static FileSystemResult<FileSystemPathResolution> Failure(
        FileSystemOperation operation,
        FileSystemErrorCode code,
        VirtualPath path) =>
        FileSystemResult<FileSystemPathResolution>.Failure(new FileSystemError(operation, code, path));
}