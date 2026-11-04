using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Lifecycle;

/// <summary>
/// ADR 0069 end to end on a real kernel: a command receives <c>StopRequested</c> before its token,
/// <see cref="KazoProcess.CheckpointAsync"/> stops a busy loop, and a process that ignores its token
/// is killed at the deadline with its continuations, timers and gateways cut off.
/// </summary>
public sealed partial class AppLifecycleOrchestratorTests
{
    [Fact]
    public async Task A_terminated_command_hears_the_request_with_its_deadline_before_its_token_and_ends_by_itself()
    {
        StopProbe.Reset();
        Fixture fixture = new(ProbeManifest("org.hackeros.polite", typeof(PoliteTerminalApp)));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        Task<AppLaunchResult> launch = fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.polite", principal, []));
        await StopProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        DateTimeOffset requestedAt = fixture.Clock.UtcNow;
        ProcessRecord stopped = await fixture.Manager.StopAsync(StopProbe.Pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated);
        AppLaunchResult result = await launch.WaitAsync(TimeSpan.FromSeconds(10));

        KazoStopRequestedEventArgs request = Assert.IsType<KazoStopRequestedEventArgs>(StopProbe.Request);
        Assert.Equal(ProcessExitReason.Terminated, request.Reason);
        Assert.Equal(requestedAt, request.RequestedAt);
        Assert.Equal(requestedAt.AddSeconds(5), request.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(5), request.GracePeriod);
        Assert.False(StopProbe.TokenCancelledAtRequest);
        Assert.True(StopProbe.HandlerRanAsTheProcess);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(ProcessExitReason.Completed, stopped.ExitReason);
    }

    [Fact]
    public Task A_busy_loop_that_checkpoints_gives_the_thread_back_and_stops_on_sigterm() =>
        // Off xUnit's synchronization context: its few shared workers would spend the spin on the
        // slices of this test instead of the tests running beside it.
        Task.Run(BusyLoopStopsOnSigtermAsync);

    private static async Task BusyLoopStopsOnSigtermAsync()
    {
        StopProbe.Reset();
        Fixture fixture = new(ProbeManifest("org.hackeros.cruncher", typeof(CheckpointingTerminalApp)));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        Task<AppLaunchResult> launch = fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.cruncher", principal, []));
        await StopProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The loop never awaits anything but checkpoints: the test only gets here because they yield.
        await WaitUntilAsync(() => Volatile.Read(ref StopProbe.Yields) > 0);
        ProcessRecord stopped = await fixture.Manager.StopAsync(StopProbe.Pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated);
        AppLaunchResult result = await launch.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(ProcessExitReason.Completed, stopped.ExitReason);
        Assert.True(Volatile.Read(ref StopProbe.Iterations) > Volatile.Read(ref StopProbe.Yields));
    }

    [Fact]
    public async Task A_command_that_ignores_its_token_is_killed_at_the_deadline_and_cut_off_from_the_os()
    {
        StopProbe.Reset();
        Fixture fixture = new(ProbeManifest("org.hackeros.stubborn", typeof(StubbornTerminalApp)));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        Task<AppLaunchResult> launch = fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.stubborn", principal, []));
        await StopProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<ProcessRecord> stop = fixture.Manager.StopAsync(StopProbe.Pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated);
        fixture.Clock.Advance(4);
        Assert.False(stop.IsCompleted);
        fixture.Clock.Advance(2);
        ProcessRecord stopped = await stop.WaitAsync(TimeSpan.FromSeconds(10));
        AppLaunchResult result = await launch.WaitAsync(TimeSpan.FromSeconds(10));

        // Its awaited work completes after the kill: the continuation is thrown away.
        StopProbe.Release.SetResult();
        fixture.Clock.Advance(20);
        await Task.Delay(50);

        Assert.Equal(ProcessExitReason.Timeout, stopped.ExitReason);
        Assert.Equal(137, result.ExitCode);
        Assert.NotNull(StopProbe.Request);
        Assert.False(StopProbe.Resumed);
        Assert.False(StopProbe.TimerFired);
        Assert.Throws<KazoProcessKilledException>(() => result.Context!.FileSystem);
        Assert.Throws<KazoProcessKilledException>(() => result.Context!.Clock);
        Assert.Equal(StopProbe.Pid, result.Context!.ProcessId);
    }

