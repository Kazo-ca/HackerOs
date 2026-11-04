using System.Globalization;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>Priority of one kernel message, the <c>KERN_*</c> levels of <c>printk</c> (0 is the most severe).</summary>
public enum KernelLogLevel
{
    /// <summary><c>KERN_EMERG</c>: the system is unusable.</summary>
    Emergency = 0,

    /// <summary><c>KERN_ALERT</c>: action must be taken at once.</summary>
    Alert = 1,

    /// <summary><c>KERN_CRIT</c>: critical conditions.</summary>
    Critical = 2,

    /// <summary><c>KERN_ERR</c>: error conditions, such as an OOM kill.</summary>
    Error = 3,

    /// <summary><c>KERN_WARNING</c>: warning conditions, such as memory nearing its limit.</summary>
    Warning = 4,

    /// <summary><c>KERN_NOTICE</c>: normal but significant condition.</summary>
    Notice = 5,

    /// <summary><c>KERN_INFO</c>: informational, such as boot lines.</summary>
    Info = 6,

    /// <summary><c>KERN_DEBUG</c>: debug-level messages.</summary>
    Debug = 7
}

/// <summary>One message of the kernel's ring buffer.</summary>
/// <param name="Sequence">Sequence number, from 0 at boot, as <c>/dev/kmsg</c> numbers its records.</param>
/// <param name="SinceBoot">Simulated time since boot, the <c>[    12.345678]</c> stamp of <c>dmesg</c>.</param>
/// <param name="TimestampUtc">Simulated wall-clock time, shown by <c>dmesg -T</c>.</param>
/// <param name="Level">Priority.</param>
/// <param name="Message">The text, one line.</param>
public sealed record KernelLogEntry(long Sequence, TimeSpan SinceBoot, DateTimeOffset TimestampUtc, KernelLogLevel Level, string Message);

/// <summary>
/// The kernel's ring buffer (<c>log_buf</c>, ADR 0126, section 3): the messages the OS writes as the
/// kernel since boot. <c>dmesg</c> reads it through <c>/dev/kmsg</c>; each message is also appended to
/// <c>/var/log/kern.log</c> and <c>/var/log/syslog</c>, as <c>rsyslog</c> does with <c>kern.*</c>.
/// </summary>
/// <remarks>
/// The buffer keeps the last <see cref="Capacity"/> messages; older ones are dropped, as when
/// <c>log_buf</c> wraps. It lives in memory for the page's life: a reload is a reboot.
/// </remarks>
public sealed class KernelLog
{
    /// <summary>Number of messages kept, like a 128 KiB <c>log_buf</c> of short lines.</summary>
    public const int Capacity = 1024;

    /// <summary>Source name of the kernel's lines in <c>syslog</c> and <c>kern.log</c>.</summary>
    public const string Source = "kernel";

    private readonly ISimulationClock _clock;
    private readonly LogFileWriter? _files;
    private readonly Lock _gate = new();
    private readonly Queue<KernelLogEntry> _entries = new();
    private long _nextSequence;

    /// <summary>Creates an empty ring buffer.</summary>
    /// <param name="clock">Simulated clock: the time since boot is its tick count.</param>
    /// <param name="files">Writer of <c>kern.log</c> and <c>syslog</c>; <see langword="null"/> keeps messages in memory only.</param>
    public KernelLog(ISimulationClock clock, LogFileWriter? files = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _files = files;
    }

    /// <summary>Raised after a message is added, outside the buffer's lock.</summary>
    public event Action<KernelLogEntry>? Written;

    /// <summary>Gets the simulated time since boot.</summary>
    public TimeSpan SinceBoot => TimeSpan.FromTicks(_clock.TickDuration.Ticks * _clock.CurrentTick);

    /// <summary>Adds one message, like <c>printk</c>.</summary>
    /// <param name="level">Priority.</param>
    /// <param name="message">The text; line breaks become spaces.</param>
    /// <returns>The recorded message.</returns>
    public KernelLogEntry Write(KernelLogLevel level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string line = message.Replace('\r', ' ').Replace('\n', ' ');
        KernelLogEntry entry;
        lock (_gate)
        {
            entry = new KernelLogEntry(_nextSequence++, SinceBoot, _clock.UtcNow, level, line);
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }

        if (_files is not null)
        {
            // rsyslog's "kern.* -/var/log/kern.log", and the catch-all syslog, with the dmesg stamp.
            string stamped = $"{FormatStamp(entry.SinceBoot)} {line}";
            DiagnosticSeverity severity = ToSeverity(level);
            _files.Write(LogFileTarget.KernLog, severity, Source, stamped);
            _files.Write(LogFileTarget.Syslog, severity, Source, stamped);
        }

        Written?.Invoke(entry);
        return entry;
    }

    /// <summary>Gets every message still in the buffer, oldest first.</summary>
    public IReadOnlyList<KernelLogEntry> ReadAll()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    /// <summary>
    /// Formats the buffer as <c>/dev/kmsg</c> does: one record per line,
    /// <c>&lt;priority&gt;,&lt;sequence&gt;,&lt;microseconds since boot&gt;,-;&lt;message&gt;</c>,
    /// the priority being the facility <c>kern</c> (0) times 8 plus the level.
    /// </summary>
    public string FormatDevKmsg()
    {
        System.Text.StringBuilder builder = new();
        foreach (KernelLogEntry entry in ReadAll())
        {
            long microseconds = entry.SinceBoot.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
            builder.Append(CultureInfo.InvariantCulture, $"{(int)entry.Level},{entry.Sequence},{microseconds},-;{entry.Message}\n");
        }

        return builder.ToString();
    }

    /// <summary>Formats a time since boot the way <c>dmesg</c> does: <c>[    12.345678]</c>.</summary>
    /// <param name="sinceBoot">Time since boot.</param>
    public static string FormatStamp(TimeSpan sinceBoot)
    {
        long microseconds = Math.Max(0, sinceBoot.Ticks / (TimeSpan.TicksPerMillisecond / 1000));
        return string.Create(CultureInfo.InvariantCulture, $"[{microseconds / 1_000_000,5}.{microseconds % 1_000_000:D6}]");
    }

    private static DiagnosticSeverity ToSeverity(KernelLogLevel level) => level switch
    {
        <= KernelLogLevel.Critical => DiagnosticSeverity.Critical,
        KernelLogLevel.Error => DiagnosticSeverity.Error,
        KernelLogLevel.Warning => DiagnosticSeverity.Warning,
        KernelLogLevel.Debug => DiagnosticSeverity.Debug,
        _ => DiagnosticSeverity.Information
    };
}
