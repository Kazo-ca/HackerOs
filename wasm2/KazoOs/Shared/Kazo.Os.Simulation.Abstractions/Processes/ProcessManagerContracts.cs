using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Simulation.Abstractions.Processes;

/// <summary>Describes one new process to create, per ADR 0012.</summary>
/// <param name="ParentPid">The parent process's PID, or <see langword="null"/> for a root process.</param>
/// <param name="AppId">Non-empty ID of the app to host.</param>
/// <param name="AppInstanceId">Identifier of this specific running instance of the app.</param>
/// <param name="Kind">Lifecycle/hosting model of the hosted app.</param>
/// <param name="UserId">User the process will run as.</param>
/// <param name="SessionId">Session the process will belong to.</param>
/// <param name="ResourceProfile">Resource profile snapshot used to derive simulated resource ticks.</param>
/// <param name="Arguments">
/// The new process's <c>argv</c>, command name included (ADR 0050, section 5);
/// <see langword="null"/> or empty records the app ID alone.
/// </param>
/// <param name="WorkingDirectory">
/// Initial working directory; <see langword="null"/> inherits the parent's, or <c>/</c> for a root process.
/// </param>
/// <param name="Environment">
/// Variables applied over the inherited environment (the parent's, copied at creation; empty for a
/// root process). The platform passes the login environment for a root process.
/// </param>
/// <param name="Auxiliary">
/// Whether the process is an auxiliary of its app rather than the app's instance (ADR 0084,
/// section 3): the renderer of a Blazor site, as <c>chrome --type=renderer</c> is to its browser. An
/// auxiliary process is never found by <see cref="IProcessManager.TryGetSingleton"/>, so a web app's
/// on-demand activation still starts its site when only renderers run.
/// </param>
/// <param name="SystemScope">
/// Whether the process belongs to the system rather than to the login session (ADR 0097, section 4):
/// a service started at boot, before any login. Its token is linked to the OS's own root, cancelled
/// only at shutdown, so a logout does not stop it; it can be created while no session is active.
/// </param>
public sealed record ProcessStartRequest(
    ProcessId? ParentPid,
    string AppId,
    AppInstanceId AppInstanceId,
    AppKind Kind,
    LocalUserId UserId,
    SessionId SessionId,
    ResourceProfile ResourceProfile,
    IReadOnlyList<string>? Arguments = null,
    VirtualPath? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool Auxiliary = false,
    bool SystemScope = false)
{
    /// <summary>
    /// Gets the niceness the new process starts with (ADR 0127, section 1), from
    /// <see cref="ProcessNice.Min"/> to <see cref="ProcessNice.Max"/>; <see langword="null"/>
    /// inherits the parent's (<see cref="ProcessNice.Default"/> for a root process), as
    /// <c>fork</c> copies the parent's <c>nice</c>. A value out of range is clamped.
    /// </summary>
    public int? Nice { get; init; }

    /// <summary>
    /// Limits of a process with no live parent (ADR 0125, section 3): the session's, computed from
    /// <c>/etc/security/limits.conf</c> at login. <see langword="null"/> gives a root process the
    /// kernel's defaults; a child always inherits its parent's limits and ignores this value.
    /// </summary>
    public ProcessResourceLimits? ResourceLimits { get; init; }

    /// <summary>
    /// Whether the start is exempt from the <c>nproc</c> limit (ADR 0125, section 5): an elevated
    /// <c>root</c> (<c>CAP_SYS_RESOURCE</c>) or a system process.
    /// </summary>
    public bool ResourceLimitExempt { get; init; }

    /// <summary>
    /// The <c>oom_score_adj</c> the process starts with (ADR 0126, section 4), from <c>-1000</c>
    /// (never chosen by the OOM killer) to <c>1000</c>, like systemd's <c>OOMScoreAdjust=</c> applied
    /// at <c>exec</c>. <see langword="null"/> inherits the parent's value, or <c>0</c> for a root process.
    /// </summary>
    public int? OomScoreAdjust { get; init; }

    /// <summary>
    /// How long the process may keep running once its parent has ended (ADR 0147), like a
    /// <c>SIGTERM</c> parent-death signal (<c>prctl(PR_SET_PDEATHSIG)</c>) followed by a
    /// <c>SIGKILL</c>. When the parent ends without being killed (its window closed, a logout), the
    /// process receives a stop request with <see cref="ProcessExitReason.DependencyStop"/> instead of
    /// ending at once, and is killed if it still runs at the end of this period. A <c>SIGKILL</c> of
    /// the parent (<c>kill -9</c>, a limit, the OOM killer) still ends it at once.
    /// <see langword="null"/> or zero ends it with its parent, as before; the background tasks of
    /// <c>HackBackground</c> use 5 seconds.
    /// </summary>
    public TimeSpan? ParentExitGracePeriod { get; init; }
}

