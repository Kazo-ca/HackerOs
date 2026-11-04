using System.Globalization;

namespace Kazo.Os.Apps.SystemMonitor.Metrics;

/// <summary>
/// Cumulated CPU time of one line of <c>/proc/stat</c>, in <c>USER_HZ</c> (ADR 0057, section 2).
/// </summary>
/// <param name="User">Time running apps.</param>
/// <param name="Nice">Time running niced apps (always <c>0</c> today).</param>
/// <param name="System">Time running OS processes.</param>
/// <param name="Idle">Time doing nothing.</param>
/// <param name="IoWait">Time waiting for I/O (always <c>0</c> today).</param>
/// <param name="Irq">Time serving interrupts (always <c>0</c> today).</param>
/// <param name="SoftIrq">Time serving soft interrupts (always <c>0</c> today).</param>
/// <param name="Steal">Time stolen by the hypervisor (always <c>0</c> today).</param>
public readonly record struct CpuTimesSample(long User, long Nice, long System, long Idle, long IoWait, long Irq, long SoftIrq, long Steal)
{
    /// <summary>Gets the time not idle, as <c>top</c> counts it (idle and iowait are idle).</summary>
    public long Busy => User + Nice + System + Irq + SoftIrq + Steal;

    /// <summary>Gets every unit of time, busy or not.</summary>
    public long Total => Busy + Idle + IoWait;

    /// <summary>
    /// Gets the busy share, in percent, between <paramref name="previous"/> and this reading, the
    /// way <c>top</c> computes it; <c>0</c> when no time passed.
    /// </summary>
    /// <param name="previous">The earlier reading of the same CPU.</param>
    public double UsagePercentSince(CpuTimesSample previous)
    {
        long total = Total - previous.Total;
        long busy = Busy - previous.Busy;
        return total <= 0 ? 0 : Math.Clamp(busy * 100d / total, 0, 100);
    }
}

/// <summary>A parsed <c>/proc/stat</c>.</summary>
/// <param name="Total">The aggregate <c>cpu</c> line.</param>
/// <param name="Cores">One <c>cpuN</c> line per core, in order.</param>
/// <param name="BootTime"><c>btime</c>: boot time, in Unix seconds.</param>
/// <param name="Processes"><c>processes</c>: processes created since boot.</param>
/// <param name="ProcessesRunning"><c>procs_running</c>.</param>
public sealed record ProcStat(CpuTimesSample Total, IReadOnlyList<CpuTimesSample> Cores, long BootTime, long Processes, long ProcessesRunning);

/// <summary>A parsed <c>/proc/meminfo</c>, in kilobytes, with every line kept for display.</summary>
/// <param name="TotalKilobytes"><c>MemTotal</c>.</param>
/// <param name="FreeKilobytes"><c>MemFree</c>.</param>
/// <param name="AvailableKilobytes"><c>MemAvailable</c>.</param>
/// <param name="Fields">Every line, in file order.</param>
public sealed record ProcMemInfo(long TotalKilobytes, long FreeKilobytes, long AvailableKilobytes, IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    /// <summary>Gets the memory in use, in bytes: total minus available, as <c>free</c> prints it.</summary>
    public long UsedBytes => Math.Max(0, TotalKilobytes - AvailableKilobytes) * 1024;

    /// <summary>Gets the total memory, in bytes.</summary>
    public long TotalBytes => TotalKilobytes * 1024;

    /// <summary>Gets the share of memory in use, in percent.</summary>
    public double UsedPercent => TotalKilobytes <= 0 ? 0 : Math.Clamp(UsedBytes * 100d / TotalBytes, 0, 100);
}

/// <summary>One processor block of <c>/proc/cpuinfo</c>.</summary>
/// <param name="Fields">Its <c>key : value</c> lines, in file order.</param>
public sealed record ProcCpuInfoEntry(IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    /// <summary>Gets one field, or an empty string.</summary>
    /// <param name="key">Field name (<c>model name</c>).</param>
    public string this[string key] => Fields.FirstOrDefault(field => field.Key == key).Value ?? string.Empty;
}

