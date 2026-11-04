using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using System.Text.Json;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>Builds ordered IndexedDB operation batches for atomic filesystem metadata mutations.</summary>
internal static class IndexedDbFileSystemMutationPlanner
{
    /// <summary>Builds an all-or-nothing parent, entry, and directory-link creation batch.</summary>
    internal static IReadOnlyList<IndexedDbOperation> PlanCreate(
        IndexedDbFileSystemEntryRecord parent,
        FileSystemEntryMetadata created,
        FileSystemEntryName name,
        DateTimeOffset committedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(created);
        if (parent.Kind != (int)FileSystemEntryKind.Directory)
        {
            throw new ArgumentException("The creation parent must be a directory.", nameof(parent));
        }

        if (string.IsNullOrEmpty(name.Value))
        {
            throw new ArgumentException("A valid directory entry name is required.", nameof(name));
        }

        if (committedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Filesystem commit timestamps must use UTC.", nameof(committedAtUtc));
        }

        IndexedDbFileSystemEntryRecord updatedParent = parent with
        {
            ContentModifiedUtcMs = committedAtUtc.ToUnixTimeMilliseconds(),
            MetadataChangedUtcMs = committedAtUtc.ToUnixTimeMilliseconds(),
            Revision = checked(parent.Revision + 1)
        };
        IndexedDbFileSystemEntryRecord child = IndexedDbFileSystemEntryRecord.FromMetadata(created);
        IndexedDbFileSystemLinkRecord link = new(parent.Id, name.Value, child.Id);

        return
        [
            new IndexedDbOperation(
                "assertPropertyEquals",
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                Key: parent.Id,
                CompareProperty: "revision",
                ExpectedValue: parent.Revision,
                FailureCode: "filesystem.revision-conflict"),
            new IndexedDbOperation(
                "put",
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                Value: updatedParent),
            new IndexedDbOperation(
                "add",
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                Value: child),
            new IndexedDbOperation(
                "add",
                HackerOsIndexedDbSchema.FileSystemLinkStoreName,
                Value: link)
        ];
    }

    /// <summary>Builds an all-or-nothing permission and metadata-revision replacement batch.</summary>
    internal static IReadOnlyList<IndexedDbOperation> PlanSetPermissions(
        IndexedDbFileSystemEntryRecord entry,
        FileSystemPermissions permissions,
        DateTimeOffset committedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (committedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Filesystem commit timestamps must use UTC.", nameof(committedAtUtc));
        }

        IndexedDbFileSystemEntryRecord updated = entry with
        {
            PermissionsMode = permissions.Mode,
            MetadataChangedUtcMs = committedAtUtc.ToUnixTimeMilliseconds(),
            Revision = checked(entry.Revision + 1)
        };
        return
        [
            new IndexedDbOperation(
                "assertPropertyEquals",
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                Key: entry.Id,
                CompareProperty: "revision",
                ExpectedValue: entry.Revision,
                FailureCode: "filesystem.revision-conflict"),
            new IndexedDbOperation(
                "put",
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                Value: updated)
        ];
    }

    /// <summary>Publishes one immutable content hash under an optimistic entry revision.</summary>
    internal static IReadOnlyList<IndexedDbOperation> PlanWrite(
        IndexedDbFileSystemEntryRecord entry,
        string contentHash,
        long length,
        FileSystemContentDescriptor descriptor,
        DateTimeOffset committedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (committedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Filesystem commit timestamps must use UTC.", nameof(committedAtUtc));
        }

        long committedUtcMs = committedAtUtc.ToUnixTimeMilliseconds();
        IndexedDbFileSystemEntryRecord updated = entry with
        {
            ContentModifiedUtcMs = committedUtcMs,
            MetadataChangedUtcMs = committedUtcMs,
            Revision = checked(entry.Revision + 1),
            Length = length,
            ContentHash = contentHash,
            ContentKind = (int)descriptor.Kind,
            MediaType = descriptor.MediaType,
            EncodingName = descriptor.EncodingName
        };
        return
        [
            RevisionAssertion(entry),
            new IndexedDbOperation("put", HackerOsIndexedDbSchema.FileSystemEntryStoreName, Value: updated)
        ];
    }

