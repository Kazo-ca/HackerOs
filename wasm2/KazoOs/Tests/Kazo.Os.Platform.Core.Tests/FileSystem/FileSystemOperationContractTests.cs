using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.FileSystem;

public sealed class FileSystemOperationContractTests
{
    private static readonly VirtualPath SourcePath = VirtualPath.Parse("/home/user/source.txt");
    private static readonly VirtualPath DestinationPath = VirtualPath.Parse("/home/user/destination.txt");
    private static readonly FileSystemPermissions Permissions = FileSystemPermissions.FromMode(0x01A4);

    [Fact]
    public void Error_codes_and_operations_have_stable_explicit_values()
    {
        Assert.Equal(1, (int)FileSystemOperation.Read);
        Assert.Equal(10, (int)FileSystemOperation.Transaction);
        Assert.Equal(1000, (int)FileSystemErrorCode.InvalidPath);
        Assert.Equal(1300, (int)FileSystemErrorCode.RevisionConflict);
        Assert.Equal(1701, (int)FileSystemErrorCode.ProviderFailure);
    }

    [Fact]
    public void Generic_result_contains_exactly_a_value_or_an_error()
    {
        FileSystemEntrySnapshot snapshot = new(SourcePath, CreateMetadata());
        FileSystemResult<FileSystemEntrySnapshot> success =
            FileSystemResult<FileSystemEntrySnapshot>.Success(snapshot);
        FileSystemError error = new(
            FileSystemOperation.Stat,
            FileSystemErrorCode.NotFound,
            SourcePath);
        FileSystemResult<FileSystemEntrySnapshot> failure =
            FileSystemResult<FileSystemEntrySnapshot>.Failure(error);

        Assert.True(success.Succeeded);
        Assert.Same(snapshot, success.Value);
        Assert.Null(success.Error);
        Assert.False(failure.Succeeded);
        Assert.Null(failure.Value);
        Assert.Same(error, failure.Error);
    }

    [Fact]
    public void Every_required_operation_has_an_immutable_request_contract()
    {
        FileSystemReadRequest read = new(SourcePath);
        FileSystemEnumerateRequest enumerate = new(VirtualPath.Parse("/home/user"));
        FileSystemCreateRequest create = new(DestinationPath, FileSystemEntryKind.File, Permissions, 2);
        FileSystemWriteRequest write = new(SourcePath, 3);
        FileSystemMoveRequest move = new(SourcePath, DestinationPath, 3, 4, 5);
        FileSystemCopyRequest copy = new(SourcePath, DestinationPath, 3, 5);
        FileSystemDeleteRequest delete = new(SourcePath, 3, 4, recursive: true);
        FileSystemStatRequest stat = new(SourcePath, FileSystemLinkBehavior.NoFollow);
        FileSystemSetPermissionsRequest setPermissions = new(SourcePath, Permissions, 3);

        Assert.Equal(FileSystemLinkBehavior.Follow, read.LinkBehavior);
        Assert.Equal("/home/user", enumerate.Path.Value);
        Assert.Equal(2, create.ExpectedParentRevision);
        Assert.Equal(3, write.ExpectedRevision);
        Assert.Equal(4, move.ExpectedSourceParentRevision);
        Assert.Equal(5, copy.ExpectedDestinationParentRevision);
        Assert.True(delete.Recursive);
        Assert.Equal(FileSystemLinkBehavior.NoFollow, stat.LinkBehavior);
        Assert.Equal(Permissions, setPermissions.Permissions);
    }

    [Fact]
    public void Mutation_requests_reject_invalid_preconditions_and_ambiguous_targets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSystemCreateRequest(DestinationPath, FileSystemEntryKind.File, Permissions, 0));
        Assert.Throws<ArgumentException>(() =>
            new FileSystemCreateRequest(DestinationPath, FileSystemEntryKind.SymbolicLink, Permissions, 1));
        Assert.Throws<ArgumentException>(() =>
            new FileSystemMoveRequest(SourcePath, SourcePath, 1, 1, 1));
        Assert.Throws<ArgumentException>(() =>
            new FileSystemCopyRequest(SourcePath, SourcePath, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSystemDeleteRequest(SourcePath, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSystemWriteRequest(SourcePath, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSystemReadRequest(SourcePath, (FileSystemLinkBehavior)99));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSystemSetPermissionsRequest(SourcePath, Permissions, 1, (FileSystemLinkBehavior)99));
    }

    [Fact]
    public void Directory_snapshot_requires_unique_ordinal_sorting_and_copies_input()
    {
        FileSystemDirectoryItem alpha = new(FileSystemEntryName.Parse("Alpha"), CreateMetadata());
        FileSystemDirectoryItem lowerAlpha = new(FileSystemEntryName.Parse("alpha"), CreateMetadata());
        FileSystemDirectoryItem[] input = [alpha, lowerAlpha];

        FileSystemDirectorySnapshot snapshot = new(
            VirtualPath.Parse("/home/user"),
            7,
            input);
        input[0] = lowerAlpha;

        Assert.Equal("Alpha", snapshot.Entries[0].Name.Value);
        Assert.Throws<ArgumentException>(() => new FileSystemDirectorySnapshot(
            VirtualPath.Parse("/home/user"),
            7,
            [lowerAlpha, alpha]));
        Assert.Throws<ArgumentException>(() => new FileSystemDirectorySnapshot(
            VirtualPath.Parse("/home/user"),
            7,
            [alpha, alpha]));
    }

    [Fact]
    public void Transaction_results_enforce_all_or_nothing_outcomes()
    {
        Guid transactionId = Guid.Parse("d9428888-122b-11e1-b85c-61cd3cbb3210");
        FileSystemEntryId entryId = FileSystemEntryId.Parse("15f88b8c98a4479d9463d68867d35e15");
        FileSystemEntryId[] affected = [entryId];
        FileSystemTransactionResult committed = FileSystemTransactionResult.Committed(transactionId, affected);
        affected[0] = FileSystemEntryId.Parse("6fa459eaee8a3ca4894e0db77e160355");
        FileSystemError conflict = new(
            FileSystemOperation.Transaction,
            FileSystemErrorCode.RevisionConflict,
            SourcePath);
        FileSystemTransactionResult rejected = FileSystemTransactionResult.Rejected(transactionId, conflict);
        FileSystemError cancellation = new(
            FileSystemOperation.Transaction,
            FileSystemErrorCode.Cancelled,
            SourcePath);
        FileSystemTransactionResult cancelled = FileSystemTransactionResult.Cancelled(transactionId, cancellation);

        Assert.Equal(FileSystemTransactionStatus.Committed, committed.Status);
        Assert.Equal(entryId, Assert.Single(committed.AffectedEntryIds));
        Assert.Null(committed.Error);
        Assert.Equal(FileSystemTransactionStatus.Rejected, rejected.Status);
        Assert.Empty(rejected.AffectedEntryIds);
        Assert.Same(conflict, rejected.Error);
        Assert.Equal(FileSystemTransactionStatus.Cancelled, cancelled.Status);
        Assert.Same(cancellation, cancelled.Error);
        Assert.Throws<ArgumentException>(() => FileSystemTransactionResult.Rejected(transactionId, cancellation));
        Assert.Throws<ArgumentException>(() => FileSystemTransactionResult.Cancelled(transactionId, conflict));
    }

    private static FileMetadata CreateMetadata() => new(
        FileSystemEntryId.Parse("15f88b8c98a4479d9463d68867d35e15"),
        "user-1",
        "users",
        Permissions,
        new FileSystemTimestamps(
            new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero)),
        1,
        0);
}