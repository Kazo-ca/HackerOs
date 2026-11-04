using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.Diagnostics;

/// <summary>
/// A real <see cref="FileSystemService"/> over an in-memory repository, seeded like a clean profile,
/// with the 100 ms simulation clock of the production hosts. Used by the ADR 0045 contract tests.
/// </summary>
internal sealed class LogTestFileSystem
{
    private int _entryId = 1;
    private int _transactionId = 1000;

    private LogTestFileSystem()
    {
        Clock = new ManualSimulationClock(new DateTimeOffset(2026, 10, 10, 3, 11, 42, 123, TimeSpan.Zero), TimeSpan.FromMilliseconds(100));
        Repository = new InMemoryFileSystemRepository(
            () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
            () => new Guid(_transactionId++, 0, 0, new byte[8]),
            new ClockTimeProvider(Clock));
        _router = new FileSystemMountRouter(Repository);
        _topicBus = new InMemoryTopicMessageBus(new CapabilityGrantRepository(() => Clock.UtcNow));
        Service = CreateService(audit: null);
    }

    private readonly FileSystemMountRouter _router;
    private readonly InMemoryTopicMessageBus _topicBus;

    /// <summary>
    /// A second <see cref="FileSystemService"/> over the same repository that reports audited
    /// mutations to <paramref name="audit"/> (ADR 0100 §3).
    /// </summary>
    public FileSystemService CreateServiceWithAudit(IFileSystemAuditSink audit) => CreateService(audit);

    private FileSystemService CreateService(IFileSystemAuditSink? audit) => new(
        _router,
        new FileSystemPathResolver(_router),
        new FileSystemAuthorizer(),
        _topicBus,
        () => new Guid(_transactionId++, 0, 0, new byte[8]),
        audit: audit);

    public ManualSimulationClock Clock { get; }

    public InMemoryFileSystemRepository Repository { get; }

    public FileSystemService Service { get; }

    public static async Task<LogTestFileSystem> CreateAsync()
    {
        LogTestFileSystem fileSystem = new();
        await new FileSystemSeeder(fileSystem.Service, new ClockTimeProvider(fileSystem.Clock)).SeedAsync("alice", "users");
        return fileSystem;
    }

    public static FileSystemAuthorizationContext SystemContext(DateTimeOffset now) => new(
        new AppOperationContext
        {
            AppId = "org.kazoos.kernel",
            UserId = "system",
            UserAuthority = AppAuthority.System,
            GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
            IsSystemOperation = true
        },
        ["system"],
        now);

    /// <summary>An ordinary user's app context holding exactly <paramref name="capabilities"/>.</summary>
    public static FileSystemAuthorizationContext UserContext(DateTimeOffset now, params string[] capabilities) => new(
        new AppOperationContext
        {
            AppId = "org.kazoos.error-log-viewer",
            UserId = "alice",
            UserName = "alice",
            UserAuthority = AppAuthority.User,
            GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal),
            IsSystemOperation = false
        },
        ["users"],
        now);

    public async Task<FileSystemEntrySnapshot?> StatAsync(string path)
    {
        FileSystemResult<FileSystemEntrySnapshot> stat = await Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse(path)), SystemContext(Clock.UtcNow));
        return stat.Succeeded ? stat.Value : null;
    }

    public async Task<string> ReadTextAsync(string path, FileSystemAuthorizationContext? context = null)
    {
        FileSystemResult<FileSystemContentReadHandle> read = await Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse(path)), context ?? SystemContext(Clock.UtcNow));
        Assert.True(read.Succeeded, $"read {path}: {read.Error?.Code}");
        await using FileSystemContentReadHandle handle = read.Value!;
        using StreamReader reader = new(handle.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    public async Task<string[]> ReadLinesAsync(string path) =>
        (await ReadTextAsync(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Creates or replaces <paramref name="path"/> with <paramref name="content"/> as the system user.</summary>
    public async Task WriteTextAsync(string path, string content, ushort mode = 0b110_100_100)
    {
        VirtualPath target = VirtualPath.Parse(path);
        FileSystemAuthorizationContext system = SystemContext(Clock.UtcNow);
        await Service.CreateAsync(new FileSystemCreateRequest(target, FileSystemEntryKind.File, FileSystemPermissions.FromMode(mode)), system);
        FileSystemMutationResult write = await Service.WriteAsync(new FileSystemWriteRequest(target), new TextSource(content), system);
        Assert.True(write.Succeeded, $"write {path}: {write.Transaction.Error?.Code}");
    }

    private sealed class TextSource(string content) : IFileSystemContentSource
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);

        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();

        public long? Length => _bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
    }

    private sealed class ClockTimeProvider(ManualSimulationClock clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.UtcNow;
    }
}