    /// <summary>Builds an all-or-nothing entry-link move with entry and parent revision updates.</summary>
    internal static IReadOnlyList<IndexedDbOperation> PlanMove(
        IndexedDbFileSystemEntryRecord entry,
        IndexedDbFileSystemEntryRecord sourceParent,
        FileSystemEntryName sourceName,
        IndexedDbFileSystemEntryRecord destinationParent,
        FileSystemEntryName destinationName,
        DateTimeOffset committedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(sourceParent);
        ArgumentNullException.ThrowIfNull(destinationParent);
        if (committedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Filesystem commit timestamps must use UTC.", nameof(committedAtUtc));
        }

        long committedUtcMs = committedAtUtc.ToUnixTimeMilliseconds();
        IndexedDbFileSystemEntryRecord updatedEntry = entry with
        {
            MetadataChangedUtcMs = committedUtcMs,
            Revision = checked(entry.Revision + 1)
        };
        bool sameParent = StringComparer.Ordinal.Equals(sourceParent.Id, destinationParent.Id);
        IndexedDbFileSystemEntryRecord updatedSourceParent = sourceParent with
        {
            ContentModifiedUtcMs = committedUtcMs,
            MetadataChangedUtcMs = committedUtcMs,
            Revision = checked(sourceParent.Revision + 1)
        };
        IndexedDbFileSystemEntryRecord updatedDestinationParent = destinationParent with
        {
            ContentModifiedUtcMs = committedUtcMs,
            MetadataChangedUtcMs = committedUtcMs,
            Revision = checked(destinationParent.Revision + 1)
        };

        List<IndexedDbOperation> operations =
        [
            RevisionAssertion(entry),
            RevisionAssertion(sourceParent)
        ];
        if (!sameParent)
        {
            operations.Add(RevisionAssertion(destinationParent));
        }

        operations.Add(new IndexedDbOperation(
            "put", HackerOsIndexedDbSchema.FileSystemEntryStoreName, Value: updatedEntry));
        operations.Add(new IndexedDbOperation(
            "put", HackerOsIndexedDbSchema.FileSystemEntryStoreName, Value: updatedSourceParent));
        if (!sameParent)
        {
            operations.Add(new IndexedDbOperation(
                "put", HackerOsIndexedDbSchema.FileSystemEntryStoreName, Value: updatedDestinationParent));
        }

        operations.Add(new IndexedDbOperation(
            "delete",
            HackerOsIndexedDbSchema.FileSystemLinkStoreName,
            Key: new object[] { sourceParent.Id, sourceName.Value }));
        operations.Add(new IndexedDbOperation(
            "add",
            HackerOsIndexedDbSchema.FileSystemLinkStoreName,
            Value: new IndexedDbFileSystemLinkRecord(destinationParent.Id, destinationName.Value, entry.Id)));
        return operations;
    }

    /// <summary>Builds an all-or-nothing subtree deletion with parent revision update.</summary>
    internal static IReadOnlyList<IndexedDbOperation> PlanDelete(
        IndexedDbFileSystemEntryRecord parent,
        IReadOnlyList<IndexedDbFileSystemDeletionEntry> removals,
        DateTimeOffset committedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(removals);
        if (removals.Count == 0)
        {
            throw new ArgumentException("Filesystem deletion requires at least one entry.", nameof(removals));
        }

        if (committedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Filesystem commit timestamps must use UTC.", nameof(committedAtUtc));
        }

        long committedUtcMs = committedAtUtc.ToUnixTimeMilliseconds();
        IndexedDbFileSystemEntryRecord updatedParent = parent with
        {
            ContentModifiedUtcMs = committedUtcMs,
            MetadataChangedUtcMs = committedUtcMs,
            Revision = checked(parent.Revision + 1)
        };
        List<IndexedDbOperation> operations = [RevisionAssertion(parent)];
        operations.AddRange(removals.Select(removal => RevisionAssertion(removal.Entry)));
        operations.Add(new IndexedDbOperation(
            "put", HackerOsIndexedDbSchema.FileSystemEntryStoreName, Value: updatedParent));
        foreach (IndexedDbFileSystemDeletionEntry removal in removals.Reverse())
        {
            operations.Add(new IndexedDbOperation(
                "delete",
                HackerOsIndexedDbSchema.FileSystemLinkStoreName,
                Key: new object[] { removal.ParentId, removal.Name.Value }));
            operations.Add(new IndexedDbOperation(
                "delete",
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                Key: removal.Entry.Id));
        }

        return operations;
    }

