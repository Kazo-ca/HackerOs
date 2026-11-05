using System.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.WorkerHost.Host;

/// <summary>
/// The CPU measurement (ADR 0067) for processes running in Web Workers (ADR 0070, section 5): their
/// <c>utime</c> and <c>stime</c> come from their worker's heartbeat, plus the kernel time the OS spent
/// on the main thread serving their calls; they are "Not responding" while their worker misses its
/// heartbeat — visible <b>during</b> the block, unlike on the main thread. Other processes read the
/// main thread's accountant unchanged.
/// </summary>
public sealed class WorkerAwareCpuTimeAccountant : ICpuTimeAccountant
{
    private static readonly long UsageWindow = Stopwatch.Frequency; // one second, like Cpu in status

    private readonly ICpuTimeAccountant _inner;
    private readonly WorkerPool _pool;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, (TimeSpan Total, long At)> _usageBase = [];

    /// <summary>Initializes the decorator.</summary>
    /// <param name="inner">The main thread's accountant (or the null one when the CPU is not measured).</param>
    /// <param name="pool">The workers and their heartbeats.</param>
    public WorkerAwareCpuTimeAccountant(ICpuTimeAccountant inner, WorkerPool pool)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    /// <inheritdoc />
    public bool IsMeasuring => _inner.IsMeasuring;

    /// <inheritdoc />
    public bool TryGetCpuUsage(ProcessId pid, out double coreEquivalents)
    {
        if (!IsMeasuring || !TryGetWorkerTimes(pid, out ProcessCpuTimes times, out _))
        {
            return _inner.TryGetCpuUsage(pid, out coreEquivalents);
        }

        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (!_usageBase.TryGetValue(pid.Value, out (TimeSpan Total, long At) start))
            {
                _usageBase[pid.Value] = (times.TotalTime, now);
                coreEquivalents = 0;
                return true;
            }

            double elapsed = Stopwatch.GetElapsedTime(start.At, now).TotalSeconds;
            coreEquivalents = elapsed <= 0 ? 0 : Math.Clamp((times.TotalTime - start.Total).TotalSeconds / elapsed, 0, 1);
            if (now - start.At >= UsageWindow)
            {
                _usageBase[pid.Value] = (times.TotalTime, now);
            }

            return true;
        }
    }

    /// <inheritdoc />
    public bool TryGetProcessTimes(ProcessId pid, out ProcessCpuTimes times)
    {
        if (IsMeasuring && TryGetWorkerTimes(pid, out times, out _))
        {
            return true;
        }

        return _inner.TryGetProcessTimes(pid, out times);
    }

    /// <inheritdoc />
    public bool TryGetThreadTimes(out MeasuredThreadCpuTimes times) => _inner.TryGetThreadTimes(out times);

    private bool TryGetWorkerTimes(ProcessId pid, out ProcessCpuTimes times, out WorkerConnection? worker)
    {
        worker = _pool.FindHost(pid.Value);
        if (worker is null)
        {
            times = default;
            return false;
        }

        _inner.TryGetProcessTimes(pid, out ProcessCpuTimes kernel);
        ProcessSample? sample = worker.GetSample(pid.Value);
        // A stuck worker runs one of its processes: the stalled time is theirs (shared if several).
        TimeSpan stalled = worker.StalledTime / Math.Max(1, worker.ProcessCount);
        times = new ProcessCpuTimes(
            kernel.UserTime + (sample?.UserTime ?? TimeSpan.Zero) + stalled,
            kernel.SystemTime + (sample?.SystemTime ?? TimeSpan.Zero),
            kernel.LongTaskCount + (sample?.LongTasks ?? 0) + (stalled > TimeSpan.FromMilliseconds(50) ? 1 : 0),
            Max(Max(sample?.LongestSlice ?? TimeSpan.Zero, kernel.LongestSlice), stalled),
            IsResponding: !worker.IsNotResponding,
            kernel.VoluntaryContextSwitches + (sample?.Slices ?? 0));
        return true;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;
}

/// <summary>
/// The memory measurement (ADR 0068) for processes running in Web Workers: each worker's runtime heap
/// is split between the processes it hosts and counted as their anonymous memory, on top of what the
/// main thread's accountant gives them (image, kernel memory held for them); <c>/proc/meminfo</c>
/// counts every worker's runtime.
/// </summary>
public sealed class WorkerAwareMemoryAccountant : IMemoryAccountant
{
    private readonly IMemoryAccountant _inner;
    private readonly WorkerPool _pool;

    /// <summary>Initializes the decorator.</summary>
    /// <param name="inner">The main thread's accountant (or the null one when memory is not measured).</param>
    /// <param name="pool">The workers and their heartbeats.</param>
    public WorkerAwareMemoryAccountant(IMemoryAccountant inner, WorkerPool pool)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    /// <inheritdoc />
    public bool IsMeasuring => _inner.IsMeasuring;

    /// <inheritdoc />
    public bool TryGetProcessMemory(ProcessRecord process, IReadOnlyList<ProcessRecord> active, out ProcessMemoryUsage usage)
    {
        bool measured = _inner.TryGetProcessMemory(process, active, out usage);
        if (!IsMeasuring || _pool.FindHost(process.Pid.Value) is not { } worker)
        {
            return measured;
        }

        long share = worker.RuntimeMemoryBytes / Math.Max(1, worker.ProcessCount);
        long allocated = worker.GetSample(process.Pid.Value)?.AllocatedBytes ?? 0;
        usage = usage with
        {
            AnonymousBytes = usage.AnonymousBytes + share,
            AllocatedBytes = usage.AllocatedBytes + allocated
        };
        return true;
    }

    /// <inheritdoc />
    public bool TryGetSystemMemory(IReadOnlyList<ProcessRecord> active, out SystemMemoryUsage usage)
    {
        bool measured = _inner.TryGetSystemMemory(active, out usage);
        if (!measured)
        {
            return false;
        }

        long workers = _pool.Connections.Where(static worker => !worker.IsTerminated).Sum(static worker => worker.RuntimeMemoryBytes);
        usage = usage with { AnonymousBytes = usage.AnonymousBytes + workers };
        return true;
    }
}
