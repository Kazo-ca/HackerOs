using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Simulation.Abstractions.Metrics;

/// <summary>
/// Cumulative I/O counters of one process, laid out like Linux's <c>/proc/&lt;pid&gt;/io</c>
/// (<c>proc_pid_io(5)</c>, ADR 0057 section 3).
/// </summary>
/// <param name="ReadChars"><c>rchar</c>: bytes the process read, files and network alike.</param>
/// <param name="WrittenChars"><c>wchar</c>: bytes the process wrote, files and network alike.</param>
/// <param name="ReadSyscalls"><c>syscr</c>: read calls the process issued.</param>
/// <param name="WriteSyscalls"><c>syscw</c>: write calls the process issued.</param>
/// <param name="ReadBytes"><c>read_bytes</c>: the part of <paramref name="ReadChars"/> fetched from persistent storage.</param>
/// <param name="WriteBytes"><c>write_bytes</c>: the part of <paramref name="WrittenChars"/> sent to persistent storage.</param>
/// <param name="CancelledWriteBytes"><c>cancelled_write_bytes</c>: always <c>0</c> in this phase.</param>
public readonly record struct ProcessIoCounters(
    long ReadChars,
    long WrittenChars,
    long ReadSyscalls,
    long WriteSyscalls,
    long ReadBytes,
    long WriteBytes,
    long CancelledWriteBytes)
{
    /// <summary>Gets the counters of a process that has done no I/O yet.</summary>
    public static ProcessIoCounters Zero { get; } = default;
}

/// <summary>
/// Counts the bytes each process really reads and writes (ADR 0057 section 1). Fed by the
/// filesystem service (files) and the app network gateway (network); read by
/// <c>/proc/&lt;pid&gt;/io</c>. Counters live in memory, start at zero and are never persisted.
/// </summary>
public interface IProcessIoAccounting
{
    /// <summary>Records one read call of <paramref name="bytes"/> bytes by <paramref name="pid"/>.</summary>
    /// <param name="pid">Process that issued the read.</param>
    /// <param name="bytes">Bytes returned by the call; zero for a call that hit end of file.</param>
    /// <param name="fromStorage">Whether the bytes came from persistent storage (counted in <c>read_bytes</c> too).</param>
    void RecordRead(ProcessId pid, long bytes, bool fromStorage);

    /// <summary>Records one write call of <paramref name="bytes"/> bytes by <paramref name="pid"/>.</summary>
    /// <param name="pid">Process that issued the write.</param>
    /// <param name="bytes">Bytes handed to the call.</param>
    /// <param name="toStorage">Whether the bytes go to persistent storage (counted in <c>write_bytes</c> too).</param>
    void RecordWrite(ProcessId pid, long bytes, bool toStorage);

    /// <summary>Gets the counters of <paramref name="pid"/>, <see cref="ProcessIoCounters.Zero"/> when it never did I/O.</summary>
    ProcessIoCounters Get(ProcessId pid);

    /// <summary>
    /// Drops the counters of every process outside <paramref name="livePids"/>, as the kernel frees
    /// a task's accounting when it is reaped.
    /// </summary>
    void Retain(IReadOnlyCollection<ProcessId> livePids);
}

/// <summary>Direction of one block-device request.</summary>
public enum BlockIoDirection
{
    /// <summary>A read request.</summary>
    Read,

    /// <summary>A write request.</summary>
    Write
}

