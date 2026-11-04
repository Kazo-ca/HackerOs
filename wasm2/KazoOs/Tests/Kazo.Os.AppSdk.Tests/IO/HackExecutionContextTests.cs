using Kazo.Os.AppSdk.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

public sealed class HackExecutionContextTests
{
    [Fact]
    public void Current_throws_outside_any_scope()
    {
        Assert.Null(HackExecutionContext.CurrentOrNull);
        Assert.Throws<HackExecutionContextMissingException>(() => HackExecutionContext.Current);
        Assert.Throws<HackExecutionContextMissingException>(() => HackExecutionContext.WorkingDirectory);
    }

    [Fact]
    public async Task File_api_throws_outside_any_scope_instead_of_borrowing_an_identity()
    {
        await Assert.ThrowsAsync<HackExecutionContextMissingException>(() => HackFile.ReadAllTextAsync("/etc/passwd"));
        Assert.Throws<HackExecutionContextMissingException>(() => HackPath.GetFullPath("relative.txt"));
    }

    [Fact]
    public void Enter_defaults_the_working_directory_to_the_users_home()
    {
        MinimalAppExecutionContext context = HackTestHost.CreateContext(new FakeAppFileSystemGateway());

        using (HackExecutionContext.Enter(context))
        {
            Assert.Same(context, HackExecutionContext.Current);
            Assert.Equal(HackTestHost.Home, HackExecutionContext.WorkingDirectory);
        }
    }

    [Fact]
    public void Enter_canonicalizes_and_validates_the_working_directory()
    {
        MinimalAppExecutionContext context = HackTestHost.CreateContext(new FakeAppFileSystemGateway());

        using (HackExecutionContext.Enter(context, "/tmp/../var//log/"))
        {
            Assert.Equal("/var/log", HackExecutionContext.WorkingDirectory);
        }

        Assert.Throws<ArgumentException>(() => HackExecutionContext.Enter(context, "relative"));
    }

    [Fact]
    public void Dispose_restores_the_previous_context_and_scopes_nest()
    {
        MinimalAppExecutionContext outer = HackTestHost.CreateContext(new FakeAppFileSystemGateway());
        MinimalAppExecutionContext inner = HackTestHost.CreateContext(new FakeAppFileSystemGateway());

        using (HackExecutionContext.Enter(outer, "/outer"))
        {
            IDisposable innerScope = HackExecutionContext.Enter(inner, "/inner");
            Assert.Same(inner, HackExecutionContext.Current);
            Assert.Equal("/inner", HackExecutionContext.WorkingDirectory);

            innerScope.Dispose();
            innerScope.Dispose(); // idempotent
            Assert.Same(outer, HackExecutionContext.Current);
            Assert.Equal("/outer", HackExecutionContext.WorkingDirectory);
        }

        Assert.Null(HackExecutionContext.CurrentOrNull);
    }

    [Fact]
    public async Task Context_flows_across_await_Task_Run_and_timers()
    {
        MinimalAppExecutionContext context = HackTestHost.CreateContext(new FakeAppFileSystemGateway());

        using (HackExecutionContext.Enter(context))
        {
            await Task.Yield();
            Assert.Same(context, HackExecutionContext.CurrentOrNull);

            Assert.Same(context, await Task.Run(() => HackExecutionContext.CurrentOrNull));

            TaskCompletionSource<object?> fromTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using Timer timer = new(_ => fromTimer.TrySetResult(HackExecutionContext.CurrentOrNull), null, 1, Timeout.Infinite);
            Assert.Same(context, await fromTimer.Task);
        }
    }

    [Fact]
    public async Task A_scope_entered_in_an_async_method_does_not_leak_to_its_caller()
    {
        MinimalAppExecutionContext context = HackTestHost.CreateContext(new FakeAppFileSystemGateway());

        await EnterWithoutDisposingAsync(context);

        Assert.Null(HackExecutionContext.CurrentOrNull);
    }

    [Fact]
    public async Task Service_app_runs_and_stops_inside_its_own_scope()
    {
        MinimalAppExecutionContext context = HackTestHost.CreateContext(new FakeAppFileSystemGateway());
        RecordingService service = new();

        await service.RunAsync(context, CancellationToken.None);
        await service.StopAsync(ServiceStopReason.Shutdown, CancellationToken.None);

        Assert.Same(context, service.SeenWhileRunning);
        Assert.Equal(HackTestHost.Home, service.WorkingDirectoryWhileRunning);
        Assert.Same(context, service.SeenWhileStopping);
        Assert.Null(HackExecutionContext.CurrentOrNull);
    }

    private static async Task EnterWithoutDisposingAsync(IAppExecutionContext context)
    {
        await Task.Yield();
        HackExecutionContext.Enter(context);
    }

    private sealed class RecordingService() : ServiceAppBase(HackTestHost.Manifest with { Kind = Kazo.Os.App.Abstractions.AppKind.Service })
    {
        public IAppExecutionContext? SeenWhileRunning { get; private set; }
        public string? WorkingDirectoryWhileRunning { get; private set; }
        public IAppExecutionContext? SeenWhileStopping { get; private set; }

        protected override async Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
        {
            await Task.Yield();
            SeenWhileRunning = HackExecutionContext.CurrentOrNull;
            WorkingDirectoryWhileRunning = HackExecutionContext.WorkingDirectory;
        }

        protected override ValueTask OnStoppingAsync(ServiceStopReason reason, CancellationToken cancellationToken)
        {
            SeenWhileStopping = HackExecutionContext.CurrentOrNull;
            return ValueTask.CompletedTask;
        }
    }
}
