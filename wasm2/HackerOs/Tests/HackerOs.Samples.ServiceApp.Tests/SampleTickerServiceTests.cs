using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.Policy;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;
using Xunit;

namespace HackerOs.Samples.ServiceApp.Tests;

/// <summary>
/// Unit tests for <see cref="SampleTickerService"/> verifying service startup, event publishing,
/// session cancellation observation, bounded cleanup, and zero volatile state retention across restart (P2-SVC-003).
/// </summary>
public sealed class SampleTickerServiceTests
{
    private static readonly AppManifest ServiceManifest = new()
    {
        SchemaVersion = 1,
        Id = "org.hackeros.samples.service-app",
        Name = "Sample Ticker Service",
        Version = "1.0.0",
        PublisherId = "pub.hackeros",
        Description = "Deterministic status and ticker background service for KazoOS sessions",
        Kind = AppKind.Service,
        EntryPoint = new AppEntryPointManifest("HackerOs.Samples.ServiceApp.dll", "HackerOs.Samples.ServiceApp.SampleTickerService"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Hidden, []),
        Capabilities = ["notifications.post"],
        Resources = new AppResourceProfileManifest(0.01, 0.05, 0.02, 0.05, 0.0, 0.0, 0.0, 0.0),
        AutoStart = true
    };

    [Fact]
    public void Manifest_HasServiceKindAndAutoStart()
    {
        SampleTickerService service = new();
        Assert.Equal(AppKind.Service, service.Manifest.Kind);
        Assert.True(service.Manifest.AutoStart);
    }

    [Fact]
    public async Task RunAsync_ExecutesTicksAndPublishesEvents_UntilCancelled()
    {
        // Arrange
        SampleTickerService service = new(ServiceManifest, TimeSpan.FromMilliseconds(10));
        InMemoryEventBus eventBus = new();
        InMemoryTopicMessageBus topicBus = new(new CapabilityGrantRepository());
        List<SampleTickerEvent> publishedEvents = [];
        PublisherIdentity testSubscriber = new("test.harness", "test-user", "0");
        using IDisposable sub = topicBus.Subscribe<SampleTickerEvent>(
            SampleTickerTopics.Ticked, testSubscriber, message => publishedEvents.Add(message.Payload));

        TestExecutionContext context = new(ServiceManifest, eventBus, topicBus);
        using CancellationTokenSource cts = new();

        // Act – run service loop in task and cancel after short delay
        Task runTask = service.RunAsync(context, cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await runTask;

        // Assert
        Assert.True(service.TickCount > 0);
        Assert.NotEmpty(publishedEvents);
        Assert.Equal(service.TickCount, publishedEvents.Count);
        Assert.True(service.IsStopping);
    }

    [Fact]
    public async Task StopAsync_ResetsVolatileState_ForFreshRestart()
    {
        // Arrange
        SampleTickerService service = new(ServiceManifest, TimeSpan.FromMilliseconds(10));
        InMemoryEventBus eventBus = new();
        InMemoryTopicMessageBus topicBus = new(new CapabilityGrantRepository());
        TestExecutionContext context = new(ServiceManifest, eventBus, topicBus);
        using CancellationTokenSource cts = new();

        Task runTask = service.RunAsync(context, cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await runTask;

        Assert.True(service.TickCount > 0);

        // Act – simulate ecosystem calling StopAsync for cleanup
        await service.StopAsync(ServiceStopReason.Logout, CancellationToken.None);

        // Assert – volatile tick count reset to 0 (P2-SVC-002, P2-SVC-003)
        Assert.Equal(0, service.TickCount);
    }

    [Fact]
    public async Task FreshRestart_StartsFromZero()
    {
        // Arrange
        SampleTickerService service = new(ServiceManifest, TimeSpan.FromMilliseconds(5));
        InMemoryEventBus eventBus = new();
        InMemoryTopicMessageBus topicBus = new(new CapabilityGrantRepository());
        TestExecutionContext context = new(ServiceManifest, eventBus, topicBus);

        // Session 1
        using (CancellationTokenSource cts1 = new())
        {
            Task task1 = service.RunAsync(context, cts1.Token);
            await Task.Delay(50);
            cts1.Cancel();
            await task1;
        }

        await service.StopAsync(ServiceStopReason.Logout, CancellationToken.None);
        Assert.Equal(0, service.TickCount);

        // Session 2 – fresh start
        using (CancellationTokenSource cts2 = new())
        {
            Task task2 = service.RunAsync(context, cts2.Token);
            await Task.Delay(50);
            cts2.Cancel();
            await task2;
        }

        await service.StopAsync(ServiceStopReason.Logout, CancellationToken.None);

        // Assert – tick count on session 2 runs fresh and resets
        Assert.Equal(0, service.TickCount);
    }

    // ──────────────────────────── Test Mocks / Stubs ─────────────────────────────

    internal sealed class TestExecutionContext : IAppExecutionContext
    {
        public TestExecutionContext(AppManifest manifest, InMemoryEventBus eventBus, InMemoryTopicMessageBus topicBus)
        {
            Manifest = manifest;
            Events = new AppEventGateway(eventBus, topicBus, manifest.Id, "user-1", "100");
            Clock = new TestClockGateway();
            Logging = new TestLoggingGateway();
        }

        public AppManifest Manifest { get; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user-1";
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>(StringComparer.Ordinal) { "notifications.post" };
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(100);
        public CancellationToken CancellationToken => CancellationToken.None;

        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events { get; }
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging { get; }
        public IAppClockGateway Clock { get; }
        public IAppProcessGateway Processes => throw new NotImplementedException();
    }

    private sealed class TestClockGateway : IAppClockGateway
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long CurrentTick => Environment.TickCount64;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
            Task.Delay(delay, cancellationToken);

        public IDisposable Schedule(TimeSpan delay, Action callback) => throw new NotImplementedException();
    }

    private sealed class TestLoggingGateway : IAppLoggingGateway
    {
        public void Log(DiagnosticSeverity severity, string message, IReadOnlyDictionary<string, string>? properties = null)
        {
            // No-op diagnostic sink for tests
        }
    }
}
