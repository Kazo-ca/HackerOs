using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Metrics;

/// <summary>
/// In-memory <see cref="ICpuTimeAccounting"/> behind <c>/proc/stat</c> (ADR 0057 section 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Units.</b> Linux counts CPU time in <c>USER_HZ</c>, 100 per second. One sampling interval of
/// <c>t</c> simulated seconds gives each core <c>100 × t</c> units to split between <c>user</c>,
/// <c>system</c> and <c>idle</c>, so the columns of a core always add up to its uptime.
/// </para>
/// <para>
/// <b>Scheduling.</b> The simulator reports a process's usage in core-equivalents, not per core.
/// Like a scheduler with CPU affinity, each process starts on core <c>pid mod cores</c> and spills
/// onto the next cores once that one is full, so two busy processes land on different cores and
/// <c>cpu0</c> and <c>cpu1</c> differ the way they do on real hardware.
/// </para>
/// <para>
/// <b>User or system.</b> The simulator does not model a kernel: busy time goes to <c>system</c>
/// for OS processes (services the host declared system apps, and processes the catalog does not
/// know) and to <c>user</c> for every other app.
/// </para>
/// <para>
/// <b>Measured (ADR 0067).</b> When an <see cref="ICpuTimeAccountant"/> measures, the simulated samples
/// are ignored: <c>cpu0</c> is the browser's main thread, the one virtual core every process runs on,
/// with its real <c>user</c> (every <c>utime</c>), <c>system</c> (every <c>stime</c> plus the compositor)
/// and <c>idle</c>. The other cores of the hardware profile stay idle for the whole uptime until Web
/// Worker processes (ADR 0070) run on them.
/// </para>
/// </remarks>
public sealed class CpuTimeAccounting : ICpuTimeAccounting
{
    /// <summary>Linux's <c>USER_HZ</c>: CPU-time units per second.</summary>
    public const int UserHz = 100;

    private readonly Func<ProcessRecord, bool> _isSystemProcess;
    private readonly ICpuTimeAccountant? _measured;
    private readonly Func<Kazo.Os.Simulation.Abstractions.Workers.IWorkerPoolStatus?>? _workers;
    private readonly Lock _gate = new();
    private readonly double[] _user;
    private readonly double[] _system;
    private readonly double[] _idle;

    /// <summary>Initializes the accounting for the cores of <paramref name="hardware"/>.</summary>
    /// <param name="hardware">Virtual hardware; the core count is <c>ceil(CpuCapacity)</c>, as in <c>/proc/cpuinfo</c>.</param>
    /// <param name="isSystemProcess">
    /// Tells whether a process is part of the OS (its time goes to <c>system</c>). Defaults to
    /// "every service": without a catalog, services are the closest match to kernel threads.
    /// </param>
    /// <param name="measured">
    /// Measured CPU time (ADR 0067); when it measures, <c>/proc/stat</c> shows real times instead of
    /// the simulated ones. <see langword="null"/> or the null accountant keeps the simulation.
    /// </param>
    /// <param name="workers">
    /// The worker pool (ADR 0070); with measured times, the busy time of each pool worker is added to
    /// the core it runs on. <see langword="null"/> when the host has no worker pool.
    /// </param>
    public CpuTimeAccounting(
        VirtualHardwareProfile hardware,
        Func<ProcessRecord, bool>? isSystemProcess = null,
        ICpuTimeAccountant? measured = null,
        Func<Kazo.Os.Simulation.Abstractions.Workers.IWorkerPoolStatus?>? workers = null)
    {
        _workers = workers;
        _measured = measured is { IsMeasuring: true } ? measured : null;
        ArgumentNullException.ThrowIfNull(hardware);
        CoreCount = Math.Max(1, (int)Math.Ceiling(hardware.CpuCapacity));
        _isSystemProcess = isSystemProcess ?? (static process => process.Kind == AppKind.Service);
        _user = new double[CoreCount];
        _system = new double[CoreCount];
        _idle = new double[CoreCount];
    }

    /// <inheritdoc />
    public int CoreCount { get; }

    /// <inheritdoc />
    public void Record(IReadOnlyList<ProcessResourceSample> samples, IReadOnlyList<ProcessRecord> processes, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(processes);
        if (interval <= TimeSpan.Zero || _measured is not null)
        {
            // Measured: the scheduler tick no longer invents CPU time (ADR 0067).
            return;
        }

        Dictionary<ProcessId, ProcessRecord> byPid = processes.ToDictionary(static process => process.Pid);
        double[] userLoad = new double[CoreCount];
        double[] systemLoad = new double[CoreCount];
        foreach (ProcessResourceSample sample in samples)
        {
            bool isSystem = byPid.TryGetValue(sample.Pid, out ProcessRecord? process) && _isSystemProcess(process);
            Schedule(sample.Pid, sample.CpuUsage, isSystem ? systemLoad : userLoad, userLoad, systemLoad);
        }

        double units = interval.TotalSeconds * UserHz;
        lock (_gate)
        {
            for (int core = 0; core < CoreCount; core++)
            {
                _user[core] += userLoad[core] * units;
                _system[core] += systemLoad[core] * units;
                _idle[core] += Math.Max(0, 1 - userLoad[core] - systemLoad[core]) * units;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CpuTimes> GetCores()
    {
        if (_measured is not null && _measured.TryGetThreadTimes(out MeasuredThreadCpuTimes thread))
        {
            long elapsed = ToUserHz(thread.Elapsed);
            long[] user = new long[CoreCount];
            long[] system = new long[CoreCount];
            user[0] = ToUserHz(thread.User);
            system[0] = ToUserHz(thread.System);
            // ADR 0070, section 2: the pool's workers are the machine's cores; the worker of core k
            // runs on cpu k (core 0 shares it with the browser's main thread, the desktop).
            foreach (Kazo.Os.Simulation.Abstractions.Workers.WorkerSnapshot worker in _workers?.Invoke()?.GetWorkers() ?? [])
            {
                if (worker.Core is { } core && core < CoreCount
                    && worker.Role == Kazo.Os.Simulation.Abstractions.Workers.WorkerRole.Pool)
                {
                    user[core] += ToUserHz(worker.BusyTime);
                }
            }

            return
            [
                // user + system + idle stays the elapsed time of each core (ADR 0067, section 3).
                .. Enumerable.Range(0, CoreCount).Select(core => new CpuTimes(
                    user[core], 0, system[core], Math.Max(0, elapsed - user[core] - system[core])))
            ];
        }

        lock (_gate)
        {
            return
            [
                .. Enumerable.Range(0, CoreCount).Select(core =>
                    new CpuTimes((long)_user[core], 0, (long)_system[core], (long)_idle[core]))
            ];
        }
    }

    private static long ToUserHz(TimeSpan time) => (long)(time.TotalSeconds * UserHz);

    /// <summary>
    /// Places <paramref name="usage"/> core-equivalents on the cores, starting with the process's
    /// home core and spilling over to the next ones while it is full.
    /// </summary>
    private void Schedule(ProcessId pid, double usage, double[] target, double[] userLoad, double[] systemLoad)
    {
        double remaining = Math.Max(0, usage);
        int home = (int)(pid.Value % CoreCount);
        for (int offset = 0; offset < CoreCount && remaining > 0; offset++)
        {
            int core = (home + offset) % CoreCount;
            double free = Math.Max(0, 1 - userLoad[core] - systemLoad[core]);
            double taken = Math.Min(free, remaining);
            target[core] += taken;
            remaining -= taken;
        }
    }
}
