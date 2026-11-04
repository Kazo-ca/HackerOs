namespace Kazo.Os.Platform.WorkerHost.Host;

/// <summary>Settings of the worker pool (ADR 0070, 0071). The defaults are the ADRs' values.</summary>
public sealed record WorkerPoolOptions
{
    /// <summary>Gets the heartbeat period of each worker (ADR 0070, section 5: 250 ms).</summary>
    public TimeSpan HeartbeatPeriod { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets how long a worker hosting a process may stay silent before its processes are shown
    /// "Not responding": <c>UnresponsiveThresholdMs</c> of the CPU measurement (ADR 0067, 5 s).
    /// </summary>
    public TimeSpan UnresponsiveThreshold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets how old a heartbeat may be for its worker to count as free for a new command (ADR 0071,
    /// section 3: a worker whose thread is running a slice is not free). Default: three periods.
    /// </summary>
    public TimeSpan BusyThreshold { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Gets how long a worker has to confirm it dropped a killed process (escalation step 3) before
    /// the OS terminates it (step 4, ADR 0069, section 3).
    /// </summary>
    public TimeSpan KillAcknowledgeTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets how long an overflow worker stays empty before the pool returns to its size (ADR 0071, section 3: 30 s).</summary>
    public TimeSpan OverflowIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the longest wait for the browser to be idle before the first worker starts (ADR 0071, section 2: 2 s).</summary>
    public TimeSpan IdleWaitTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the number of virtual cores, hence of pool workers (<c>ceil(CpuCapacity)</c> of the
    /// virtual hardware); clamped to [1, ceiling - 1] so the ready worker always fits (ADR 0070, section 2).
    /// </summary>
    public int VirtualCores { get; init; } = 1;

    /// <summary>Gets the assembly whose exported entry point receives the worker's messages.</summary>
    public string EntryAssembly { get; init; } = "Kazo.Os.Platform.WorkerHost";

    /// <summary>Gets the lazy-loaded assemblies a worker loads while warming up (the built-in commands).</summary>
    public IReadOnlyList<string> PreloadAssemblies { get; init; } = [];

    /// <summary>
    /// Gets the app assemblies known to this build (<c>BuildKnownLazyAssemblies</c>, ADR 0124, section 4):
    /// a worker loads any of them on demand and keeps it loaded, and refuses an assembly that is neither
    /// in this list nor already part of its runtime (exit code 127, like a command that is not found).
    /// Empty: no list is enforced (a host that composes its apps as ordinary references, the tests).
    /// </summary>
    public IReadOnlyList<string> KnownAssemblies { get; init; } = [];

    /// <summary>Gets whether workers measure the CPU time of their processes (follows the CPU measurement, ADR 0066).</summary>
    public bool MeterCpu { get; init; } = true;

    /// <summary>
    /// Gets the period of the browser's memory measurement of the workers
    /// (<c>performance.measureUserAgentSpecificMemory()</c>, ADR 0131 section 4), when the runtime
    /// offers it. A measurement older than three periods is dropped for the heartbeat's figure.
    /// </summary>
    public TimeSpan MemoryMeasurePeriod { get; init; } = TimeSpan.FromSeconds(20);
}