/// <summary>A parsed <c>/proc/loadavg</c>.</summary>
/// <param name="OneMinute">Load over 1 minute.</param>
/// <param name="FiveMinutes">Load over 5 minutes.</param>
/// <param name="FifteenMinutes">Load over 15 minutes.</param>
/// <param name="Running">Runnable processes.</param>
/// <param name="Total">Every process.</param>
/// <param name="LastPid">Last PID handed out.</param>
public sealed record ProcLoadAverage(double OneMinute, double FiveMinutes, double FifteenMinutes, long Running, long Total, long LastPid);

/// <summary>One line of <c>/proc/diskstats</c> (the first fourteen fields, <c>Documentation/admin-guide/iostats.rst</c>).</summary>
/// <param name="Major">Major device number.</param>
/// <param name="Minor">Minor device number.</param>
/// <param name="Name">Device name (<c>hda</c>).</param>
/// <param name="ReadsCompleted">Reads completed.</param>
/// <param name="ReadsMerged">Reads merged.</param>
/// <param name="SectorsRead">512-byte sectors read.</param>
/// <param name="MillisecondsReading">Milliseconds spent reading.</param>
/// <param name="WritesCompleted">Writes completed.</param>
/// <param name="WritesMerged">Writes merged.</param>
/// <param name="SectorsWritten">512-byte sectors written.</param>
/// <param name="MillisecondsWriting">Milliseconds spent writing.</param>
/// <param name="IosInProgress">Requests in flight.</param>
/// <param name="MillisecondsDoingIo">Milliseconds with at least one request in flight.</param>
/// <param name="WeightedMillisecondsDoingIo">Milliseconds weighted by queue depth.</param>
public sealed record ProcDiskStat(
    int Major,
    int Minor,
    string Name,
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
    long WeightedMillisecondsDoingIo)
{
    /// <summary>Size of a sector in <c>diskstats</c>, whatever the device.</summary>
    public const int SectorSize = 512;

    /// <summary>Gets the bytes read.</summary>
    public long BytesRead => SectorsRead * SectorSize;

    /// <summary>Gets the bytes written.</summary>
    public long BytesWritten => SectorsWritten * SectorSize;
}

/// <summary>One interface line of <c>/proc/net/dev</c>.</summary>
/// <param name="Name">Interface name (<c>lo</c>, <c>eth0</c>).</param>
/// <param name="ReceiveBytes">Bytes received.</param>
/// <param name="ReceivePackets">Packets received.</param>
/// <param name="ReceiveErrors">Receive errors.</param>
/// <param name="ReceiveDropped">Received packets dropped.</param>
/// <param name="TransmitBytes">Bytes sent.</param>
/// <param name="TransmitPackets">Packets sent.</param>
/// <param name="TransmitErrors">Transmit errors.</param>
/// <param name="TransmitDropped">Sent packets dropped.</param>
public sealed record ProcNetDevice(
    string Name,
    long ReceiveBytes,
    long ReceivePackets,
    long ReceiveErrors,
    long ReceiveDropped,
    long TransmitBytes,
    long TransmitPackets,
    long TransmitErrors,
    long TransmitDropped);

/// <summary>One Web Worker of <c>/proc/kazoos/workers</c> (ADR 0071, section 5).</summary>
/// <param name="Id">Worker number.</param>
/// <param name="Role"><c>pool</c>, <c>overflow</c>, <c>dedicated</c> or <c>ready</c>.</param>
/// <param name="State"><c>starting</c>, <c>idle</c>, <c>running</c>, <c>not-responding</c> or <c>terminated</c>.</param>
/// <param name="Core">The virtual core a pool worker embodies, or <see langword="null"/>.</param>
/// <param name="MemoryKilobytes">Managed heap of its .NET runtime.</param>
/// <param name="HeartbeatMilliseconds">Age of its last heartbeat, or <see langword="null"/> before the first.</param>
/// <param name="BusyMilliseconds">CPU time its thread spent running processes.</param>
/// <param name="ProcessIds">The visible processes it hosts.</param>
public sealed record ProcWorker(
    int Id,
    string Role,
    string State,
    int? Core,
    long MemoryKilobytes,
    long? HeartbeatMilliseconds,
    long BusyMilliseconds,
    IReadOnlyList<long> ProcessIds)
{
    /// <summary>Gets whether its thread is stuck (no heartbeat for longer than the threshold).</summary>
    public bool IsNotResponding => State == "not-responding";
}

