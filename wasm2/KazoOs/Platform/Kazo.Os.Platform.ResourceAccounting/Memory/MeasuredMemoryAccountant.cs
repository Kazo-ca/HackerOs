using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.ResourceAccounting.Memory;

/// <summary>
/// The measured memory of ADR 0068: four layers per process, read by <c>/proc/&lt;pid&gt;/status</c>,
/// <c>/proc/meminfo</c> and <c>HackProcess</c>. It only measures: no limit, no OOM killer.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Layer</term><description>Source</description></listheader>
/// <item><term>Image (<c>VmExe</c>, <c>RssFile</c>)</term><description>The app's assembly sizes from the host's catalog; exact.</description></item>
/// <item><term>OS resources (<c>VmData</c>)</term><description>The <see cref="KernelMemoryLedger"/>, plus <see cref="MemoryAccountingConstants.WindowBytes"/> for a window app; exact.</description></item>
/// <item><term>Allocations (<c>HackAllocBytes</c>)</term><description>The <see cref="AllocationTracker"/>, at every context switch; exact.</description></item>
/// <item><term>Resident (<c>RssAnon</c>)</term><description>The real heap, split after each collection by decayed recent allocations; an estimate.</description></item>
/// </list>
/// <para>
/// <b>The split.</b> Every read first ages each process's weight by
/// <see cref="MemoryAccountingConstants.ResidentHalfLife"/> and adds what it allocated since the last
/// read. When the collection count changed (or on the first read), the heap the GC reports, minus
/// what the kernel layer already names, is divided between the live processes and the OS in
/// proportion to their weights. Between two collections the shares stay as they are: a process
/// started since the last collection shows no anonymous memory yet.
/// </para>
/// <para>
/// <b>Measured or simulated.</b> A process is measured when the host's catalog knows its image (every
/// built-in app) or when it has run code under its frame (the shell, an app installed at run time).
/// Anything else (a simulated process that runs no code) stays simulated, as ADR 0066 section 4 requires.
/// </para>
/// </remarks>
public sealed class MeasuredMemoryAccountant : IMemoryAccountant
{
    private readonly AllocationTracker _allocations;
    private readonly KernelMemoryLedger _kernel;
    private readonly IReadOnlyDictionary<string, AppImage> _images;
    private readonly IManagedHeapProbe _probe;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, ResidentState> _residents = [];
    private readonly ResidentState _os = new();
    private long _lastRefresh;
    private int _lastCollectionCount;
    private bool _split;

    /// <summary>Creates the accountant.</summary>
    /// <param name="allocations">The allocation layer, registered on the context-switch hook.</param>
    /// <param name="kernel">The OS-resources layer.</param>
    /// <param name="images">The image of each built-in app, by app id.</param>
    /// <param name="probe">The garbage collector's figures.</param>
    /// <param name="time">The clock aging the weights; <see cref="TimeProvider.System"/> when omitted.</param>
    public MeasuredMemoryAccountant(
        AllocationTracker allocations,
        KernelMemoryLedger kernel,
        IReadOnlyDictionary<string, AppImage> images,
        IManagedHeapProbe probe,
        TimeProvider? time = null)
    {
        _allocations = allocations ?? throw new ArgumentNullException(nameof(allocations));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _time = time ?? TimeProvider.System;
        _lastRefresh = _time.GetTimestamp();
    }

    /// <inheritdoc />
    public bool IsMeasuring => true;

    /// <inheritdoc />
    public bool TryGetProcessMemory(ProcessRecord process, IReadOnlyList<ProcessRecord> active, out ProcessMemoryUsage usage)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(active);

        _allocations.FlushCurrentThread();
        if (!IsMeasured(process))
        {
            usage = default;
            return false;
        }

