using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Metrics;

namespace Kazo.Os.Platform.Core.Tests.FileSystem;

public sealed class FileSystemServiceTests
{
    [Fact]
    public async Task Read_requires_exact_home_capability_even_when_mode_allows()
    {
        ServiceFixture fixture = await ServiceFixture.CreateAsync();

        FileSystemResult<FileSystemContentReadHandle> result = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/home/user/file.txt")),
            fixture.UserContext());

        Assert.Equal(FileSystemErrorCode.CapabilityDenied, result.Error?.Code);
    }

    [Fact]
    public async Task Read_with_capability_and_mode_returns_streamed_content()
    {
        ServiceFixture fixture = await ServiceFixture.CreateAsync();
        FileSystemAuthorizationContext context = fixture.UserContext(AppCapabilities.FileSystemUserHomeRead);

        FileSystemResult<FileSystemContentReadHandle> result = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/home/user/file.txt")),
            context);

        await using FileSystemContentReadHandle handle = result.Value!;
        Assert.Equal(7, handle.Content.ReadByte());
    }

    [Fact]
    public async Task User_cannot_write_system_path_with_capability_but_without_authority()
    {
        ServiceFixture fixture = await ServiceFixture.CreateAsync();
        FileSystemAuthorizationContext user = fixture.UserContext(AppCapabilities.FileSystemSystemWrite);
        FileSystemEntrySnapshot etc = (await fixture.Repository.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/etc")),
            fixture.SystemContext)).Value!;

        FileSystemMutationResult result = await fixture.Service.CreateAsync(
            new FileSystemCreateRequest(
                VirtualPath.Parse("/etc/user.conf"),
                FileSystemEntryKind.File,
                FileSystemPermissions.FromMode(0x01A4),
                etc.Metadata.Revision),
            user);

        Assert.Equal(FileSystemErrorCode.AuthorityDenied, result.Transaction.Error?.Code);
    }

    [Fact]
    public async Task Selected_handle_allows_scoped_read_without_broad_capability()
    {
        ServiceFixture fixture = await ServiceFixture.CreateAsync();
        FileSystemSelectedResourceHandle handle = new(
            Guid.Parse("d9428888-122b-11e1-b85c-61cd3cbb3210"),
            "org.kazoos.test",
            "user",
            VirtualPath.Parse("/home/user/file.txt"),
            FileSystemHandleAccess.Read,
            fixture.Now.AddMinutes(-1),
            fixture.Now.AddMinutes(1),
            1);

        FileSystemResult<FileSystemContentReadHandle> result = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/home/user/file.txt")),
            fixture.UserContext(handle: handle));

        Assert.True(result.Succeeded);
        await result.Value!.DisposeAsync();
    }

    [Fact]
    public async Task In_the_root_reserve_a_users_write_fails_with_no_space_and_asks_for_a_new_measure()
    {
        FullGuard guard = new();
        ServiceFixture fixture = await ServiceFixture.CreateAsync(guard);

        FileSystemMutationResult result = await fixture.WriteFileAsync(fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));

        Assert.Equal(FileSystemErrorCode.NoSpace, result.Transaction.Error?.Code);
        Assert.Equal(1, guard.Refreshes);
    }

    [Fact]
    public async Task In_the_root_reserve_the_system_and_root_keep_writing()
    {
        FullGuard guard = new();
        ServiceFixture fixture = await ServiceFixture.CreateAsync(guard);

        FileSystemMutationResult system = await fixture.WriteFileAsync(fixture.SystemContext);

        Assert.True(system.Succeeded);
        Assert.Equal(1, guard.Refreshes);
    }

    [Fact]
    public async Task Outside_the_reserve_a_users_write_succeeds_and_asks_for_a_new_measure()
    {
        FullGuard guard = new() { IsRootReserveReached = false };
        ServiceFixture fixture = await ServiceFixture.CreateAsync(guard);

        FileSystemMutationResult result = await fixture.WriteFileAsync(fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));

        Assert.True(result.Succeeded);
        Assert.Equal(1, guard.Refreshes);
    }

    private sealed class ServiceFixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;

        private ServiceFixture(IStorageSpaceGuard? spaceGuard)
        {
            Now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            Repository = new InMemoryFileSystemRepository(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                new FixedTimeProvider());
            SystemContext = Context(
                "system",
                AppAuthority.System,
                AppCapabilities.All,
                isSystemOperation: true);
            FileSystemMountRouter router = new(Repository);
            Service = new FileSystemService(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                new InMemoryTopicMessageBus(new CapabilityGrantRepository()),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                spaceGuard: spaceGuard);
        }

        internal DateTimeOffset Now { get; }
        internal InMemoryFileSystemRepository Repository { get; }
        internal FileSystemService Service { get; }
        internal FileSystemAuthorizationContext SystemContext { get; }

        internal static async Task<ServiceFixture> CreateAsync(IStorageSpaceGuard? spaceGuard = null)
        {
            ServiceFixture fixture = new(spaceGuard);
            FileSystemMutationResult home = await fixture.Repository.CreateAsync(
                CreateDirectory("/home", 1),
                fixture.SystemContext);
            FileSystemMutationResult user = await fixture.Repository.CreateAsync(
                CreateDirectory("/home/user", home.Entry!.Metadata.Revision),
                fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));
            FileSystemMutationResult file = await fixture.Repository.CreateAsync(
                new FileSystemCreateRequest(
                    VirtualPath.Parse("/home/user/file.txt"),
                    FileSystemEntryKind.File,
                    FileSystemPermissions.FromMode(0x01A4),
                    user.Entry!.Metadata.Revision),
                fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));
            await fixture.Repository.WriteAsync(
                new FileSystemWriteRequest(VirtualPath.Parse("/home/user/file.txt"), file.Entry!.Metadata.Revision),
                new BytesSource([7]),
                fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));
            FileSystemEntrySnapshot root = (await fixture.Repository.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse("/")),
                fixture.SystemContext)).Value!;
            await fixture.Repository.CreateAsync(CreateDirectory("/etc", root.Metadata.Revision), fixture.SystemContext);
            return fixture;
        }

        /// <summary>Overwrites <c>/home/user/file.txt</c> through the service.</summary>
        internal async Task<FileSystemMutationResult> WriteFileAsync(FileSystemAuthorizationContext context)
        {
            FileSystemEntrySnapshot file = (await Repository.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse("/home/user/file.txt")),
                SystemContext)).Value!;
            return await Service.WriteAsync(
                new FileSystemWriteRequest(VirtualPath.Parse("/home/user/file.txt"), file.Metadata.Revision),
                new BytesSource([1, 2, 3]),
                context);
        }

        internal FileSystemAuthorizationContext UserContext(
            string? capability = null,
            FileSystemSelectedResourceHandle? handle = null) =>
            Context(
                "user",
                AppAuthority.User,
                capability is null ? [] : [capability],
                selectedHandle: handle);

        private FileSystemAuthorizationContext Context(
            string userId,
            AppAuthority authority,
            IEnumerable<string> capabilities,
            bool isSystemOperation = false,
            FileSystemSelectedResourceHandle? selectedHandle = null)
        {
            AppOperationContext operation = new()
            {
                AppId = "org.kazoos.test",
                UserId = userId,
                UserAuthority = authority,
                GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal),
                IsSystemOperation = isSystemOperation
            };
            return new FileSystemAuthorizationContext(operation, [userId == "system" ? "system" : "users"], Now, selectedHandle);
        }

        private static FileSystemCreateRequest CreateDirectory(string path, long parentRevision) =>
            new(
                VirtualPath.Parse(path),
                FileSystemEntryKind.Directory,
                FileSystemPermissions.FromMode(0x01FF),
                parentRevision);
    }

    /// <summary>Free-space state of the reserve tests; counts the measures the service asked for.</summary>
    private sealed class FullGuard : IStorageSpaceGuard
    {
        public bool IsRootReserveReached { get; init; } = true;

        public int Refreshes { get; private set; }

        public void RequestRefresh() => Refreshes++;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class BytesSource(byte[] bytes) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Binary();
        public long? Length => bytes.LongLength;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}