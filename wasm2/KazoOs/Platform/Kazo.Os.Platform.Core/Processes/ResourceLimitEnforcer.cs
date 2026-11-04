using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// Applies the limits that depend on a measurement (ADR 0125, section 2): <c>RLIMIT_CPU</c> and
/// <c>RLIMIT_RSS</c>. Every quarter of a simulated second it compares each process's measured CPU time
/// and resident memory with its limits, like the kernel's tick checking <c>posix-cpu-timers</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Hard CPU limit</b>: <c>SIGKILL</c>, <see cref="ProcessExitReason.CpuLimitExceeded"/> (exit 137). Checked
/// first, as Linux does: with <c>ulimit -t 1</c> (soft = hard), the process is killed.</item>
/// <item><b>Soft CPU limit</b>: <c>SIGXCPU</c>, delivered once as a stop request
/// (<see cref="ProcessExitReason.CpuLimitExceeded"/>, ADR 0069): the process may end on it (exit 152);
/// otherwise the grace period of <c>kill</c> leads to <c>SIGKILL</c>, unless the hard limit comes first.</item>
/// <item><b>Memory limit</b>: <c>SIGKILL</c> at once, <see cref="ProcessExitReason.MemoryLimitExceeded"/> (exit 137):
/// nothing can refuse a .NET allocation.</item>
/// </list>
/// <para>
/// A limit whose measurement is off (<c>CpuSource</c>/<c>MemSource: simulated</c>, ADR 0066) is
/// inactive. On the browser's main thread the check runs between two slices, so a loop that never
/// gives the thread back is not stopped ("best effort"); in a Web Worker the measurement keeps going
/// while the worker is busy (its heartbeat, ADR 0070) and the kill terminates the worker ("strict").
/// </para>
/// </remarks>
public sealed class ResourceLimitEnforcer : IDisposable
{
    /// <summary>Default interval between two checks: a quarter of a simulated second.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Grace period after <c>SIGXCPU</c>, the one of <c>kill</c> and "End task" (ADR 0069).</summary>
    public static readonly TimeSpan SignalGracePeriod = TimeSpan.FromSeconds(5);

    private readonly ISimulationClock _clock;
    private readonly Func<IProcessManager> _processManager;
    private readonly IProcessResourceLimitTable _limits;
    private readonly ICpuTimeAccountant _cpu;
    private readonly IMemoryAccountant _memory;
    private readonly Lock _gate = new();
    private readonly HashSet<ProcessId> _signalled = [];
    private IDisposable? _scheduled;
    private bool _disposed;

