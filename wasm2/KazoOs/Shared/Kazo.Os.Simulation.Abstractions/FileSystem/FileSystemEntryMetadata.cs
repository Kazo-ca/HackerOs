using System.Text;

namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>Identifies the persisted kind of a filesystem entry.</summary>
public enum FileSystemEntryKind
{
    /// <summary>A regular file with separately stored content.</summary>
    File,

    /// <summary>A directory whose children are represented by directory links.</summary>
    Directory,

    /// <summary>A symbolic link containing an absolute or relative virtual target.</summary>
    SymbolicLink,

    /// <summary>
    /// A character device such as <c>/dev/null</c> (ADR 0046). Only virtual providers expose this
    /// kind; persistent providers refuse to create it with
    /// <see cref="FileSystemErrorCode.UnsupportedOperation"/>.
    /// </summary>
    CharacterDevice
}

/// <summary>Groups the UTC timestamps persisted for an entry.</summary>
public sealed record FileSystemTimestamps
{
    /// <summary>Initializes validated entry timestamps.</summary>
    /// <param name="createdAtUtc">Entry creation time.</param>
    /// <param name="contentModifiedAtUtc">Last content modification time.</param>
    /// <param name="metadataChangedAtUtc">Last metadata change time.</param>
    /// <exception cref="ArgumentException">A value is not UTC or predates creation.</exception>
    public FileSystemTimestamps(
        DateTimeOffset createdAtUtc,
        DateTimeOffset contentModifiedAtUtc,
        DateTimeOffset metadataChangedAtUtc)
    {
        EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        EnsureUtc(contentModifiedAtUtc, nameof(contentModifiedAtUtc));
        EnsureUtc(metadataChangedAtUtc, nameof(metadataChangedAtUtc));

        if (contentModifiedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("Content modification time cannot predate creation.", nameof(contentModifiedAtUtc));
        }

        if (metadataChangedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("Metadata change time cannot predate creation.", nameof(metadataChangedAtUtc));
        }

        CreatedAtUtc = createdAtUtc;
        ContentModifiedAtUtc = contentModifiedAtUtc;
        MetadataChangedAtUtc = metadataChangedAtUtc;
    }

    /// <summary>Gets the entry creation time in UTC.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets the last content modification time in UTC.</summary>
    public DateTimeOffset ContentModifiedAtUtc { get; }

    /// <summary>Gets the last metadata change time in UTC.</summary>
    public DateTimeOffset MetadataChangedAtUtc { get; }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Filesystem timestamps must use UTC.", parameterName);
        }
    }
}

/// <summary>Contains metadata shared by every filesystem entry kind.</summary>
public abstract record FileSystemEntryMetadata
{
    /// <summary>Initializes validated common metadata.</summary>
    /// <param name="id">Stable entry identity.</param>
    /// <param name="ownerId">Owning user identity.</param>
    /// <param name="groupId">Owning group identity.</param>
    /// <param name="permissions">Owner/group/other permission mode.</param>
    /// <param name="timestamps">Entry timestamps.</param>
    /// <param name="revision">Positive optimistic concurrency revision.</param>
    protected FileSystemEntryMetadata(
        FileSystemEntryId id,
        string ownerId,
        string groupId,
        FileSystemPermissions permissions,
        FileSystemTimestamps timestamps,
        long revision)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("Filesystem metadata requires a non-empty entry ID.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(timestamps);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);

        Id = id;
        OwnerId = ownerId;
        GroupId = groupId;
        Permissions = permissions;
        Timestamps = timestamps;
        Revision = revision;
    }

    /// <summary>Gets the stable entry identity.</summary>
    public FileSystemEntryId Id { get; }

    /// <summary>Gets the persisted entry kind.</summary>
    public abstract FileSystemEntryKind Kind { get; }

    /// <summary>Gets the owning user identity.</summary>
    public string OwnerId { get; }

    /// <summary>Gets the owning group identity.</summary>
    public string GroupId { get; }

    /// <summary>Gets the mode-based permissions.</summary>
    public FileSystemPermissions Permissions { get; }

    /// <summary>Gets the persisted UTC timestamps.</summary>
    public FileSystemTimestamps Timestamps { get; }

    /// <summary>Gets the positive optimistic concurrency revision.</summary>
    public long Revision { get; }
}

/// <summary>Contains metadata for a regular file.</summary>
public sealed record FileMetadata : FileSystemEntryMetadata
{
    /// <summary>Initializes regular-file metadata.</summary>
    /// <param name="id">Stable entry identity.</param>
    /// <param name="ownerId">Owning user identity.</param>
    /// <param name="groupId">Owning group identity.</param>
    /// <param name="permissions">Owner/group/other permission mode.</param>
    /// <param name="timestamps">Entry timestamps.</param>
    /// <param name="revision">Positive optimistic concurrency revision.</param>
    /// <param name="length">Logical content length in bytes.</param>
    /// <param name="mediaType">Normalized content media type without parameters.</param>
    public FileMetadata(
        FileSystemEntryId id,
        string ownerId,
        string groupId,
        FileSystemPermissions permissions,
        FileSystemTimestamps timestamps,
        long revision,
        long length,
        string mediaType = "application/octet-stream")
        : base(id, ownerId, groupId, permissions, timestamps, revision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        MediaType = FileSystemContentDescriptor.Binary(mediaType).MediaType;
    }

    /// <inheritdoc />
    public override FileSystemEntryKind Kind => FileSystemEntryKind.File;

    /// <summary>Gets the logical content length in bytes.</summary>
    public long Length { get; }

    /// <summary>Gets the normalized content media type without parameters.</summary>
    public string MediaType { get; }
}

