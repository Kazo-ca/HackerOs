using System.Globalization;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Dmesg;

/// <summary>
/// Implements <c>dmesg</c> (ADR 0126, section 3), after util-linux: reads the kernel ring buffer
/// from <c>/dev/kmsg</c> and prints one line per message, stamped with the seconds since boot
/// (<c>[    12.345678] message</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>/dev/kmsg</c> gives one record per line, <c>priority,sequence,microseconds,-;message</c>, as on
/// Linux; the priority is the level, the facility being <c>kern</c> (0). <c>-T</c> turns the stamp
/// into a wall-clock time (boot time from <c>/proc/uptime</c>, so it drifts like the real one after a
/// suspend), <c>-x</c> prefixes the facility and level, <c>-l</c> keeps the given levels.
/// </para>
/// <para>
/// Reading is open to every user (<c>kernel.dmesg_restrict = 0</c>); writing a line to
/// <c>/dev/kmsg</c> adds a message, as <c>echo text &gt; /dev/kmsg</c> does.
/// </para>
/// </remarks>
public sealed class DmesgCommand : TerminalAppBase
{
    /// <summary>The device the ring buffer is read from.</summary>
    internal const string DevicePath = "/dev/kmsg";

    /// <summary>The level names, by number (<c>syslog.h</c>).</summary>
    internal static readonly string[] LevelNames = ["emerg", "alert", "crit", "err", "warn", "notice", "info", "debug"];

    /// <summary>Initializes the command with its manifest.</summary>
    public DmesgCommand(AppManifest manifest) : base(manifest)
    {
    }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        bool wallClock = false;
        bool decode = false;
        HashSet<int>? levels = null;
        IReadOnlyList<string> args = context.Arguments;
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "-T" or "--ctime":
                    wallClock = true;
                    break;
                case "-x" or "--decode":
                    decode = true;
                    break;
                case "-k" or "--kernel":
                    // Every message of the buffer is the kernel's.
                    break;
                case "-h" or "--help":
                    await context.StandardOutput.WriteLineAsync(text["Usage"]);
                    return 0;
                case "-l" or "--level":
                    if (index + 1 >= args.Count || ParseLevels(args[++index]) is not { } parsed)
                    {
                        await context.StandardError.WriteLineAsync(text["UnknownLevel", index < args.Count ? args[index] : string.Empty]);
                        return 1;
                    }

                    levels = parsed;
                    break;
                default:
                    if (arg.StartsWith("--level=", StringComparison.Ordinal) && ParseLevels(arg["--level=".Length..]) is { } inline)
                    {
                        levels = inline;
                        break;
                    }

                    await context.StandardError.WriteLineAsync(text["InvalidOption", arg]);
                    await context.StandardError.WriteLineAsync(text["Usage"]);
                    return 1;
            }
        }

        string records;
        DateTimeOffset bootTime = default;
        try
        {
            records = await HackFile.ReadAllTextAsync(DevicePath, cancellationToken);
            if (wallClock)
            {
                bootTime = context.App.Clock.UtcNow - await ReadUptimeAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HackIOException or HackUnauthorizedAccessException or IOException or UnauthorizedAccessException)
        {
            await context.StandardError.WriteLineAsync(text["ReadFailed", DevicePath, exception.Message]);
            return 1;
        }

        StringBuilder output = new();
        foreach (KernelRecord record in Parse(records))
        {
            if (levels is not null && !levels.Contains(record.Level))
            {
                continue;
            }

            if (decode)
            {
                output.Append(CultureInfo.InvariantCulture, $"kern  :{LevelNames[record.Level],-6}: ");
            }

            output.Append(wallClock ? FormatWallClock(bootTime + record.SinceBoot, context.App.TimeZone) : FormatStamp(record.SinceBoot));
            output.Append(' ').Append(record.Message).Append('\n');
        }

        await context.StandardOutput.WriteAsync(output.ToString());
        return 0;
    }

    /// <summary>Parses the records of <c>/dev/kmsg</c>; a malformed line is skipped.</summary>
    /// <param name="content">The device's content.</param>
    internal static IEnumerable<KernelRecord> Parse(string content)
    {
        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(';', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            string[] header = line[..separator].Split(',');
            if (header.Length < 3
                || !int.TryParse(header[0], NumberStyles.None, CultureInfo.InvariantCulture, out int priority)
                || !long.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out long microseconds))
            {
                continue;
            }

            yield return new KernelRecord(priority & 7, TimeSpan.FromTicks(microseconds * 10), line[(separator + 1)..]);
        }
    }

    /// <summary>Formats the time since boot as <c>[    12.345678]</c>.</summary>
    /// <param name="sinceBoot">Time since boot.</param>
    internal static string FormatStamp(TimeSpan sinceBoot)
    {
        long microseconds = Math.Max(0, sinceBoot.Ticks / 10);
        return string.Create(CultureInfo.InvariantCulture, $"[{microseconds / 1_000_000,5}.{microseconds % 1_000_000:D6}]");
    }

    /// <summary>Formats a wall-clock time as <c>dmesg -T</c> does, in the C locale: <c>[Sat Oct 18 07:12:34 2026]</c>.</summary>
    /// <param name="utc">The time.</param>
    /// <param name="zone">The process's time zone (ADR 0193).</param>
    internal static string FormatWallClock(DateTimeOffset utc, TimeZoneInfo zone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(utc, zone);
        return "[" + local.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "]";
    }

    /// <summary>Parses <c>err,warn</c> into level numbers; <see langword="null"/> when a name is unknown.</summary>
    private static HashSet<int>? ParseLevels(string list)
    {
        HashSet<int> levels = [];
        foreach (string name in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int level = Array.IndexOf(LevelNames, name.ToLowerInvariant());
            if (level < 0)
            {
                return null;
            }

            levels.Add(level);
        }

        return levels.Count == 0 ? null : levels;
    }

    private static async Task<TimeSpan> ReadUptimeAsync(CancellationToken cancellationToken)
    {
        string uptime = await HackFile.ReadAllTextAsync("/proc/uptime", cancellationToken);
        string first = uptime.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0";
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;
    }

    /// <summary>One record of <c>/dev/kmsg</c>.</summary>
    /// <param name="Level">Level, <c>0</c> (emerg) to <c>7</c> (debug).</param>
    /// <param name="SinceBoot">Time since boot.</param>
    /// <param name="Message">The text.</param>
    internal readonly record struct KernelRecord(int Level, TimeSpan SinceBoot, string Message);
}
