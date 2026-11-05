namespace Kazo.Os.Apps.SystemMonitor.Metrics;

/// <summary>A fixed-length window of the latest values of one measurement, oldest first.</summary>
public sealed class MetricHistory
{
    private readonly Queue<double> _values;

    /// <summary>Initializes an empty history.</summary>
    /// <param name="capacity">Values kept; older ones fall off.</param>
    public MetricHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _values = new Queue<double>(capacity);
    }

    /// <summary>Gets how many values are kept.</summary>
    public int Capacity { get; }

    /// <summary>Gets the kept values, oldest first.</summary>
    public IReadOnlyList<double> Values => [.. _values];

    /// <summary>Gets the newest value, or <c>0</c> when empty.</summary>
    public double Latest => _values.Count == 0 ? 0 : _values.Last();

    /// <summary>Appends a value, dropping the oldest when full.</summary>
    /// <param name="value">The new value.</param>
    public void Add(double value)
    {
        if (_values.Count == Capacity)
        {
            _values.Dequeue();
        }

        _values.Enqueue(value);
    }
}

/// <summary>The storage counters of one process at one reading, from <c>/proc/&lt;pid&gt;/io</c>.</summary>
/// <param name="ReadBytes"><c>read_bytes</c>.</param>
/// <param name="WriteBytes"><c>write_bytes</c>.</param>
public readonly record struct ProcessIoSample(long ReadBytes, long WriteBytes);

/// <summary>Disk throughput of one process between two readings, in bytes per second.</summary>
/// <param name="ReadPerSecond">Bytes read from the disk per second.</param>
/// <param name="WritePerSecond">Bytes written to the disk per second.</param>
public readonly record struct ProcessIoRate(double ReadPerSecond, double WritePerSecond);

/// <summary>
/// Everything read from <c>/proc</c> at one refresh (ADR 0057, section 8). A missing file leaves
/// its member <see langword="null"/> or empty: the other tabs keep working.
/// </summary>
/// <param name="TimestampSeconds">Monotonic real time of the reading, in seconds: the divisor of the disk and network rates.</param>
/// <param name="Stat"><c>/proc/stat</c>.</param>
/// <param name="Memory"><c>/proc/meminfo</c>.</param>
/// <param name="Load"><c>/proc/loadavg</c>.</param>
/// <param name="UptimeSeconds">First field of <c>/proc/uptime</c>.</param>
/// <param name="Disks"><c>/proc/diskstats</c>.</param>
/// <param name="Network"><c>/proc/net/dev</c>.</param>
/// <param name="ProcessIo">Storage counters per PID, for the processes whose <c>io</c> was readable.</param>
/// <param name="Workers"><c>/proc/kazoos/workers</c>; <see langword="null"/> on a host without Web Workers (ADR 0071).</param>
public sealed record MetricsReading(
    double TimestampSeconds,
    ProcStat? Stat,
    ProcMemInfo? Memory,
    ProcLoadAverage? Load,
    double? UptimeSeconds,
    IReadOnlyList<ProcDiskStat> Disks,
    IReadOnlyList<ProcNetDevice> Network,
    IReadOnlyDictionary<long, ProcessIoSample> ProcessIo,
    ProcWorkers? Workers = null);

/// <summary>Live activity of one disk of <c>/proc/diskstats</c>.</summary>
public sealed class DiskActivity
{
    internal DiskActivity(string name, int capacity)
    {
        Name = name;
        ReadPerSecond = new MetricHistory(capacity);
        WritePerSecond = new MetricHistory(capacity);
        BusyPercent = new MetricHistory(capacity);
    }

    /// <summary>Gets the device name (<c>hda</c>).</summary>
    public string Name { get; }

    /// <summary>Gets the read throughput history, in bytes per second.</summary>
    public MetricHistory ReadPerSecond { get; }

    /// <summary>Gets the write throughput history, in bytes per second.</summary>
    public MetricHistory WritePerSecond { get; }

    /// <summary>Gets the share of time with a request in flight (<c>io_ticks</c>), in percent, like <c>iostat %util</c>.</summary>
    public MetricHistory BusyPercent { get; }

