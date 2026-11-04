using Kazo.Os.AppSdk;
using Kazo.Os.Platform.ResourceAccounting.Memory;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.ResourceAccounting.Tests;

/// <summary>The allocation layer of ADR 0068: bytes allocated between two switches go to the process that ran.</summary>
public sealed class AllocationTrackerTests
{
    [Fact]
    public void Bytes_allocated_between_two_switches_go_to_the_process_that_ran()
    {
        FakeHeapProbe probe = new() { Allocated = 1_000 };
        AllocationTracker tracker = new(probe);

        tracker.OnContextSwitch(new ExecutionContextSwitch(null, ProcessId.FromInt64(5), false)); // first reading
        probe.Allocated += 300;
        tracker.OnContextSwitch(new ExecutionContextSwitch(ProcessId.FromInt64(5), ProcessId.FromInt64(6), false));
        probe.Allocated += 50;
        tracker.OnContextSwitch(new ExecutionContextSwitch(ProcessId.FromInt64(6), null, true));
        probe.Allocated += 7;
        tracker.OnContextSwitch(new ExecutionContextSwitch(null, ProcessId.FromInt64(5), true));

        Assert.Equal(300, tracker.GetAllocatedBytes(ProcessId.FromInt64(5)));
        Assert.Equal(50, tracker.GetAllocatedBytes(ProcessId.FromInt64(6)));
        Assert.Equal(7, tracker.OsAllocatedBytes);
    }

    [Fact]
    public void Nothing_is_charged_before_the_first_reading_of_a_thread()
    {
        FakeHeapProbe probe = new() { Allocated = 5_000_000 };
        AllocationTracker tracker = new(probe);

        tracker.OnContextSwitch(new ExecutionContextSwitch(ProcessId.FromInt64(5), null, false));

        Assert.Equal(0, tracker.GetAllocatedBytes(ProcessId.FromInt64(5)));
        Assert.Equal(0, tracker.OsAllocatedBytes);
    }

    [Fact]
    public void A_running_process_is_charged_for_its_slice_so_far_on_flush()
    {
        // A process reading its own /proc/self/status sees its allocations, like a running task on Linux.
        FakeHeapProbe probe = new();
        AllocationTracker tracker = new(probe);
        tracker.OnContextSwitch(new ExecutionContextSwitch(null, ProcessId.FromInt64(5), false));
        probe.Allocated += 400;

        tracker.FlushCurrentThread();
        Assert.Equal(400, tracker.GetAllocatedBytes(ProcessId.FromInt64(5)));

        probe.Allocated += 100;
        tracker.OnContextSwitch(new ExecutionContextSwitch(ProcessId.FromInt64(5), null, false));
        probe.Allocated += 30;
        tracker.FlushCurrentThread();

        Assert.Equal(500, tracker.GetAllocatedBytes(ProcessId.FromInt64(5))); // nothing counted twice
        Assert.Equal(30, tracker.OsAllocatedBytes);
    }

    [Fact]
    public void Retain_forgets_dead_processes()
    {
        FakeHeapProbe probe = new();
        AllocationTracker tracker = new(probe);
        tracker.OnContextSwitch(new ExecutionContextSwitch(null, null, false));
        tracker.Run(probe, 5, 10);
        tracker.Run(probe, 6, 10);

        tracker.Retain(new HashSet<long> { 6 });

        Assert.False(tracker.HasRun(ProcessId.FromInt64(5)));
        Assert.True(tracker.HasRun(ProcessId.FromInt64(6)));
        Assert.Equal(0, tracker.GetAllocatedBytes(ProcessId.FromInt64(5)));
    }

    [Fact]
    public void On_the_real_hook_a_process_is_charged_with_what_it_really_allocated()
    {
        AllocationTracker tracker = new(GcManagedHeapProbe.Instance);
        using (ContextSwitchHook.Install(tracker))
        {
            using (HackExecutionContext.Enter(TestContexts.Process(77)))
            {
                GC.KeepAlive(new byte[200_000]);
            }
        }

        long charged = tracker.GetAllocatedBytes(ProcessId.FromInt64(77));
        Assert.InRange(charged, 200_000, 400_000);
    }
}