    /// <summary>Initializes a stopped enforcer.</summary>
    /// <param name="clock">Simulation clock the checks are scheduled on.</param>
    /// <param name="processManager">Lazily resolves the process table.</param>
    /// <param name="limits">The limit table.</param>
    /// <param name="cpu">Measured CPU times; the CPU limit is inactive when it does not measure.</param>
    /// <param name="memory">Measured memory; the memory limit is inactive when it does not measure.</param>
    /// <param name="interval">Simulated time between two checks; <see cref="DefaultInterval"/> when omitted.</param>
    public ResourceLimitEnforcer(
        ISimulationClock clock,
        Func<IProcessManager> processManager,
        IProcessResourceLimitTable limits,
        ICpuTimeAccountant cpu,
        IMemoryAccountant memory,
        TimeSpan? interval = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Interval = interval ?? DefaultInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Interval, TimeSpan.Zero, nameof(interval));
    }

    /// <summary>Gets the simulated time between two checks.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Gets whether any limit can apply: limits on, and at least one measurement active.</summary>
    public bool IsEffective => _limits.IsEnabled && (_cpu.IsMeasuring || _memory.IsMeasuring);

    /// <summary>Schedules the first check one interval from now. Idempotent; does nothing when no limit can apply.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsEffective)
            {
                _scheduled ??= _clock.Schedule(Interval, OnDue);
            }
        }
    }

    /// <summary>
    /// Checks every active process now. Called by the clock; public for tests and hosts that check
    /// explicitly.
    /// </summary>
    /// <returns>The processes acted on, with the reason.</returns>
    public IReadOnlyList<(ProcessId Pid, ResourceLimitAction Action)> CheckNow()
    {
        List<(ProcessId, ResourceLimitAction)> actions = [];
        if (!IsEffective)
        {
            return actions;
        }

        IProcessManager manager = _processManager();
        IReadOnlyList<ProcessRecord> active = manager.GetActiveProcesses();
        foreach (ProcessRecord process in active)
        {
            if (process.IsTerminal || !_limits.TryGet(process.Pid, out ProcessResourceLimits limits))
            {
                continue;
            }

            ResourceLimitAction action = Check(process, limits, active);
            if (action != ResourceLimitAction.None && Apply(manager, process.Pid, action))
            {
                actions.Add((process.Pid, action));
            }
        }

        lock (_gate)
        {
            // Forget the SIGXCPU of processes that are gone, like the kernel freeing a reaped task.
            _signalled.RemoveWhere(pid => !active.Any(process => process.Pid == pid));
        }

        return actions;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _scheduled?.Dispose();
            _scheduled = null;
        }
    }

    private ResourceLimitAction Check(ProcessRecord process, ProcessResourceLimits limits, IReadOnlyList<ProcessRecord> active)
    {
        if (limits.Cpu.IsFinite && _cpu.IsMeasuring && _cpu.TryGetProcessTimes(process.Pid, out ProcessCpuTimes times))
        {
            double seconds = times.TotalTime.TotalSeconds;
            if (limits.Cpu.Hard != ResourceLimit.Unlimited && seconds >= limits.Cpu.Hard)
            {
                return ResourceLimitAction.CpuKill;
            }

            if (seconds >= limits.Cpu.Soft)
            {
                lock (_gate)
                {
                    // One SIGXCPU: a pending stop request is not queued twice (ADR 0069).
                    if (_signalled.Add(process.Pid))
                    {
                        return ResourceLimitAction.CpuSignal;
                    }
                }
            }
        }

        if (limits.Rss.IsFinite && _memory.IsMeasuring
            && _memory.TryGetProcessMemory(process, active, out ProcessMemoryUsage usage)
            && usage.ResidentBytes >= limits.Rss.Soft)
        {
            return ResourceLimitAction.MemoryKill;
        }

        return ResourceLimitAction.None;
    }

    private static bool Apply(IProcessManager manager, ProcessId pid, ResourceLimitAction action)
    {
        try
        {
            switch (action)
            {
                case ResourceLimitAction.CpuKill:
                    manager.Kill(pid, ProcessExitReason.CpuLimitExceeded);
                    return true;
                case ResourceLimitAction.MemoryKill:
                    manager.Kill(pid, ProcessExitReason.MemoryLimitExceeded);
                    return true;
                case ResourceLimitAction.CpuSignal:
                    Task<ProcessRecord> stop = manager.StopAsync(pid, SignalGracePeriod, ProcessExitReason.CpuLimitExceeded);
                    // The grace period runs on its own; a process that ended meanwhile is no error.
                    _ = stop.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            // The process ended between the snapshot and the signal.
            return false;
        }
    }

    private void OnDue()
    {
        lock (_gate)
        {
            if (_disposed || _scheduled is null)
            {
                return;
            }
        }

        try
        {
            CheckNow();
        }
        finally
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _scheduled = _clock.Schedule(Interval, OnDue);
                }
            }
        }
    }
}

/// <summary>What <see cref="ResourceLimitEnforcer"/> did to one process.</summary>
public enum ResourceLimitAction
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary><c>SIGXCPU</c>: the soft CPU limit was crossed; the process was asked to stop.</summary>
    CpuSignal,

    /// <summary><c>SIGKILL</c> at the hard CPU limit.</summary>
    CpuKill,

    /// <summary><c>SIGKILL</c> at the memory limit.</summary>
    MemoryKill
}
