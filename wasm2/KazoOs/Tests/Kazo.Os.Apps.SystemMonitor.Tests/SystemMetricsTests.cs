using Kazo.Os.Apps.SystemMonitor.Metrics;
using Xunit;

namespace Kazo.Os.Apps.SystemMonitor.Tests;

/// <summary>Usage and throughput are differences between two readings (ADR 0058, section 2).</summary>
public sealed class SystemMetricsTests
{
    [Fact]
    public void The_first_reading_is_only_a_baseline()
    {
        SystemMetrics metrics = new();

        metrics.Apply(Reading(0, cpu: (100, 300), disk: (0, 0), net: (0, 0)));

        Assert.Empty(metrics.CpuTotal.Values);
        Assert.Empty(Assert.Single(metrics.Disks).ReadPerSecond.Values);
        Assert.Single(metrics.MemoryUsedPercent.Values);
    }

    [Fact]
    public void Cpu_disk_network_and_process_rates_come_from_the_second_reading()
    {
        SystemMetrics metrics = new();
        metrics.Apply(Reading(10, cpu: (100, 300), disk: (0, 0), net: (1000, 500), processRead: 0));

        // Two seconds later: 50 busy units out of 100, 8 sectors read (4 KiB), 2 000 bytes received.
        metrics.Apply(Reading(12, cpu: (150, 350), disk: (8, 4), net: (3000, 900), processRead: 2048, busyMs: 500));

        Assert.Equal([50.0], metrics.CpuTotal.Values);
        Assert.Equal([50.0], metrics.CpuCores[0].Values);
        DiskActivity disk = Assert.Single(metrics.Disks);
        Assert.Equal([2048.0], disk.ReadPerSecond.Values);
        Assert.Equal([1024.0], disk.WritePerSecond.Values);
        Assert.Equal([25.0], disk.BusyPercent.Values);
        NetworkActivity eth0 = Assert.Single(metrics.Network);
        Assert.Equal([1000.0], eth0.ReceivePerSecond.Values);
        Assert.Equal([200.0], eth0.TransmitPerSecond.Values);
        Assert.Equal(new ProcessIoRate(1024, 0), metrics.RateOf(42));
        Assert.Equal(default, metrics.RateOf(99));
    }

    [Fact]
    public void A_counter_that_went_backwards_reads_as_zero()
    {
        SystemMetrics metrics = new();
        metrics.Apply(Reading(0, cpu: (100, 300), disk: (100, 100), net: (5000, 5000)));
        metrics.Apply(Reading(1, cpu: (110, 310), disk: (10, 10), net: (10, 10)));

        Assert.Equal([0.0], metrics.Disks[0].ReadPerSecond.Values);
        Assert.Equal([0.0], metrics.Network[0].ReceivePerSecond.Values);
    }

    [Fact]
    public void History_keeps_one_minute()
    {
        MetricHistory history = new(SystemMetrics.HistoryLength);
        for (int value = 0; value < 75; value++)
        {
            history.Add(value);
        }

        Assert.Equal(60, history.Values.Count);
        Assert.Equal(15, history.Values[0]);
        Assert.Equal(74, history.Latest);
    }

    private static MetricsReading Reading(
        double seconds,
        (long Busy, long Idle) cpu,
        (long SectorsRead, long SectorsWritten) disk,
        (long Received, long Sent) net,
        long processRead = 0,
        long busyMs = 0)
    {
        CpuTimesSample times = new(cpu.Busy, 0, 0, cpu.Idle, 0, 0, 0, 0);
        return new MetricsReading(
            seconds,
            new ProcStat(times, [times], 0, 0, 0),
            new ProcMemInfo(1000, 400, 400, []),
            null,
            seconds,
            [new ProcDiskStat(8, 0, "hda", 0, 0, disk.SectorsRead, 0, 0, 0, disk.SectorsWritten, 0, 0, busyMs, 0)],
            [new ProcNetDevice("eth0", net.Received, 0, 0, 0, net.Sent, 0, 0, 0)],
            new Dictionary<long, ProcessIoSample> { [42] = new(processRead, 0) });
    }
}
