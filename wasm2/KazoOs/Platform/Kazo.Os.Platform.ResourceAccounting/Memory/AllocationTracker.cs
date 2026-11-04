using System.Collections.Concurrent;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.ResourceAccounting.Memory;

/// <summary>
/// The allocation layer of ADR 0068: at every context switch, reads the thread's allocation counter
/// and charges the bytes allocated since the previous switch to the process that was running.
/// </summary>
/// <remarks>
/// <para>
/// One thread runs app code at a time on WebAssembly, so the difference between two readings belongs
/// to whoever ran in between: <see cref="ExecutionContextSwitch.From"/>. Bytes allocated outside any
/// process frame (the compositor, Blazor rendering, OS services) go to <see cref="OsAllocatedBytes"/>.
/// The last reading and the process now running are kept per thread, so the counts stay right on
/// a multithreaded host too, and <see cref="FlushCurrentThread"/> can charge a running process for
/// its slice so far, as a kernel's counters for a running task are always current.
/// </para>
/// <para>
/// The hot path is allocation-free once a process has its counter (one dictionary lookup and one
/// interlocked add), lock-free, and never throws.
/// </para>
/// </remarks>
public sealed class AllocationTracker : IExecutionContextSwitchObserver
{
    private readonly IManagedHeapProbe _probe;
    private readonly ConcurrentDictionary<long, Counter> _counters = new();
    // [0] = last reading of the allocation counter (-1: none yet); [1] = PID running now (0: none).
    private readonly ThreadLocal<long[]> _lastReading = new(static () => [-1, 0]);
    private long _osBytes;

    /// <summary>Creates a tracker over <paramref name="probe"/>.</summary>
    /// <param name="probe">The allocation counter to read; <see cref="GcManagedHeapProbe.Instance"/> in a host.</param>
    public AllocationTracker(IManagedHeapProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>Gets the bytes allocated outside any process frame, by the OS itself.</summary>
    public long OsAllocatedBytes => Interlocked.Read(ref _osBytes);

    /// <inheritdoc />
    public void OnContextSwitch(in ExecutionContextSwitch contextSwitch)
    {
        long[] last = _lastReading.Value!;
        Charge(last, contextSwitch.From?.Value ?? 0);
        last[1] = contextSwitch.To?.Value ?? 0;
    }

    /// <summary>
    /// Charges what the current thread allocated since its last switch to whoever is running on it
    /// now, without waiting for the next switch: a process reading its own <c>/proc/self/status</c>
    /// sees its allocations so far. Called by the accountant before every read.
    /// </summary>
    public void FlushCurrentThread()
    {
        long[] last = _lastReading.Value!;
        Charge(last, last[1]);
    }

    /// <summary>Charges the bytes allocated since the thread's last reading to <paramref name="pid"/> (0: the OS).</summary>
    private void Charge(long[] last, long pid)
    {
        long now = _probe.GetAllocatedBytesForCurrentThread();
        long previous = last[0];
        last[0] = now;
        if (previous < 0 || now <= previous)
        {
            // First reading on this thread: nobody can be charged for what came before.
            return;
        }

        long delta = now - previous;
        if (pid > 0)
        {
            if (!_counters.TryGetValue(pid, out Counter? counter))
            {
                counter = _counters.GetOrAdd(pid, static _ => new Counter());
            }

            Interlocked.Add(ref counter.Bytes, delta);
        }
        else
        {
            Interlocked.Add(ref _osBytes, delta);
        }
    }

    /// <summary>Gets the bytes <paramref name="pid"/> allocated since it started (<c>KazoAllocBytes</c>).</summary>
    /// <param name="pid">The process.</param>
    public long GetAllocatedBytes(ProcessId pid) =>
        _counters.TryGetValue(pid.Value, out Counter? counter) ? Interlocked.Read(ref counter.Bytes) : 0;

    /// <summary>Gets whether <paramref name="pid"/> has ever run code and left a frame (it allocated, or at least ran).</summary>
    /// <param name="pid">The process.</param>
    public bool HasRun(ProcessId pid) => _counters.ContainsKey(pid.Value);

    /// <summary>Forgets every process not in <paramref name="live"/>, like the kernel freeing a reaped task.</summary>
    /// <param name="live">The processes still alive.</param>
    public void Retain(IReadOnlySet<long> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        foreach (long pid in _counters.Keys)
        {
            if (!live.Contains(pid))
            {
                _counters.TryRemove(pid, out _);
            }
        }
    }

    private sealed class Counter
    {
        public long Bytes;
    }
}
