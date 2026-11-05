using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// In-memory filesystem/topic-bus/gateway wiring shared by every <c>FileView</c>-family test (Details,
/// Icons, Tree) — one <c>/home/user</c> home directory, a real <see cref="FileSystemService"/>, and scoped
/// <see cref="IAppFileSystemGateway"/>/<see cref="IAppFileSystemWatchGateway"/> gateways for a single test app.
/// </summary>
internal sealed class FileViewTestFixture
{
    private int _entryId = 1;
    private int _transactionId = 100;
    private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private const string AppId = "org.kazoos.test-app";

    private FileViewTestFixture()
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
        Clock = new ManualSimulationClock(_now, TimeSpan.FromSeconds(1));

        AppOperationContext operationContext = new()
        {
            AppId = AppId,
            UserId = "user",
            UserAuthority = AppAuthority.User,
            GrantedCapabilities = new HashSet<string>(
                [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite],
                StringComparer.Ordinal),
            IsSystemOperation = false
        };
        FileSystem = new AppFileSystemGateway(Service, Clock, operationContext, ["users"]);
        Watch = new AppFileSystemWatchGateway(Service, TopicBus, Clock, operationContext, ["users"], "1");
        Intents = new FakeAppIntentGateway();
    }

    internal IAppFileSystemGateway FileSystem { get; }
    internal IAppFileSystemWatchGateway Watch { get; }
    internal FakeAppIntentGateway Intents { get; }
    internal InMemoryTopicMessageBus TopicBus { get; }
    internal FileSystemService Service { get; }
    private InMemoryFileSystemRepository Repository { get; }
    internal ManualSimulationClock Clock { get; }

    internal static async Task<FileViewTestFixture> CreateAsync()
    {
        FileViewTestFixture fixture = new();
        FileSystemAuthorizationContext system = fixture.SystemContext();
        FileSystemMutationResult home = await fixture.Repository.CreateAsync(
            new FileSystemCreateRequest(
                VirtualPath.Parse("/home"), FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0x01FF), 1),
            system);
        await fixture.Repository.CreateAsync(
            new FileSystemCreateRequest(
                VirtualPath.Parse("/home/user"),
                FileSystemEntryKind.Directory,
                FileSystemPermissions.FromMode(0x01FF),
                home.Entry!.Metadata.Revision),
            system);
        return fixture;
    }

    internal async Task CreateDirectoryAsync(string path)
    {
        long parentRevision = await StatRevisionAsync(ParentOf(path));
        FileSystemMutationResult result = await Service.CreateAsync(
            new FileSystemCreateRequest(
                VirtualPath.Parse(path), FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0x01FF), parentRevision),
            UserContext());
        Assert.Equal(FileSystemTransactionStatus.Committed, result.Transaction.Status);
    }

    internal async Task CreateFileAsync(string path)
    {
        long parentRevision = await StatRevisionAsync(ParentOf(path));
        FileSystemMutationResult result = await Service.CreateAsync(
            new FileSystemCreateRequest(
                VirtualPath.Parse(path), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x01A4), parentRevision),
            UserContext());
        Assert.Equal(FileSystemTransactionStatus.Committed, result.Transaction.Status);
    }

    private static string ParentOf(string path)
    {
        int lastSlash = path.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : path[..lastSlash];
    }

    private async Task<long> StatRevisionAsync(string path) =>
        (await Service.StatAsync(new FileSystemStatRequest(VirtualPath.Parse(path)), UserContext())).Value!.Metadata.Revision;

    internal FileSystemAuthorizationContext UserContext() =>
        Context("user", AppAuthority.User, [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite]);

    private FileSystemAuthorizationContext SystemContext() =>
        Context("system", AppAuthority.System, AppCapabilities.All, isSystemOperation: true);

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