    /// <summary>Builds an all-or-nothing subtree copy with immutable content references.</summary>
    internal static IReadOnlyList<IndexedDbOperation> PlanCopy(
        IndexedDbFileSystemEntryRecord destinationParent,
        IReadOnlyList<IndexedDbFileSystemCopyEntry> copies,
        DateTimeOffset committedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(destinationParent);
        ArgumentNullException.ThrowIfNull(copies);
        if (copies.Count == 0)
        {
            throw new ArgumentException("Filesystem copy requires at least one entry.", nameof(copies));
        }

        if (committedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Filesystem commit timestamps must use UTC.", nameof(committedAtUtc));
        }

        long committedUtcMs = committedAtUtc.ToUnixTimeMilliseconds();
        IndexedDbFileSystemEntryRecord updatedParent = destinationParent with
        {
            ContentModifiedUtcMs = committedUtcMs,
            MetadataChangedUtcMs = committedUtcMs,
            Revision = checked(destinationParent.Revision + 1)
        };
        List<IndexedDbOperation> operations = [RevisionAssertion(destinationParent)];
        operations.AddRange(copies.Select(copy => RevisionAssertion(copy.Source)));
        operations.Add(new IndexedDbOperation(
            "put", HackerOsIndexedDbSchema.FileSystemEntryStoreName, Value: updatedParent));
        foreach (IndexedDbFileSystemCopyEntry copy in copies)
        {
            operations.Add(new IndexedDbOperation(
                "add", HackerOsIndexedDbSchema.FileSystemEntryStoreName, Value: copy.Copy));
            operations.Add(new IndexedDbOperation(
                "add",
                HackerOsIndexedDbSchema.FileSystemLinkStoreName,
                Value: new IndexedDbFileSystemLinkRecord(copy.DestinationParentId, copy.Name.Value, copy.Copy.Id)));
        }

        return operations;
    }

    private static IndexedDbOperation RevisionAssertion(IndexedDbFileSystemEntryRecord entry) => new(
        "assertPropertyEquals",
        HackerOsIndexedDbSchema.FileSystemEntryStoreName,
        Key: entry.Id,
        CompareProperty: "revision",
        ExpectedValue: entry.Revision,
        FailureCode: "filesystem.revision-conflict");
}

/// <summary>Identifies one entry and its owning directory link for atomic deletion.</summary>
internal sealed record IndexedDbFileSystemDeletionEntry(
    string ParentId,
    FileSystemEntryName Name,
    IndexedDbFileSystemEntryRecord Entry);

/// <summary>Maps one observed source entry to its newly identified copied record and parent link.</summary>
internal sealed record IndexedDbFileSystemCopyEntry(
    IndexedDbFileSystemEntryRecord Source,
    IndexedDbFileSystemEntryRecord Copy,
    string DestinationParentId,
    FileSystemEntryName Name);

