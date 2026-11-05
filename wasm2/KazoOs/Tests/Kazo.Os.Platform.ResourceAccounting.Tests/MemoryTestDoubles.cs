using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.ResourceAccounting.Memory;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.ResourceAccounting.Tests;

/// <summary>A garbage collector the test drives: allocations, collections and heap size.</summary>
internal sealed class FakeHeapProbe : IManagedHeapProbe
{
    public long Allocated { get; set; }

    public int CollectionCount { get; set; }

    public long HeapSizeBytes { get; set; }

    public long GetAllocatedBytesForCurrentThread() => Allocated;
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks = 1;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}

internal static class MemoryRecords
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    public static ProcessRecord Process(long pid, string appId, AppKind kind = AppKind.Terminal, bool stopped = false) => new(
        ProcessId.FromInt64(pid),
        null,
        appId,
        AppInstanceId.FromGuid(Guid.NewGuid()),
        kind,
        LocalUserId.FromGuid(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
        SessionId.FromGuid(new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
        stopped ? ProcessState.Stopped : ProcessState.Running,
        ResourceProfile.None,
        serviceHealth: null,
        Boot,
        Boot,
        stopped ? Boot.AddSeconds(1) : null,
        exitCode: null,
        exitReason: stopped ? ProcessExitReason.Completed : null);

    /// <summary>Charges <paramref name="bytes"/> to <paramref name="pid"/> as if it ran and left the CPU.</summary>
    public static void Run(this AllocationTracker tracker, FakeHeapProbe probe, long pid, long bytes)
    {
        tracker.OnContextSwitch(new ExecutionContextSwitch(null, ProcessId.FromInt64(pid), false));
        probe.Allocated += bytes;
        tracker.OnContextSwitch(new ExecutionContextSwitch(ProcessId.FromInt64(pid), null, false));
    }
}