/// <summary>The Web Workers of the machine, from <c>/proc/kazoos/workers</c> (ADR 0071, section 5).</summary>
/// <param name="Capacity">The ceiling (<c>navigator.hardwareConcurrency</c>, at least 2).</param>
/// <param name="PoolSize">Workers of the pool: one per virtual core.</param>
/// <param name="Workers">Every live worker.</param>
public sealed record ProcWorkers(int Capacity, int PoolSize, IReadOnlyList<ProcWorker> Workers);

/// <summary>
/// Parsers of the system files of <c>/proc</c> the task manager reads (ADR 0057, section 8). They
/// follow the Linux layouts, so they would read a real <c>/proc</c> too, and they tolerate a
/// malformed line by skipping it rather than failing the whole refresh.
/// </summary>
public static class ProcFiles
{
    /// <summary>Parses <c>/proc/stat</c>.</summary>
    /// <param name="text">File content.</param>
    public static ProcStat ParseStat(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        CpuTimesSample total = default;
        List<CpuTimesSample> cores = [];
        long bootTime = 0, processes = 0, running = 0;

        foreach (string line in Lines(text))
        {
            string[] fields = Fields(line);
            if (fields.Length == 0)
            {
                continue;
            }

            string key = fields[0];
            if (key == "cpu")
            {
                total = ParseCpu(fields);
            }
            else if (key.StartsWith("cpu", StringComparison.Ordinal) && key.Length > 3 && key[3..].All(char.IsAsciiDigit))
            {
                cores.Add(ParseCpu(fields));
            }
            else if (fields.Length > 1)
            {
                long value = Long(fields[1]);
                switch (key)
                {
                    case "btime": bootTime = value; break;
                    case "processes": processes = value; break;
                    case "procs_running": running = value; break;
                }
            }
        }

        return new ProcStat(total, cores, bootTime, processes, running);

        static CpuTimesSample ParseCpu(string[] fields) => new(
            At(fields, 1), At(fields, 2), At(fields, 3), At(fields, 4), At(fields, 5), At(fields, 6), At(fields, 7), At(fields, 8));
    }

