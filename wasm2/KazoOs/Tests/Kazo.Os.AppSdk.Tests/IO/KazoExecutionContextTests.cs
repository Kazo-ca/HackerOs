using Kazo.Os.AppSdk.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

public sealed class KazoExecutionContextTests
{
    [Fact]
    public void Current_throws_outside_any_scope()
    {
        Assert.Null(KazoExecutionContext.CurrentOrNull);
        Assert.Throws<KazoExecutionContextMissingException>(() => KazoExecutionContext.Current);
        Assert.Throws<KazoExecutionContextMissingException>(() => KazoExecutionContext.WorkingDirectory);
    }

    [Fact]
    public async Task File_api_throws_outside_any_scope_instead_of_borrowing_an_identity()
    {
        await Assert.ThrowsAsync<KazoExecutionContextMissingException>(() => KazoFile.ReadAllTextAsync("/etc/passwd"));
        Assert.Throws<KazoExecutionContextMissingException>(() => KazoPath.GetFullPath("relative.txt"));
    }

    [Fact]
    public void Enter_defaults_the_working_directory_to_the_users_home()
    {
        MinimalAppExecutionContext context = KazoTestHost.CreateContext(new FakeAppFileSystemGateway());

        using (KazoExecutionContext.Enter(context))
        {
            Assert.Same(context, KazoExecutionContext.Current);
            Assert.Equal(KazoTestHost.Home, KazoExecutionContext.WorkingDirectory);
        }
    }

    [Fact]
    public void Enter_canonicalizes_and_validates_the_working_directory()
    {
        MinimalAppExecutionContext context = KazoTestHost.CreateContext(new FakeAppFileSystemGateway());

        using (KazoExecutionContext.Enter(context, "/tmp/../var//log/"))
        {
            Assert.Equal("/var/log", KazoExecutionContext.WorkingDirectory);
        }

        Assert.Throws<ArgumentException>(() => KazoExecutionContext.Enter(context, "relative"));
    }

    [Fact]
    public void Dispose_restores_the_previous_context_and_scopes_nest()
    {
        MinimalAppExecutionContext outer = KazoTestHost.CreateContext(new FakeAppFileSystemGateway());
        MinimalAppExecutionContext inner = KazoTestHost.CreateContext(new FakeAppFileSystemGateway());

        using (KazoExecutionContext.Enter(outer, "/outer"))
        {
            IDisposable innerScope = KazoExecutionContext.Enter(inner, "/inner");
            Assert.Same(inner, KazoExecutionContext.Current);
            Assert.Equal("/inner", KazoExecutionContext.WorkingDirectory);

            innerScope.Dispose();
            innerScope.Dispose(); // idempotent
            Assert.Same(outer, KazoExecutionContext.Current);
            Assert.Equal("/outer", KazoExecutionContext.WorkingDirectory);
        }

        Assert.Null(KazoExecutionContext.CurrentOrNull);
    }

    [Fact]
    public async Task Context_flows_across_await_Task_Run_and_timers()
    {
        MinimalAppExecutionContext context = KazoTestHost.CreateContext(new FakeAppFileSystemGateway());

        using (KazoExecutionContext.Enter(context))
        {
            await Task.Yield();
            Assert.Same(context, KazoExecutionContext.CurrentOrNull);

            Assert.Same(context, await Task.Run(() => KazoExecutionContext.CurrentOrNull));

            TaskCompletionSource<object?> fromTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using Timer timer = new(_ => fromTimer.TrySetResult(KazoExecutionContext.CurrentOrNull), null, 1, Timeout.Infinite);
            Assert.Same(context, await fromTimer.Task);
        }
    }

    [Fact]
    public async Task A_scope_entered_in_an_async_method_does_not_leak_to_its_caller()
    {
        MinimalAppExecutionContext context = KazoTestHost.CreateContext(new FakeAppFileSystemGateway());

        await EnterWithoutDisposingAsync(context);

        Assert.Null(KazoExecutionContext.CurrentOrNull);
    }

    [Fact]
    public async Task Service_app_runs_and_stops_inside_its_own_scope()
    {
        MinimalAppExecutionContext context = KazoTestHost.CreateContext(new FakeAppFileSystemGateway());
        RecordingService service = new();

        await service.RunAsync(context, CancellationToken.None);
        await service.StopAsync(ServiceStopReason.Shutdown, CancellationToken.None);

        Assert.Same(context, service.SeenWhileRunning);
        Assert.Equal(KazoTestHost.Home, service.WorkingDirectoryWhileRunning);
        Assert.Same(context, service.SeenWhileStopping);
        Assert.Null(KazoExecutionContext.CurrentOrNull);
    }

    private static async Task EnterWithoutDisposingAsync(IAppExecutionContext context)
    {
        await Task.Yield();
        KazoExecutionContext.Enter(context);
    }

    private sealed class RecordingService() : ServiceAppBase(KazoTestHost.Manifest with { Kind = Kazo.Os.App.Abstractions.AppKind.Service })
    {
        public IAppExecutionContext? SeenWhileRunning { get; private set; }
        public string? WorkingDirectoryWhileRunning { get; private set; }
        public IAppExecutionContext? SeenWhileStopping { get; private set; }

        protected override async Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
        {
            await Task.Yield();
            SeenWhileRunning = KazoExecutionContext.CurrentOrNull;
            WorkingDirectoryWhileRunning = KazoExecutionContext.WorkingDirectory;
        }

        protected override ValueTask OnStoppingAsync(ServiceStopReason reason, CancellationToken cancellationToken)
        {
            SeenWhileStopping = KazoExecutionContext.CurrentOrNull;
            return ValueTask.CompletedTask;
        }
    }
}
