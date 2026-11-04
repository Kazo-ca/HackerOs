using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.ResourceAccounting.Tests;

/// <summary>
/// ADR 0067: utime and stime per process charged at the context switches of the common hook,
/// compositor and idle outside any frame, long tasks and "Not responding". Time is driven by hand
/// (one tick = one millisecond) so every figure is exact.
/// </summary>
public sealed class MeasuredCpuTimeAccountantTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void Time_inside_a_process_scope_is_its_utime()
    {
        ManualClock clock = new();
        MeasuredCpuTimeAccountant accountant = clock.Accountant(Threshold);
        using (ContextSwitchHook.Install(accountant))
        {
            using (HackExecutionContext.Enter(TestContexts.Process(10)))
            {
                clock.Advance(30);
            }
        }

        ProcessCpuTimes times = Times(accountant, 10);
        Assert.Equal(TimeSpan.FromMilliseconds(30), times.UserTime);
        Assert.Equal(TimeSpan.Zero, times.SystemTime);
        Assert.Equal(1, times.VoluntaryContextSwitches);
        Assert.True(times.IsResponding);
    }

    [Fact]
    public void Time_inside_an_os_gateway_is_the_callers_stime()
    {
        // Every clock read advances 10 ms: Enter, gateway entry, gateway exit, Dispose.
        ManualClock clock = new() { Step = 10 };
        MeasuredCpuTimeAccountant accountant = clock.Accountant(Threshold);
        AppDriveGateway gateway = new(() => new NoMounts(), VirtualHardwareProfile.Default);
        using (ContextSwitchHook.Install(accountant))
        {
            using (HackExecutionContext.Enter(TestContexts.Process(11)))
            {
                Assert.True(gateway.GetDrivesAsync().IsCompletedSuccessfully);
            }
        }

        ProcessCpuTimes times = Times(accountant, 11);
        Assert.Equal(TimeSpan.FromMilliseconds(20), times.UserTime);
        Assert.Equal(TimeSpan.FromMilliseconds(10), times.SystemTime);
        // Entering and leaving the gateway is a change of mode, not a context switch.
        Assert.Equal(1, times.VoluntaryContextSwitches);
    }

    [Fact]
    public void Through_the_dispatcher_the_os_composes_gateway_time_is_still_stime()
    {
        ManualClock clock = new() { Step = 10 };
        MeasuredCpuTimeAccountant accountant = clock.Accountant(Threshold);
        ContextSwitchDispatcher dispatcher = new();
        AppDriveGateway gateway = new(() => new NoMounts(), VirtualHardwareProfile.Default);
        using (dispatcher.Register(new RecordingObserver()))
        using (dispatcher.Register(accountant))
        using (ContextSwitchHook.Install(dispatcher))
        using (HackExecutionContext.Enter(TestContexts.Process(13)))
        {
            Assert.True(gateway.GetDrivesAsync().IsCompletedSuccessfully);
        }

        Assert.Equal(TimeSpan.FromMilliseconds(10), Times(accountant, 13).SystemTime);
    }

    [Fact]
    public void A_nested_process_interrupts_the_slice_of_its_parent()
    {
        ManualClock clock = new();
        MeasuredCpuTimeAccountant accountant = clock.Accountant(Threshold);
        using (ContextSwitchHook.Install(accountant))
        {
            using (HackExecutionContext.Enter(TestContexts.Process(20)))
            {
                clock.Advance(5);
                using (HackExecutionContext.Enter(TestContexts.Process(21)))
                {
                    clock.Advance(7);
                }

                clock.Advance(3);
            }
        }

        Assert.Equal(TimeSpan.FromMilliseconds(8), Times(accountant, 20).UserTime);
        Assert.Equal(2, Times(accountant, 20).VoluntaryContextSwitches);
        Assert.Equal(TimeSpan.FromMilliseconds(7), Times(accountant, 21).UserTime);
    }

    [Fact]
    public void Long_slices_are_long_tasks_and_one_over_the_threshold_is_not_responding_until_a_short_one()
    {
        ManualClock clock = new();
        MeasuredCpuTimeAccountant accountant = clock.Accountant(Threshold);
        using (ContextSwitchHook.Install(accountant))
        {
            RunSlice(clock, 30, 40);
            Assert.Equal(0, Times(accountant, 30).LongTaskCount);

            RunSlice(clock, 30, 60);
            Assert.Equal(1, Times(accountant, 30).LongTaskCount);
            Assert.True(Times(accountant, 30).IsResponding);

            RunSlice(clock, 30, 150);
            Assert.Equal(2, Times(accountant, 30).LongTaskCount);
            Assert.False(Times(accountant, 30).IsResponding);
            Assert.Equal(TimeSpan.FromMilliseconds(150), Times(accountant, 30).LongestSlice);

            // It answers the next event quickly: responding again. Nothing was ever interrupted.
            RunSlice(clock, 30, 10);
            Assert.True(Times(accountant, 30).IsResponding);
        }
    }

    [Fact]
    public void Gap_after_a_scope_the_os_closed_is_compositor_time_up_to_the_budget()
    {
        ManualClock clock = new();
        MeasuredCpuTimeAccountant accountant = clock.Accountant(Threshold, compositorBudgetMs: 4);
        using (ContextSwitchHook.Install(accountant))
        {
            RunSlice(clock, 40, 10);
            clock.Advance(2); // the OS renders right after the handler returned
            RunSlice(clock, 41, 10);
            clock.Advance(50); // ...then the browser waits for the next event
            RunSlice(clock, 40, 10);
        }

        Assert.True(accountant.TryGetThreadTimes(out MeasuredThreadCpuTimes thread));
        Assert.Equal(TimeSpan.FromMilliseconds(6), thread.Compositor);
        Assert.Equal(TimeSpan.FromMilliseconds(52), accountant.OutOfFrameTime);
    }

    [Fact]
    public void Gap_after_a_continuation_ended_is_idle()
    {
        ManualClock clock = new();
        MeasuredCpuTimeAccountant accountant = clock.Accountant(Threshold);
        ProcessId pid = ProcessId.FromInt64(50);

        // The runtime restores an empty context when a continuation ends (ThreadContextChanged).
        accountant.OnContextSwitch(new ExecutionContextSwitch(null, pid, true));
        clock.Advance(10);
        accountant.OnContextSwitch(new ExecutionContextSwitch(pid, null, true));
        clock.Advance(30);
        accountant.OnContextSwitch(new ExecutionContextSwitch(null, pid, true));

        Assert.True(accountant.TryGetThreadTimes(out MeasuredThreadCpuTimes thread));
        Assert.Equal(TimeSpan.Zero, thread.Compositor);
        Assert.Equal(TimeSpan.FromMilliseconds(30), accountant.OutOfFrameTime);
    }

    [Fact]
    public void Thread_times_add_up_to_the_wall_time()
    {
        ManualClock clock = new();
        MeasuredCpuTimeAccountant accountant = clock.Accountant(Threshold, compositorBudgetMs: 4);
        ProcessId pid = ProcessId.FromInt64(60);
        clock.Advance(100); // boot: idle
        accountant.OnContextSwitch(new ExecutionContextSwitch(null, pid, false));
        clock.Advance(20);
        accountant.OnModeSwitch(pid, toKernel: true, threadContextChanged: false);
        clock.Advance(5);
        accountant.OnContextSwitch(new ExecutionContextSwitch(pid, null, false, FromKernel: true));
        clock.Advance(3); // compositor
        accountant.OnContextSwitch(new ExecutionContextSwitch(null, pid, false));

        Assert.True(accountant.TryGetThreadTimes(out MeasuredThreadCpuTimes thread));
        Assert.Equal(TimeSpan.FromMilliseconds(20), thread.User);
        Assert.Equal(TimeSpan.FromMilliseconds(8), thread.System); // 5 stime + 3 compositor
        Assert.Equal(TimeSpan.FromMilliseconds(3), thread.Compositor);
        Assert.Equal(TimeSpan.FromMilliseconds(128), thread.Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(100), thread.Idle);
    }

    [Fact]
    public void Usage_is_the_share_of_the_last_second()
    {
        ManualClock clock = new();
        MeasuredCpuTimeAccountant accountant = clock.Accountant(Threshold);
        ProcessId pid = ProcessId.FromInt64(70);
        accountant.OnContextSwitch(new ExecutionContextSwitch(null, pid, false));
        clock.Advance(250);
        accountant.OnContextSwitch(new ExecutionContextSwitch(pid, null, false));
        clock.Advance(750);

        Assert.True(accountant.TryGetCpuUsage(pid, out double first));
        Assert.Equal(0.25, first, 3);

        // Within the same second, the figure holds; a second later, an idle second reads 0.
        clock.Advance(500);
        Assert.True(accountant.TryGetCpuUsage(pid, out double held));
        Assert.Equal(0.25, held, 3);
        clock.Advance(500);
        Assert.True(accountant.TryGetCpuUsage(pid, out double idle));
        Assert.Equal(0, idle, 3);
    }

    [Fact]
    public void A_process_that_never_ran_is_not_measured()
    {
        MeasuredCpuTimeAccountant accountant = new ManualClock().Accountant(Threshold);

        Assert.True(accountant.IsMeasuring);
        Assert.False(accountant.TryGetCpuUsage(ProcessId.FromInt64(99), out _));
        Assert.False(accountant.TryGetProcessTimes(ProcessId.FromInt64(99), out _));
    }

    [Fact]
    public void Pids_beyond_the_initial_table_grow_it()
    {
        ManualClock clock = new();
        MeasuredCpuTimeAccountant accountant = clock.Accountant(Threshold);
        ProcessId pid = ProcessId.FromInt64(5_000);
        accountant.OnContextSwitch(new ExecutionContextSwitch(null, pid, false));
        clock.Advance(4);
        accountant.OnContextSwitch(new ExecutionContextSwitch(pid, null, false));

        Assert.Equal(TimeSpan.FromMilliseconds(4), Times(accountant, 5_000).UserTime);
    }

    [Fact]
    public async Task Async_work_inside_a_scope_is_charged_to_the_process_across_continuations()
    {
        MeasuredCpuTimeAccountant accountant = new(Threshold);
        using (ContextSwitchHook.Install(accountant))
        {
            using (HackExecutionContext.Enter(TestContexts.Process(80)))
            {
                await Task.Yield();
                SpinFor(TimeSpan.FromMilliseconds(20));
            }
        }

        Assert.True(Times(accountant, 80).UserTime >= TimeSpan.FromMilliseconds(15));
    }

    private static void RunSlice(ManualClock clock, long pid, long milliseconds)
    {
        using (HackExecutionContext.Enter(TestContexts.Process(pid)))
        {
            clock.Advance(milliseconds);
        }
    }

    private static void SpinFor(TimeSpan duration)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            Thread.SpinWait(100);
        }
    }

    private static ProcessCpuTimes Times(MeasuredCpuTimeAccountant accountant, long pid)
    {
        Assert.True(accountant.TryGetProcessTimes(ProcessId.FromInt64(pid), out ProcessCpuTimes times));
        return times;
    }

    /// <summary>A clock in milliseconds that only moves when told to (or by <see cref="Step"/> per read).</summary>
    private sealed class ManualClock
    {
        public long Now { get; private set; } = 1_000;

        public long Step { get; init; }

        public void Advance(long milliseconds) => Now += milliseconds;

        public MeasuredCpuTimeAccountant Accountant(TimeSpan threshold, long compositorBudgetMs = 4) =>
            new(threshold, Read, 1_000, TimeSpan.FromMilliseconds(compositorBudgetMs));

        private long Read()
        {
            Now += Step;
            return Now;
        }
    }

    /// <summary>A mount table with no disk: <see cref="AppDriveGateway"/> answers at once, inside its kernel frame.</summary>
    private sealed class NoMounts : IFileSystemMountRouter
    {
        public IReadOnlyList<FileSystemMount> Mounts => [];

        public FileSystemMountResolution Resolve(VirtualPath path) => throw new NotSupportedException();
    }
}