/// <summary>
/// Cumulative statistics of one block device, the eleven fields Linux prints after the device name
/// in <c>/proc/diskstats</c> (<c>Documentation/admin-guide/iostats.rst</c>, ADR 0057 section 4).
/// </summary>
/// <param name="ReadsCompleted">Reads completed successfully.</param>
/// <param name="ReadsMerged">Adjacent reads merged; always <c>0</c> (no request queue).</param>
/// <param name="SectorsRead">512-byte sectors read.</param>
/// <param name="MillisecondsReading">Milliseconds spent by all reads.</param>
/// <param name="WritesCompleted">Writes completed successfully.</param>
/// <param name="WritesMerged">Adjacent writes merged; always <c>0</c>.</param>
/// <param name="SectorsWritten">512-byte sectors written.</param>
/// <param name="MillisecondsWriting">Milliseconds spent by all writes.</param>
/// <param name="IosInProgress">Requests issued but not completed yet.</param>
/// <param name="MillisecondsDoingIo">Milliseconds during which at least one request was in flight.</param>
/// <param name="WeightedMillisecondsDoingIo">Milliseconds spent by all requests, each weighted by the queue depth.</param>
public readonly record struct BlockDeviceCounters(
    long ReadsCompleted,
    long ReadsMerged,
    long SectorsRead,
    long MillisecondsReading,
    long WritesCompleted,
    long WritesMerged,
    long SectorsWritten,
    long MillisecondsWriting,
    long IosInProgress,
    long MillisecondsDoingIo,
    long WeightedMillisecondsDoingIo);

/// <summary>
/// Statistics of the OS disk <c>hda</c>, the IndexedDB database behind <c>/</c> (ADR 0057 section 4).
/// The persistence layer reports each transaction it really sends to the browser.
/// </summary>
public interface IBlockDeviceStatistics
{
    /// <summary>Kernel name of the device, <c>hda</c>.</summary>
    string DeviceName { get; }

    /// <summary>Linux major number of the device (<c>8</c>).</summary>
    int Major { get; }

    /// <summary>Linux minor number of the device (<c>0</c>).</summary>
    int Minor { get; }

    /// <summary>Marks one request as issued; pair every call with <see cref="Complete"/>.</summary>
    void Begin();

    /// <summary>Marks one request issued by <see cref="Begin"/> as finished.</summary>
    /// <param name="direction">Whether the request read or wrote.</param>
    /// <param name="bytes">Bytes moved; rounded up to whole 512-byte sectors.</param>
    /// <param name="elapsed">Real time the request took.</param>
    /// <param name="succeeded">Whether it completed; a failed request leaves the completion counters alone.</param>
    void Complete(BlockIoDirection direction, long bytes, TimeSpan elapsed, bool succeeded);

    /// <summary>Gets a snapshot of the cumulative counters.</summary>
    BlockDeviceCounters GetCounters();
}

/// <summary>Names of the network interfaces Kazo OS exposes (ADR 0057 section 5).</summary>
public static class NetworkInterfaceNames
{
    /// <summary>The loopback interface; reserved, always zero in this phase.</summary>
    public const string Loopback = "lo";

    /// <summary>The interface carrying the simulated network and the real-network fallback.</summary>
    public const string Ethernet = "eth0";
}

/// <summary>
/// Cumulative counters of one network interface, the fields of one <c>/proc/net/dev</c> line
/// (ADR 0057 section 5). The FIFO, frame, compressed, multicast, collision and carrier columns are
/// always <c>0</c> and are not stored.
/// </summary>
/// <param name="Name">Interface name, e.g. <c>eth0</c>.</param>
/// <param name="ReceiveBytes">Bytes received.</param>
/// <param name="ReceivePackets">Packets received; a packet is one response.</param>
/// <param name="ReceiveErrors">Receive errors: hosts not found, unreachable or failed exchanges.</param>
/// <param name="ReceiveDropped">Received packets dropped; always <c>0</c>.</param>
/// <param name="TransmitBytes">Bytes sent.</param>
/// <param name="TransmitPackets">Packets sent; a packet is one request.</param>
/// <param name="TransmitErrors">Transmit errors; always <c>0</c>.</param>
/// <param name="TransmitDropped">Sent packets dropped; always <c>0</c>.</param>
public readonly record struct NetworkInterfaceCounters(
    string Name,
    long ReceiveBytes,
    long ReceivePackets,
    long ReceiveErrors,
    long ReceiveDropped,
    long TransmitBytes,
    long TransmitPackets,
    long TransmitErrors,
    long TransmitDropped);

