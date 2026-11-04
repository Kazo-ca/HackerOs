using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>Controls whether an operation follows the final symbolic link.</summary>
public enum FileSystemLinkBehavior
{
    /// <summary>Resolve the final symbolic link to its target.</summary>
    Follow = 1,

    /// <summary>Operate on the final symbolic-link entry itself.</summary>
    NoFollow = 2
}

/// <summary>Provides a path and immutable metadata from one filesystem revision.</summary>
public sealed record FileSystemEntrySnapshot
{
    /// <summary>Initializes an immutable entry snapshot.</summary>
    /// <param name="path">Canonical path observed by the operation.</param>
    /// <param name="metadata">Immutable entry metadata.</param>
    public FileSystemEntrySnapshot(VirtualPath path, FileSystemEntryMetadata metadata)
    {
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        ArgumentNullException.ThrowIfNull(metadata);

        Path = path;
        Metadata = metadata;
    }

    /// <summary>Gets the canonical path observed by the operation.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the immutable entry metadata.</summary>
    public FileSystemEntryMetadata Metadata { get; }
}

/// <summary>Provides one immediate child from a directory enumeration.</summary>
public sealed record FileSystemDirectoryItem
{
    /// <summary>Initializes an immutable directory item.</summary>
    /// <param name="name">Normalized child name.</param>
    /// <param name="metadata">Immutable child metadata.</param>
    public FileSystemDirectoryItem(FileSystemEntryName name, FileSystemEntryMetadata metadata)
    {
        if (string.IsNullOrEmpty(name.Value))
        {
            throw new ArgumentException("Directory items require a valid entry name.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(metadata);
        Name = name;
        Metadata = metadata;
    }

    /// <summary>Gets the normalized child name.</summary>
    public FileSystemEntryName Name { get; }

    /// <summary>Gets the immutable child metadata.</summary>
    public FileSystemEntryMetadata Metadata { get; }
}

/// <summary>Provides one deterministic immediate-directory enumeration.</summary>
public sealed record FileSystemDirectorySnapshot
{
    /// <summary>Initializes an immutable directory snapshot.</summary>
    /// <param name="path">Enumerated canonical directory path.</param>
    /// <param name="revision">Directory revision observed by the enumeration.</param>
    /// <param name="entries">Immediate children in ordinal name order.</param>
    public FileSystemDirectorySnapshot(
        VirtualPath path,
        long revision,
        IEnumerable<FileSystemDirectoryItem> entries)
    {
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        FileSystemContractGuard.ValidateRevision(revision, nameof(revision));
        ArgumentNullException.ThrowIfNull(entries);

        FileSystemDirectoryItem[] copiedEntries = entries.ToArray();
        if (copiedEntries.Any(static entry => entry is null))
        {
            throw new ArgumentException("Directory snapshots cannot contain null entries.", nameof(entries));
        }

        for (int index = 1; index < copiedEntries.Length; index++)
        {
            if (string.Compare(
                    copiedEntries[index - 1].Name.Value,
                    copiedEntries[index].Name.Value,
                    StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException(
                    "Directory snapshot entries must be unique and sorted by ordinal name.",
                    nameof(entries));
            }
        }

        Path = path;
        Revision = revision;
        Entries = Array.AsReadOnly(copiedEntries);
    }

    /// <summary>Gets the enumerated canonical directory path.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the directory revision observed by the enumeration.</summary>
    public long Revision { get; }

    /// <summary>Gets immediate children in unique ordinal name order.</summary>
    public IReadOnlyList<FileSystemDirectoryItem> Entries { get; }
}

/// <summary>Requests regular-file content access.</summary>
public sealed record FileSystemReadRequest
{
    /// <summary>Initializes a file-content read request.</summary>
    /// <param name="path">Canonical file path.</param>
    /// <param name="linkBehavior">Final symbolic-link behavior.</param>
    public FileSystemReadRequest(
        VirtualPath path,
        FileSystemLinkBehavior linkBehavior = FileSystemLinkBehavior.Follow)
    {
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        FileSystemContractGuard.ValidateLinkBehavior(linkBehavior, nameof(linkBehavior));
        Path = path;
        LinkBehavior = linkBehavior;
    }

    /// <summary>Gets the canonical file path.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the final symbolic-link behavior.</summary>
    public FileSystemLinkBehavior LinkBehavior { get; }
}

/// <summary>Requests deterministic immediate-directory enumeration.</summary>
public sealed record FileSystemEnumerateRequest
{
    /// <summary>Initializes a directory-enumeration request.</summary>
    /// <param name="path">Canonical directory path.</param>
    /// <param name="linkBehavior">Final symbolic-link behavior.</param>
    public FileSystemEnumerateRequest(
        VirtualPath path,
        FileSystemLinkBehavior linkBehavior = FileSystemLinkBehavior.Follow)
    {
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        FileSystemContractGuard.ValidateLinkBehavior(linkBehavior, nameof(linkBehavior));
        Path = path;
        LinkBehavior = linkBehavior;
    }

    /// <summary>Gets the canonical directory path.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the final symbolic-link behavior.</summary>
    public FileSystemLinkBehavior LinkBehavior { get; }
}

/// <summary>Requests creation of an empty file, directory, or symbolic link.</summary>
public sealed record FileSystemCreateRequest
{
    /// <summary>Initializes a validated create request.</summary>
    /// <param name="path">Canonical destination path.</param>
    /// <param name="kind">Entry kind to create.</param>
    /// <param name="permissions">Initial mode-based permissions.</param>
    /// <param name="expectedParentRevision">
    /// Required destination-parent revision, or <see langword="null"/> to create
    /// unconditionally regardless of the parent's current revision. Pass a revision
    /// only when the caller genuinely needs to detect a concurrent structural change
    /// (e.g. a sibling was added or removed since it last listed the directory).
    /// </param>
    /// <param name="symbolicLinkTarget">Required target only for a symbolic link.</param>
    public FileSystemCreateRequest(
        VirtualPath path,
        FileSystemEntryKind kind,
        FileSystemPermissions permissions,
        long? expectedParentRevision = null,
        string? symbolicLinkTarget = null)
    {
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        FileSystemContractGuard.ValidateRevision(expectedParentRevision, nameof(expectedParentRevision));

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown filesystem entry kind.");
        }

        if (kind == FileSystemEntryKind.SymbolicLink && string.IsNullOrEmpty(symbolicLinkTarget))
        {
            throw new ArgumentException("Symbolic-link creation requires a target.", nameof(symbolicLinkTarget));
        }

        if (kind != FileSystemEntryKind.SymbolicLink && symbolicLinkTarget is not null)
        {
            throw new ArgumentException("Only symbolic-link creation accepts a target.", nameof(symbolicLinkTarget));
        }

        Path = path;
        Kind = kind;
        Permissions = permissions;
        ExpectedParentRevision = expectedParentRevision;
        SymbolicLinkTarget = symbolicLinkTarget;
    }

    /// <summary>Gets the canonical destination path.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the entry kind to create.</summary>
    public FileSystemEntryKind Kind { get; }

    /// <summary>Gets the initial mode-based permissions.</summary>
    public FileSystemPermissions Permissions { get; }

    /// <summary>
    /// Gets the required destination-parent revision, or <see langword="null"/> when
    /// the create is unconditional.
    /// </summary>
    public long? ExpectedParentRevision { get; }

    /// <summary>Gets the symbolic-link target, when creating a link.</summary>
    public string? SymbolicLinkTarget { get; }
}

/// <summary>Requests atomic replacement of regular-file content.</summary>
public sealed record FileSystemWriteRequest
{
    /// <summary>Initializes a validated file-content write request.</summary>
    /// <param name="path">Canonical file path.</param>
    /// <param name="expectedRevision">
    /// Required current file revision, or <see langword="null"/> to overwrite
    /// unconditionally regardless of the file's current revision. Pass a revision only
    /// when the caller genuinely needs to detect a concurrent edit (e.g. an editor that
    /// must warn the user before clobbering a change made elsewhere since it loaded the
    /// file).
    /// </param>
    public FileSystemWriteRequest(VirtualPath path, long? expectedRevision = null)
    {
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        FileSystemContractGuard.ValidateRevision(expectedRevision, nameof(expectedRevision));
        Path = path;
        ExpectedRevision = expectedRevision;
    }

    /// <summary>Gets the canonical file path.</summary>
    public VirtualPath Path { get; }

    /// <summary>
    /// Gets the required current file revision, or <see langword="null"/> when the
    /// write is unconditional.
    /// </summary>
    public long? ExpectedRevision { get; }
}

/// <summary>Requests an atomic move or rename within one provider.</summary>
public sealed record FileSystemMoveRequest
{
    /// <summary>Initializes a move request with all required revision preconditions.</summary>
    /// <param name="sourcePath">Canonical source path.</param>
    /// <param name="destinationPath">Canonical destination path.</param>
    /// <param name="expectedEntryRevision">Required source-entry revision.</param>
    /// <param name="expectedSourceParentRevision">Required source-parent revision.</param>
    /// <param name="expectedDestinationParentRevision">Required destination-parent revision.</param>
    public FileSystemMoveRequest(
        VirtualPath sourcePath,
        VirtualPath destinationPath,
        long expectedEntryRevision,
        long expectedSourceParentRevision,
        long expectedDestinationParentRevision)
    {
        FileSystemContractGuard.ValidateTransfer(
            sourcePath,
            destinationPath,
            expectedEntryRevision,
            expectedSourceParentRevision,
            expectedDestinationParentRevision);

        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        ExpectedEntryRevision = expectedEntryRevision;
        ExpectedSourceParentRevision = expectedSourceParentRevision;
        ExpectedDestinationParentRevision = expectedDestinationParentRevision;
    }

    /// <summary>Gets the canonical source path.</summary>
    public VirtualPath SourcePath { get; }

    /// <summary>Gets the canonical destination path.</summary>
    public VirtualPath DestinationPath { get; }

    /// <summary>Gets the required source-entry revision.</summary>
    public long ExpectedEntryRevision { get; }

    /// <summary>Gets the required source-parent revision.</summary>
    public long ExpectedSourceParentRevision { get; }

    /// <summary>Gets the required destination-parent revision.</summary>
    public long ExpectedDestinationParentRevision { get; }
}

/// <summary>Requests an atomic subtree copy into one destination provider.</summary>
public sealed record FileSystemCopyRequest
{
    /// <summary>Initializes a copy request with source and destination preconditions.</summary>
    /// <param name="sourcePath">Canonical source path.</param>
    /// <param name="destinationPath">Canonical destination path.</param>
    /// <param name="expectedEntryRevision">Required source-entry revision.</param>
    /// <param name="expectedDestinationParentRevision">Required destination-parent revision.</param>
    public FileSystemCopyRequest(
        VirtualPath sourcePath,
        VirtualPath destinationPath,
        long expectedEntryRevision,
        long expectedDestinationParentRevision)
    {
        FileSystemContractGuard.ValidatePath(sourcePath, nameof(sourcePath));
        FileSystemContractGuard.ValidatePath(destinationPath, nameof(destinationPath));
        FileSystemContractGuard.ValidateDistinctPaths(sourcePath, destinationPath);
        FileSystemContractGuard.ValidateRevision(expectedEntryRevision, nameof(expectedEntryRevision));
        FileSystemContractGuard.ValidateRevision(
            expectedDestinationParentRevision,
            nameof(expectedDestinationParentRevision));

        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        ExpectedEntryRevision = expectedEntryRevision;
        ExpectedDestinationParentRevision = expectedDestinationParentRevision;
    }

    /// <summary>Gets the canonical source path.</summary>
    public VirtualPath SourcePath { get; }

    /// <summary>Gets the canonical destination path.</summary>
    public VirtualPath DestinationPath { get; }

    /// <summary>Gets the required source-entry revision.</summary>
    public long ExpectedEntryRevision { get; }

    /// <summary>Gets the required destination-parent revision.</summary>
    public long ExpectedDestinationParentRevision { get; }
}

/// <summary>Requests deletion of one entry or one complete subtree.</summary>
public sealed record FileSystemDeleteRequest
{
    /// <summary>Initializes a delete request with entry and parent preconditions.</summary>
    /// <param name="path">Canonical entry path.</param>
    /// <param name="expectedEntryRevision">Required entry revision.</param>
    /// <param name="expectedParentRevision">Required parent revision.</param>
    /// <param name="recursive">Whether a complete directory subtree may be deleted.</param>
    public FileSystemDeleteRequest(
        VirtualPath path,
        long expectedEntryRevision,
        long expectedParentRevision,
        bool recursive = false)
    {
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        FileSystemContractGuard.ValidateRevision(expectedEntryRevision, nameof(expectedEntryRevision));
        FileSystemContractGuard.ValidateRevision(expectedParentRevision, nameof(expectedParentRevision));

        Path = path;
        ExpectedEntryRevision = expectedEntryRevision;
        ExpectedParentRevision = expectedParentRevision;
        Recursive = recursive;
    }

    /// <summary>Gets the canonical entry path.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the required entry revision.</summary>
    public long ExpectedEntryRevision { get; }

    /// <summary>Gets the required parent revision.</summary>
    public long ExpectedParentRevision { get; }

    /// <summary>Gets whether a complete directory subtree may be deleted.</summary>
    public bool Recursive { get; }
}

/// <summary>Requests immutable metadata for one entry.</summary>
public sealed record FileSystemStatRequest
{
    /// <summary>Initializes a metadata request.</summary>
    /// <param name="path">Canonical entry path.</param>
    /// <param name="linkBehavior">Final symbolic-link behavior.</param>
    public FileSystemStatRequest(
        VirtualPath path,
        FileSystemLinkBehavior linkBehavior = FileSystemLinkBehavior.Follow)
    {
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        FileSystemContractGuard.ValidateLinkBehavior(linkBehavior, nameof(linkBehavior));
        Path = path;
        LinkBehavior = linkBehavior;
    }

    /// <summary>Gets the canonical entry path.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the final symbolic-link behavior.</summary>
    public FileSystemLinkBehavior LinkBehavior { get; }
}

/// <summary>Requests atomic replacement of owner/group/other permission bits.</summary>
public sealed record FileSystemSetPermissionsRequest
{
    /// <summary>Initializes a permission update request.</summary>
    /// <param name="path">Canonical entry path.</param>
    /// <param name="permissions">Replacement owner/group/other mode.</param>
    /// <param name="expectedRevision">Required current entry revision.</param>
    /// <param name="linkBehavior">Final symbolic-link behavior.</param>
    public FileSystemSetPermissionsRequest(
        VirtualPath path,
        FileSystemPermissions permissions,
        long expectedRevision,
        FileSystemLinkBehavior linkBehavior = FileSystemLinkBehavior.NoFollow)
    {
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        FileSystemContractGuard.ValidateRevision(expectedRevision, nameof(expectedRevision));
        FileSystemContractGuard.ValidateLinkBehavior(linkBehavior, nameof(linkBehavior));

        Path = path;
        Permissions = permissions;
        ExpectedRevision = expectedRevision;
        LinkBehavior = linkBehavior;
    }

    /// <summary>Gets the canonical entry path.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the replacement owner/group/other mode.</summary>
    public FileSystemPermissions Permissions { get; }

    /// <summary>Gets the required current entry revision.</summary>
    public long ExpectedRevision { get; }

    /// <summary>Gets the final symbolic-link behavior.</summary>
    public FileSystemLinkBehavior LinkBehavior { get; }
}

internal static class FileSystemContractGuard
{
    internal static void ValidatePath(VirtualPath path, string parameterName)
    {
        if (string.IsNullOrEmpty(path.Value))
        {
            throw new ArgumentException("Filesystem requests require a canonical path.", parameterName);
        }
    }

    internal static void ValidateRevision(long revision, string parameterName) =>
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1, parameterName);

    /// <summary>Validates an optional revision precondition; <see langword="null"/> (unconditional) is always valid.</summary>
    internal static void ValidateRevision(long? revision, string parameterName)
    {
        if (revision is { } value)
        {
            ValidateRevision(value, parameterName);
        }
    }

    internal static void ValidateLinkBehavior(FileSystemLinkBehavior behavior, string parameterName)
    {
        if (!Enum.IsDefined(behavior))
        {
            throw new ArgumentOutOfRangeException(parameterName, behavior, "Unknown symbolic-link behavior.");
        }
    }

    internal static void ValidateDistinctPaths(VirtualPath sourcePath, VirtualPath destinationPath)
    {
        if (sourcePath == destinationPath)
        {
            throw new ArgumentException("Source and destination paths must differ.", nameof(destinationPath));
        }
    }

    internal static void ValidateTransfer(
        VirtualPath sourcePath,
        VirtualPath destinationPath,
        long expectedEntryRevision,
        long expectedSourceParentRevision,
        long expectedDestinationParentRevision)
    {
        ValidatePath(sourcePath, nameof(sourcePath));
        ValidatePath(destinationPath, nameof(destinationPath));
        ValidateDistinctPaths(sourcePath, destinationPath);
        ValidateRevision(expectedEntryRevision, nameof(expectedEntryRevision));
        ValidateRevision(expectedSourceParentRevision, nameof(expectedSourceParentRevision));
        ValidateRevision(expectedDestinationParentRevision, nameof(expectedDestinationParentRevision));
    }
}