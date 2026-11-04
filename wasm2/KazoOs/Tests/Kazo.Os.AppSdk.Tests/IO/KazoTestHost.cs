using System.Threading.Channels;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

/// <summary>Builds an ambient app context over the in-memory filesystem of Kazo.Os.Tests.Support.</summary>
internal static class KazoTestHost
{
    public const string Home = "/home/alice";

    public static AppManifest Manifest { get; } = new()
    {
        Id = "org.kazoos.test-io",
        Name = "Test IO",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "Kazo* file API contract test application.",
        Kind = AppKind.Terminal,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Tests", "TestIoApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    /// <summary>Fixed simulated "now" of every test context (used by the trash's DeletionDate).</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 10, 14, 3, 27, TimeSpan.Zero);

    public static MinimalAppExecutionContext CreateContext(
        IAppFileSystemGateway fileSystem,
        IAppFileSystemWatchGateway? watch = null,
        IAppLoggingGateway? logging = null,
        IAppClockGateway? clock = null,
        bool isRootElevated = false) =>
        new(
            Manifest,
            fileSystem: fileSystem,
            userId: "0f00",
            watch: watch,
            homeDirectory: Home,
            logging: logging ?? new FakeAppLoggingGateway(),
            clock: clock ?? new FakeAppClockGateway(Now),
            isRootElevated: isRootElevated);

    /// <summary>Seeds the home directory and enters an ambient scope working in it.</summary>
    public static IDisposable Enter(out FakeAppFileSystemGateway fileSystem, string workingDirectory = Home)
    {
        fileSystem = new FakeAppFileSystemGateway().WithDirectory(Home);
        return KazoExecutionContext.Enter(CreateContext(fileSystem), workingDirectory);
    }
}

/// <summary>
/// Decorates a gateway so a test can replace the outcome of any call (inject an error, a race, ...)
/// while every other call reaches the in-memory filesystem.
/// </summary>
internal sealed class ScriptedFileSystemGateway(IAppFileSystemGateway inner) : IAppFileSystemGateway
{
    public Func<FileSystemReadRequest, FileSystemError?>? OnRead { get; set; }
    public Func<FileSystemEnumerateRequest, FileSystemError?>? OnEnumerate { get; set; }
    public Func<FileSystemStatRequest, FileSystemError?>? OnStat { get; set; }
    public Func<FileSystemCreateRequest, FileSystemError?>? OnCreate { get; set; }
    public Func<FileSystemWriteRequest, FileSystemError?>? OnWrite { get; set; }
    public Func<FileSystemMoveRequest, FileSystemError?>? OnMove { get; set; }
    public Func<FileSystemCopyRequest, FileSystemError?>? OnCopy { get; set; }
    public Func<FileSystemDeleteRequest, FileSystemError?>? OnDelete { get; set; }

    public int WriteCalls { get; private set; }
    public int CreateCalls { get; private set; }

    public async ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(FileSystemReadRequest request, CancellationToken cancellationToken = default) =>
        OnRead?.Invoke(request) is { } error
            ? FileSystemResult<FileSystemContentReadHandle>.Failure(error)
            : await inner.ReadAsync(request, cancellationToken);

    public async ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(FileSystemEnumerateRequest request, CancellationToken cancellationToken = default) =>
        OnEnumerate?.Invoke(request) is { } error
            ? FileSystemResult<FileSystemDirectorySnapshot>.Failure(error)
            : await inner.EnumerateAsync(request, cancellationToken);

    public async ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(FileSystemStatRequest request, CancellationToken cancellationToken = default) =>
        OnStat?.Invoke(request) is { } error
            ? FileSystemResult<FileSystemEntrySnapshot>.Failure(error)
            : await inner.StatAsync(request, cancellationToken);

    public async ValueTask<FileSystemMutationResult> CreateAsync(FileSystemCreateRequest request, CancellationToken cancellationToken = default)
    {
        CreateCalls++;
        return OnCreate?.Invoke(request) is { } error ? Rejected(error) : await inner.CreateAsync(request, cancellationToken);
    }

