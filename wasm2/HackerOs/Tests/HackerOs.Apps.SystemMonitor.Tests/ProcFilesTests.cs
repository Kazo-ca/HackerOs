using HackerOs.Apps.SystemMonitor.Metrics;
using Xunit;

namespace HackerOs.Apps.SystemMonitor.Tests;

/// <summary>The parsers read the exact layouts the OS renders (ADR 0057) and the Linux ones.</summary>
public sealed class ProcFilesTests
{
    [Fact]
    public void Stat_parses_the_aggregate_line_every_core_and_the_counters()
    {
        ProcStat stat = ProcFiles.ParseStat(
            "cpu  4705 0 1210 93620 0 0 0 0 0 0\n" +
            "cpu0 2402 0 611 46790 0 0 0 0 0 0\n" +
            "cpu1 2303 0 599 46830 0 0 0 0 0 0\n" +
            "btime 1791640800\n" +
            "processes 312\n" +
            "procs_running 3\n" +
            "procs_blocked 0\n");

        Assert.Equal(new CpuTimesSample(4705, 0, 1210, 93620, 0, 0, 0, 0), stat.Total);
        Assert.Equal(2, stat.Cores.Count);
        Assert.Equal(2303, stat.Cores[1].User);
        Assert.Equal(1791640800, stat.BootTime);
        Assert.Equal(312, stat.Processes);
        Assert.Equal(3, stat.ProcessesRunning);
    }

    [Fact]
    public void Cpu_usage_is_the_busy_share_between_two_readings_like_top()
    {
        CpuTimesSample before = new(100, 0, 50, 250, 0, 0, 0, 0);
        CpuTimesSample after = new(160, 0, 70, 370, 0, 0, 0, 0);

        // 80 busy units out of 200: 40 %.
        Assert.Equal(40, after.UsagePercentSince(before), 6);
        Assert.Equal(0, before.UsagePercentSince(before));
    }

    [Fact]
    public void MemInfo_reads_kilobytes_and_keeps_every_line()
    {
        ProcMemInfo memory = ProcFiles.ParseMemInfo(
            "MemTotal:        16384 kB\n" +
            "MemFree:          4096 kB\n" +
            "MemAvailable:     6144 kB\n");

        Assert.Equal(16384, memory.TotalKilobytes);
        Assert.Equal(6144, memory.AvailableKilobytes);
        Assert.Equal((16384 - 6144) * 1024L, memory.UsedBytes);
        Assert.Equal(62.5, memory.UsedPercent, 6);
        Assert.Equal(["MemTotal", "MemFree", "MemAvailable"], memory.Fields.Select(static field => field.Key));
    }

    [Fact]
    public void CpuInfo_has_one_entry_per_processor_block()
    {
        IReadOnlyList<ProcCpuInfoEntry> processors = ProcFiles.ParseCpuInfo(
            "processor\t: 0\nvendor_id\t: Kazo OS\nmodel name\t: Kazo OS Virtual CPU (Default)\ncpu cores\t: 2\n\n" +
            "processor\t: 1\nvendor_id\t: Kazo OS\nmodel name\t: Kazo OS Virtual CPU (Default)\ncpu cores\t: 2\n\n");

        Assert.Equal(2, processors.Count);
        Assert.Equal("Kazo OS Virtual CPU (Default)", processors[1]["model name"]);
        Assert.Equal(string.Empty, processors[0]["missing"]);
    }

    [Fact]
    public void Uptime_and_loadavg_parse_their_single_line()
    {
        Assert.Equal((3725.5, 7000.25), ProcFiles.ParseUptime("3725.50 7000.25\n"));
        Assert.Equal(new ProcLoadAverage(0.42, 0.3, 0.12, 2, 17, 120), ProcFiles.ParseLoadAverage("0.42 0.30 0.12 2/17 120\n"));
    }

    [Fact]
    public void DiskStats_reads_the_kernel_layout_and_converts_sectors_to_bytes()
    {
        ProcDiskStat disk = Assert.Single(ProcFiles.ParseDiskStats("   8       0 hda 1204 0 9632 410 388 0 3104 220 0 630 630\n"));

        Assert.Equal(new ProcDiskStat(8, 0, "hda", 1204, 0, 9632, 410, 388, 0, 3104, 220, 0, 630, 630), disk);
        Assert.Equal(9632L * 512, disk.BytesRead);
        Assert.Equal(3104L * 512, disk.BytesWritten);
    }

    [Fact]
    public void NetDev_skips_the_headers_and_reads_both_directions()
    {
        IReadOnlyList<ProcNetDevice> devices = ProcFiles.ParseNetDev(
            "Inter-|   Receive                                                |  Transmit\n" +
            " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n" +
            "    lo:       0       0    0    0    0     0          0         0        0       0    0    0    0     0       0          0\n" +
            "  eth0:  482113     214    3    0    0     0          0         0    38211     214    0    0    0     0       0          0\n");

        Assert.Equal(["lo", "eth0"], devices.Select(static device => device.Name));
        Assert.Equal(new ProcNetDevice("eth0", 482113, 214, 3, 0, 38211, 214, 0, 0), devices[1]);
    }

    [Fact]
    public void Malformed_lines_are_skipped_instead_of_failing_the_refresh()
    {
        Assert.Empty(ProcFiles.ParseDiskStats("garbage\n8 0 hda 1 2\n"));
        Assert.Empty(ProcFiles.ParseNetDev("eth0: 1 2 3\n"));
        Assert.Empty(ProcFiles.ParseStat("nonsense\n").Cores);
    }

    [Fact]
    public void ParseWorkers_reads_the_ceiling_the_pool_and_each_worker()
    {
        ProcWorkers workers = ProcFiles.ParseWorkers(
            "cross-origin-isolated: yes\n" +
            "isolation-reason: -\n" +
            "timer-resolution-us: 5\n" +
            "capacity 4\n" +
            "pool_size 1\n" +
            "worker role       state          core memory_kb heartbeat_ms busy_ms pids\n" +
            "1      overflow   running        -    5087      57           38      18\n" +
            "2      pool       not-responding 0    5189      8974         91      12,14\n" +
            "3      ready      starting       -    0         -            0       -\n");

        Assert.Equal(4, workers.Capacity);
        Assert.Equal(1, workers.PoolSize);
        Assert.Equal(3, workers.Workers.Count);
        Assert.Equal(new ProcWorker(1, "overflow", "running", null, 5087, 57, 38, [18]) with { ProcessIds = workers.Workers[0].ProcessIds }, workers.Workers[0]);
        Assert.Equal([18L], workers.Workers[0].ProcessIds);
        Assert.True(workers.Workers[1].IsNotResponding);
        Assert.Equal(0, workers.Workers[1].Core);
        Assert.Equal([12L, 14L], workers.Workers[1].ProcessIds);
        Assert.Null(workers.Workers[2].HeartbeatMilliseconds);
        Assert.Empty(workers.Workers[2].ProcessIds);
    }
}
