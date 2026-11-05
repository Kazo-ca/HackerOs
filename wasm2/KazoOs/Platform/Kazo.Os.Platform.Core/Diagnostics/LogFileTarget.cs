using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// Identifies one log file under <c>/var/log</c> that <see cref="LogFileWriter"/> appends to, with
/// the Unix mode it is created with (ADR 0045, section 1). Every log is owned by <c>system:system</c>.
/// </summary>
public sealed record LogFileTarget
{
    /// <summary>Canonical <c>/var/log</c> directory.</summary>
    public static readonly VirtualPath LogDirectory = VirtualPath.Parse("/var/log");

    /// <summary>Canonical directory holding one log per app.</summary>
    public static readonly VirtualPath AppLogDirectory = VirtualPath.Parse("/var/log/apps");

    /// <summary>Platform log: every <c>ILogger</c> category, boot, and OS service errors. Mode <c>0644</c>.</summary>
    public static readonly LogFileTarget Syslog = new(VirtualPath.Parse("/var/log/syslog"), 0b110_100_100);

    /// <summary>Audit log: logins, <c>sudo</c>, permission approvals. Mode <c>0600</c>, like Linux.</summary>
    public static readonly LogFileTarget AuthLog = new(VirtualPath.Parse("/var/log/auth.log"), 0b110_000_000);

    /// <summary>
    /// Filesystem audit log: every protected write under <c>/etc</c>, <c>/bin</c> and <c>/var</c>, in the
    /// format of <c>auditd</c> (ADR 0100, section 3). Mode <c>0600</c>, read with <c>sudo ausearch</c>.
    /// </summary>
    public static readonly LogFileTarget AuditLog = new(VirtualPath.Parse("/var/log/audit.log"), 0b110_000_000);

    /// <summary>
    /// Kernel log: every line of the kernel's ring buffer (<c>kern.*</c>, ADR 0126), such as the OOM
    /// killer's. Mode <c>0644</c>; <c>dmesg</c> reads the ring buffer itself through <c>/dev/kmsg</c>.
    /// </summary>
    public static readonly LogFileTarget KernLog = new(VirtualPath.Parse("/var/log/kern.log"), 0b110_100_100);

    private LogFileTarget(VirtualPath path, ushort mode)
    {
        Path = path;
        Mode = mode;
    }

    /// <summary>Gets the canonical path of the active log file.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the Unix permission bits the file is created with.</summary>
    public ushort Mode { get; }

    /// <summary>Gets the directory containing the log.</summary>
    public VirtualPath Directory => VirtualPath.Parse(Path.Value[..Path.Value.LastIndexOf('/')]);

    /// <summary>
    /// Returns the per-app log <c>/var/log/apps/&lt;appId&gt;.log</c>, mode <c>0644</c>.
    /// </summary>
    /// <param name="appId">App identifier; must be a valid single file-name segment.</param>
    /// <exception cref="ArgumentException">The app ID cannot be used as a file name.</exception>
    public static LogFileTarget ForApp(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        FileSystemEntryName name = FileSystemEntryName.Parse($"{appId}.log");
        return new LogFileTarget(VirtualPath.Parse($"{AppLogDirectory.Value}/{name.Value}"), 0b110_100_100);
    }

    /// <summary>
    /// Returns a daemon log of a system app, <c>/var/log/&lt;directory&gt;/&lt;file&gt;.log</c>, mode
    /// <c>0644</c> (ADR 0080, section 3), for example <c>/var/log/kazoweb/docs.access.log</c>.
    /// </summary>
    /// <param name="relativePath"><c>&lt;directory&gt;/&lt;file&gt;.log</c>: two plain segments, the second ending in <c>.log</c>.</param>
    /// <exception cref="ArgumentException">
    /// The path has not exactly two segments, a segment is not a valid file name, the file does not
    /// end in <c>.log</c>, or the directory is <c>apps</c> (reserved for <see cref="ForApp"/>).
    /// </exception>
    public static LogFileTarget ForDaemon(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string[] segments = relativePath.Split('/');
        if (segments.Length != 2
            || !TryParseName(segments[0], out FileSystemEntryName directory)
            || !TryParseName(segments[1], out FileSystemEntryName file)
            || !file.Value.EndsWith(".log", StringComparison.Ordinal)
            || file.Value.Length == ".log".Length)
        {
            throw new ArgumentException($"'{relativePath}' is not a daemon log path ('<directory>/<file>.log').", nameof(relativePath));
        }

        if (string.Equals(directory.Value, "apps", StringComparison.Ordinal))
        {
            throw new ArgumentException("/var/log/apps holds the per-app logs; a daemon log uses a directory of its own.", nameof(relativePath));
        }

        return new LogFileTarget(VirtualPath.Parse($"{LogDirectory.Value}/{directory.Value}/{file.Value}"), 0b110_100_100);
    }

    private static bool TryParseName(string value, out FileSystemEntryName name)
    {
        try
        {
            name = FileSystemEntryName.Parse(value);
            return true;
        }
        catch (FormatException)
        {
            name = default;
            return false;
        }
    }

    /// <summary>Returns the path of archive <paramref name="index"/> (<c>&lt;file&gt;.1</c> is the newest).</summary>
    /// <param name="index">Archive number, from 1 to <see cref="LogRotationPolicy.MaxArchives"/>.</param>
    public VirtualPath ArchivePath(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, LogRotationPolicy.MaxArchives);
        return VirtualPath.Parse($"{Path.Value}.{index}");
    }
}
