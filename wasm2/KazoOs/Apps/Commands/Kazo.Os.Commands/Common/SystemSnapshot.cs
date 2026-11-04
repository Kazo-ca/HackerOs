using System.Globalization;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Commands.Common;

/// <summary>
/// One reading of the machine for <c>top</c> and <c>htop</c> (ADR 0064), taken from <c>/proc</c>
/// as procps does: <c>uptime</c>, <c>loadavg</c>, <c>meminfo</c>, <c>cpuinfo</c>, <c>stat</c>
/// and every process through <see cref="KazoProcess"/>. A file the command cannot read leaves
/// its fields at zero.
/// </summary>
internal sealed record SystemSnapshot(
    TimeSpan Uptime,
    string LoadAverage,
    long MemoryTotalKb,
    long MemoryFreeKb,
    long MemoryAvailableKb,
    int Cores,
    IReadOnlyList<long> CpuTicks,
    IReadOnlyList<IReadOnlyList<long>> CoreTicks,
    IReadOnlyList<KazoProcess> Processes)
{
    /// <summary>Reads everything once.</summary>
    public static async Task<SystemSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        string uptime = await TryReadAsync("/proc/uptime", cancellationToken);
        string load = await TryReadAsync("/proc/loadavg", cancellationToken);
        string meminfo = await TryReadAsync("/proc/meminfo", cancellationToken);
        string cpuinfo = await TryReadAsync("/proc/cpuinfo", cancellationToken);
        string stat = await TryReadAsync("/proc/stat", cancellationToken);
        KazoProcess[] processes;
        try
        {
            processes = await KazoProcess.GetProcessesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            processes = [];
        }

        double seconds = double.TryParse(uptime.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double up) ? up : 0;
        string[] loadFields = load.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, long> memory = ParseMeminfo(meminfo);
        int cores = Math.Max(1, cpuinfo.Split('\n').Count(line => line.StartsWith("processor", StringComparison.Ordinal)));
        List<long> total = [];
        List<IReadOnlyList<long>> perCore = [];
        foreach (string line in stat.Split('\n'))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || !fields[0].StartsWith("cpu", StringComparison.Ordinal))
            {
                continue;
            }

            List<long> ticks = [.. fields.Skip(1).Select(field => long.TryParse(field, out long value) ? value : 0)];
            if (fields[0] == "cpu")
            {
                total = ticks;
            }
            else
            {
                perCore.Add(ticks);
            }
        }

        return new SystemSnapshot(
            TimeSpan.FromSeconds(seconds),
            loadFields.Length >= 3 ? $"{loadFields[0]}, {loadFields[1]}, {loadFields[2]}" : "0.00, 0.00, 0.00",
            memory.GetValueOrDefault("MemTotal"),
            memory.GetValueOrDefault("MemFree"),
            memory.GetValueOrDefault("MemAvailable", memory.GetValueOrDefault("MemFree")),
            cores,
            total,
            perCore,
            processes);
    }

    /// <summary>Busy share of the CPU between two readings, from <c>/proc/stat</c> (user+nice+system over all but idle+iowait).</summary>
    public static double BusyPercent(IReadOnlyList<long> before, IReadOnlyList<long> after)
    {
        if (before.Count < 4 || after.Count < 4)
        {
            return 0;
        }

        long Idle(IReadOnlyList<long> ticks) => ticks[3] + (ticks.Count > 4 ? ticks[4] : 0);
        long totalDelta = after.Sum() - before.Sum();
        long idleDelta = Idle(after) - Idle(before);
        return totalDelta <= 0 ? 0 : Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    /// <summary>Formats an uptime like <c>top</c>: <c>3 days, 4:05</c>, <c>4:05</c> or <c>12 min</c>.</summary>
    public static string FormatUptime(TimeSpan uptime) =>
        uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays} day{((int)uptime.TotalDays == 1 ? string.Empty : "s")}, {uptime.Hours,2}:{uptime.Minutes:00}"
        : uptime.TotalHours >= 1 ? $"{uptime.Hours,2}:{uptime.Minutes:00}"
        : $"{uptime.Minutes} min";

    /// <summary>CPU time of a process as <c>top</c>'s <c>TIME+</c> column (<c>m:ss.hh</c>), estimated from its age and current usage.</summary>
    public static string FormatCpuTime(KazoProcess process, DateTimeOffset now)
    {
        double seconds = Math.Max(0, (now - process.StartTime).TotalSeconds) * process.CpuUsage / 100.0;
        TimeSpan time = TimeSpan.FromSeconds(seconds);
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 10:00}";
    }

    private static Dictionary<string, long> ParseMeminfo(string text)
    {
        Dictionary<string, long> values = new(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string number = new([.. line[(colon + 1)..].Trim().TakeWhile(char.IsAsciiDigit)]);
            if (long.TryParse(number, out long value))
            {
                values[line[..colon]] = value;
            }
        }

        return values;
    }

    private static async Task<string> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await KazoFile.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