    /// <summary>Parses <c>/proc/meminfo</c> (<c>MemTotal:  16384 kB</c> lines).</summary>
    /// <param name="text">File content.</param>
    public static ProcMemInfo ParseMemInfo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<KeyValuePair<string, string>> fields = [];
        foreach (string line in Lines(text))
        {
            int separator = line.IndexOf(':');
            if (separator > 0)
            {
                fields.Add(new(line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }
        }

        long total = Kilobytes(fields, "MemTotal");
        long free = Kilobytes(fields, "MemFree");
        // Older kernels have no MemAvailable: free memory is the best estimate.
        long available = fields.Any(static field => field.Key == "MemAvailable") ? Kilobytes(fields, "MemAvailable") : free;
        return new ProcMemInfo(total, free, available, fields);

        static long Kilobytes(List<KeyValuePair<string, string>> fields, string key) =>
            Long(fields.FirstOrDefault(field => field.Key == key).Value?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
    }

    /// <summary>Parses <c>/proc/cpuinfo</c>: one entry per blank-line-separated processor block.</summary>
    /// <param name="text">File content.</param>
    public static IReadOnlyList<ProcCpuInfoEntry> ParseCpuInfo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<ProcCpuInfoEntry> processors = [];
        List<KeyValuePair<string, string>> current = [];
        foreach (string line in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                Flush();
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator > 0)
            {
                current.Add(new(line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }
        }

        Flush();
        return processors;

        void Flush()
        {
            if (current.Count > 0)
            {
                processors.Add(new ProcCpuInfoEntry([.. current]));
                current = [];
            }
        }
    }

    /// <summary>Parses <c>/proc/uptime</c>: seconds since boot and idle seconds summed over the cores.</summary>
    /// <param name="text">File content.</param>
    public static (double Uptime, double Idle) ParseUptime(string text)
    {
        string[] fields = Fields(text ?? string.Empty);
        return (Double(fields, 0), Double(fields, 1));
    }

    /// <summary>Parses <c>/proc/loadavg</c> (<c>0.42 0.30 0.12 2/17 120</c>).</summary>
    /// <param name="text">File content.</param>
    public static ProcLoadAverage ParseLoadAverage(string text)
    {
        string[] fields = Fields(text ?? string.Empty);
        string[] tasks = fields.Length > 3 ? fields[3].Split('/') : [];
        return new ProcLoadAverage(
            Double(fields, 0),
            Double(fields, 1),
            Double(fields, 2),
            tasks.Length > 0 ? Long(tasks[0]) : 0,
            tasks.Length > 1 ? Long(tasks[1]) : 0,
            fields.Length > 4 ? Long(fields[4]) : 0);
    }

    /// <summary>Parses <c>/proc/diskstats</c>; lines with fewer than fourteen fields are skipped.</summary>
    /// <param name="text">File content.</param>
    public static IReadOnlyList<ProcDiskStat> ParseDiskStats(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<ProcDiskStat> disks = [];
        foreach (string line in Lines(text))
        {
            string[] fields = Fields(line);
            if (fields.Length < 14)
            {
                continue;
            }

            disks.Add(new ProcDiskStat(
                (int)Long(fields[0]), (int)Long(fields[1]), fields[2],
                Long(fields[3]), Long(fields[4]), Long(fields[5]), Long(fields[6]),
                Long(fields[7]), Long(fields[8]), Long(fields[9]), Long(fields[10]),
                Long(fields[11]), Long(fields[12]), Long(fields[13])));
        }

        return disks;
    }

    /// <summary>Parses <c>/proc/net/dev</c>: the two header lines are skipped, then one interface per line.</summary>
    /// <param name="text">File content.</param>
    public static IReadOnlyList<ProcNetDevice> ParseNetDev(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<ProcNetDevice> devices = [];
        foreach (string line in Lines(text))
        {
            // "  eth0:  482113  214 ..." — the header lines contain '|' and no interface colon.
            int colon = line.IndexOf(':');
            if (colon <= 0 || line.Contains('|', StringComparison.Ordinal))
            {
                continue;
            }

            string[] counters = Fields(line[(colon + 1)..]);
            if (counters.Length < 12)
            {
                continue;
            }

            devices.Add(new ProcNetDevice(
                line[..colon].Trim(),
                Long(counters[0]), Long(counters[1]), Long(counters[2]), Long(counters[3]),
                Long(counters[8]), Long(counters[9]), Long(counters[10]), Long(counters[11])));
        }

        return devices;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] Fields(string line) =>
        line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    private static long At(string[] fields, int index) => index < fields.Length ? Long(fields[index]) : 0;

    private static long Long(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

    private static double Double(string[] fields, int index) =>
        index < fields.Length && double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;

    /// <summary>
    /// Parses <c>/proc/kazoos/workers</c>: <c>capacity N</c>, <c>pool_size N</c>, a header line, then
    /// one line per worker (<c>id role state core memory_kb heartbeat_ms busy_ms pids</c>, <c>-</c> for none).
    /// </summary>
    public static ProcWorkers ParseWorkers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int capacity = 0;
        int poolSize = 0;
        List<ProcWorker> workers = [];
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields is ["capacity", string value])
            {
                capacity = ParseInt(value);
            }
            else if (fields is ["pool_size", string pool])
            {
                poolSize = ParseInt(pool);
            }
            else if (fields.Length >= 8 && int.TryParse(fields[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id))
            {
                workers.Add(new ProcWorker(
                    id,
                    fields[1],
                    fields[2],
                    fields[3] == "-" ? null : ParseInt(fields[3]),
                    ParseLong(fields[4]),
                    fields[5] == "-" ? null : ParseLong(fields[5]),
                    ParseLong(fields[6]),
                    fields[7] == "-" ? [] : [.. fields[7].Split(',').Select(ParseLong)]));
            }
        }

        return new ProcWorkers(capacity, poolSize, workers);

        static int ParseInt(string value) =>
            int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

        static long ParseLong(string value) =>
            long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
    }
}
