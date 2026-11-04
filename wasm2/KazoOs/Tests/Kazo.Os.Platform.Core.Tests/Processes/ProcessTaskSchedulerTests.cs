using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>ADR 0069, section 3: the per-process scheduler runs a process's continuations and drops them once it is killed.</summary>
public sealed class ProcessTaskSchedulerTests
{
    [Fact]
    public async Task The_entry_point_and_its_continuations_run_as_the_process()
    {
        ProcessControlBlock control = new(ProcessId.FromInt64(12));
        SynchronizationContext? callerContext = SynchronizationContext.Current;
        List<bool> asProcess = [];

        Task<int> run = control.RunAsync(async () =>
        {
            asProcess.Add(IsRunningAs(control));
            await Task.Yield();
            asProcess.Add(IsRunningAs(control));
            await Task.Factory.StartNew(() => asProcess.Add(IsRunningAs(control)));
            return 42;
        });
        // The entry point ran inline, then the caller got its own context back.
        Assert.Same(callerContext, SynchronizationContext.Current);

        Assert.Equal(42, await run);
        Assert.Equal([true, true, true], asProcess);
    }

    [Fact]
    public async Task A_killed_process_loses_its_pending_continuations_and_the_wait_ends_at_once()
    {
        ProcessControlBlock control = new(ProcessId.FromInt64(13));
        TaskCompletionSource release = new();
        bool resumed = false;

        Task<int> run = control.RunAsync(async () =>
        {
            await release.Task;
            resumed = true;
            return 0;
        });
        control.MarkKilled();
        release.SetResult();

        HackProcessKilledException killed = await Assert.ThrowsAsync<HackProcessKilledException>(() => run);
        await Task.Delay(50);
        Assert.Equal(13, killed.ProcessId);
        Assert.False(resumed);
        Assert.Equal(1, control.DroppedContinuations);
        // A dead PID runs nothing new: the refusal is synchronous, like exec on a reaped process.
        Assert.Throws<HackProcessKilledException>(() => { _ = control.RunAsync(() => Task.FromResult(0)); });
    }

    [Fact]
    public async Task A_child_started_from_a_process_survives_its_parents_kill()
    {
        ProcessControlBlock parent = new(ProcessId.FromInt64(20));
        ProcessControlBlock child = new(ProcessId.FromInt64(21));
        TaskCompletionSource release = new();
        Task<int>? childRun = null;

        Task<int> parentRun = parent.RunAsync(() =>
        {
            childRun = child.RunAsync(async () =>
            {
                await release.Task;
                return 7;
            });
            return Task.FromResult(0);
        });
        await parentRun;
        parent.MarkKilled();
        release.SetResult();

        Assert.Equal(7, await childRun!);
    }

    private static bool IsRunningAs(ProcessControlBlock control) =>
        ReferenceEquals(SynchronizationContext.Current, control.Scheduler.SynchronizationContext)
        && ReferenceEquals(TaskScheduler.Current, control.Scheduler);
}
