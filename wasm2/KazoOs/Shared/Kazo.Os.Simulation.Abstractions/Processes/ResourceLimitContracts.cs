using System.Globalization;

namespace Kazo.Os.Simulation.Abstractions.Processes;

/// <summary>
/// The per-process resource limits of ADR 0125, Linux's <c>RLIMIT_*</c> retained by Kazo OS. The
/// order is the order of the lines of <c>/proc/&lt;pid&gt;/limits</c>.
/// </summary>
public enum ResourceLimitKind
{
    /// <summary><c>RLIMIT_CPU</c>: CPU time (<c>utime + stime</c>), in seconds (<c>ulimit -t</c>).</summary>
    Cpu,

    /// <summary>
    /// <c>RLIMIT_RSS</c>: resident memory (<c>VmRSS</c>), in bytes (<c>ulimit -m</c> counts KiB). Linux
    /// has ignored it since 2.6; Kazo OS applies it, because resident memory is what it measures.
    /// </summary>
    Rss,

    /// <summary><c>RLIMIT_NOFILE</c>: one more than the highest descriptor number a process may open (<c>ulimit -n</c>).</summary>
    NoFile,

    /// <summary><c>RLIMIT_NPROC</c>: live processes of the user, checked when one more is started (<c>ulimit -u</c>).</summary>
    NProc
}

/// <summary>
/// One limit as <c>setrlimit(2)</c> knows it: the <b>soft</b> value, the one that applies, and the
/// <b>hard</b> value, the ceiling of the soft one.
/// </summary>
/// <param name="Soft">The value that applies; <see cref="Unlimited"/> for <c>RLIM_INFINITY</c>.</param>
/// <param name="Hard">The ceiling of <paramref name="Soft"/>; <see cref="Unlimited"/> for <c>RLIM_INFINITY</c>.</param>
public readonly record struct ResourceLimit(long Soft, long Hard)
{
    /// <summary><c>RLIM_INFINITY</c>, written <c>unlimited</c>.</summary>
    public const long Unlimited = long.MaxValue;

    /// <summary>Gets a limit that never applies (<c>unlimited</c> both ways).</summary>
    public static ResourceLimit Infinite { get; } = new(Unlimited, Unlimited);

    /// <summary>Gets whether the values are acceptable to <c>setrlimit</c>: not negative, soft not above hard.</summary>
    public bool IsValid => Soft >= 0 && Hard >= 0 && Soft <= Hard;

    /// <summary>Gets whether the soft value is finite, that is, whether the limit applies at all.</summary>
    public bool IsFinite => Soft != Unlimited;

    /// <summary>Formats one value as <c>ulimit</c> and <c>/proc/&lt;pid&gt;/limits</c> print it.</summary>
    /// <param name="value">A soft or hard value.</param>
    /// <returns><c>unlimited</c>, or the decimal value.</returns>
    public static string Format(long value) =>
        value == Unlimited ? "unlimited" : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Returns <c>soft:hard</c>, the notation of <c>prlimit</c>.</summary>
    public override string ToString() => $"{Format(Soft)}:{Format(Hard)}";
}

/// <summary>
/// The four limits of one process (ADR 0125, section 1). Immutable: a change yields a new instance,
/// which the <see cref="IProcessResourceLimitTable"/> stores.
/// </summary>
/// <param name="Cpu">CPU time, in seconds.</param>
/// <param name="Rss">Resident memory, in bytes.</param>
/// <param name="NoFile">Open file descriptors.</param>
/// <param name="NProc">Live processes of the user.</param>
public sealed record ProcessResourceLimits(ResourceLimit Cpu, ResourceLimit Rss, ResourceLimit NoFile, ResourceLimit NProc)
{
    /// <summary>The soft <c>nofile</c> of a common Linux distribution (systemd's default).</summary>
    public const long DefaultNoFileSoft = 1024;

    /// <summary>The hard <c>nofile</c> of a common Linux distribution (systemd's default, <c>524288</c>).</summary>
    public const long DefaultNoFileHard = 524288;

    /// <summary>The <c>nproc</c> soft and hard value used by Kazo OS (ADR 0125, section 6).</summary>
    public const long DefaultNProc = 4096;

    /// <summary>
    /// Gets the kernel's values, before any <c>limits.conf</c> (ADR 0125, section 6, confirmed by
    /// Benoit on 2026-10-18): the counters of a common Linux distribution, unlimited for what depends
    /// on a measurement.
    /// </summary>
    public static ProcessResourceLimits KernelDefaults { get; } = new(
        ResourceLimit.Infinite,
        ResourceLimit.Infinite,
        new ResourceLimit(DefaultNoFileSoft, DefaultNoFileHard),
        new ResourceLimit(DefaultNProc, DefaultNProc));

    /// <summary>Gets limits that never apply: what every process has when the host turns the limits off.</summary>
    public static ProcessResourceLimits None { get; } = new(
        ResourceLimit.Infinite, ResourceLimit.Infinite, ResourceLimit.Infinite, ResourceLimit.Infinite);

    /// <summary>Gets one limit by kind.</summary>
    /// <param name="kind">The resource.</param>
    public ResourceLimit this[ResourceLimitKind kind] => kind switch
    {
        ResourceLimitKind.Cpu => Cpu,
        ResourceLimitKind.Rss => Rss,
        ResourceLimitKind.NoFile => NoFile,
        ResourceLimitKind.NProc => NProc,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource limit.")
    };