    public async ValueTask<FileSystemMutationResult> WriteAsync(FileSystemWriteRequest request, IFileSystemContentSource content, CancellationToken cancellationToken = default)
    {
        WriteCalls++;
        return OnWrite?.Invoke(request) is { } error ? Rejected(error) : await inner.WriteAsync(request, content, cancellationToken);
    }

    public async ValueTask<FileSystemMutationResult> MoveAsync(FileSystemMoveRequest request, CancellationToken cancellationToken = default) =>
        OnMove?.Invoke(request) is { } error ? Rejected(error) : await inner.MoveAsync(request, cancellationToken);

    public async ValueTask<FileSystemMutationResult> CopyAsync(FileSystemCopyRequest request, CancellationToken cancellationToken = default) =>
        OnCopy?.Invoke(request) is { } error ? Rejected(error) : await inner.CopyAsync(request, cancellationToken);

    public async ValueTask<FileSystemMutationResult> DeleteAsync(FileSystemDeleteRequest request, CancellationToken cancellationToken = default) =>
        OnDelete?.Invoke(request) is { } error ? Rejected(error) : await inner.DeleteAsync(request, cancellationToken);

    public ValueTask<FileSystemMutationResult> SetPermissionsAsync(FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default) =>
        inner.SetPermissionsAsync(request, cancellationToken);

    public IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle) => this;

    public static FileSystemError Error(FileSystemOperation operation, FileSystemErrorCode code, string? path = null) =>
        new(operation, code, path is null ? null : VirtualPath.Parse(path));

    private static FileSystemMutationResult Rejected(FileSystemError error) =>
        error.Code == FileSystemErrorCode.Cancelled
            ? new(FileSystemTransactionResult.Cancelled(Guid.NewGuid(), error))
            : new(FileSystemTransactionResult.Rejected(Guid.NewGuid(), error));
}

/// <summary>A watch gateway whose changes the test pushes by hand.</summary>
internal sealed class FakeWatchGateway : IAppFileSystemWatchGateway
{
    private readonly Channel<TopicMessage<FileSystemChangeEvent>> _channel = Channel.CreateUnbounded<TopicMessage<FileSystemChangeEvent>>();

    public VirtualPath? WatchedPath { get; private set; }
    public FileSystemWatchScope? Scope { get; private set; }
    public bool Disposed { get; private set; }
    public bool Deny { get; set; }

    public ValueTask<ITopicChannelSubscription<FileSystemChangeEvent>> WatchAsync(
        VirtualPath path, FileSystemWatchScope scope, CancellationToken cancellationToken = default)
    {
        if (Deny)
        {
            throw new AppGatewayAccessDeniedException(
                "filesystem-watch:" + path.Value, Kazo.Os.App.Abstractions.Policy.CapabilityPolicyEvaluation.DenyMissing(1));
        }

        WatchedPath = path;
        Scope = scope;
        return ValueTask.FromResult<ITopicChannelSubscription<FileSystemChangeEvent>>(new Subscription(this));
    }

    public void Push(string path, FileSystemChangeKind kind, string? movedTo = null) =>
        _channel.Writer.TryWrite(new TopicMessage<FileSystemChangeEvent>(
            FileSystemTopics.ForDirectory(VirtualPath.Parse(Kazo.Os.AppSdk.IO.KazoPath.GetDirectoryName(path)!)),
            new FileSystemChangeEvent(
                VirtualPath.Parse(path), kind, FileSystemEntryKind.File, 1, DateTimeOffset.UtcNow,
                movedTo is null ? null : VirtualPath.Parse(movedTo)),
            "kernel",
            DateTimeOffset.UtcNow));

    private sealed class Subscription(FakeWatchGateway owner) : ITopicChannelSubscription<FileSystemChangeEvent>
    {
        public ChannelReader<TopicMessage<FileSystemChangeEvent>> Reader => owner._channel.Reader;

        public ValueTask DisposeAsync()
        {
            owner.Disposed = true;
            owner._channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
