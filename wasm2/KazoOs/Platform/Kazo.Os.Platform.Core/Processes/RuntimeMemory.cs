using Kazo.Os.Simulation.Abstractions.Workers;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// The memory of one real .NET runtime of the page (ADR 0126, section 5): the main thread's, or a
/// Web Worker's.
/// </summary>
/// <param name="Id">Stable id: <c>main</c>, or <c>worker-&lt;n&gt;</c>.</param>
/// <param name="Name">Name shown in <c>kern.log</c> and the notification, e.g. <c>kazoos-worker-2</c>.</param>
/// <param name="UsedBytes">The runtime's managed heap: what it really holds, the part that can make its memory grow.</param>
/// <param name="CeilingBytes">The most memory the runtime can ever have (its WebAssembly memory's maximum).</param>
/// <param name="ProcessIds">
/// The processes the runtime hosts, or <see langword="null"/> for the main thread, which hosts every
/// process no worker hosts.
/// </param>
public sealed record RuntimeMemoryReading(string Id, string Name, long UsedBytes, long CeilingBytes, IReadOnlyCollection<long>? ProcessIds)
{
    /// <summary>Gets the share of the ceiling in use, from 0 to 1.</summary>
    public double Ratio => CeilingBytes <= 0 ? 0 : (double)UsedBytes / CeilingBytes;
}

/// <summary>Reads the real memory of the page's .NET runtimes, for the browser guard of ADR 0126 (section 5).</summary>
public interface IRuntimeMemorySource
{
    /// <summary>Reads every runtime now.</summary>
    IReadOnlyList<RuntimeMemoryReading> Read();

    /// <summary>
    /// Asks a runtime to give memory back after the OOM killer dropped one of its processes: on the main
    /// thread, a full garbage collection; a worker collects by itself once its process is gone.
    /// </summary>
    /// <param name="runtimeId">The runtime's <see cref="RuntimeMemoryReading.Id"/>.</param>
    void Collect(string runtimeId);

    /// <summary>
    /// The OOM killer is about to kill <paramref name="pid"/>: once it is gone, its runtime must give
    /// its memory back to the browser. A pool worker's WebAssembly memory never shrinks, so the worker
    /// is replaced as soon as it hosts no process any more (ADR 0126, section 5; ADR 0142).
    /// </summary>
    /// <param name="pid">The victim.</param>
    void ReleaseProcess(long pid);
}

/// <summary>
/// A worker pool that can give a worker's memory back to the browser after an OOM kill (ADR 0142):
/// it replaces the worker that hosted the victim once that worker hosts no process any more.
/// </summary>
public interface IWorkerMemoryReclaimer
{
    /// <summary>Marks the worker hosting <paramref name="pid"/> to be replaced once it is idle.</summary>
    /// <param name="pid">The process the OOM killer is about to kill.</param>
    /// <returns>Whether a worker hosts the process.</returns>
    bool RecycleAfterOomKill(long pid);
}

/// <summary>
/// The page's real runtimes in a browser (ADR 0126, section 5): the main thread's .NET runtime and each
/// Web Worker's (ADR 0070), each against the maximum of its WebAssembly memory.
/// </summary>
/// <remarks>
/// <para>
/// The ceiling is the maximum the .NET WebAssembly runtime declares for its memory: 32 768 pages of
/// 64 KiB, 2 GiB (ADR 0142 §4: lower than the 4 GiB of wasm32 that ADR 0126 assumed). The figure
/// compared with it is the managed heap, not the size of the WebAssembly memory: that memory never
/// shrinks, so once grown it would read as full forever, while the heap is what has to fit in it.
/// </para>
/// <para>
/// The main thread is read only in a browser (<see cref="OperatingSystem.IsBrowser"/>): elsewhere (the
/// unit tests, a desktop runtime) the process's memory is not the page's.
/// </para>
/// </remarks>
public sealed class BrowserRuntimeMemorySource : IRuntimeMemorySource
{
    /// <summary>The maximum of the .NET runtime's WebAssembly memory: 32 768 pages of 64 KiB.</summary>
    public const long WebAssemblyMemoryCeilingBytes = 32_768L * 65_536;

    /// <summary>Id of the main thread's runtime.</summary>
    public const string MainRuntimeId = "main";

    private readonly Func<IWorkerPoolStatus?> _workers;
    private readonly bool _includeMainThread;
    private readonly long _ceiling;

    /// <summary>Creates the source.</summary>
    /// <param name="workers">Lazily resolves the worker pool; <see langword="null"/> on a host without workers.</param>
    /// <param name="includeMainThread">Whether to read the main thread's runtime; <see langword="null"/> reads it only in a browser.</param>
    /// <param name="ceilingBytes">The ceiling of every runtime; <see cref="WebAssemblyMemoryCeilingBytes"/> when omitted.</param>
    public BrowserRuntimeMemorySource(Func<IWorkerPoolStatus?> workers, bool? includeMainThread = null, long? ceilingBytes = null)
    {
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _includeMainThread = includeMainThread ?? OperatingSystem.IsBrowser();
        _ceiling = ceilingBytes ?? WebAssemblyMemoryCeilingBytes;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_ceiling, 0, nameof(ceilingBytes));
    }

    /// <inheritdoc />
    public IReadOnlyList<RuntimeMemoryReading> Read()
    {
        List<RuntimeMemoryReading> readings = [];
        if (_includeMainThread)
        {
            long heap = GC.GetGCMemoryInfo().HeapSizeBytes;
            readings.Add(new RuntimeMemoryReading(MainRuntimeId, "main thread", heap > 0 ? heap : GC.GetTotalMemory(false), _ceiling, null));
        }

        if (_workers() is { } pool)
        {
            foreach (WorkerSnapshot worker in pool.GetWorkers())
            {
                if (worker.State is WorkerState.Terminated or WorkerState.Starting)
                {
                    continue;
                }

                long used = worker.RuntimeHeapBytes > 0 ? worker.RuntimeHeapBytes : worker.RuntimeMemoryBytes;
                readings.Add(new RuntimeMemoryReading($"worker-{worker.Id}", $"kazoos-worker-{worker.Id}", used, _ceiling, worker.ProcessIds));
            }
        }

        return readings;
    }

    /// <inheritdoc />
    public void Collect(string runtimeId)
    {
        if (runtimeId == MainRuntimeId)
        {
            // The killed process's objects are unreachable now: give the heap back at once.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }

    /// <inheritdoc />
    public void ReleaseProcess(long pid)
    {
        if (_workers() is IWorkerMemoryReclaimer reclaimer)
        {
            reclaimer.RecycleAfterOomKill(pid);
        }
    }
}