/// <summary>Stable browser-storage representation shared by all filesystem entry kinds.</summary>
internal sealed record IndexedDbFileSystemEntryRecord(
    string Id,
    int Kind,
    string OwnerId,
    string GroupId,
    ushort PermissionsMode,
    long CreatedUtcMs,
    long ContentModifiedUtcMs,
    long MetadataChangedUtcMs,
    long Revision,
    long Length,
    string? SymbolicLinkTarget,
    string? ContentHash,
    int ContentKind,
    string MediaType,
    string? EncodingName)
{
    /// <summary>Reads and validates one persisted entry record without reflection serialization.</summary>
    internal static IndexedDbFileSystemEntryRecord FromJson(JsonElement element) => new(
        RequiredString(element, "id"),
        element.GetProperty("kind").GetInt32(),
        RequiredString(element, "ownerId"),
        RequiredString(element, "groupId"),
        element.GetProperty("permissionsMode").GetUInt16(),
        element.GetProperty("createdUtcMs").GetInt64(),
        element.GetProperty("contentModifiedUtcMs").GetInt64(),
        element.GetProperty("metadataChangedUtcMs").GetInt64(),
        element.GetProperty("revision").GetInt64(),
        element.GetProperty("length").GetInt64(),
        OptionalString(element, "symbolicLinkTarget"),
        OptionalString(element, "contentHash"),
        element.GetProperty("contentKind").GetInt32(),
        RequiredString(element, "mediaType"),
        OptionalString(element, "encodingName"));

    /// <summary>Converts validated domain metadata into its persisted record.</summary>
    internal static IndexedDbFileSystemEntryRecord FromMetadata(FileSystemEntryMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        long length = metadata switch
        {
            FileMetadata file => file.Length,
            SymbolicLinkMetadata symbolicLink => symbolicLink.Length,
            _ => 0
        };
        string? target = (metadata as SymbolicLinkMetadata)?.Target;

        return new IndexedDbFileSystemEntryRecord(
            metadata.Id.ToString(),
            (int)metadata.Kind,
            metadata.OwnerId,
            metadata.GroupId,
            metadata.Permissions.Mode,
            metadata.Timestamps.CreatedAtUtc.ToUnixTimeMilliseconds(),
            metadata.Timestamps.ContentModifiedAtUtc.ToUnixTimeMilliseconds(),
            metadata.Timestamps.MetadataChangedAtUtc.ToUnixTimeMilliseconds(),
            metadata.Revision,
            length,
            target,
            null,
            (int)FileSystemContentKind.Binary,
            "application/octet-stream",
            null);
    }

    /// <summary>Converts the persisted representation back to validated domain metadata.</summary>
    internal FileSystemEntryMetadata ToMetadata()
    {
        FileSystemEntryId id = FileSystemEntryId.Parse(Id);
        FileSystemPermissions permissions = FileSystemPermissions.FromMode(PermissionsMode);
        FileSystemTimestamps timestamps = new(
            DateTimeOffset.FromUnixTimeMilliseconds(CreatedUtcMs),
            DateTimeOffset.FromUnixTimeMilliseconds(ContentModifiedUtcMs),
            DateTimeOffset.FromUnixTimeMilliseconds(MetadataChangedUtcMs));
        return (FileSystemEntryKind)Kind switch
        {
            FileSystemEntryKind.File => new FileMetadata(
                id, OwnerId, GroupId, permissions, timestamps, Revision, Length, MediaType),
            FileSystemEntryKind.Directory => new DirectoryMetadata(id, OwnerId, GroupId, permissions, timestamps, Revision),
            FileSystemEntryKind.SymbolicLink => new SymbolicLinkMetadata(
                id,
                OwnerId,
                GroupId,
                permissions,
                timestamps,
                Revision,
                SymbolicLinkTarget ?? throw new InvalidDataException("Persisted symbolic link has no target.")),
            _ => throw new InvalidDataException($"Persisted filesystem entry kind '{Kind}' is unknown.")
        };
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidDataException($"Persisted filesystem property '{propertyName}' is missing.");

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).ValueKind == JsonValueKind.Null
            ? null
            : element.GetProperty(propertyName).GetString();
}

/// <summary>Stable browser-storage representation of one parent/name to child link.</summary>
internal sealed record IndexedDbFileSystemLinkRecord(string ParentId, string Name, string EntryId);