        lock (_gate)
        {
            Refresh(active);
            usage = Layers(process);
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryGetSystemMemory(IReadOnlyList<ProcessRecord> active, out SystemMemoryUsage usage)
    {
        ArgumentNullException.ThrowIfNull(active);

        _allocations.FlushCurrentThread();
        lock (_gate)
        {
            Refresh(active);
            long anonymous = 0;
            long kernel = _os.AnonymousBytes;
            int measured = 0;
            Dictionary<string, long> mapped = new(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessRecord process in active)
            {
                if (!IsMeasured(process))
                {
                    continue;
                }

                ProcessMemoryUsage layers = Layers(process);
                anonymous += layers.AnonymousBytes;
                kernel += layers.KernelBytes;
                measured++;
                if (_images.TryGetValue(process.AppId, out AppImage? image))
                {
                    // A shared assembly is mapped once, however many processes use it.
                    foreach (AppImageAssembly assembly in image.Assemblies)
                    {
                        mapped[assembly.Name] = assembly.Bytes;
                    }
                }
            }

            usage = new SystemMemoryUsage(anonymous, mapped.Values.Sum(), kernel, measured);
        }

        return true;
    }

    private bool IsMeasured(ProcessRecord process) =>
        !process.IsTerminal && (_images.ContainsKey(process.AppId) || _allocations.HasRun(process.Pid));

    /// <summary>The four layers of one process; called under the lock, after <see cref="Refresh"/>.</summary>
    private ProcessMemoryUsage Layers(ProcessRecord process)
    {
        AppImage image = _images.GetValueOrDefault(process.AppId) ?? AppImage.Empty;
        long kernel = KernelBytes(process);
        long anonymous = _residents.TryGetValue(process.Pid.Value, out ResidentState? state) ? state.AnonymousBytes : 0;
        return new ProcessMemoryUsage(
            image.OwnBytes,
            image.TotalBytes,
            anonymous,
            kernel,
            _allocations.GetAllocatedBytes(process.Pid));
    }

    private long KernelBytes(ProcessRecord process) =>
        _kernel.GetBytes(process.Pid) + (process.Kind == AppKind.Window ? MemoryAccountingConstants.WindowBytes : 0);

    /// <summary>
    /// Ages the weights, adds the allocations made since the last call, forgets dead processes, and
    /// splits the heap again when a collection happened. Called under the lock.
    /// </summary>
    private void Refresh(IReadOnlyList<ProcessRecord> active)
    {
        long now = _time.GetTimestamp();
        TimeSpan elapsed = _time.GetElapsedTime(_lastRefresh, now);
        _lastRefresh = now;
        double decay = elapsed <= TimeSpan.Zero
            ? 1d
            : Math.Pow(0.5, elapsed / MemoryAccountingConstants.ResidentHalfLife);

        HashSet<long> live = [];
        foreach (ProcessRecord process in active)
        {
            if (!IsMeasured(process))
            {
                continue;
            }

            live.Add(process.Pid.Value);
            if (!_residents.TryGetValue(process.Pid.Value, out ResidentState? state))
            {
                state = new ResidentState();
                _residents[process.Pid.Value] = state;
            }

            state.Age(decay, _allocations.GetAllocatedBytes(process.Pid));
        }

        _os.Age(decay, _allocations.OsAllocatedBytes);
        foreach (long pid in _residents.Keys.Where(pid => !live.Contains(pid)).ToList())
        {
            _residents.Remove(pid);
        }

        _allocations.Retain(live);
        _kernel.Retain(live);

        int collections = _probe.CollectionCount;
        if (_split && collections == _lastCollectionCount)
        {
            return;
        }

        _split = true;
        _lastCollectionCount = collections;
        Split(active);
    }

    /// <summary>
    /// Divides the heap, minus what the kernel layer already names, between the live processes and
    /// the OS, in proportion to their weights. Called under the lock.
    /// </summary>
    private void Split(IReadOnlyList<ProcessRecord> active)
    {
        long named = 0;
        foreach (ProcessRecord process in active)
        {
            if (_residents.ContainsKey(process.Pid.Value))
            {
                named += _kernel.GetBytes(process.Pid);
            }
        }

        long pool = Math.Max(0, _probe.HeapSizeBytes - named);
        double total = _os.Weight + _residents.Values.Sum(static state => state.Weight);
        if (total <= 0)
        {
            foreach (ResidentState state in _residents.Values)
            {
                state.AnonymousBytes = 0;
            }

            _os.AnonymousBytes = pool;
            return;
        }

        long given = 0;
        foreach (ResidentState state in _residents.Values)
        {
            state.AnonymousBytes = (long)Math.Round(pool * (state.Weight / total), MidpointRounding.AwayFromZero);
            given += state.AnonymousBytes;
        }

        // The OS keeps the rest, rounding included: the shares always add up to the heap.
        _os.AnonymousBytes = Math.Max(0, pool - given);
    }

    /// <summary>One process's (or the OS's) weight in the split and its last share.</summary>
    private sealed class ResidentState
    {
        private long _counted;
        private bool _started;

        /// <summary>Gets the decayed recent allocations.</summary>
        public double Weight { get; private set; }

        /// <summary>Gets or sets the share of the heap given at the last split.</summary>
        public long AnonymousBytes { get; set; }

        /// <summary>Ages the weight and adds what was allocated since the previous call.</summary>
        /// <param name="decay">The aging factor for the elapsed time.</param>
        /// <param name="allocated">The cumulative allocation counter.</param>
        public void Age(double decay, long allocated)
        {
            long fresh = _started ? Math.Max(0, allocated - _counted) : allocated;
            _started = true;
            _counted = allocated;
            Weight = (Weight * decay) + fresh;
        }
    }
}