/// <summary>Contains metadata for a directory.</summary>
public sealed record DirectoryMetadata : FileSystemEntryMetadata
{
    /// <summary>Initializes directory metadata.</summary>
    /// <param name="id">Stable entry identity.</param>
    /// <param name="ownerId">Owning user identity.</param>
    /// <param name="groupId">Owning group identity.</param>
    /// <param name="permissions">Owner/group/other permission mode.</param>
    /// <param name="timestamps">Entry timestamps.</param>
    /// <param name="revision">Positive optimistic concurrency revision.</param>
    public DirectoryMetadata(
        FileSystemEntryId id,
        string ownerId,
        string groupId,
        FileSystemPermissions permissions,
        FileSystemTimestamps timestamps,
        long revision)
        : base(id, ownerId, groupId, permissions, timestamps, revision)
    {
    }

    /// <inheritdoc />
    public override FileSystemEntryKind Kind => FileSystemEntryKind.Directory;
}

/// <summary>Contains metadata and target text for a symbolic link.</summary>
public sealed record SymbolicLinkMetadata : FileSystemEntryMetadata
{
    /// <summary>Maximum encoded target length retained by the metadata contract.</summary>
    public const int MaximumTargetUtf8ByteLength = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Initializes symbolic-link metadata.</summary>
    /// <param name="id">Stable entry identity.</param>
    /// <param name="ownerId">Owning user identity.</param>
    /// <param name="groupId">Owning group identity.</param>
    /// <param name="permissions">Owner/group/other permission mode.</param>
    /// <param name="timestamps">Entry timestamps.</param>
    /// <param name="revision">Positive optimistic concurrency revision.</param>
    /// <param name="target">Absolute or relative virtual target text.</param>
    public SymbolicLinkMetadata(
        FileSystemEntryId id,
        string ownerId,
        string groupId,
        FileSystemPermissions permissions,
        FileSystemTimestamps timestamps,
        long revision,
        string target)
        : base(id, ownerId, groupId, permissions, timestamps, revision)
    {
        Target = ValidateTarget(target);
    }

    /// <inheritdoc />
    public override FileSystemEntryKind Kind => FileSystemEntryKind.SymbolicLink;

    /// <summary>Gets the normalized absolute or relative virtual target.</summary>
    public string Target { get; }

    /// <summary>Gets the target's logical UTF-8 byte length.</summary>
    public int Length => StrictUtf8.GetByteCount(Target);

    private static string ValidateTarget(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            throw new ArgumentException("Symbolic-link targets cannot be empty.", nameof(target));
        }

        string normalized;
        int byteLength;
        try
        {
            normalized = target.Normalize(NormalizationForm.FormC);
            byteLength = StrictUtf8.GetByteCount(normalized);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Symbolic-link targets must contain valid Unicode.", nameof(target), exception);
        }

        if (normalized.Contains('\\') || normalized.Contains('\0') || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Symbolic-link targets cannot contain backslashes, nulls, or control characters.",
                nameof(target));
        }

        if (byteLength > MaximumTargetUtf8ByteLength)
        {
            throw new ArgumentException(
                $"Symbolic-link targets cannot exceed {MaximumTargetUtf8ByteLength} UTF-8 bytes.",
                nameof(target));
        }

        return normalized;
    }
}

/// <summary>
/// Contains metadata for a character device exposed by a virtual provider (ADR 0046).
/// </summary>
/// <remarks>
/// A device has no stored content and no meaningful length: reads are streamed by the provider
/// until end of stream (or forever for <c>/dev/zero</c>), exactly like a Linux character device.
/// </remarks>
public sealed record CharacterDeviceMetadata : FileSystemEntryMetadata
{
    /// <summary>Initializes character-device metadata.</summary>
    /// <param name="id">Stable entry identity.</param>
    /// <param name="ownerId">Owning user identity.</param>
    /// <param name="groupId">Owning group identity.</param>
    /// <param name="permissions">Owner/group/other permission mode.</param>
    /// <param name="timestamps">Entry timestamps.</param>
    /// <param name="revision">Positive optimistic concurrency revision.</param>
    public CharacterDeviceMetadata(
        FileSystemEntryId id,
        string ownerId,
        string groupId,
        FileSystemPermissions permissions,
        FileSystemTimestamps timestamps,
        long revision)
        : base(id, ownerId, groupId, permissions, timestamps, revision)
    {
    }

    /// <inheritdoc />
    public override FileSystemEntryKind Kind => FileSystemEntryKind.CharacterDevice;
}

/// <summary>
/// Relates one normalized name in a directory to a stable child entry identity.
/// </summary>
public sealed record FileSystemDirectoryEntry
{
    /// <summary>Initializes an immutable directory link.</summary>
    /// <param name="parentId">Containing directory identity.</param>
    /// <param name="name">Normalized name within the directory.</param>
    /// <param name="entryId">Child entry identity.</param>
    public FileSystemDirectoryEntry(
        FileSystemEntryId parentId,
        FileSystemEntryName name,
        FileSystemEntryId entryId)
    {
        if (parentId.IsEmpty)
        {
            throw new ArgumentException("Directory links require a non-empty parent ID.", nameof(parentId));
        }

        if (string.IsNullOrEmpty(name.Value))
        {
            throw new ArgumentException("Directory links require a valid entry name.", nameof(name));
        }

        if (entryId.IsEmpty)
        {
            throw new ArgumentException("Directory links require a non-empty child ID.", nameof(entryId));
        }

        ParentId = parentId;
        Name = name;
        EntryId = entryId;
    }

    /// <summary>Gets the containing directory identity.</summary>
    public FileSystemEntryId ParentId { get; }

    /// <summary>Gets the normalized name within the directory.</summary>
    public FileSystemEntryName Name { get; }

    /// <summary>Gets the child entry identity.</summary>
    public FileSystemEntryId EntryId { get; }
}