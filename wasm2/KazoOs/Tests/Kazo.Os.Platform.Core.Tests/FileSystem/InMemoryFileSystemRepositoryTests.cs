using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.FileSystem;

public sealed class InMemoryFileSystemRepositoryTests
{
    [Fact]
    public async Task Create_write_and_read_round_trip_streamed_content()
    {
        TestRepository test = new();
        FileSystemMutationResult created = await test.Repository.CreateAsync(
            new FileSystemCreateRequest(Path("/file.txt"), FileSystemEntryKind.File, Mode(0x01A4), 1),
            test.Context);
        FileSystemMutationResult written = await test.Repository.WriteAsync(
            new FileSystemWriteRequest(Path("/file.txt"), created.Entry!.Metadata.Revision),
            new BytesSource("hello"u8.ToArray(), FileSystemContentDescriptor.Text()),
            test.Context);
        FileSystemResult<FileSystemContentReadHandle> read = await test.Repository.ReadAsync(
            new FileSystemReadRequest(Path("/file.txt")),
            test.Context);
        FileSystemResult<FileSystemDirectorySnapshot> directory = await test.Repository.EnumerateAsync(
            new FileSystemEnumerateRequest(Path("/")),
            test.Context);

        await using FileSystemContentReadHandle handle = read.Value!;
        using StreamReader reader = new(handle.Content);

        Assert.True(written.Succeeded);
        Assert.Equal("hello", await reader.ReadToEndAsync());
        Assert.Equal(FileSystemContentKind.Text, handle.Descriptor.Kind);
        FileMetadata metadata = Assert.IsType<FileMetadata>(Assert.Single(directory.Value!.Entries).Metadata);
        Assert.Equal("text/plain", metadata.MediaType);
    }

    [Fact]
    public async Task Enumeration_is_unique_and_ordinal_sorted()
    {
        TestRepository test = new();
        await test.Repository.CreateAsync(new FileSystemCreateRequest(Path("/b"), FileSystemEntryKind.File, Mode(0x01A4), 1), test.Context);
        FileSystemEntrySnapshot root = (await test.Repository.StatAsync(new FileSystemStatRequest(Path("/")), test.Context)).Value!;
        await test.Repository.CreateAsync(new FileSystemCreateRequest(Path("/A"), FileSystemEntryKind.File, Mode(0x01A4), root.Metadata.Revision), test.Context);

        FileSystemResult<FileSystemDirectorySnapshot> result = await test.Repository.EnumerateAsync(
            new FileSystemEnumerateRequest(Path("/")),
            test.Context);

        Assert.Equal(new[] { "A", "b" }, result.Value!.Entries.Select(static entry => entry.Name.Value));
    }

    [Fact]
    public async Task Move_preserves_identity_while_copy_allocates_new_identity()
    {
        TestRepository test = new();
        FileSystemMutationResult created = await test.Repository.CreateAsync(
            new FileSystemCreateRequest(Path("/source"), FileSystemEntryKind.File, Mode(0x01A4), 1),
            test.Context);
        FileSystemEntrySnapshot root = (await test.Repository.StatAsync(new FileSystemStatRequest(Path("/")), test.Context)).Value!;
        FileSystemMutationResult moved = await test.Repository.MoveAsync(
            new FileSystemMoveRequest(
                Path("/source"),
                Path("/moved"),
                created.Entry!.Metadata.Revision,
                root.Metadata.Revision,
                root.Metadata.Revision),
            test.Context);
        root = (await test.Repository.StatAsync(new FileSystemStatRequest(Path("/")), test.Context)).Value!;
        FileSystemMutationResult copied = await test.Repository.CopyAsync(
            new FileSystemCopyRequest(
                Path("/moved"),
                Path("/copy"),
                moved.Entry!.Metadata.Revision,
                root.Metadata.Revision),
            test.Context);

        Assert.Equal(created.Entry.Metadata.Id, moved.Entry.Metadata.Id);
        Assert.NotEqual(moved.Entry.Metadata.Id, copied.Entry!.Metadata.Id);
    }

