using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Time;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Platform.Core.Tests.FileSystem;

/// <summary>
/// Tests for <c>MSG-011</c>/<c>MSG-012</c>/<c>MSG-013</c> — <see cref="FileSystemService"/> publishing
/// <see cref="FileSystemChangeEvent"/>s and <see cref="AppFileSystemWatchGateway"/> reusing filesystem
/// read authorization, per doc/wasm/dev/old/Global-FileView-And-MessagingSystem/MessagingSystem.md.
/// </summary>
/// <remarks>
/// <see cref="InMemoryTopicMessageBus.Publish{TPayload}"/> dispatches synchronously, so a change event is
/// already sitting in a <see cref="ITopicChannelSubscription{TPayload}"/>'s channel by the time the
/// triggering mutation's <c>await</c> returns — no background reader loop is needed in these tests.
/// </remarks>
public sealed class FileSystemWatchTests
{
    [Fact]
    public async Task Create_publishes_a_created_event_to_the_parents_topic()
    {
        Fixture fixture = await Fixture.CreateAsync();
        ITopicChannelSubscription<FileSystemChangeEvent> watch = fixture.WatchDirectory("/home/user");

        await fixture.Service.CreateAsync(
            new FileSystemCreateRequest(
                VirtualPath.Parse("/home/user/notes.txt"),
                FileSystemEntryKind.File,
                FileSystemPermissions.FromMode(0x01A4),
                fixture.HomeUserRevision),
            fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));

