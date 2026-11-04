using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.AppSdk.Tests;

public sealed class AppSdkLifecycleTests
{
    [Fact]
    public async Task Terminal_app_executes_without_a_terminal_renderer()
    {
        EchoTerminalApp app = new(CreateManifest(AppKind.Terminal) with
        {
            Terminal = new TerminalCommandManifest("echo", [], "echo [text]")
        });
        StringWriter output = new();
        TerminalExecutionContext context = new(
            CreateContext(app.Manifest),
            ["hello", "world"],
            TextReader.Null,
            output,
            TextWriter.Null,
            "/home/user",
            new Dictionary<string, string>());

        int exitCode = await app.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal($"hello world{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Service_app_observes_session_cancellation_without_resume_state()
    {
        WaitingServiceApp app = new(CreateManifest(AppKind.Service));
        using CancellationTokenSource session = new();

        Task running = app.RunAsync(CreateContext(app.Manifest), session.Token);
        session.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task Service_stop_hook_receives_the_stop_reason()
    {
        WaitingServiceApp app = new(CreateManifest(AppKind.Service));

        await app.StopAsync(ServiceStopReason.Shutdown, CancellationToken.None);

        Assert.Equal(ServiceStopReason.Shutdown, app.LastStopReason);
    }

    [Fact]
    public void App_base_rejects_a_manifest_for_a_different_app_kind()
    {
        AppManifest windowManifest = CreateManifest(AppKind.Window);

        Assert.Throws<ArgumentException>(() => new WaitingServiceApp(windowManifest));
    }

    private static AppManifest CreateManifest(AppKind kind) => new()
    {
        Id = $"org.hackeros.test-{kind.ToString().ToLowerInvariant()}",
        Name = $"Test {kind}",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "App SDK lifecycle test application.",
        Kind = kind,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Tests", $"Test{kind}App"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    private static IAppExecutionContext CreateContext(AppManifest manifest) =>
        new TestAppExecutionContext(manifest);

    private sealed class EchoTerminalApp(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(
            TerminalExecutionContext context,
            CancellationToken cancellationToken)
        {
            await context.StandardOutput.WriteLineAsync(
                string.Join(' ', context.Arguments).AsMemory(),
                cancellationToken);
            return 0;
        }
    }

    private sealed class WaitingServiceApp(AppManifest manifest) : ServiceAppBase(manifest)
    {
        public ServiceStopReason? LastStopReason { get; private set; }

        protected override Task RunCoreAsync(
            IAppExecutionContext context,
            CancellationToken sessionCancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellationToken);

        protected override ValueTask OnStoppingAsync(
            ServiceStopReason reason,
            CancellationToken cancellationToken)
        {
            LastStopReason = reason;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestAppExecutionContext(AppManifest manifest) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = manifest;

        public Guid InstanceId { get; } = Guid.NewGuid();

        public string UserId => "user";

        public AppAuthority UserAuthority => AppAuthority.User;

        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>();

        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());

        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);

        public CancellationToken CancellationToken => CancellationToken.None;

        public ICapabilityChecker Capabilities => throw new NotSupportedException();

        public IAppFileSystemGateway FileSystem => throw new NotSupportedException();


        public IAppEventGateway Events => throw new NotSupportedException();

        public IAppNotificationGateway Notifications => throw new NotSupportedException();

        public IAppLoggingGateway Logging => throw new NotSupportedException();


        public IAppClockGateway Clock => throw new NotSupportedException();

        public IAppProcessGateway Processes => throw new NotSupportedException();
    }
}