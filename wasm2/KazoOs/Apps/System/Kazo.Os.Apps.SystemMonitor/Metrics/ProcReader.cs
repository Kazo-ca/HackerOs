using System.Diagnostics;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Apps.SystemMonitor.Metrics;

/// <summary>What one refresh of the task manager read: the processes and the system files.</summary>
/// <param name="Processes">Every visible process (<see cref="HackProcess.GetProcessesAsync"/>).</param>
/// <param name="Metrics">The system files, for <see cref="SystemMetrics.Apply"/>.</param>
public sealed record ProcRefresh(IReadOnlyList<HackProcess> Processes, MetricsReading Metrics);

/// <summary>
/// Reads <c>/proc</c> through the app's own SDK, exactly what <c>ps</c>, <c>top</c> or any
/// third-party app could read (ADR 0057, section 8). Every file is optional: an unreadable one
/// leaves its part of the reading empty instead of failing the refresh.
/// </summary>
public static class ProcReader
{
    /// <summary>Reads the processes and the system files once.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<ProcRefresh> ReadAsync(CancellationToken cancellationToken = default)
    {
        HackProcess[] processes;
        try
        {
            processes = await HackProcess.GetProcessesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HackIOException or HackUnauthorizedAccessException)
        {
            processes = [];
        }

        string? stat = await TryReadAsync("/proc/stat", cancellationToken);
        string? memInfo = await TryReadAsync("/proc/meminfo", cancellationToken);
        string? loadAvg = await TryReadAsync("/proc/loadavg", cancellationToken);
        string? uptime = await TryReadAsync("/proc/uptime", cancellationToken);
        string? diskStats = await TryReadAsync("/proc/diskstats", cancellationToken);
        string? netDev = await TryReadAsync("/proc/net/dev", cancellationToken);
        // ADR 0071, section 5: the Web Workers, shown with the cores; absent on a host without workers.
        string? workers = await TryReadAsync("/proc/hackeros/workers", cancellationToken);

        Dictionary<long, ProcessIoSample> io = [];
        foreach (HackProcess process in processes)
        {
            if (process.IoCounters is { } counters)
            {
                io[process.Id] = new ProcessIoSample(counters.ReadBytes, counters.WriteBytes);
            }
        }

        MetricsReading reading = new(
            Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
            stat is null ? null : ProcFiles.ParseStat(stat),
            memInfo is null ? null : ProcFiles.ParseMemInfo(memInfo),
            loadAvg is null ? null : ProcFiles.ParseLoadAverage(loadAvg),
            uptime is null ? null : ProcFiles.ParseUptime(uptime).Uptime,
            diskStats is null ? [] : ProcFiles.ParseDiskStats(diskStats),
            netDev is null ? [] : ProcFiles.ParseNetDev(netDev),
            io,
            workers is null ? null : ProcFiles.ParseWorkers(workers));
        return new ProcRefresh(processes, reading);
    }

    /// <summary>Reads <c>/proc/cpuinfo</c>, which never changes while the OS runs.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<ProcCpuInfoEntry>> ReadCpuInfoAsync(CancellationToken cancellationToken = default) =>
        await TryReadAsync("/proc/cpuinfo", cancellationToken) is { } text ? ProcFiles.ParseCpuInfo(text) : [];

    /// <summary>Reads the drives (<c>statfs</c>); none when the gateway is unavailable.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<HackDriveInfo>> ReadDrivesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await HackDriveInfo.GetDrivesAsync(cancellationToken);
        }
        catch (NotSupportedException)
        {
            // A host without the drive gateway (a test harness): the Disk tab shows no drive.
            return [];
        }
    }

    private static async Task<string?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await HackFile.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is HackIOException or HackUnauthorizedAccessException)
        {
            return null;
        }
    }
}
