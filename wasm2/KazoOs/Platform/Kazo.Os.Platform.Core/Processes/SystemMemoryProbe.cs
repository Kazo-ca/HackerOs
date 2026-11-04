using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>The memory of the machine at one instant, as <c>/proc/meminfo</c> shows it.</summary>
/// <param name="TotalBytes"><c>MemTotal</c>: the simulated hardware profile's memory.</param>
/// <param name="UsedBytes">Memory in use: measured (ADR 0068) plus the simulated memory of processes that run no code, or all simulated.</param>
/// <param name="Measured">The measured totals, or <see langword="null"/> when memory is simulated (<c>MemSource: simulated</c>).</param>
public readonly record struct SystemMemorySnapshot(long TotalBytes, long UsedBytes, SystemMemoryUsage? Measured)
{
    /// <summary>
    /// Gets <c>MemAvailable</c>: there is no page cache to reclaim in the simulation, so available
    /// memory equals free memory. Never negative.
    /// </summary>
    public long AvailableBytes => Math.Max(0, TotalBytes - UsedBytes);

    /// <summary>Gets whether the figures come from the measurement of ADR 0068.</summary>
    public bool IsMeasured => Measured is not null;
}

/// <summary>The resident memory of one process, measured or simulated.</summary>
/// <param name="ResidentBytes"><c>VmRSS</c>.</param>
/// <param name="Layers">The measured layers of ADR 0068, or <see langword="null"/> when the process is simulated.</param>
public readonly record struct ProcessMemoryReading(long ResidentBytes, ProcessMemoryUsage? Layers);

/// <summary>
/// Reads the memory figures shared by <c>/proc/meminfo</c>, <c>/proc/&lt;pid&gt;/status</c>,
/// <c>oom_score</c> and the OOM killer (ADR 0068, ADR 0126), so all of them always agree: a measured
/// value when the memory accountant answers, the resource simulator's last sample otherwise.
/// </summary>
public sealed class SystemMemoryProbe
{
    private readonly Func<IProcessManager> _processManager;
    private readonly IResourceSimulator _resources;
    private readonly VirtualHardwareProfile _hardware;
    private readonly IMemoryAccountant? _accountant;

    /// <summary>Creates the probe.</summary>
    /// <param name="processManager">Lazily resolves the process table.</param>
    /// <param name="resources">The resource simulator, source of every simulated figure.</param>
    /// <param name="hardware">The simulated hardware: <c>MemTotal</c>.</param>
    /// <param name="accountant">The memory accountant of ADR 0068; <see langword="null"/> or not measuring keeps every figure simulated.</param>
    public SystemMemoryProbe(
        Func<IProcessManager> processManager,
        IResourceSimulator resources,
        VirtualHardwareProfile hardware,
        IMemoryAccountant? accountant)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        _accountant = accountant;
    }

    /// <summary>Gets whether memory is measured (<c>MemSource</c> is not <c>simulated</c>, ADR 0066).</summary>
    public bool IsMeasuring => _accountant?.IsMeasuring == true;

    /// <summary>Gets <c>MemTotal</c> in bytes.</summary>
    public long TotalBytes => (long)_hardware.MemoryCapacityBytes;

    /// <summary>Gets the live processes; terminal records never count.</summary>
    public IReadOnlyList<ProcessRecord> ActiveProcesses() =>
        [.. _processManager().GetActiveProcesses().Where(static process => !process.IsTerminal)];

    /// <summary>Reads the machine's memory now.</summary>
    /// <param name="active">The live processes, when the caller already holds them.</param>
    public SystemMemorySnapshot ReadSystem(IReadOnlyList<ProcessRecord>? active = null)
    {
        double usedBytes = _resources.GetSystemHistory().LastOrDefault()?.TotalMemoryUsageBytes ?? 0;
        SystemMemoryUsage? measured = null;
        if (_accountant is { IsMeasuring: true } accountant)
        {
            IReadOnlyList<ProcessRecord> processes = active ?? ActiveProcesses();
            if (accountant.TryGetSystemMemory(processes, out SystemMemoryUsage system))
            {
                // ADR 0068 section 2: measured totals (shared images counted once, the OS's own heap
                // included) plus the simulated memory of the processes that stay simulated.
                double simulated = 0;
                foreach (ProcessRecord process in processes)
                {
                    if (!accountant.TryGetProcessMemory(process, processes, out _))
                    {
                        simulated += _resources.GetHistory(process.Pid).LastOrDefault()?.MemoryUsageBytes ?? 0;
                    }
                }

                usedBytes = system.UsedBytes + simulated;
                measured = system;
            }
        }

        return new SystemMemorySnapshot(TotalBytes, (long)Math.Round(usedBytes, MidpointRounding.AwayFromZero), measured);
    }

    /// <summary>Reads one process's resident memory now.</summary>
    /// <param name="process">The process.</param>
    /// <param name="active">The live processes, when the caller already holds them.</param>
    public ProcessMemoryReading ReadProcess(ProcessRecord process, IReadOnlyList<ProcessRecord>? active = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (_accountant is { IsMeasuring: true } accountant
            && accountant.TryGetProcessMemory(process, active ?? ActiveProcesses(), out ProcessMemoryUsage usage))
        {
            return new ProcessMemoryReading(usage.ResidentBytes, usage);
        }

        double simulated = _resources.GetHistory(process.Pid).LastOrDefault()?.MemoryUsageBytes ?? 0;
        return new ProcessMemoryReading((long)Math.Round(simulated, MidpointRounding.AwayFromZero), null);
    }

    /// <summary>
    /// Computes <c>/proc/&lt;pid&gt;/oom_score</c> (ADR 0126, section 2): <c>VmRSS × 1000 / MemTotal +
    /// oom_score_adj</c>, bounded to <c>[0, 2000]</c>. A process at <c>-1000</c> scores <c>0</c>.
    /// </summary>
    /// <param name="residentBytes">The process's <c>VmRSS</c>.</param>
    /// <param name="adjust">Its <c>oom_score_adj</c>.</param>
    public long OomScore(long residentBytes, int adjust)
    {
        if (adjust <= InMemoryProcessOomScoreTable.Exempt)
        {
            return 0;
        }

        long total = Math.Max(1, TotalBytes);
        long points = (long)(Math.Max(0, residentBytes) * 1000d / total);
        return Math.Clamp(points + adjust, 0, 2000);
    }
}
