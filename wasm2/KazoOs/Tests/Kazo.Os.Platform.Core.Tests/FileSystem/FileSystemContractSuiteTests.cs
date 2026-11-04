using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.FileSystem;

public sealed class FileSystemContractSuiteTests
{
    [Fact]
    public async Task Crud_round_trip_streams_large_binary_content()
    {
        Fixture fixture = await Fixture.CreateAsync();
        byte[] content = Enumerable.Range(0, 192 * 1024)
            .Select(static index => (byte)(index % 251))
            .ToArray();
        FileSystemMutationResult created = await fixture.CreateAsync(
            "/home/alice/Documents/data.bin",
            FileSystemEntryKind.File,
            fixture.AliceContext);
        FileSystemMutationResult written = await fixture.Service.WriteAsync(
            new FileSystemWriteRequest(
                VirtualPath.Parse("/home/alice/Documents/data.bin"),
                created.Entry!.Metadata.Revision),
            new BytesSource(content, FileSystemContentDescriptor.Binary()),
            fixture.AliceContext);
        FileSystemResult<FileSystemContentReadHandle> read = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/home/alice/Documents/data.bin")),
            fixture.AliceContext);

        await using FileSystemContentReadHandle handle = read.Value!;
        using MemoryStream actual = new();
        await handle.Content.CopyToAsync(actual);
        FileSystemEntrySnapshot parent = await fixture.StatAsync("/home/alice/Documents", fixture.AliceContext);
        FileSystemMutationResult deleted = await fixture.Service.DeleteAsync(
            new FileSystemDeleteRequest(
                VirtualPath.Parse("/home/alice/Documents/data.bin"),
                written.Entry!.Metadata.Revision,
                parent.Metadata.Revision),
            fixture.AliceContext);

