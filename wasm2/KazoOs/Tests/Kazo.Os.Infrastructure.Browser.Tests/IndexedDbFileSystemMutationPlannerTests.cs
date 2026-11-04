using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>Verifies filesystem metadata mutations are planned as atomic ordered batches.</summary>
public sealed class IndexedDbFileSystemMutationPlannerTests
{
    [Fact]
    public void PlanCreate_AssertsParentBeforeUpdatingEntryAndLink()
    {
        DateTimeOffset createdAt = new(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset committedAt = createdAt.AddMinutes(1);
        DirectoryMetadata parentMetadata = new(
            FileSystemEntryId.FromGuid(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            "system",
            "system",
            FileSystemPermissions.FromMode(0x01ED),
            new FileSystemTimestamps(createdAt, createdAt, createdAt),
            4);
        FileMetadata childMetadata = new(
            FileSystemEntryId.FromGuid(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            "user",
            "users",
            FileSystemPermissions.FromMode(0x01A4),
            new FileSystemTimestamps(committedAt, committedAt, committedAt),
            1,
            0);
        IndexedDbFileSystemEntryRecord parent = IndexedDbFileSystemEntryRecord.FromMetadata(parentMetadata);

        IReadOnlyList<IndexedDbOperation> operations = IndexedDbFileSystemMutationPlanner.PlanCreate(
            parent,
            childMetadata,
            FileSystemEntryName.Parse("notes.txt"),
            committedAt);

        Assert.Equal(
            ["assertPropertyEquals", "put", "add", "add"],
            operations.Select(operation => operation.Kind));
        Assert.Equal(
            [
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                HackerOsIndexedDbSchema.FileSystemLinkStoreName
            ],
            operations.Select(operation => operation.ObjectStoreName));
        Assert.Equal(parent.Id, operations[0].Key);
        Assert.Equal("revision", operations[0].CompareProperty);
        Assert.Equal(4L, operations[0].ExpectedValue);

        IndexedDbFileSystemEntryRecord updatedParent =
            Assert.IsType<IndexedDbFileSystemEntryRecord>(operations[1].Value);
        Assert.Equal(5, updatedParent.Revision);
        Assert.Equal(committedAt.ToUnixTimeMilliseconds(), updatedParent.ContentModifiedUtcMs);
        Assert.Equal(committedAt.ToUnixTimeMilliseconds(), updatedParent.MetadataChangedUtcMs);

        IndexedDbFileSystemEntryRecord child =
            Assert.IsType<IndexedDbFileSystemEntryRecord>(operations[2].Value);
        Assert.Equal("22222222222222222222222222222222", child.Id);
        Assert.Equal(0x01A4, child.PermissionsMode);

        IndexedDbFileSystemLinkRecord link =
            Assert.IsType<IndexedDbFileSystemLinkRecord>(operations[3].Value);
        Assert.Equal(parent.Id, link.ParentId);
        Assert.Equal("notes.txt", link.Name);
        Assert.Equal(child.Id, link.EntryId);
    }

    [Fact]
    public void PlanSetPermissions_ChangesOnlyModeMetadataTimeAndRevision()
    {
        DateTimeOffset originalTime = new(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset committedAt = originalTime.AddMinutes(5);
        FileMetadata metadata = new(
            FileSystemEntryId.FromGuid(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            "user",
            "users",
            FileSystemPermissions.FromMode(0x01A4),
            new FileSystemTimestamps(originalTime, originalTime, originalTime),
            7,
            42);
        IndexedDbFileSystemEntryRecord entry = IndexedDbFileSystemEntryRecord.FromMetadata(metadata);

        IReadOnlyList<IndexedDbOperation> operations = IndexedDbFileSystemMutationPlanner.PlanSetPermissions(
            entry,
            FileSystemPermissions.FromMode(0x01ED),
            committedAt);

        Assert.Equal(["assertPropertyEquals", "put"], operations.Select(operation => operation.Kind));
        Assert.Equal("filesystem.revision-conflict", operations[0].FailureCode);
        IndexedDbFileSystemEntryRecord updated =
            Assert.IsType<IndexedDbFileSystemEntryRecord>(operations[1].Value);
        Assert.Equal(0x01ED, updated.PermissionsMode);
        Assert.Equal(8, updated.Revision);
        Assert.Equal(committedAt.ToUnixTimeMilliseconds(), updated.MetadataChangedUtcMs);
        Assert.Equal(entry.ContentModifiedUtcMs, updated.ContentModifiedUtcMs);
        Assert.Equal(42, updated.Length);
    }

    [Fact]
    public void PlanWrite_PublishesHashDescriptorLengthAndRevision()
    {
        DateTimeOffset now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        IndexedDbFileSystemEntryRecord entry = EntryRecord(
            "11111111-1111-1111-1111-111111111111",
            FileSystemEntryKind.File,
            3,
            now);

        IReadOnlyList<IndexedDbOperation> operations = IndexedDbFileSystemMutationPlanner.PlanWrite(
            entry,
            "content-hash",
            42,
            FileSystemContentDescriptor.Text("text/markdown", "utf-8"),
            now.AddMinutes(1));

        Assert.Equal(["assertPropertyEquals", "put"], operations.Select(operation => operation.Kind));
        IndexedDbFileSystemEntryRecord updated =
            Assert.IsType<IndexedDbFileSystemEntryRecord>(operations[1].Value);
        Assert.Equal("content-hash", updated.ContentHash);
        Assert.Equal(42, updated.Length);
        Assert.Equal(4, updated.Revision);
        Assert.Equal((int)FileSystemContentKind.Text, updated.ContentKind);
        Assert.Equal("text/markdown", updated.MediaType);
        Assert.Equal("utf-8", updated.EncodingName);
    }

    [Fact]
    public void PlanMove_SameParentUpdatesParentOnlyOnce()
    {
        DateTimeOffset now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        IndexedDbFileSystemEntryRecord parent = EntryRecord(
            "11111111-1111-1111-1111-111111111111",
            FileSystemEntryKind.Directory,
            4,
            now);
        IndexedDbFileSystemEntryRecord entry = EntryRecord(
            "22222222-2222-2222-2222-222222222222",
            FileSystemEntryKind.File,
            2,
            now);

        IReadOnlyList<IndexedDbOperation> operations = IndexedDbFileSystemMutationPlanner.PlanMove(
            entry,
            parent,
            FileSystemEntryName.Parse("old.txt"),
            parent,
            FileSystemEntryName.Parse("new.txt"),
            now.AddMinutes(1));

        Assert.Equal(
            ["assertPropertyEquals", "assertPropertyEquals", "put", "put", "delete", "add"],
            operations.Select(operation => operation.Kind));
        IndexedDbFileSystemEntryRecord updatedParent =
            Assert.IsType<IndexedDbFileSystemEntryRecord>(operations[3].Value);
        Assert.Equal(5, updatedParent.Revision);
    }

    [Fact]
    public void PlanMove_DifferentParentsUpdatesBothParents()
    {
        DateTimeOffset now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        IndexedDbFileSystemEntryRecord sourceParent = EntryRecord(
            "11111111-1111-1111-1111-111111111111",
            FileSystemEntryKind.Directory,
            4,
            now);
        IndexedDbFileSystemEntryRecord destinationParent = EntryRecord(
            "33333333-3333-3333-3333-333333333333",
            FileSystemEntryKind.Directory,
            6,
            now);
        IndexedDbFileSystemEntryRecord entry = EntryRecord(
            "22222222-2222-2222-2222-222222222222",
            FileSystemEntryKind.File,
            2,
            now);

        IReadOnlyList<IndexedDbOperation> operations = IndexedDbFileSystemMutationPlanner.PlanMove(
            entry,
            sourceParent,
            FileSystemEntryName.Parse("old.txt"),
            destinationParent,
            FileSystemEntryName.Parse("new.txt"),
            now.AddMinutes(1));

        Assert.Equal(3, operations.Count(operation => operation.Kind == "assertPropertyEquals"));
        Assert.Equal(3, operations.Count(operation => operation.Kind == "put"));
        Assert.Equal(8, operations.Count);
    }

    [Fact]
    public void PlanDelete_AssertsSnapshotAndDeletesDescendantsFirst()
    {
        DateTimeOffset now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        IndexedDbFileSystemEntryRecord parent = EntryRecord(
            "11111111-1111-1111-1111-111111111111",
            FileSystemEntryKind.Directory,
            4,
            now);
        IndexedDbFileSystemEntryRecord directory = EntryRecord(
            "22222222-2222-2222-2222-222222222222",
            FileSystemEntryKind.Directory,
            2,
            now);
        IndexedDbFileSystemEntryRecord child = EntryRecord(
            "33333333-3333-3333-3333-333333333333",
            FileSystemEntryKind.File,
            1,
            now);

        IReadOnlyList<IndexedDbOperation> operations = IndexedDbFileSystemMutationPlanner.PlanDelete(
            parent,
            [
                new(parent.Id, FileSystemEntryName.Parse("folder"), directory),
                new(directory.Id, FileSystemEntryName.Parse("child.txt"), child)
            ],
            now.AddMinutes(1));

        Assert.Equal(3, operations.Count(operation => operation.Kind == "assertPropertyEquals"));
        Assert.Equal("put", operations[3].Kind);
        Assert.Equal(new object[] { directory.Id, "child.txt" }, Assert.IsType<object[]>(operations[4].Key));
        Assert.Equal(child.Id, operations[5].Key);
        Assert.Equal(new object[] { parent.Id, "folder" }, Assert.IsType<object[]>(operations[6].Key));
        Assert.Equal(directory.Id, operations[7].Key);
    }

    [Fact]
    public void PlanCopy_AssertsSourceSnapshotAndPreservesContentReference()
    {
        DateTimeOffset now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        IndexedDbFileSystemEntryRecord destinationParent = EntryRecord(
            "11111111-1111-1111-1111-111111111111",
            FileSystemEntryKind.Directory,
            4,
            now);
        IndexedDbFileSystemEntryRecord source = EntryRecord(
            "22222222-2222-2222-2222-222222222222",
            FileSystemEntryKind.File,
            7,
            now) with { ContentHash = "sha256:content" };
        IndexedDbFileSystemEntryRecord copy = source with
        {
            Id = "33333333333333333333333333333333",
            Revision = 1
        };

        IReadOnlyList<IndexedDbOperation> operations = IndexedDbFileSystemMutationPlanner.PlanCopy(
            destinationParent,
            [new(source, copy, destinationParent.Id, FileSystemEntryName.Parse("copy.txt"))],
            now.AddMinutes(1));

        Assert.Equal(
            ["assertPropertyEquals", "assertPropertyEquals", "put", "add", "add"],
            operations.Select(operation => operation.Kind));
        IndexedDbFileSystemEntryRecord persistedCopy =
            Assert.IsType<IndexedDbFileSystemEntryRecord>(operations[3].Value);
        Assert.Equal("sha256:content", persistedCopy.ContentHash);
        Assert.Equal(1, persistedCopy.Revision);
        IndexedDbFileSystemLinkRecord link = Assert.IsType<IndexedDbFileSystemLinkRecord>(operations[4].Value);
        Assert.Equal(destinationParent.Id, link.ParentId);
        Assert.Equal(persistedCopy.Id, link.EntryId);
    }

    private static IndexedDbFileSystemEntryRecord EntryRecord(
        string id,
        FileSystemEntryKind kind,
        long revision,
        DateTimeOffset now)
    {
        FileSystemEntryId entryId = FileSystemEntryId.FromGuid(Guid.Parse(id));
        FileSystemTimestamps timestamps = new(now, now, now);
        FileSystemEntryMetadata metadata = kind == FileSystemEntryKind.Directory
            ? new DirectoryMetadata(entryId, "user", "users", FileSystemPermissions.FromMode(0x01ED), timestamps, revision)
            : new FileMetadata(entryId, "user", "users", FileSystemPermissions.FromMode(0x01A4), timestamps, revision, 0);
        return IndexedDbFileSystemEntryRecord.FromMetadata(metadata);
    }
}