        FileSystemChangeEvent change = ReadOne(watch);
        Assert.Equal(FileSystemChangeKind.Created, change.Kind);
        Assert.Equal(VirtualPath.Parse("/home/user/notes.txt"), change.Path);
        Assert.Equal(FileSystemEntryKind.File, change.EntryKind);
        AssertNoMore(watch);
    }

    [Fact]
    public async Task Write_publishes_a_contentModified_event()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemMutationResult file = await fixture.CreateFileAsync("/home/user/notes.txt");
        ITopicChannelSubscription<FileSystemChangeEvent> watch = fixture.WatchDirectory("/home/user");

        await fixture.Service.WriteAsync(
            new FileSystemWriteRequest(VirtualPath.Parse("/home/user/notes.txt"), file.Entry!.Metadata.Revision),
            new BytesSource([1, 2, 3]),
            fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));

        Assert.Equal(FileSystemChangeKind.ContentModified, ReadOne(watch).Kind);
    }

    [Fact]
    public async Task Delete_publishes_a_deleted_event_with_the_original_entry_kind()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemMutationResult file = await fixture.CreateFileAsync("/home/user/notes.txt");
        ITopicChannelSubscription<FileSystemChangeEvent> watch = fixture.WatchDirectory("/home/user");

        await fixture.Service.DeleteAsync(
            new FileSystemDeleteRequest(
                VirtualPath.Parse("/home/user/notes.txt"), file.Entry!.Metadata.Revision, fixture.HomeUserRevision),
            fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));

        FileSystemChangeEvent change = ReadOne(watch);
        Assert.Equal(FileSystemChangeKind.Deleted, change.Kind);
        Assert.Equal(FileSystemEntryKind.File, change.EntryKind);
    }

    [Fact]
    public async Task SetPermissions_publishes_a_metadataModified_event()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemMutationResult file = await fixture.CreateFileAsync("/home/user/notes.txt");
        ITopicChannelSubscription<FileSystemChangeEvent> watch = fixture.WatchDirectory("/home/user");

        await fixture.Service.SetPermissionsAsync(
            new FileSystemSetPermissionsRequest(
                VirtualPath.Parse("/home/user/notes.txt"),
                FileSystemPermissions.FromMode(0x0180),
                file.Entry!.Metadata.Revision),
            fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));

        Assert.Equal(FileSystemChangeKind.MetadataModified, ReadOne(watch).Kind);
    }

    [Fact]
    public async Task Move_within_the_same_directory_publishes_only_movedFrom()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemMutationResult file = await fixture.CreateFileAsync("/home/user/notes.txt");
        ITopicChannelSubscription<FileSystemChangeEvent> watch = fixture.WatchDirectory("/home/user");

        await fixture.Service.MoveAsync(
            new FileSystemMoveRequest(
                VirtualPath.Parse("/home/user/notes.txt"),
                VirtualPath.Parse("/home/user/renamed.txt"),
                file.Entry!.Metadata.Revision,
                fixture.HomeUserRevision,
                fixture.HomeUserRevision),
            fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));

        FileSystemChangeEvent change = ReadOne(watch);
        Assert.Equal(FileSystemChangeKind.MovedFrom, change.Kind);
        Assert.Equal(VirtualPath.Parse("/home/user/notes.txt"), change.Path);
        Assert.Equal(VirtualPath.Parse("/home/user/renamed.txt"), change.MovedToPath);
        AssertNoMore(watch);
    }

    [Fact]
    public async Task Move_across_directories_publishes_movedFrom_and_movedTo()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemMutationResult docs = await fixture.CreateDirectoryAsync("/home/user/Documents");
        FileSystemMutationResult file = await fixture.CreateFileAsync("/home/user/notes.txt");
        ITopicChannelSubscription<FileSystemChangeEvent> sourceWatch = fixture.WatchDirectory("/home/user");
        ITopicChannelSubscription<FileSystemChangeEvent> destinationWatch = fixture.WatchDirectory("/home/user/Documents");

        await fixture.Service.MoveAsync(
            new FileSystemMoveRequest(
                VirtualPath.Parse("/home/user/notes.txt"),
                VirtualPath.Parse("/home/user/Documents/notes.txt"),
                file.Entry!.Metadata.Revision,
                fixture.HomeUserRevision,
                docs.Entry!.Metadata.Revision),
            fixture.UserContext(AppCapabilities.FileSystemUserHomeWrite));

        Assert.Equal(FileSystemChangeKind.MovedFrom, ReadOne(sourceWatch).Kind);
        FileSystemChangeEvent destinationChange = ReadOne(destinationWatch);
        Assert.Equal(FileSystemChangeKind.MovedTo, destinationChange.Kind);
        Assert.Equal(VirtualPath.Parse("/home/user/Documents/notes.txt"), destinationChange.Path);
    }

    [Fact]
    public async Task Copy_publishes_a_created_event_at_the_destination_only()
    {
        Fixture fixture = await Fixture.CreateAsync();
        FileSystemMutationResult docs = await fixture.CreateDirectoryAsync("/home/user/Documents");
        await fixture.CreateFileAsync("/home/user/notes.txt");
        FileSystemEntrySnapshot sourceStat = (await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/notes.txt")),
            fixture.UserContext(AppCapabilities.FileSystemUserHomeRead))).Value!;
        ITopicChannelSubscription<FileSystemChangeEvent> sourceWatch = fixture.WatchDirectory("/home/user");
        ITopicChannelSubscription<FileSystemChangeEvent> destinationWatch = fixture.WatchDirectory("/home/user/Documents");

        await fixture.Service.CopyAsync(
            new FileSystemCopyRequest(
                VirtualPath.Parse("/home/user/notes.txt"),
                VirtualPath.Parse("/home/user/Documents/notes.txt"),
                sourceStat.Metadata.Revision,
                docs.Entry!.Metadata.Revision),
            fixture.UserContext(AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite));

        AssertNoMore(sourceWatch);
        Assert.Equal(FileSystemChangeKind.Created, ReadOne(destinationWatch).Kind);
    }

    [Fact]
    public async Task Disposing_a_watch_subscription_stops_delivery()
    {
        Fixture fixture = await Fixture.CreateAsync();
        ITopicChannelSubscription<FileSystemChangeEvent> watch = fixture.WatchDirectory("/home/user");
        await watch.DisposeAsync();

        await fixture.CreateFileAsync("/home/user/notes.txt");

        Assert.False(watch.Reader.TryRead(out _));
        Assert.True(watch.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task WatchAsync_succeeds_for_a_path_the_caller_can_read()
    {
        Fixture fixture = await Fixture.CreateAsync();
        AppFileSystemWatchGateway gateway = fixture.CreateWatchGateway(AppCapabilities.FileSystemUserHomeRead);

        await using ITopicChannelSubscription<FileSystemChangeEvent> subscription = await gateway.WatchAsync(
            VirtualPath.Parse("/home/user"), FileSystemWatchScope.ImmediateChildren);

        Assert.NotNull(subscription);
    }

    [Fact]
    public async Task WatchAsync_denies_a_path_the_caller_cannot_read()
    {
        Fixture fixture = await Fixture.CreateAsync();
        AppFileSystemWatchGateway gateway = fixture.CreateWatchGateway();

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() =>
            gateway.WatchAsync(VirtualPath.Parse("/home/user"), FileSystemWatchScope.ImmediateChildren).AsTask());
    }

    [Theory]
    [InlineData(FileSystemWatchScope.ThisEntry)]
    [InlineData(FileSystemWatchScope.Recursive)]
    public async Task WatchAsync_rejects_unimplemented_scopes(FileSystemWatchScope scope)
    {
        Fixture fixture = await Fixture.CreateAsync();
        AppFileSystemWatchGateway gateway = fixture.CreateWatchGateway(AppCapabilities.FileSystemUserHomeRead);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            gateway.WatchAsync(VirtualPath.Parse("/home/user"), scope).AsTask());
    }

    [Fact]
    public async Task WatchAsync_delivers_a_real_change_end_to_end()
    {
        Fixture fixture = await Fixture.CreateAsync();
        AppFileSystemWatchGateway gateway = fixture.CreateWatchGateway(AppCapabilities.FileSystemUserHomeRead);
        await using ITopicChannelSubscription<FileSystemChangeEvent> subscription = await gateway.WatchAsync(
            VirtualPath.Parse("/home/user"), FileSystemWatchScope.ImmediateChildren);

        await fixture.CreateFileAsync("/home/user/notes.txt");

        Assert.Equal(FileSystemChangeKind.Created, ReadOne(subscription).Kind);
    }

    private static FileSystemChangeEvent ReadOne(ITopicChannelSubscription<FileSystemChangeEvent> subscription)
    {
        bool got = subscription.Reader.TryRead(out TopicMessage<FileSystemChangeEvent>? message);
        Assert.True(got, "Expected a change event but none was delivered.");
        return message!.Payload;
    }

    private static void AssertNoMore(ITopicChannelSubscription<FileSystemChangeEvent> subscription) =>
        Assert.False(subscription.Reader.TryRead(out _), "Expected no further change events.");

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        private const string AppId = "org.hackeros.test-app";

        private Fixture()
        {
            TopicBus = new InMemoryTopicMessageBus(new CapabilityGrantRepository(() => _now));
            Repository = new InMemoryFileSystemRepository(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                new FixedTimeProvider());
            FileSystemMountRouter router = new(Repository);
            Service = new FileSystemService(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                TopicBus,
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                () => _now);
        }

        internal InMemoryTopicMessageBus TopicBus { get; }
        internal InMemoryFileSystemRepository Repository { get; }
        internal FileSystemService Service { get; }
        internal long HomeUserRevision { get; private set; }

        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            FileSystemAuthorizationContext system = fixture.Context("system", AppAuthority.System, AppCapabilities.All, isSystemOperation: true);
            FileSystemMutationResult home = await fixture.Repository.CreateAsync(
                new FileSystemCreateRequest(
                    VirtualPath.Parse("/home"), FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0x01FF), 1),
                system);
            FileSystemMutationResult user = await fixture.Repository.CreateAsync(
                new FileSystemCreateRequest(
                    VirtualPath.Parse("/home/user"),
                    FileSystemEntryKind.Directory,
                    FileSystemPermissions.FromMode(0x01FF),
                    home.Entry!.Metadata.Revision),
                system);
            fixture.HomeUserRevision = user.Entry!.Metadata.Revision;
            return fixture;
        }

        internal async Task<FileSystemMutationResult> CreateFileAsync(string path)
        {
            FileSystemMutationResult result = await Service.CreateAsync(
                new FileSystemCreateRequest(
                    VirtualPath.Parse(path), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x01A4), HomeUserRevision),
                UserContext(AppCapabilities.FileSystemUserHomeWrite));
            await RefreshHomeUserRevisionAsync();
            return result;
        }

        internal async Task<FileSystemMutationResult> CreateDirectoryAsync(string path)
        {
            FileSystemMutationResult result = await Service.CreateAsync(
                new FileSystemCreateRequest(
                    VirtualPath.Parse(path), FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0x01FF), HomeUserRevision),
                UserContext(AppCapabilities.FileSystemUserHomeWrite));
            await RefreshHomeUserRevisionAsync();
            return result;
        }

        private async Task RefreshHomeUserRevisionAsync()
        {
            HomeUserRevision = (await Service.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse("/home/user")),
                UserContext(AppCapabilities.FileSystemUserHomeRead))).Value!.Metadata.Revision;
        }

        internal ITopicChannelSubscription<FileSystemChangeEvent> WatchDirectory(string path) =>
            TopicBus.SubscribeChannel<FileSystemChangeEvent>(
                FileSystemTopics.ForDirectory(VirtualPath.Parse(path)), KernelPublisherIdentity.Value);

        internal AppFileSystemWatchGateway CreateWatchGateway(params string[] capabilities)
        {
            AppOperationContext operationContext = new()
            {
                AppId = AppId,
                UserId = "user",
                UserAuthority = AppAuthority.User,
                GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal),
                IsSystemOperation = false
            };
            return new AppFileSystemWatchGateway(
                Service, TopicBus, new ManualSimulationClock(_now, TimeSpan.FromSeconds(1)), operationContext, ["users"], "1");
        }

        internal FileSystemAuthorizationContext UserContext(params string[] capabilities) =>
            Context("user", AppAuthority.User, capabilities);

        private FileSystemAuthorizationContext Context(
            string userId, AppAuthority authority, IEnumerable<string> capabilities, bool isSystemOperation = false)
        {
            AppOperationContext operation = new()
            {
                AppId = AppId,
                UserId = userId,
                UserAuthority = authority,
                GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal),
                IsSystemOperation = isSystemOperation
            };
            return new FileSystemAuthorizationContext(operation, [userId == "system" ? "system" : "users"], _now, null);
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        }
    }

    private sealed class BytesSource(byte[] bytes) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Binary();
        public long? Length => bytes.LongLength;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
