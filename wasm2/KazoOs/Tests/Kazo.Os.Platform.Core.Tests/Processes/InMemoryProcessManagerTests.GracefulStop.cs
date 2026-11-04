using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>ADR 0069, sections 1 and 3: stop request, deadline and kill escalation in the process manager.</summary>
public sealed partial class InMemoryProcessManagerTests
{
    [Fact]
    public async Task StopAsync_moves_to_stopping_then_raises_the_request_then_cancels_the_token()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.MarkRunning(process.Pid);
        ProcessControlBlock control = fixture.Manager.GetControlBlock(process.Pid);
        CancellationToken token = fixture.Manager.GetCancellationToken(process.Pid);
        List<string> sequence = [];
        using IDisposable states = fixture.EventBus.Subscribe<ProcessStateChangedEvent>(e => sequence.Add(e.NewState.ToString()));
        control.StopRequested += request =>
        {
            Assert.False(token.IsCancellationRequested);
            sequence.Add($"StopRequested:{request.Reason}");
        };
        using CancellationTokenRegistration cancelled = token.Register(() => sequence.Add("Cancelled"));
        DateTimeOffset requestedAt = fixture.Clock.UtcNow;

        Task<ProcessRecord> stop = fixture.Manager.StopAsync(process.Pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated);
        fixture.Manager.Complete(process.Pid, exitCode: 0);
        ProcessRecord stopped = await stop;

        Assert.Equal(["Stopping", "StopRequested:Terminated", "Cancelled", "Stopped"], sequence);
        Assert.Equal(new ProcessStopRequest(process.Pid, ProcessExitReason.Terminated, requestedAt, requestedAt.AddSeconds(5)), control.StopRequest);
        Assert.Equal(ProcessExitReason.Completed, stopped.ExitReason);
        Assert.False(control.IsKilled);
    }

    [Fact]
    public async Task Overlapping_stops_raise_the_request_once_and_share_one_outcome()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.MarkRunning(process.Pid);
        ProcessControlBlock control = fixture.Manager.GetControlBlock(process.Pid);
        List<ProcessStopRequest> requests = [];
        control.StopRequested += requests.Add;

        Task<ProcessRecord> first = fixture.Manager.StopAsync(process.Pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated);
        Task<ProcessRecord> second = fixture.Manager.StopAsync(process.Pid, TimeSpan.FromSeconds(30), ProcessExitReason.CloseRequested);
        fixture.Manager.Complete(process.Pid, exitCode: 0);

        ProcessStopRequest request = Assert.Single(requests);
        Assert.Equal(ProcessExitReason.Terminated, request.Reason);
        Assert.Same(await first, await second);
    }

    [Fact]
    public async Task A_stop_whose_deadline_passes_kills_the_process_for_good()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.MarkRunning(process.Pid);
        ProcessControlBlock control = fixture.Manager.GetControlBlock(process.Pid);

        Task<ProcessRecord> stop = fixture.Manager.StopAsync(process.Pid, TimeSpan.FromSeconds(5), ProcessExitReason.Terminated);
        fixture.Clock.Advance(4);
        Assert.False(stop.IsCompleted);
        Assert.False(control.IsKilled);
        fixture.Clock.Advance(2);
        ProcessRecord stopped = await stop;

        Assert.Equal(ProcessExitReason.Timeout, stopped.ExitReason);
        Assert.True(control.IsKilled);
        Assert.True(control.Killed.IsCompleted);
        Assert.Throws<Kazo.Os.AppSdk.Diagnostics.HackProcessKilledException>(control.ThrowIfKilled);
    }

    [Fact]
    public void Kill_is_a_sigkill_for_the_process_and_its_descendants_without_a_stop_request()
    {
        Fixture fixture = new();
        ProcessRecord parent = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        ProcessRecord child = fixture.Manager.Start(fixture.RequestFor("com.hackeros.child", parent.Pid));
        ProcessControlBlock parentControl = fixture.Manager.GetControlBlock(parent.Pid);
        ProcessControlBlock childControl = fixture.Manager.GetControlBlock(child.Pid);
        bool requested = false;
        parentControl.StopRequested += _ => requested = true;

        fixture.Manager.Kill(parent.Pid);

        Assert.False(requested);
        Assert.Null(parentControl.StopRequest);
        Assert.True(parentControl.IsKilled);
        Assert.True(childControl.IsKilled);
    }

    [Fact]
    public void Ending_a_process_for_another_reason_keeps_it_reachable_for_its_own_cleanup()
    {
        Fixture fixture = new();
        ProcessRecord window = fixture.Manager.Start(fixture.RequestFor("com.hackeros.notes"));
        ProcessRecord child = fixture.Manager.Start(fixture.RequestFor("com.hackeros.child", window.Pid));
        ProcessControlBlock windowControl = fixture.Manager.GetControlBlock(window.Pid);
        ProcessControlBlock childControl = fixture.Manager.GetControlBlock(child.Pid);

        // The orchestrator closes a window this way: not a SIGKILL.
        fixture.Manager.Kill(window.Pid, ProcessExitReason.CloseRequested);

        Assert.False(windowControl.IsKilled);
        Assert.False(childControl.IsKilled);
    }

    [Fact]
    public void A_throwing_stop_handler_does_not_keep_the_others_from_running()
    {
        ProcessControlBlock control = new(ProcessId.FromInt64(7));
        List<string> calls = [];
        control.StopRequested += _ => throw new InvalidOperationException("boom");
        control.StopRequested += _ => calls.Add("second");
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        Assert.True(control.TryRequestStop(new ProcessStopRequest(control.Pid, ProcessExitReason.Terminated, now, now.AddSeconds(5))));
        Assert.False(control.TryRequestStop(new ProcessStopRequest(control.Pid, ProcessExitReason.Shutdown, now, now.AddSeconds(1))));
        control.RaiseStopRequested();
        control.RaiseStopRequested();

        Assert.Equal(["second"], calls);
        Assert.Equal(ProcessExitReason.Terminated, control.StopRequest!.Reason);
    }
}