    /// <summary>Gets the counters of the last reading.</summary>
    public ProcDiskStat? Counters { get; internal set; }
}

/// <summary>Live activity of one interface of <c>/proc/net/dev</c>.</summary>
public sealed class NetworkActivity
{
    internal NetworkActivity(string name, int capacity)
    {
        Name = name;
        ReceivePerSecond = new MetricHistory(capacity);
        TransmitPerSecond = new MetricHistory(capacity);
    }

    /// <summary>Gets the interface name (<c>eth0</c>).</summary>
    public string Name { get; }

    /// <summary>Gets the receive throughput history, in bytes per second.</summary>
    public MetricHistory ReceivePerSecond { get; }

    /// <summary>Gets the transmit throughput history, in bytes per second.</summary>
    public MetricHistory TransmitPerSecond { get; }

    /// <summary>Gets the counters of the last reading.</summary>
    public ProcNetDevice? Counters { get; internal set; }
}

/// <summary>
/// Turns successive <see cref="MetricsReading"/>s into what the task manager draws: usage and
/// throughput are <b>differences between two readings</b>, like <c>top</c>, <c>iostat</c> and
/// <c>ifstat</c> (ADR 0058, section 2), kept for <see cref="HistoryLength"/> readings.
/// </summary>
/// <remarks>
/// CPU usage needs no clock: <c>/proc/stat</c> counts busy and idle time on the same simulated
/// clock, so their ratio is the usage. Disk and network counters are real bytes, divided by the
/// real time between the readings. A counter that went backwards (an OS restart) yields <c>0</c>.
/// </remarks>
public sealed class SystemMetrics
{
    /// <summary>Readings kept: one minute at one reading per second.</summary>
    public const int HistoryLength = 60;

    private readonly Dictionary<string, DiskActivity> _disks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NetworkActivity> _network = new(StringComparer.Ordinal);
    private readonly List<MetricHistory> _cores = [];
    private Dictionary<long, ProcessIoRate> _processRates = [];
    private MetricsReading? _previous;

    /// <summary>Gets the total CPU usage history, in percent of every core.</summary>
    public MetricHistory CpuTotal { get; } = new(HistoryLength);

    /// <summary>Gets one CPU usage history per core, in percent.</summary>
    public IReadOnlyList<MetricHistory> CpuCores => _cores;

    /// <summary>Gets the memory-in-use history, in percent of the total.</summary>
    public MetricHistory MemoryUsedPercent { get; } = new(HistoryLength);

    /// <summary>Gets the disks seen, in <c>/proc/diskstats</c> order.</summary>
    public IReadOnlyList<DiskActivity> Disks => [.. _disks.Values];

    /// <summary>Gets the interfaces seen, in <c>/proc/net/dev</c> order.</summary>
    public IReadOnlyList<NetworkActivity> Network => [.. _network.Values];

    /// <summary>Gets the disk throughput of each process between the last two readings.</summary>
    public IReadOnlyDictionary<long, ProcessIoRate> ProcessRates => _processRates;

    /// <summary>Gets the last reading, or <see langword="null"/> before the first.</summary>
    public MetricsReading? Latest => _previous;

    /// <summary>Gets how many readings were applied.</summary>
    public int ReadingCount { get; private set; }

    /// <summary>
    /// Applies a new reading. The first one only sets the baseline: rates need two readings.
    /// </summary>
    /// <param name="reading">The new reading.</param>
    public void Apply(MetricsReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        MetricsReading? previous = _previous;
        _previous = reading;
        ReadingCount++;

        if (reading.Memory is { } memory)
        {
            MemoryUsedPercent.Add(memory.UsedPercent);
        }

        UpdateDiskCounters(reading);
        UpdateNetworkCounters(reading);

        if (previous is null)
        {
            return;
        }

        double seconds = reading.TimestampSeconds - previous.TimestampSeconds;
        ApplyCpu(previous.Stat, reading.Stat);
        if (seconds <= 0)
        {
            return;
        }

        ApplyDisks(previous, reading, seconds);
        ApplyNetwork(previous, reading, seconds);
        ApplyProcesses(previous, reading, seconds);
    }

