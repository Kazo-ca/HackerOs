using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// The scheduler tick of the simulated machine (ADR 0012, ADR 0057 section 2): once per sampling
/// interval of simulated time, asks the <see cref="IResourceSimulator"/> for one sample of every
/// active process and folds the CPU usage into <see cref="ICpuTimeAccounting"/>.
/// </summary>
/// <remarks>
/// <para>
/// Before this service, nothing called <see cref="IResourceSimulator.Tick"/> in a live host, so
/// <c>/proc/&lt;pid&gt;/status</c>, <c>meminfo</c> and <c>loadavg</c> stayed at zero. Sampling every
/// second rather than every 100 ms clock tick keeps the bounded simulator history (120 samples) at
/// two minutes, enough for the task manager's graphs, and costs one pass over the process table
/// per second.
/// </para>
/// <para>
/// Each pass also drops the I/O counters of processes that are gone
/// (<see cref="IProcessIoAccounting.Retain"/>), like the kernel freeing a reaped task.
/// </para>
/// </remarks>
public sealed class ResourceSamplingService : IDisposable
{
    /// <summary>Default sampling interval: one simulated second.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly ISimulationClock _clock;
    private readonly IResourceSimulator _simulator;
    private readonly Func<IProcessManager> _processManager;
    private readonly ICpuTimeAccounting _cpu;
    private readonly IProcessIoAccounting? _io;
    private readonly Lock _gate = new();
    private IDisposable? _scheduled;
    private bool _disposed;

    /// <summary>Initializes a stopped sampler.</summary>
    /// <param name="clock">Simulation clock the sampling is scheduled on.</param>
    /// <param name="simulator">Resource simulator producing each sample.</param>
    /// <param name="processManager">Lazily resolves the process table sampled at each pass.</param>
    /// <param name="cpu">CPU-time accounting fed with each pass.</param>
    /// <param name="io">Optional per-process I/O accounting pruned at each pass.</param>
    /// <param name="interval">Simulated time between passes; <see cref="DefaultInterval"/> when omitted.</param>
    public ResourceSamplingService(
        ISimulationClock clock,
        IResourceSimulator simulator,
        Func<IProcessManager> processManager,
        ICpuTimeAccounting cpu,
        IProcessIoAccounting? io = null,
        TimeSpan? interval = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _io = io;
        Interval = interval ?? DefaultInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Interval, TimeSpan.Zero, nameof(interval));
    }

    /// <summary>Gets the simulated time between two passes.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Gets whether passes are scheduled.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _scheduled is not null;
            }
        }
    }

    /// <summary>Schedules the first pass one interval from now. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _scheduled ??= _clock.Schedule(Interval, OnDue);
        }
    }

    /// <summary>
    /// Runs one pass now: samples every active process and records one interval of CPU time.
    /// Called by the clock; public for tests and hosts that sample explicitly.
    /// </summary>
    /// <returns>The samples produced.</returns>
    public IReadOnlyList<ProcessResourceSample> SampleNow()
    {
        IReadOnlyList<ProcessRecord> active = _processManager().GetActiveProcesses();
        IReadOnlyList<ProcessResourceSample> samples = _simulator.Tick(active);
        _cpu.Record(samples, active, Interval);
        _io?.Retain([.. active.Select(static process => process.Pid)]);
        return samples;
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
            SampleNow();
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