/// <summary>
/// Creates, tracks, and terminates processes in memory, per ADR 0012. All state transitions are
/// platform-owned: callers only request starts, report a hosted app's own lifecycle progress
/// (<see cref="MarkRunning"/>, <see cref="Complete"/>, <see cref="Fault"/>), or request a stop
/// (<see cref="StopAsync"/>, <see cref="Kill"/>).
/// </summary>
public interface IProcessManager
{
    /// <summary>
    /// Creates a new process in the <see cref="ProcessState.Starting"/> state, linked to the
    /// current session's root cancellation token.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="request"/> declares a parent PID that is not currently active.</exception>
    /// <exception cref="ProcessLimitExceededException">The user already runs as many processes as the <c>nproc</c> soft limit allows (ADR 0125).</exception>
    ProcessRecord Start(ProcessStartRequest request);

    /// <summary>
    /// Reports that a <see cref="ProcessState.Starting"/> process has reached steady-state
    /// execution, recording the current simulation time as its start time.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    /// <exception cref="InvalidOperationException">The process is not currently <see cref="ProcessState.Starting"/>.</exception>
    ProcessRecord MarkRunning(ProcessId pid);

    /// <summary>Reports that a process finished its own work and reached <see cref="ProcessState.Stopped"/>.</summary>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    ProcessRecord Complete(ProcessId pid, int exitCode);

    /// <summary>Reports that a process terminated abnormally and reached <see cref="ProcessState.Faulted"/>.</summary>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    ProcessRecord Fault(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Fault);