    [Fact]
    public async Task Sigkill_skips_the_stop_request_and_ends_the_command_at_once()
    {
        StopProbe.Reset();
        Fixture fixture = new(ProbeManifest("org.hackeros.stubborn", typeof(StubbornTerminalApp)));
        AuthenticatedPrincipal principal = await fixture.LoginAsync();

        Task<AppLaunchResult> launch = fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.stubborn", principal, []));
        await StopProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Manager.Kill(StopProbe.Pid);
        AppLaunchResult result = await launch.WaitAsync(TimeSpan.FromSeconds(10));
        StopProbe.Release.SetResult();
        await Task.Delay(50);

        Assert.Equal(137, result.ExitCode);
        Assert.Null(StopProbe.Request);
        Assert.False(StopProbe.Resumed);
        Assert.Equal(ProcessExitReason.Killed, result.Process!.ExitReason);
    }

    [Fact]
    public async Task A_service_hears_logout_with_its_grace_period_before_it_is_stopped()
    {
        StopProbe.Reset();
        Fixture fixture = new(WaiterManifest() with
        {
            Id = "org.hackeros.listening-service",
            EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", typeof(ListeningServiceApp).FullName!)
        });
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.listening-service", principal, []));
        await StopProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        DateTimeOffset now = fixture.Clock.UtcNow;

        await fixture.Orchestrator.StopAllAsync(ProcessExitReason.Logout);

        KazoStopRequestedEventArgs request = Assert.IsType<KazoStopRequestedEventArgs>(StopProbe.Request);
        Assert.Equal(ProcessExitReason.Logout, request.Reason);
        Assert.Equal(now.AddSeconds(5), request.Deadline);
        Assert.False(StopProbe.TokenCancelledAtRequest);
        Assert.False(fixture.Manager.TryGetActive(StopProbe.Pid, out _));
        // Announced first, then stopped by the ecosystem: OnStoppingAsync runs once.
        Assert.Equal([ServiceStopReason.Logout], StopProbe.ServiceStops);
    }