/// <summary>
/// Per-interface traffic counters behind <c>/proc/net/dev</c>, fed by the app network gateway, the
/// single place app traffic goes through (ADR 0057 section 5).
/// </summary>
public interface INetworkInterfaceStatistics
{
    /// <summary>Records one packet of <paramref name="bytes"/> bytes sent on <paramref name="interfaceName"/>.</summary>
    void RecordTransmit(string interfaceName, long bytes);

    /// <summary>Records one packet of <paramref name="bytes"/> bytes received on <paramref name="interfaceName"/>.</summary>
    void RecordReceive(string interfaceName, long bytes);

    /// <summary>Records one receive error (host not found, unreachable, failed exchange).</summary>
    void RecordReceiveError(string interfaceName);

    /// <summary>Gets every interface's counters, <c>lo</c> first.</summary>
    IReadOnlyList<NetworkInterfaceCounters> GetInterfaces();
}

/// <summary>
/// Cumulative time one CPU spent in each state since boot, in <c>USER_HZ</c> (1/100 s of simulated
/// time), the columns of one <c>/proc/stat</c> <c>cpuN</c> line (ADR 0057 section 2). The columns
/// after <c>idle</c> stay at zero: the simulator does not model them.
/// </summary>
/// <param name="User">Time running app processes.</param>
/// <param name="Nice">Time running niced processes; always <c>0</c>.</param>
/// <param name="System">Time running OS processes (system services and processes without an app).</param>
/// <param name="Idle">Time doing nothing.</param>
public readonly record struct CpuTimes(long User, long Nice, long System, long Idle)
{
    /// <summary>Adds two time sets column by column (the aggregate <c>cpu</c> line).</summary>
    public static CpuTimes operator +(CpuTimes left, CpuTimes right) =>
        new(left.User + right.User, left.Nice + right.Nice, left.System + right.System, left.Idle + right.Idle);
}

/// <summary>
/// Accumulates simulated CPU time per core from the resource simulator's samples (ADR 0057
/// section 2): CPU stays simulated while disk and network are really counted.
/// </summary>
public interface ICpuTimeAccounting
{
    /// <summary>Gets the number of cores, <c>ceil(CpuCapacity)</c>, like <c>/proc/cpuinfo</c>.</summary>
    int CoreCount { get; }

    /// <summary>
    /// Adds one sampling interval: every sampled process's CPU usage, split into user or system
    /// time and spread over the cores, the rest of each core's interval going to idle.
    /// </summary>
    /// <param name="samples">Samples the resource simulator produced for this interval.</param>
    /// <param name="processes">Processes those samples describe, to classify user versus system time.</param>
    /// <param name="interval">Simulated time the samples stand for.</param>
    void Record(IReadOnlyList<ProcessResourceSample> samples, IReadOnlyList<ProcessRecord> processes, TimeSpan interval);

    /// <summary>Gets the cumulative times of each core, <c>cpu0</c> first.</summary>
    IReadOnlyList<CpuTimes> GetCores();
}

/// <summary>Space of the OS disk, what <c>statfs</c> reports for <c>/</c> (ADR 0057 section 7).</summary>
/// <param name="UsedBytes">
/// Bytes used: the browser origin's real usage when it reports a quota (ADR 0099 section 2), else
/// the content blocks the deduplicated persistence stores.
/// </param>
/// <param name="QuotaBytes">Browser storage quota for the origin, when the browser tells it.</param>
public readonly record struct StorageUsage(long UsedBytes, long? QuotaBytes);

/// <summary>
/// Measures the space the persistent filesystem occupies, behind <c>HackDriveInfo</c>. Implemented
/// by the browser persistence; the browser quota comes from <c>navigator.storage.estimate()</c>,
/// called by infrastructure code only (ADR 0055).
/// </summary>
public interface IStorageUsageProvider
{
    /// <summary>Measures the current usage.</summary>
    ValueTask<StorageUsage> GetUsageAsync(CancellationToken cancellationToken = default);
}