    /// <summary>Gets the disk throughput of one process, zero when unknown.</summary>
    /// <param name="pid">The PID.</param>
    public ProcessIoRate RateOf(long pid) => _processRates.TryGetValue(pid, out ProcessIoRate rate) ? rate : default;

    private void ApplyCpu(ProcStat? before, ProcStat? after)
    {
        if (before is null || after is null)
        {
            return;
        }

        CpuTotal.Add(after.Total.UsagePercentSince(before.Total));
        while (_cores.Count < after.Cores.Count)
        {
            _cores.Add(new MetricHistory(HistoryLength));
        }

        for (int core = 0; core < after.Cores.Count; core++)
        {
            CpuTimesSample earlier = core < before.Cores.Count ? before.Cores[core] : after.Cores[core];
            _cores[core].Add(after.Cores[core].UsagePercentSince(earlier));
        }
    }

    private void UpdateDiskCounters(MetricsReading reading)
    {
        foreach (ProcDiskStat disk in reading.Disks)
        {
            if (!_disks.TryGetValue(disk.Name, out DiskActivity? activity))
            {
                activity = new DiskActivity(disk.Name, HistoryLength);
                _disks[disk.Name] = activity;
            }

            activity.Counters = disk;
        }
    }

    private void UpdateNetworkCounters(MetricsReading reading)
    {
        foreach (ProcNetDevice device in reading.Network)
        {
            if (!_network.TryGetValue(device.Name, out NetworkActivity? activity))
            {
                activity = new NetworkActivity(device.Name, HistoryLength);
                _network[device.Name] = activity;
            }

            activity.Counters = device;
        }
    }

    private void ApplyDisks(MetricsReading before, MetricsReading after, double seconds)
    {
        foreach (ProcDiskStat disk in after.Disks)
        {
            ProcDiskStat? earlier = before.Disks.FirstOrDefault(candidate => candidate.Name == disk.Name);
            if (earlier is null)
            {
                continue;
            }

            DiskActivity activity = _disks[disk.Name];
            activity.ReadPerSecond.Add(Rate(earlier.BytesRead, disk.BytesRead, seconds));
            activity.WritePerSecond.Add(Rate(earlier.BytesWritten, disk.BytesWritten, seconds));
            double busyMilliseconds = Math.Max(0, disk.MillisecondsDoingIo - earlier.MillisecondsDoingIo);
            activity.BusyPercent.Add(Math.Clamp(busyMilliseconds / (seconds * 1000) * 100, 0, 100));
        }
    }

    private void ApplyNetwork(MetricsReading before, MetricsReading after, double seconds)
    {
        foreach (ProcNetDevice device in after.Network)
        {
            ProcNetDevice? earlier = before.Network.FirstOrDefault(candidate => candidate.Name == device.Name);
            if (earlier is null)
            {
                continue;
            }

            NetworkActivity activity = _network[device.Name];
            activity.ReceivePerSecond.Add(Rate(earlier.ReceiveBytes, device.ReceiveBytes, seconds));
            activity.TransmitPerSecond.Add(Rate(earlier.TransmitBytes, device.TransmitBytes, seconds));
        }
    }

    private void ApplyProcesses(MetricsReading before, MetricsReading after, double seconds)
    {
        Dictionary<long, ProcessIoRate> rates = [];
        foreach ((long pid, ProcessIoSample io) in after.ProcessIo)
        {
            if (before.ProcessIo.TryGetValue(pid, out ProcessIoSample earlier))
            {
                rates[pid] = new ProcessIoRate(
                    Rate(earlier.ReadBytes, io.ReadBytes, seconds),
                    Rate(earlier.WriteBytes, io.WriteBytes, seconds));
            }
        }

        _processRates = rates;
    }

    private static double Rate(long before, long after, double seconds) =>
        after < before ? 0 : (after - before) / seconds;
}
