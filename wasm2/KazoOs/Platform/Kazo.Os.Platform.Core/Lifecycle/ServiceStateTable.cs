using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Lifecycle;

/// <summary>
/// The service manager's own record of every <c>Service</c> app's state (ADR 0097, sections 1 and
/// 2), like the unit table systemd keeps in memory: it outlives the service's process, so a crashed
/// service still reads as <c>failed</c> with its reason until it is started again or reset.
/// </summary>
/// <remarks>
/// <para>
/// The table is written only by <see cref="AppLifecycleOrchestrator"/>: at start
/// (<see cref="MarkActivating"/>), when the service says it is ready (<see cref="Notify"/>), when a
/// stop begins (<see cref="MarkDeactivating"/>), and when the process ends
/// (<see cref="MarkEnded"/>). Every transition is timestamped on the simulation clock.
/// </para>
/// <para>
/// Messages are keyed by PID: a notification from a process that is no longer the service's main
/// process is dropped, as systemd ignores <c>sd_notify</c> from a stale PID.
/// </para>
/// </remarks>
internal sealed class ServiceStateTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Creates an empty table.</summary>
    /// <param name="now">Simulation clock for the state timestamps.</param>
    public ServiceStateTable(Func<DateTimeOffset> now) => _now = now ?? throw new ArgumentNullException(nameof(now));

    /// <summary>Raised after a service entered <see cref="ServiceActiveState.Failed"/>, outside the table's lock.</summary>
    public event Action<string, ServiceStatusSnapshot>? Failed;

    /// <summary>Gets the state of one service, or <see cref="ServiceStatusSnapshot.NeverStarted"/>.</summary>
    /// <param name="appId">Service app ID.</param>
    public ServiceStatusSnapshot Get(string appId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(appId, out Entry? entry) ? entry.Snapshot : ServiceStatusSnapshot.NeverStarted;
        }
    }

    /// <summary>Records a new run: the process exists, its start code has not returned yet.</summary>
    /// <param name="appId">Service app ID.</param>
    /// <param name="pid">The new main process.</param>
    public void MarkActivating(string appId, ProcessId pid)
    {
        lock (_gate)
        {
            // A new run clears the previous failure, its health and its text (systemd resets Result too).
            _entries[appId] = new Entry(pid, new ServiceStatusSnapshot(
                ServiceActiveState.Activating, ServiceRunResult.Success, ServiceHealth.Unknown, null, _now(), pid.Value, null));
        }
    }

    /// <summary>
    /// Applies one message of the running service (ADR 0097, section 2): <c>READY</c> moves it to
    /// <see cref="ServiceActiveState.Active"/> with health <see cref="ServiceHealth.Healthy"/> unless
    /// it reported another one; health and text replace the current ones.
    /// </summary>
    /// <param name="appId">Service app ID.</param>
    /// <param name="pid">The process that sent the message.</param>
    /// <param name="notification">The message.</param>
    /// <returns>The new health when it changed, so the process record can carry it; otherwise <see langword="null"/>.</returns>
    public ServiceHealth? Notify(string appId, ProcessId pid, ServiceNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_gate)
        {
            if (!_entries.TryGetValue(appId, out Entry? entry) || entry.Pid != pid
                || entry.Snapshot.ActiveState is not (ServiceActiveState.Activating or ServiceActiveState.Active))
            {
                return null;
            }

            ServiceStatusSnapshot old = entry.Snapshot;
            ServiceHealth health = notification.Health ?? (notification.Ready && old.Health == ServiceHealth.Unknown ? ServiceHealth.Healthy : old.Health);
            string? text = notification.StatusText is null ? old.StatusText : notification.StatusText.Length == 0 ? null : notification.StatusText;
            bool becomesActive = notification.Ready && old.ActiveState == ServiceActiveState.Activating;
            entry.Snapshot = old with
            {
                ActiveState = becomesActive ? ServiceActiveState.Active : old.ActiveState,
                StateChangedAtUtc = becomesActive ? _now() : old.StateChangedAtUtc,
                Health = health,
                StatusText = text
            };
            return health == old.Health ? null : health;
        }
    }

    /// <summary>Gets whether the run of <paramref name="pid"/> still waits for its start code to return.</summary>
    public bool IsActivating(string appId, ProcessId pid)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(appId, out Entry? entry) && entry.Pid == pid
                && entry.Snapshot.ActiveState == ServiceActiveState.Activating;
        }
    }

    /// <summary>Records that the OS asked the service to stop (<c>deactivating</c>).</summary>
    public void MarkDeactivating(string appId, ProcessId pid)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(appId, out Entry? entry) && entry.Pid == pid
                && entry.Snapshot.ActiveState is ServiceActiveState.Activating or ServiceActiveState.Active)
            {
                entry.Snapshot = entry.Snapshot with { ActiveState = ServiceActiveState.Deactivating, StateChangedAtUtc = _now() };
            }
        }
    }

    /// <summary>Marks the next end of <paramref name="pid"/> as a start timeout, whatever its exit reason.</summary>
    public void MarkStartTimedOut(string appId, ProcessId pid)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(appId, out Entry? entry) && entry.Pid == pid)
            {
                entry.StartTimedOut = true;
            }
        }
    }

    /// <summary>
    /// Records the end of the service's process: <see cref="ServiceActiveState.Inactive"/> when it
    /// ended as asked (or exited 0 on its own), <see cref="ServiceActiveState.Failed"/> otherwise,
    /// with the reason (ADR 0097, section 1).
    /// </summary>
    /// <param name="appId">Service app ID.</param>
    /// <param name="record">The terminal record of the process.</param>
    public void MarkEnded(string appId, ProcessRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ServiceStatusSnapshot? failed = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(appId, out Entry? entry) || entry.Pid != record.Pid)
            {
                return;
            }

            ServiceRunResult result = entry.StartTimedOut ? ServiceRunResult.Timeout : ResultOf(record);
            ServiceStatusSnapshot snapshot = entry.Snapshot with
            {
                ActiveState = result == ServiceRunResult.Success ? ServiceActiveState.Inactive : ServiceActiveState.Failed,
                Result = result,
                // A stopped service has no health; a failed one keeps what it last said, for the diagnosis.
                Health = result == ServiceRunResult.Success ? ServiceHealth.Unknown : entry.Snapshot.Health,
                StatusText = result == ServiceRunResult.Success ? null : entry.Snapshot.StatusText,
                StateChangedAtUtc = record.StoppedAtUtc ?? _now(),
                MainPid = null,
                ExitCode = record.ExitCode
            };
            entry.Snapshot = snapshot;
            if (snapshot.ActiveState == ServiceActiveState.Failed)
            {
                failed = snapshot;
            }
        }

        if (failed is not null)
        {
            Failed?.Invoke(appId, failed);
        }
    }

    /// <summary>Clears a <see cref="ServiceActiveState.Failed"/> state (<c>systemctl reset-failed</c>).</summary>
    /// <returns><see langword="true"/> when the service was failed.</returns>
    public bool ResetFailed(string appId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(appId, out Entry? entry) && entry.Snapshot.ActiveState == ServiceActiveState.Failed)
            {
                entry.Snapshot = ServiceStatusSnapshot.NeverStarted with { StateChangedAtUtc = _now() };
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Maps how a process ended to systemd's <c>Result</c>: every end the OS or a user asked for is
    /// a success; an exit on its own is a success only with code 0.
    /// </summary>
    internal static ServiceRunResult ResultOf(ProcessRecord record) => record.ExitReason switch
    {
        ProcessExitReason.Completed => record.ExitCode is null or 0 ? ServiceRunResult.Success : ServiceRunResult.ExitCode,
        ProcessExitReason.Fault => ServiceRunResult.Exception,
        ProcessExitReason.Killed => ServiceRunResult.Killed,
        ProcessExitReason.Timeout => ServiceRunResult.Timeout,
        _ when record.State == ProcessState.Faulted => ServiceRunResult.Exception,
        _ => ServiceRunResult.Success
    };

    private sealed class Entry(ProcessId pid, ServiceStatusSnapshot snapshot)
    {
        public ProcessId Pid { get; } = pid;

        public ServiceStatusSnapshot Snapshot { get; set; } = snapshot;

        public bool StartTimedOut { get; set; }
    }
}
