using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// The <c>oom_score_adj</c> of every live process (ADR 0126, section 4), next to
/// <see cref="ProcessRecord"/> like the limit table of ADR 0125. Entries are created and dropped only by
/// <see cref="InMemoryProcessManager"/>, at process creation and termination.
/// </summary>
/// <remarks>
/// <para>
/// As on Linux, the value is copied to a child at <c>fork</c>; a start request that names one (a
/// system service, from its definition, like systemd's <c>OOMScoreAdjust=</c>) overrides it at
/// <c>exec</c>. A root process with none starts at <c>0</c>.
/// </para>
/// <para>
/// Writing follows <c>proc(5)</c>: anyone allowed to write the file may raise the value; lowering it
/// below the lowest value the process ever had needs <c>CAP_SYS_RESOURCE</c> (here, an elevated
/// <c>root</c> or the system).
/// </para>
/// </remarks>
public sealed class InMemoryProcessOomScoreTable
{
    /// <summary>The lowest value: the process is never chosen by the OOM killer.</summary>
    public const int Exempt = -1000;

    /// <summary>The highest value: the process is chosen first.</summary>
    public const int Maximum = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<ProcessId, Entry> _entries = [];

    /// <summary>Reads the <c>oom_score_adj</c> of a live process.</summary>
    /// <param name="pid">The process.</param>
    /// <param name="adjust">Its value, <c>0</c> when it has no entry.</param>
    /// <returns><see langword="true"/> when the process has an entry.</returns>
    public bool TryGet(ProcessId pid, out int adjust)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(pid, out Entry entry))
            {
                adjust = entry.Value;
                return true;
            }

            adjust = 0;
            return false;
        }
    }

    /// <summary>Reads the <c>oom_score_adj</c> of a process, <c>0</c> when it has none.</summary>
    /// <param name="pid">The process.</param>
    public int GetOrDefault(ProcessId pid) => TryGet(pid, out int adjust) ? adjust : 0;

    /// <summary>Changes the <c>oom_score_adj</c> of a live process, the write of <c>/proc/&lt;pid&gt;/oom_score_adj</c>.</summary>
    /// <param name="pid">The process.</param>
    /// <param name="value">The new value, <c>-1000</c> to <c>1000</c>.</param>
    /// <param name="privileged">Whether the writer holds <c>CAP_SYS_RESOURCE</c> (elevated root or the system).</param>
    /// <returns>
    /// <see cref="ResourceLimitChangeResult.Applied"/>; <see cref="ResourceLimitChangeResult.InvalidArgument"/>
    /// (<c>EINVAL</c>) out of range; <see cref="ResourceLimitChangeResult.PermissionDenied"/> (<c>EACCES</c>)
    /// when an unprivileged writer goes below the lowest value the process had;
    /// <see cref="ResourceLimitChangeResult.NoSuchProcess"/> (<c>ESRCH</c>).
    /// </returns>
    public ResourceLimitChangeResult TrySet(ProcessId pid, int value, bool privileged)
    {
        if (value is < Exempt or > Maximum)
        {
            return ResourceLimitChangeResult.InvalidArgument;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(pid, out Entry current))
            {
                return ResourceLimitChangeResult.NoSuchProcess;
            }

            // proc(5): "oom_score_adj_min", the lowest value set by a privileged writer, is the floor.
            if (value < current.Floor && !privileged)
            {
                return ResourceLimitChangeResult.PermissionDenied;
            }

            _entries[pid] = new Entry(value, privileged ? Math.Min(current.Floor, value) : current.Floor);
            return ResourceLimitChangeResult.Applied;
        }
    }

    /// <summary>Creates the entry of a new process: the request's value, else its live parent's, else <c>0</c>.</summary>
    /// <param name="request">The start request.</param>
    /// <param name="pid">The PID just assigned.</param>
    internal void Create(ProcessStartRequest request, ProcessId pid)
    {
        lock (_gate)
        {
            Entry entry = request.OomScoreAdjust is { } requested
                ? new Entry(Math.Clamp(requested, Exempt, Maximum), Math.Clamp(requested, Exempt, Maximum))
                : request.ParentPid is { } parent && _entries.TryGetValue(parent, out Entry inherited)
                    ? inherited
                    : new Entry(0, 0);
            _entries[pid] = entry;
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

    /// <summary>A value and the floor an unprivileged writer may not go under.</summary>
    private readonly record struct Entry(int Value, int Floor);
}