    [Fact]
    public async Task A_killed_service_runs_its_cleanup_once_with_the_terminated_reason()
    {
        StopProbe.Reset();
        Fixture fixture = new(WaiterManifest() with
        {
            Id = "org.hackeros.listening-service",
            EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", typeof(ListeningServiceApp).FullName!)
        });
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.listening-service", principal, []));
        await StopProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        ProcessRecord stopped = await fixture.Manager.StopAsync(StopProbe.Pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([ServiceStopReason.Terminated], StopProbe.ServiceStops);
        Assert.NotEqual(ProcessExitReason.Timeout, stopped.ExitReason);
    }

    [Fact]
    public async Task A_terminated_window_closes_once_its_deferred_work_is_done()
    {
        Fixture fixture = new(SingletonWindowManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        AppLaunchResult window = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.notes", principal, []));
        TaskCompletionSource draftWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Context!.StopRequested += (_, e) => e.Defer(draftWritten.Task);

        Task<ProcessRecord> stop = fixture.Manager.StopAsync(window.Process!.Pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated);
        await Task.Delay(50);
        Assert.False(stop.IsCompleted);
        Assert.True(fixture.Orchestrator.TryGetRunningInstance(window.Process.Pid, out _, out _));
        draftWritten.SetResult();
        ProcessRecord stopped = await stop.WaitAsync(TimeSpan.FromSeconds(10));

        // Ended by itself with the request's reason: not timed out, not killed.
        Assert.Equal(ProcessExitReason.Terminated, stopped.ExitReason);
        Assert.False(fixture.Orchestrator.TryGetRunningInstance(window.Process.Pid, out _, out _));
    }

    [Fact]
    public async Task A_window_whose_deferred_work_overruns_the_deadline_is_killed()
    {
        Fixture fixture = new(SingletonWindowManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        AppLaunchResult window = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.notes", principal, []));
        window.Context!.StopRequested += (_, e) => e.Defer(new TaskCompletionSource().Task);

        Task<ProcessRecord> stop = fixture.Manager.StopAsync(window.Process!.Pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated);
        fixture.Clock.Advance(6);
        ProcessRecord stopped = await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ProcessExitReason.Timeout, stopped.ExitReason);
        Assert.Throws<KazoProcessKilledException>(() => window.Context.FileSystem);
    }

    [Fact]
    public async Task Logout_announces_itself_to_windows_but_a_user_close_does_not()
    {
        Fixture fixture = new(SingletonWindowManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        AppLaunchResult closed = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.notes", principal, []));
        List<ProcessExitReason> heardByClosed = [];
        closed.Context!.StopRequested += (_, e) => heardByClosed.Add(e.Reason);
        await fixture.Orchestrator.StopAsync(closed.Process!.Pid, ProcessExitReason.CloseRequested);

        AppLaunchResult open = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.notes", principal, []));
        List<ProcessExitReason> heardByOpen = [];
        open.Context!.StopRequested += (_, e) => heardByOpen.Add(e.Reason);
        await fixture.Orchestrator.StopAllAsync(ProcessExitReason.Logout);

        Assert.Empty(heardByClosed);
        Assert.Equal([ProcessExitReason.Logout], heardByOpen);
        Assert.Equal(ProcessExitReason.Logout, fixture.Manager.GetHistory().Last(r => r.Pid == open.Process!.Pid).ExitReason);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "Timed out waiting for the probe.");
            await Task.Delay(5);
        }
    }

    private static AppManifest ProbeManifest(string id, Type entryPoint) => EchoManifest() with
    {
        Id = id,
        Name = id,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", entryPoint.FullName!),
        Terminal = new TerminalCommandManifest(id[(id.LastIndexOf('.') + 1)..], [], id)
    };

    /// <summary>What the probe commands observed; the orchestrator constructs them, so the state is static.</summary>
    private static class StopProbe
    {
        public static ProcessId Pid;
        public static TaskCompletionSource Started = new();
        public static TaskCompletionSource Release = new();
        public static KazoStopRequestedEventArgs? Request;
        public static bool TokenCancelledAtRequest;
        public static bool HandlerRanAsTheProcess;
        public static bool Resumed;
        public static bool TimerFired;
        public static long Iterations;
        public static long Yields;
        public static readonly List<ServiceStopReason> ServiceStops = [];

        public static void Reset()
        {
            Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new TaskCompletionSource();
            Request = null;
            TokenCancelledAtRequest = false;
            HandlerRanAsTheProcess = false;
            Resumed = false;
            TimerFired = false;
            Iterations = 0;
            Yields = 0;
            lock (ServiceStops)
            {
                ServiceStops.Clear();
            }
        }

        public static void Listen()
        {
            KazoCurrentProcess current = KazoProcess.Current;
            Pid = ProcessId.FromInt64(current.Id);
            current.StopRequested += (_, e) =>
            {
                Request = e;
                TokenCancelledAtRequest = current.StopToken.IsCancellationRequested;
                HandlerRanAsTheProcess = KazoExecutionContext.CurrentOrNull?.ProcessId == Pid;
            };
        }
    }

    /// <summary>Waits on its token, then exits with 3.</summary>
    private sealed class PoliteTerminalApp(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            StopProbe.Listen();
            StopProbe.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Asked to stop: clean up and leave.
            }

            return 3;
        }
    }

    /// <summary>Spins forever, calling only <see cref="KazoProcess.CheckpointAsync"/>.</summary>
    private sealed class CheckpointingTerminalApp(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            StopProbe.Listen();
            StopProbe.Started.TrySetResult();
            while (true)
            {
                ValueTask checkpoint = KazoProcess.CheckpointAsync(CancellationToken.None);
                if (!checkpoint.IsCompleted)
                {
                    Interlocked.Increment(ref StopProbe.Yields);
                }

                await checkpoint;
                Interlocked.Increment(ref StopProbe.Iterations);
            }
        }
    }

    /// <summary>A service that listens for the stop request, then waits on its token.</summary>
    private sealed class ListeningServiceApp(AppManifest manifest) : ServiceAppBase(manifest)
    {
        protected override Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
        {
            StopProbe.Listen();
            StopProbe.Started.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellationToken);
        }

        protected override ValueTask OnStoppingAsync(ServiceStopReason reason, CancellationToken cancellationToken)
        {
            lock (StopProbe.ServiceStops)
            {
                StopProbe.ServiceStops.Add(reason);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Ignores its token: waits on work the test releases, and has a timer pending.</summary>
    private sealed class StubbornTerminalApp(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            StopProbe.Listen();
            context.App.Clock.Schedule(TimeSpan.FromSeconds(10), () => StopProbe.TimerFired = true);
            StopProbe.Started.TrySetResult();
            await StopProbe.Release.Task;
            StopProbe.Resumed = true;
            return 0;
        }
    }
}