    /// <summary>
    /// Requests a graceful stop: cancels the process's token and moves it to
    /// <see cref="ProcessState.Stopping"/>, then waits up to <paramref name="timeout"/> (measured by
    /// the simulation clock) for <see cref="Complete"/>, <see cref="Fault"/>, or <see cref="Kill"/> to
    /// resolve it. If the deadline elapses first, the process is force-stopped with
    /// <see cref="ProcessExitReason.Timeout"/>.
    /// </summary>
    /// <remarks>
    /// ADR 0069: the process first moves to <see cref="ProcessState.Stopping"/>, then receives one
    /// <see cref="ProcessStopRequest"/> carrying <paramref name="reason"/> and the deadline (raised
    /// once, even when several stops overlap), then its token is cancelled. Concurrent calls share
    /// the same outcome.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    Task<ProcessRecord> StopAsync(
        ProcessId pid,
        TimeSpan timeout,
        ProcessExitReason reason = ProcessExitReason.CloseRequested,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Immediately force-stops one process and every active descendant (recorded with
    /// <see cref="ProcessExitReason.DependencyStop"/>), cancelling their tokens.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed);

    /// <summary>Attempts to find one active (non-terminal) process by PID.</summary>
    bool TryGetActive(ProcessId pid, out ProcessRecord process);

    /// <summary>Attempts to find the first active (non-terminal) process hosting a given app, for single-instance apps.</summary>
    bool TryGetSingleton(string appId, out ProcessRecord process);

    /// <summary>Gets every currently active (non-terminal) process.</summary>
    IReadOnlyList<ProcessRecord> GetActiveProcesses();

    /// <summary>Gets bounded, most-recent terminal process history, oldest first.</summary>
    IReadOnlyList<ProcessRecord> GetHistory();

    /// <summary>Gets the cancellation token owned by one active process.</summary>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    CancellationToken GetCancellationToken(ProcessId pid);

    /// <summary>
    /// Records the health a running <see cref="AppKind.Service"/> process reported about itself
    /// (ADR 0097, section 2) in its record. No state change, no event.
    /// </summary>
    /// <param name="pid">An active service process.</param>
    /// <param name="health">The reported health.</param>
    /// <returns>The updated record, or <see langword="null"/> when the process is not active or not a service.</returns>
    ProcessRecord? SetServiceHealth(ProcessId pid, ServiceHealth health) => null;

    /// <summary>
    /// Gets the niceness of one active process (ADR 0127, section 1): <c>-20</c> (most favored)
    /// to <c>19</c>, <c>0</c> by default. Defaults to <see cref="ProcessNice.Default"/> so existing
    /// implementations keep compiling.
    /// </summary>
    /// <param name="pid">An active process.</param>
    /// <exception cref="KeyNotFoundException">No active process has this PID (implementations that track niceness).</exception>
    int GetNice(ProcessId pid) => ProcessNice.Default;

    /// <summary>
    /// Sets the niceness of one active process, as <c>setpriority(2)</c> does once the caller's
    /// rights were checked (the process gateway checks them, ADR 0127). Children started afterwards
    /// inherit it; running children keep theirs. Defaults to refusing, so existing implementations
    /// keep compiling.
    /// </summary>
    /// <param name="pid">An active process.</param>
    /// <param name="nice">The new niceness, clamped to <see cref="ProcessNice.Min"/>..<see cref="ProcessNice.Max"/>.</param>
    /// <returns>The niceness now in effect.</returns>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    /// <exception cref="NotSupportedException">This process manager does not track niceness.</exception>
    int SetNice(ProcessId pid, int nice) => throw new NotSupportedException("This process manager does not track niceness.");
}

/// <summary>
/// The niceness range of <c>nice(1)</c> and <c>setpriority(2)</c> (ADR 0127, section 1). In
/// Kazo OS niceness only orders the queue of the worker pool (ADR 0071): there is no quantum to
/// share, so a process on the main thread runs exactly as before whatever its niceness.
/// </summary>
public static class ProcessNice
{
    /// <summary>The most favored niceness, <c>-20</c>.</summary>
    public const int Min = -20;

    /// <summary>The least favored niceness, <c>19</c>.</summary>
    public const int Max = 19;

    /// <summary>The niceness of a process nobody changed, <c>0</c>.</summary>
    public const int Default = 0;

    /// <summary>The adjustment <c>nice command</c> applies without <c>-n</c>, <c>10</c>.</summary>
    public const int DefaultAdjustment = 10;

    /// <summary>Clamps a requested niceness into <see cref="Min"/>..<see cref="Max"/>, as the kernel does.</summary>
    /// <param name="nice">Requested niceness.</param>
    public static int Clamp(int nice) => Math.Clamp(nice, Min, Max);

    /// <summary>
    /// Gets the kernel priority shown next to the niceness (<c>PR</c> of <c>top</c>, field 18 of
    /// <c>/proc/&lt;pid&gt;/stat</c>): <c>20 + nice</c>.
    /// </summary>
    /// <param name="nice">A niceness.</param>
    public static int ToPriority(int nice) => 20 + Clamp(nice);
}

/// <summary>
/// One request to stop a process (ADR 0069, section 1): the <c>SIGTERM</c> the OS delivers before
/// it cancels the process's token and waits for it to end.
/// </summary>
/// <param name="Pid">The process asked to stop.</param>
/// <param name="Reason">Why it is asked to stop (<see cref="ProcessExitReason.CloseRequested"/>, <see cref="ProcessExitReason.Terminated"/>…).</param>
/// <param name="RequestedAtUtc">Simulated time of the request.</param>
/// <param name="Deadline">Simulated time at which the OS forces the process (<c>SIGKILL</c>) if it is still running.</param>
public sealed record ProcessStopRequest(
    ProcessId Pid, ProcessExitReason Reason, DateTimeOffset RequestedAtUtc, DateTimeOffset Deadline);

/// <summary>Published whenever a process transitions between lifecycle states.</summary>
public sealed record ProcessStateChangedEvent(
    ProcessId Pid, ProcessState OldState, ProcessState NewState, DateTimeOffset AtUtc);
