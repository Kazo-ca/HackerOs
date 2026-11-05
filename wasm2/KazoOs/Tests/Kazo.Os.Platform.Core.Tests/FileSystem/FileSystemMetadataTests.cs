using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.FileSystem;

public sealed class FileSystemMetadataTests
{
    private static readonly FileSystemEntryId EntryId =
        FileSystemEntryId.Parse("15f88b8c98a4479d9463d68867d35e15");

    private static readonly FileSystemTimestamps Timestamps = new(
        new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 1, 10, 1, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 1, 10, 2, 0, TimeSpan.Zero));

    [Fact]
    public void Entry_id_uses_canonical_opaque_value_and_rejects_empty()
    {
        Assert.Equal("15f88b8c98a4479d9463d68867d35e15", EntryId.ToString());
        Assert.Equal(EntryId, FileSystemEntryId.Parse(EntryId.ToString()));
        Assert.Throws<ArgumentException>(() => FileSystemEntryId.FromGuid(Guid.Empty));
        Assert.False(FileSystemEntryId.TryParse("15f88b8c-98a4-479d-9463-d68867d35e15", out _));
    }

    [Fact]
    public void Entry_name_normalizes_unicode_and_enforces_segment_rules()
    {
        FileSystemEntryName name = FileSystemEntryName.Parse("re\u0301sume\u0301.txt");

        Assert.Equal("résumé.txt", name.Value);
        Assert.Throws<FormatException>(() => FileSystemEntryName.Parse(".."));
        Assert.Throws<FormatException>(() => FileSystemEntryName.Parse("folder/name"));
        Assert.Throws<FormatException>(() => FileSystemEntryName.Parse(new string('é', 128)));
    }

    [Fact]
    public void Permission_mode_round_trips_owner_group_and_other_access()
    {
        FileSystemPermissions permissions = FileSystemPermissions.FromMode(0x01A4);

        Assert.Equal(FileSystemAccess.Read | FileSystemAccess.Write, permissions.Owner);
        Assert.Equal(FileSystemAccess.Read, permissions.Group);
        Assert.Equal(FileSystemAccess.Read, permissions.Other);
        Assert.Equal((ushort)0x01A4, permissions.Mode);
        Assert.Equal("644", permissions.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => FileSystemPermissions.FromMode(0x0200));
    }

    [Fact]
    public void Metadata_preserves_identity_and_exposes_kind_specific_values()
    {
        FileMetadata file = new(
            EntryId,
            "user-1",
            "users",
            FileSystemPermissions.FromMode(0x01A4),
            Timestamps,
            3,
            42);
        DirectoryMetadata directory = new(
            FileSystemEntryId.Parse("6fa459eaee8a3ca4894e0db77e160355"),
            "user-1",
            "users",
            FileSystemPermissions.FromMode(0x01ED),
            Timestamps,
            1);
        SymbolicLinkMetadata link = new(
            FileSystemEntryId.Parse("7c9e6679742f40de944b15f931413d51"),
            "user-1",
            "users",
            FileSystemPermissions.FromMode(0x01FF),
            Timestamps,
            2,
            "../Documents/re\u0301sume\u0301.txt");

        Assert.Equal(FileSystemEntryKind.File, file.Kind);
        Assert.Equal(42, file.Length);
        Assert.Equal(FileSystemEntryKind.Directory, directory.Kind);
        Assert.Equal(FileSystemEntryKind.SymbolicLink, link.Kind);
        Assert.Equal("../Documents/résumé.txt", link.Target);
        Assert.Equal(25, link.Length);
    }

    [Fact]
    public void Directory_link_changes_name_without_changing_child_identity()
    {
        FileSystemEntryId parentId = FileSystemEntryId.Parse("a8098c1a-f86e-11da-bd1a-00112444be1e".Replace("-", string.Empty));
        FileSystemDirectoryEntry before = new(parentId, FileSystemEntryName.Parse("before.txt"), EntryId);
        FileSystemDirectoryEntry after = new(parentId, FileSystemEntryName.Parse("after.txt"), EntryId);

        Assert.NotEqual(before.Name, after.Name);
        Assert.Equal(before.EntryId, after.EntryId);
    }

    [Fact]
    public void Metadata_rejects_invalid_revision_size_and_timestamp_order()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileMetadata(
            EntryId,
            "user-1",
            "users",
            FileSystemPermissions.FromMode(0x01A4),
            Timestamps,
            0,
            0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileMetadata(
            EntryId,
            "user-1",
            "users",
            FileSystemPermissions.FromMode(0x01A4),
            Timestamps,
            1,
            -1));
        Assert.Throws<ArgumentException>(() => new FileSystemTimestamps(
            Timestamps.CreatedAtUtc,
            Timestamps.CreatedAtUtc.AddMinutes(-1),
            Timestamps.MetadataChangedAtUtc));
        Assert.Throws<ArgumentException>(() => new FileSystemTimestamps(
            Timestamps.CreatedAtUtc.ToOffset(TimeSpan.FromHours(1)),
            Timestamps.ContentModifiedAtUtc,
            Timestamps.MetadataChangedAtUtc));
    }
}