    [Fact]
    public async Task Stale_write_revision_rejects_without_changing_content()
    {
        TestRepository test = new();
        FileSystemMutationResult created = await test.Repository.CreateAsync(
            new FileSystemCreateRequest(Path("/file"), FileSystemEntryKind.File, Mode(0x01A4), 1),
            test.Context);
        await test.Repository.WriteAsync(
            new FileSystemWriteRequest(Path("/file"), created.Entry!.Metadata.Revision),
            new BytesSource([1]),
            test.Context);
        FileSystemMutationResult conflict = await test.Repository.WriteAsync(
            new FileSystemWriteRequest(Path("/file"), created.Entry.Metadata.Revision),
            new BytesSource([2]),
            test.Context);
        FileSystemResult<FileSystemContentReadHandle> read = await test.Repository.ReadAsync(
            new FileSystemReadRequest(Path("/file")),
            test.Context);

        await using FileSystemContentReadHandle handle = read.Value!;
        Assert.Equal(FileSystemErrorCode.RevisionConflict, conflict.Transaction.Error?.Code);
        Assert.Equal(1, handle.Content.ReadByte());
    }

    [Fact]
    public async Task Non_recursive_directory_delete_is_atomic_then_recursive_delete_succeeds()
    {
        TestRepository test = new();
        FileSystemMutationResult directory = await test.Repository.CreateAsync(
            new FileSystemCreateRequest(Path("/dir"), FileSystemEntryKind.Directory, Mode(0x01ED), 1),
            test.Context);
        await test.Repository.CreateAsync(
            new FileSystemCreateRequest(Path("/dir/file"), FileSystemEntryKind.File, Mode(0x01A4), directory.Entry!.Metadata.Revision),
            test.Context);
        FileSystemEntrySnapshot currentDirectory = (await test.Repository.StatAsync(new FileSystemStatRequest(Path("/dir")), test.Context)).Value!;
        FileSystemEntrySnapshot root = (await test.Repository.StatAsync(new FileSystemStatRequest(Path("/")), test.Context)).Value!;
        FileSystemMutationResult rejected = await test.Repository.DeleteAsync(
            new FileSystemDeleteRequest(Path("/dir"), currentDirectory.Metadata.Revision, root.Metadata.Revision),
            test.Context);
        FileSystemMutationResult deleted = await test.Repository.DeleteAsync(
            new FileSystemDeleteRequest(Path("/dir"), currentDirectory.Metadata.Revision, root.Metadata.Revision, recursive: true),
            test.Context);

        Assert.Equal(FileSystemErrorCode.DirectoryNotEmpty, rejected.Transaction.Error?.Code);
        Assert.True((await test.Repository.StatAsync(new FileSystemStatRequest(Path("/dir/file")), test.Context)).Succeeded == false);
        Assert.True(deleted.Succeeded);
    }

    [Fact]
    public async Task Cancellation_before_commit_leaves_namespace_unchanged()
    {
        TestRepository test = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        FileSystemMutationResult result = await test.Repository.CreateAsync(
            new FileSystemCreateRequest(Path("/cancelled"), FileSystemEntryKind.File, Mode(0x01A4), 1),
            test.Context,
            cancellation.Token);

        Assert.Equal(FileSystemTransactionStatus.Cancelled, result.Transaction.Status);
        Assert.False((await test.Repository.StatAsync(new FileSystemStatRequest(Path("/cancelled")), test.Context)).Succeeded);
    }

    private static VirtualPath Path(string value) => VirtualPath.Parse(value);

    private static FileSystemPermissions Mode(ushort mode) => FileSystemPermissions.FromMode(mode);

    private sealed class TestRepository
    {
        private int _entryId = 1;
        private int _transactionId = 100;

        internal TestRepository()
        {
            Repository = new InMemoryFileSystemRepository(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                new FixedTimeProvider());
            Context = CreateContext();
        }

        internal InMemoryFileSystemRepository Repository { get; }

        internal FileSystemAuthorizationContext Context { get; }

        private static FileSystemAuthorizationContext CreateContext()
        {
            AppOperationContext operationContext = new()
            {
                AppId = "org.kazoos.test",
                UserId = "user",
                UserAuthority = AppAuthority.Administrator,
                GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                IsSystemOperation = true
            };
            return new FileSystemAuthorizationContext(
                operationContext,
                ["users"],
                new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class BytesSource(
        byte[] bytes,
        FileSystemContentDescriptor? descriptor = null) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } =
            descriptor ?? FileSystemContentDescriptor.Binary();

        public long? Length => bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }
}