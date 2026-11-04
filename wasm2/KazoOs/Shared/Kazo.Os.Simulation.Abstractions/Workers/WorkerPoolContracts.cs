namespace Kazo.Os.Simulation.Abstractions.Workers;

/// <summary>What a worker is for (ADR 0071, section 5): the <c>role</c> column of <c>/proc/hackeros/workers</c>.</summary>
public enum WorkerRole
{
    /// <summary>One of the virtual cores: runs commands and pipelines (ADR 0070, section 2).</summary>
    Pool,

    /// <summary>A pool worker added while every core was busy, removed after 30 s empty (ADR 0071, section 3).</summary>
    Overflow,

    /// <summary>Runs one <c>Dedicated</c> process (a service), freed when it ends (ADR 0070, section 3).</summary>
    Dedicated,

    /// <summary>The always-ready worker: runtime loaded, no process yet (ADR 0071, section 1).</summary>
    Ready,

    /// <summary>
    /// The one worker shared by every web app (ADR 0074, like an IIS application pool): created
    /// with the first site, ended when the last one stops.
    /// </summary>
    Web
}

/// <summary>Life-cycle state of one worker, the <c>state</c> column of <c>/proc/hackeros/workers</c>.</summary>
public enum WorkerState
{
    /// <summary>The runtime is loading (cold start).</summary>
    Starting,

    /// <summary>Loaded, no process.</summary>
    Idle,

    /// <summary>Hosts at least one process and answers its heartbeat.</summary>
    Running,

    /// <summary>
    /// Hosts a process and missed its heartbeat for longer than the unresponsive threshold: its thread
    /// is stuck (ADR 0070, section 5).
    /// </summary>
    NotResponding,

    /// <summary>Terminated (escalation step 4, or the pool shrank); kept briefly for the record.</summary>
    Terminated
}

/// <summary>One line of <c>/proc/hackeros/workers</c> (ADR 0071, section 5).</summary>
/// <param name="Id">Worker number, stable for its life.</param>
/// <param name="Role">What it is for.</param>
/// <param name="State">Where it is in its life cycle.</param>
/// <param name="Core">The virtual core it embodies (0-based, <c>cpu1</c> is core 0's worker), or <see langword="null"/> for a dedicated or ready worker.</param>
/// <param name="ProcessIds">The processes it hosts.</param>
/// <param name="RuntimeMemoryBytes">Managed heap of its .NET runtime at the last heartbeat (0 before the first).</param>
/// <param name="StartedAtUtc">When it was created (simulated clock).</param>
/// <param name="LastHeartbeatAgo">Time since its last heartbeat, or <see langword="null"/> before the first.</param>
/// <param name="BusyTime">CPU time its thread spent running processes, from its heartbeats.</param>
public sealed record WorkerSnapshot(
    int Id,
    WorkerRole Role,
    WorkerState State,
    int? Core,
    IReadOnlyList<long> ProcessIds,
    long RuntimeMemoryBytes,
    DateTimeOffset StartedAtUtc,
    TimeSpan? LastHeartbeatAgo,
    TimeSpan BusyTime)
{
    /// <summary>
    /// Gets the managed heap of its .NET runtime at the last heartbeat (0 before the first), the part
    /// of <see cref="RuntimeMemoryBytes"/> really in use: the browser guard of ADR 0126 compares it with
    /// the runtime's memory ceiling, since a WebAssembly memory never shrinks once grown.
    /// </summary>
    public long RuntimeHeapBytes { get; init; }
}

/// <summary>
/// Read-only view of the worker pool for <c>/proc</c> (ADR 0071, section 5) and <c>/proc/stat</c>
/// (one <c>cpuN</c> line per pool worker, ADR 0070, section 2).
/// </summary>
public interface IWorkerPoolStatus
{
    /// <summary>Gets every live worker, ordered by <see cref="WorkerSnapshot.Id"/>.</summary>
    IReadOnlyList<WorkerSnapshot> GetWorkers();

    /// <summary>Gets the ceiling of the worker count (<c>navigator.hardwareConcurrency</c>, at least 2).</summary>
    int Capacity { get; }

    /// <summary>Gets the number of pool workers wanted: one per virtual core.</summary>
    int PoolSize { get; }
}
