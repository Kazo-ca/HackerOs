using System.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.WorkerHost.Worker;

/// <summary>
/// Measures, inside a worker, the CPU time of each process it hosts (ADR 0070, section 5): the same
/// context-switch hook as the main thread's accountant (ADR 0067), on the worker's single thread.
/// <c>utime</c> is the process's own code, <c>stime</c> the remote gateway calls it makes; a slice is
/// an uninterrupted run, a long task one over 50 ms. The heartbeat carries the totals to the OS.
/// </summary>
/// <remarks>
/// Installed only in a real Web Worker, where this thread runs nothing but these processes. The
/// in-process loopback of the tests shares the OS's static hook, so it never installs one.
/// </remarks>
internal sealed class WorkerCpuMeter : IExecutionContextSwitchObserver, IExecutionModeSwitchObserver
{
    private static readonly long LongTaskThreshold = Stopwatch.Frequency / 20; // 50 ms

    private readonly Lock _gate = new();
    private readonly Dictionary<long, ProcessTimes> _times = [];
    private long? _running;
    private bool _kernel;
    private long _since = Stopwatch.GetTimestamp();
    private long _sliceStart;
    private long _allocatedAtSwitch = GC.GetAllocatedBytesForCurrentThread();
    private long _busyTicks;

    /// <inheritdoc />
    public void OnContextSwitch(in ExecutionContextSwitch contextSwitch)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            Charge(now, endSlice: true);
            _running = contextSwitch.To?.Value;
            _kernel = contextSwitch.ToKernel;
            _sliceStart = now;
        }
    }

    /// <inheritdoc />
    public void OnModeSwitch(ProcessId process, bool toKernel, bool threadContextChanged)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            Charge(now, endSlice: false);
            _kernel = toKernel;
        }
    }

    /// <summary>Gets the time this thread spent running processes since the worker started.</summary>
    public TimeSpan BusyTime
    {
        get
        {
            lock (_gate)
            {
                long current = _running is null ? 0 : Stopwatch.GetTimestamp() - _since;
                return Stopwatch.GetElapsedTime(0, _busyTicks + current);
            }
        }
    }

    /// <summary>Gets the measured times of one process, or <see langword="null"/> before it first ran.</summary>
    public MeteredTimes? Get(long pid)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_running == pid)
            {
                // Charge the slice in progress so a heartbeat sent by the running process is exact.
                Charge(now, endSlice: false);
            }

            return _times.TryGetValue(pid, out ProcessTimes? times)
                ? new MeteredTimes(
                    Stopwatch.GetElapsedTime(0, times.UserTicks),
                    Stopwatch.GetElapsedTime(0, times.SystemTicks),
                    times.LongTasks,
                    Stopwatch.GetElapsedTime(0, times.LongestSliceTicks),
                    times.Slices,
                    times.AllocatedBytes)
                : null;
        }
    }

    /// <summary>Forgets a process that ended.</summary>
    public void Forget(long pid)
    {
        lock (_gate)
        {
            _times.Remove(pid);
        }
    }

    private void Charge(long now, bool endSlice)
    {
        long elapsed = now - _since;
        _since = now;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long allocatedDelta = allocated - _allocatedAtSwitch;
        _allocatedAtSwitch = allocated;
        if (_running is not { } pid)
        {
            return;
        }

        if (!_times.TryGetValue(pid, out ProcessTimes? times))
        {
            times = new ProcessTimes();
            _times[pid] = times;
        }

        if (_kernel)
        {
            times.SystemTicks += elapsed;
        }
        else
        {
            times.UserTicks += elapsed;
        }

        times.AllocatedBytes += Math.Max(0, allocatedDelta);
        _busyTicks += elapsed;
        if (endSlice)
        {
            long slice = now - _sliceStart;
            times.Slices++;
            times.LongestSliceTicks = Math.Max(times.LongestSliceTicks, slice);
            if (slice > LongTaskThreshold)
            {
                times.LongTasks++;
            }
        }
    }

    private sealed class ProcessTimes
    {
        public long UserTicks;
        public long SystemTicks;
        public long LongTasks;
        public long LongestSliceTicks;
        public long Slices;
        public long AllocatedBytes;
    }
}

/// <summary>The CPU times and allocations of one process measured in its worker.</summary>
internal readonly record struct MeteredTimes(
    TimeSpan UserTime,
    TimeSpan SystemTime,
    long LongTasks,
    TimeSpan LongestSlice,
    long Slices,
    long AllocatedBytes);