        Assert.Equal(content, actual.ToArray());
        Assert.Equal(FileSystemContentKind.Binary, handle.Descriptor.Kind);
        Assert.True(deleted.Succeeded);
    }

    [Fact]
    public async Task Mode_permissions_deny_other_user_until_owner_grants_read()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemMutationResult created = await fixture.CreateAsync(
            "/tmp/private.txt",
            FileSystemEntryKind.File,
            fixture.AliceContext,
            0x0180);

        FileSystemResult<FileSystemContentReadHandle> denied = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/tmp/private.txt")),
            fixture.BobContext);
        FileSystemMutationResult changed = await fixture.Service.SetPermissionsAsync(
            new FileSystemSetPermissionsRequest(
                VirtualPath.Parse("/tmp/private.txt"),
                FileSystemPermissions.FromMode(0x0184),
                created.Entry!.Metadata.Revision),
            fixture.AliceContext);
        FileSystemResult<FileSystemContentReadHandle> allowed = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/tmp/private.txt")),
            fixture.BobContext);

        Assert.Equal(FileSystemErrorCode.PermissionDenied, denied.Error?.Code);
        Assert.True(changed.Succeeded);
        Assert.True(allowed.Succeeded);
        await allowed.Value!.DisposeAsync();
    }

    [Fact]
    public async Task Relative_symlink_reads_target_but_delete_removes_only_link()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemMutationResult target = await fixture.CreateAsync(
            "/home/alice/Documents/target.txt",
            FileSystemEntryKind.File,
            fixture.AliceContext);
        await fixture.Service.WriteAsync(
            new FileSystemWriteRequest(
                VirtualPath.Parse("/home/alice/Documents/target.txt"),
                target.Entry!.Metadata.Revision),
            new BytesSource("target"u8.ToArray(), FileSystemContentDescriptor.Text()),
            fixture.AliceContext);
        FileSystemMutationResult link = await fixture.CreateAsync(
            "/home/alice/Documents/link.txt",
            FileSystemEntryKind.SymbolicLink,
            fixture.AliceContext,
            symbolicLinkTarget: "target.txt");

        FileSystemResult<FileSystemContentReadHandle> read = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/home/alice/Documents/link.txt")),
            fixture.AliceContext);
        await using FileSystemContentReadHandle handle = read.Value!;
        using StreamReader reader = new(handle.Content, Encoding.UTF8);
        FileSystemEntrySnapshot parent = await fixture.StatAsync("/home/alice/Documents", fixture.AliceContext);
        FileSystemMutationResult deleted = await fixture.Service.DeleteAsync(
            new FileSystemDeleteRequest(
                VirtualPath.Parse("/home/alice/Documents/link.txt"),
                link.Entry!.Metadata.Revision,
                parent.Metadata.Revision),
            fixture.AliceContext);

        Assert.Equal("target", await reader.ReadToEndAsync());
        Assert.True(deleted.Succeeded);
        Assert.True((await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/alice/Documents/target.txt")),
            fixture.AliceContext)).Succeeded);
    }

    [Fact]
    public async Task Symbolic_link_loop_returns_stable_error()
    {
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.CreateAsync(
            "/home/alice/Documents/a",
            FileSystemEntryKind.SymbolicLink,
            fixture.AliceContext,
            symbolicLinkTarget: "b");
        await fixture.CreateAsync(
            "/home/alice/Documents/b",
            FileSystemEntryKind.SymbolicLink,
            fixture.AliceContext,
            symbolicLinkTarget: "a");

        FileSystemResult<FileSystemContentReadHandle> result = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/home/alice/Documents/a")),
            fixture.AliceContext);

        Assert.Equal(FileSystemErrorCode.SymbolicLinkLoop, result.Error?.Code);
    }

    [Fact]
    public async Task Atomic_move_and_copy_preserve_subtree_and_identity_rules()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemMutationResult project = await fixture.CreateAsync(
            "/home/alice/Documents/project",
            FileSystemEntryKind.Directory,
            fixture.AliceContext,
            0x01C0);
        await fixture.CreateAsync(
            "/home/alice/Documents/project/readme.txt",
            FileSystemEntryKind.File,
            fixture.AliceContext);
        FileSystemEntrySnapshot projectCurrent = await fixture.StatAsync(
            "/home/alice/Documents/project",
            fixture.AliceContext);
        FileSystemEntrySnapshot documents = await fixture.StatAsync(
            "/home/alice/Documents",
            fixture.AliceContext);
        FileSystemEntrySnapshot downloads = await fixture.StatAsync(
            "/home/alice/Downloads",
            fixture.AliceContext);

        FileSystemMutationResult stale = await fixture.Service.MoveAsync(
            new FileSystemMoveRequest(
                VirtualPath.Parse("/home/alice/Documents/project"),
                VirtualPath.Parse("/home/alice/Downloads/project"),
                project.Entry!.Metadata.Revision,
                documents.Metadata.Revision,
                downloads.Metadata.Revision),
            fixture.AliceContext);
        FileSystemMutationResult moved = await fixture.Service.MoveAsync(
            new FileSystemMoveRequest(
                VirtualPath.Parse("/home/alice/Documents/project"),
                VirtualPath.Parse("/home/alice/Downloads/project"),
                projectCurrent.Metadata.Revision,
                documents.Metadata.Revision,
                downloads.Metadata.Revision),
            fixture.AliceContext);
        documents = await fixture.StatAsync("/home/alice/Documents", fixture.AliceContext);
        FileSystemMutationResult copied = await fixture.Service.CopyAsync(
            new FileSystemCopyRequest(
                VirtualPath.Parse("/home/alice/Downloads/project"),
                VirtualPath.Parse("/home/alice/Documents/project-copy"),
                moved.Entry!.Metadata.Revision,
                documents.Metadata.Revision),
            fixture.AliceContext);

        Assert.Equal(FileSystemErrorCode.RevisionConflict, stale.Transaction.Error?.Code);
        Assert.Equal(projectCurrent.Metadata.Id, moved.Entry.Metadata.Id);
        Assert.NotEqual(moved.Entry.Metadata.Id, copied.Entry!.Metadata.Id);
        Assert.True((await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/alice/Documents/project-copy/readme.txt")),
            fixture.AliceContext)).Succeeded);
    }

    [Fact]
    public async Task Revision_conflict_and_cancellation_leave_state_unchanged()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemMutationResult created = await fixture.CreateAsync(
            "/home/alice/Documents/file.txt",
            FileSystemEntryKind.File,
            fixture.AliceContext);
        FileSystemMutationResult first = await fixture.Service.WriteAsync(
            new FileSystemWriteRequest(
                VirtualPath.Parse("/home/alice/Documents/file.txt"),
                created.Entry!.Metadata.Revision),
            new BytesSource([1]),
            fixture.AliceContext);
        FileSystemMutationResult conflict = await fixture.Service.WriteAsync(
            new FileSystemWriteRequest(
                VirtualPath.Parse("/home/alice/Documents/file.txt"),
                created.Entry.Metadata.Revision),
            new BytesSource([2]),
            fixture.AliceContext);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        FileSystemEntrySnapshot parent = await fixture.StatAsync("/home/alice/Documents", fixture.AliceContext);
        FileSystemMutationResult cancelled = await fixture.Service.CreateAsync(
            new FileSystemCreateRequest(
                VirtualPath.Parse("/home/alice/Documents/cancelled"),
                FileSystemEntryKind.File,
                FileSystemPermissions.FromMode(0x01A4),
                parent.Metadata.Revision),
            fixture.AliceContext,
            cancellation.Token);
        FileSystemResult<FileSystemContentReadHandle> read = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/home/alice/Documents/file.txt")),
            fixture.AliceContext);

        await using FileSystemContentReadHandle handle = read.Value!;
        Assert.True(first.Succeeded);
        Assert.Equal(FileSystemErrorCode.RevisionConflict, conflict.Transaction.Error?.Code);
        Assert.Equal(FileSystemTransactionStatus.Cancelled, cancelled.Transaction.Status);
        Assert.Equal(1, handle.Content.ReadByte());
        Assert.False((await fixture.Repository.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/alice/Documents/cancelled")),
            fixture.SystemContext)).Succeeded);
    }

    [Fact]
    public async Task Clean_profile_seed_is_idempotent()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemEntrySnapshot before = await fixture.StatAsync(
            "/home/alice/Documents",
            fixture.SystemContext);

        await fixture.Seeder.SeedAsync("alice", "users");
        FileSystemEntrySnapshot after = await fixture.StatAsync(
            "/home/alice/Documents",
            fixture.SystemContext);

        Assert.Equal(before.Metadata.Id, after.Metadata.Id);
        Assert.Equal(before.Metadata.Revision, after.Metadata.Revision);
    }

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;

        private Fixture()
        {
            TimeProvider = new FixedTimeProvider();
            Repository = new InMemoryFileSystemRepository(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                NextTransactionId,
                TimeProvider);
            FileSystemMountRouter router = new(Repository);
            Service = new FileSystemService(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                new InMemoryTopicMessageBus(new CapabilityGrantRepository()),
                NextTransactionId);
            Seeder = new FileSystemSeeder(Service, TimeProvider);
            AliceContext = Context("alice", AppAuthority.User);
            BobContext = Context("bob", AppAuthority.User);
            AdminContext = Context("admin", AppAuthority.Administrator);
            SystemContext = Context("system", AppAuthority.System, isSystem: true);
        }

        internal TimeProvider TimeProvider { get; }
        internal InMemoryFileSystemRepository Repository { get; }
        internal FileSystemService Service { get; }
        internal FileSystemSeeder Seeder { get; }
        internal FileSystemAuthorizationContext AliceContext { get; }
        internal FileSystemAuthorizationContext BobContext { get; }
        internal FileSystemAuthorizationContext AdminContext { get; }
        internal FileSystemAuthorizationContext SystemContext { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            await fixture.Seeder.SeedAsync("alice", "users");
            return fixture;
        }

        internal Guid NextTransactionId() => new(_transactionId++, 0, 0, new byte[8]);

        internal async ValueTask<FileSystemMutationResult> CreateAsync(
            string pathValue,
            FileSystemEntryKind kind,
            FileSystemAuthorizationContext context,
            ushort mode = 0x01A4,
            string? symbolicLinkTarget = null)
        {
            VirtualPath path = VirtualPath.Parse(pathValue);
            FileSystemEntrySnapshot parent = await StatAsync(GetParent(path).Value, context);
            return await Service.CreateAsync(
                new FileSystemCreateRequest(
                    path,
                    kind,
                    FileSystemPermissions.FromMode(mode),
                    parent.Metadata.Revision,
                    symbolicLinkTarget),
                context);
        }

        internal async ValueTask<FileSystemEntrySnapshot> StatAsync(
            string path,
            FileSystemAuthorizationContext context) =>
            (await Service.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse(path)),
                context)).Value!;

        private FileSystemAuthorizationContext Context(
            string userId,
            AppAuthority authority,
            bool isSystem = false)
        {
            AppOperationContext operation = new()
            {
                AppId = "org.kazoos.test",
                UserId = userId,
                UserAuthority = authority,
                GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                IsSystemOperation = isSystem
            };
            string group = authority == AppAuthority.Administrator ? "administrators" : $"{userId}s";
            return new FileSystemAuthorizationContext(
                operation,
                [group, "users"],
                TimeProvider.GetUtcNow());
        }

        private static VirtualPath GetParent(VirtualPath path)
        {
            int separator = path.Value.LastIndexOf('/');
            return separator <= 0 ? VirtualPath.Parse("/") : VirtualPath.Parse(path.Value[..separator]);
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
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}