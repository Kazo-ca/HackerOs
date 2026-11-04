using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// In-memory <see cref="IProcessResourceLimitTable"/> (ADR 0125, section 3). Entries are created and
/// dropped only by <see cref="InMemoryProcessManager"/>, at process creation and termination.
/// </summary>
/// <remarks>
/// A child gets a copy of its parent's limits taken at that instant (<c>fork</c>); later changes on
/// either side never reach the other. A root process gets the limits of its start request (the
/// session's, from <c>limits.conf</c>) or the kernel's defaults.
/// </remarks>
public sealed class InMemoryProcessResourceLimitTable : IProcessResourceLimitTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ProcessId, ProcessResourceLimits> _entries = [];

    /// <summary>Initializes the table.</summary>
    /// <param name="enabled">
    /// <c>HackerOs:ResourceLimits:Enabled</c>: <see langword="false"/> turns every limit off, every
    /// process reading <see cref="ProcessResourceLimits.None"/>.
    /// </param>
    /// <param name="kernelDefaults">The limits of a root process started without any; <see cref="ProcessResourceLimits.KernelDefaults"/> when omitted.</param>
    public InMemoryProcessResourceLimitTable(bool enabled = true, ProcessResourceLimits? kernelDefaults = null)
    {
        IsEnabled = enabled;
        KernelDefaults = enabled ? kernelDefaults ?? ProcessResourceLimits.KernelDefaults : ProcessResourceLimits.None;
    }

    /// <inheritdoc />
    public bool IsEnabled { get; }

    /// <summary>Gets the limits a root process starts with when its request carries none.</summary>
    public ProcessResourceLimits KernelDefaults { get; }

    /// <inheritdoc />
    public bool TryGet(ProcessId pid, out ProcessResourceLimits limits)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(pid, out limits!);
        }
    }

    /// <summary>Reads the limits of one process, or <see cref="ProcessResourceLimits.None"/> when it has no entry.</summary>
    /// <param name="pid">The process.</param>
    public ProcessResourceLimits GetOrNone(ProcessId pid) =>
        TryGet(pid, out ProcessResourceLimits limits) ? limits : ProcessResourceLimits.None;

    /// <inheritdoc />
    public ResourceLimitChangeResult TrySet(ProcessId pid, ResourceLimitKind kind, ResourceLimit limit, bool privileged)
    {
        if (!Enum.IsDefined(kind) || !limit.IsValid)
        {
            return ResourceLimitChangeResult.InvalidArgument;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(pid, out ProcessResourceLimits? current))
            {
                return ResourceLimitChangeResult.NoSuchProcess;
            }

            if (!IsEnabled)
            {
                return ResourceLimitChangeResult.PermissionDenied;
            }

            // setrlimit(2): raising the hard limit needs CAP_SYS_RESOURCE; the soft one only has to
            // stay under the (new) hard one, which IsValid checked.
            if (limit.Hard > current[kind].Hard && !privileged)
            {
                return ResourceLimitChangeResult.PermissionDenied;
            }

            _entries[pid] = current.With(kind, limit);
            return ResourceLimitChangeResult.Applied;
        }
    }

    /// <summary>
    /// Creates the entry of a new process: its parent's limits when the parent is live, otherwise the
    /// request's, otherwise the kernel's defaults.
    /// </summary>
    /// <param name="request">The start request.</param>
    /// <param name="pid">The PID just assigned.</param>
    /// <returns>The limits the process starts with.</returns>
    internal ProcessResourceLimits Create(ProcessStartRequest request, ProcessId pid)
    {
        lock (_gate)
        {
            ProcessResourceLimits limits = !IsEnabled
                ? ProcessResourceLimits.None
                : request.ParentPid is { } parent && _entries.TryGetValue(parent, out ProcessResourceLimits? inherited)
                    ? inherited
                    : request.ResourceLimits ?? KernelDefaults;
            _entries[pid] = limits;
            return limits;
        }
    }

    /// <summary>
    /// Gets the limits a new process would start with, before it exists: the ones the <c>nproc</c>
    /// check of <c>fork</c> reads (the caller's).
    /// </summary>
    internal ProcessResourceLimits Preview(ProcessStartRequest request)
    {
        lock (_gate)
        {
            return !IsEnabled
                ? ProcessResourceLimits.None
                : request.ParentPid is { } parent && _entries.TryGetValue(parent, out ProcessResourceLimits? inherited)
                    ? inherited
                    : request.ResourceLimits ?? KernelDefaults;
        }
    }

    /// <summary>Drops the entry of a process that reached a terminal state.</summary>
    internal void Remove(ProcessId pid)
    {
        lock (_gate)
        {
            _entries.Remove(pid);
        }
    }
}