    /// <summary>Returns a copy with one limit replaced.</summary>
    /// <param name="kind">The resource.</param>
    /// <param name="limit">Its new limit.</param>
    public ProcessResourceLimits With(ResourceLimitKind kind, ResourceLimit limit) => kind switch
    {
        ResourceLimitKind.Cpu => this with { Cpu = limit },
        ResourceLimitKind.Rss => this with { Rss = limit },
        ResourceLimitKind.NoFile => this with { NoFile = limit },
        ResourceLimitKind.NProc => this with { NProc = limit },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource limit.")
    };
}

/// <summary>The outcome of one <c>setrlimit</c>/<c>prlimit</c> request, with its <c>errno</c>.</summary>
public enum ResourceLimitChangeResult
{
    /// <summary>The limit was changed.</summary>
    Applied,

    /// <summary><c>EINVAL</c>: a negative value, or a soft value above the hard one.</summary>
    InvalidArgument,

    /// <summary>
    /// <c>EPERM</c>: raising the hard limit without elevation, acting on another user's process, or
    /// limits turned off by the host.
    /// </summary>
    PermissionDenied,

    /// <summary><c>ESRCH</c>: no active process has this PID.</summary>
    NoSuchProcess
}

/// <summary>
/// The limits of every active process, indexed by PID (ADR 0125, section 3). Like
/// <see cref="IProcessEnvironmentTable"/>, the process manager fills an entry when it creates a process
/// (copying its parent's limits, like <c>fork</c>) and drops it when the process ends.
/// </summary>
public interface IProcessResourceLimitTable
{
    /// <summary>
    /// Gets whether limits apply on this host (<c>HackerOs:ResourceLimits:Enabled</c>). When
    /// <see langword="false"/>, every process reads <see cref="ProcessResourceLimits.None"/> and no
    /// change is accepted.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>Attempts to read the limits of one active process.</summary>
    bool TryGet(ProcessId pid, out ProcessResourceLimits limits);

    /// <summary>
    /// Changes one limit of one process, with the rules of <c>setrlimit(2)</c>: any value may be lowered;
    /// the soft value may rise up to the hard one; only a <paramref name="privileged"/> caller
    /// (<c>sudo</c>, ADR 0042) raises the hard value.
    /// </summary>
    /// <param name="pid">The process.</param>
    /// <param name="kind">The resource.</param>
    /// <param name="limit">The new soft and hard values.</param>
    /// <param name="privileged">Whether the caller holds the equivalent of <c>CAP_SYS_RESOURCE</c>.</param>
    ResourceLimitChangeResult TrySet(ProcessId pid, ResourceLimitKind kind, ResourceLimit limit, bool privileged);
}

/// <summary>One open descriptor of a process, as <c>/proc/&lt;pid&gt;/fd</c> lists it.</summary>
/// <param name="Number">The descriptor number; 0, 1 and 2 are the standard streams.</param>
/// <param name="Target">What it designates: a file path, or <c>/dev/tty</c>, <c>/dev/null</c> for the standard streams.</param>
public readonly record struct ProcessFileDescriptor(int Number, string Target);

/// <summary>
/// The open descriptors of every active process (ADR 0125, section 1, <c>RLIMIT_NOFILE</c>). Every
/// process starts with the three standard streams (0, 1 and 2); each open takes the lowest free number,
/// and fails with <c>EMFILE</c> once that number reaches the soft <c>nofile</c> limit.
/// </summary>
public interface IProcessFileDescriptorTable
{
    /// <summary>
    /// Opens one descriptor for <paramref name="target"/>, like <c>open(2)</c>.
    /// </summary>
    /// <param name="pid">The process opening it.</param>
    /// <param name="target">The canonical path it designates.</param>
    /// <param name="descriptor">The descriptor number, valid until <see cref="Close"/>.</param>
    /// <returns>
    /// <see langword="false"/> when the process already holds as many descriptors as its soft
    /// <c>nofile</c> limit allows (<c>EMFILE</c>). An unknown PID (a process that already ended, or
    /// the OS itself) is never limited: it gets <c>-1</c> and <see langword="true"/>.
    /// </returns>
    bool TryOpen(ProcessId pid, string target, out int descriptor);

    /// <summary>Closes one descriptor; closing an unknown or already closed one does nothing.</summary>
    void Close(ProcessId pid, int descriptor);

    /// <summary>Gets the open descriptors of one process, standard streams included, by number.</summary>
    IReadOnlyList<ProcessFileDescriptor> GetOpen(ProcessId pid);
}

/// <summary>
/// A process cannot be started because its user already runs as many processes as the
/// <c>nproc</c> soft limit allows: Linux's <c>EAGAIN</c> from <c>fork(2)</c>, "Resource temporarily
/// unavailable" (ADR 0125, section 1).
/// </summary>
public sealed class ProcessLimitExceededException : InvalidOperationException
{
    /// <summary>The message <c>strerror(EAGAIN)</c> gives.</summary>
    public const string ResourceTemporarilyUnavailable = "Resource temporarily unavailable";

    /// <summary>Initializes the exception.</summary>
    /// <param name="limit">The <c>nproc</c> soft limit that was reached.</param>
    public ProcessLimitExceededException(long limit)
        : base(ResourceTemporarilyUnavailable) => Limit = limit;

    /// <summary>Gets the <c>nproc</c> soft limit that was reached.</summary>
    public long Limit { get; }

    /// <summary>Gets the C name of the error, <c>EAGAIN</c>.</summary>
    public string ErrorName => "EAGAIN";
}
