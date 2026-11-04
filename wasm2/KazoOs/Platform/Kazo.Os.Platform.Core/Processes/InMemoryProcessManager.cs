using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// In-memory <see cref="IProcessManager"/> implementing the ADR 0012 process lifecycle,
/// cancellation, bounded stop, kill/child-cleanup, and bounded history retention, plus the stop
/// request, deadline and kill escalation of ADR 0069 through one <see cref="ProcessControlBlock"/>
/// per process.
/// </summary>
public sealed class InMemoryProcessManager : IProcessManager
{
    /// <summary>Exit code of a process ended by <c>SIGXCPU</c>: 128 + 24 (ADR 0125, section 2).</summary>
    public const int CpuLimitExitCode = 152;

    private readonly ISimulationClock _clock;
    private readonly ISessionService _session;
    private readonly IEventBus _eventBus;
    private readonly int _maxHistory;
    private readonly InMemoryProcessEnvironmentTable _environments;
    private readonly InMemoryProcessResourceLimitTable _limits;
    private readonly InMemoryProcessFileDescriptorTable _descriptors;
    private readonly InMemoryProcessOomScoreTable _oomScores;

    private readonly Lock _gate = new();
    private long _nextPid = 1;
    private readonly Dictionary<ProcessId, Entry> _active = [];
    private readonly List<ProcessRecord> _history = [];
    private readonly CancellationTokenSource _systemRoot = new();

    /// <summary>Initializes an in-memory process manager.</summary>
    /// <param name="clock">Simulation clock used for every timestamp and bounded-stop deadline.</param>
    /// <param name="session">Active session whose root token every process token links to.</param>
    /// <param name="eventBus">Bus used to publish <see cref="ProcessStateChangedEvent"/> transitions.</param>
    /// <param name="maxHistory">Maximum retained terminal process records; oldest is evicted first.</param>
    /// <param name="environments">
    /// Table receiving each process's arguments, working directory and environment (ADR 0050,
    /// section 5); a private one is created when omitted. Reachable through <see cref="Environments"/>.
    /// </param>
    public InMemoryProcessManager(
        ISimulationClock clock,
        ISessionService session,
        IEventBus eventBus,
        int maxHistory = 200,
        InMemoryProcessEnvironmentTable? environments = null)
        : this(clock, session, eventBus, maxHistory, environments, limits: null)
    {
    }

    /// <summary>Initializes an in-memory process manager with its resource limits (ADR 0125).</summary>
    /// <param name="clock">Simulation clock used for every timestamp and bounded-stop deadline.</param>
    /// <param name="session">Active session whose root token every process token links to.</param>
    /// <param name="eventBus">Bus used to publish <see cref="ProcessStateChangedEvent"/> transitions.</param>
    /// <param name="maxHistory">Maximum retained terminal process records; oldest is evicted first.</param>
    /// <param name="environments">Table of arguments, working directory and environment; a private one when <see langword="null"/>.</param>
    /// <param name="limits">
    /// Table receiving each process's resource limits (ADR 0125); a private one with the kernel's
    /// defaults is created when <see langword="null"/>. Reachable through <see cref="ResourceLimits"/>.
    /// </param>
    /// <param name="descriptors">
    /// Table receiving each process's open descriptors (ADR 0125, <c>RLIMIT_NOFILE</c>); a private one
    /// bound to <paramref name="limits"/> is created when omitted. Reachable through <see cref="FileDescriptors"/>.
    /// </param>
    /// <param name="oomScores">
    /// Table receiving each process's <c>oom_score_adj</c> (ADR 0126, section 4); a private one is
    /// created when omitted. Reachable through <see cref="OomScores"/>.
    /// </param>
    public InMemoryProcessManager(
        ISimulationClock clock,
        ISessionService session,
        IEventBus eventBus,
        int maxHistory,
        InMemoryProcessEnvironmentTable? environments,
        InMemoryProcessResourceLimitTable? limits,
        InMemoryProcessFileDescriptorTable? descriptors = null,
        InMemoryProcessOomScoreTable? oomScores = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHistory, 1);

        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _maxHistory = maxHistory;
        _environments = environments ?? new InMemoryProcessEnvironmentTable();
        _limits = limits ?? new InMemoryProcessResourceLimitTable();
        _descriptors = descriptors ?? new InMemoryProcessFileDescriptorTable(pid => _limits.GetOrNone(pid).NoFile.Soft);
        _oomScores = oomScores ?? new InMemoryProcessOomScoreTable();
    }

    /// <summary>
    /// Gets the per-process arguments, working directory and environment of the active processes
    /// (ADR 0050, section 5): filled at <see cref="Start"/>, dropped when a process ends.
    /// </summary>
    public IProcessEnvironmentTable Environments => _environments;

    /// <summary>
    /// Gets the resource limits of the active processes (ADR 0125): inherited at <see cref="Start"/>,
    /// dropped when a process ends.
    /// </summary>
    public IProcessResourceLimitTable ResourceLimits => _limits;

    /// <summary>
    /// Gets the open descriptors of the active processes (ADR 0125, <c>RLIMIT_NOFILE</c>): the three
    /// standard streams at <see cref="Start"/>, all closed when a process ends.
    /// </summary>
    public IProcessFileDescriptorTable FileDescriptors => _descriptors;

    /// <summary>
    /// Gets the <c>oom_score_adj</c> of the active processes (ADR 0126, section 4): inherited or set
    /// at <see cref="Start"/>, dropped when a process ends.
    /// </summary>
    public InMemoryProcessOomScoreTable OomScores => _oomScores;

    /// <inheritdoc />
    public ProcessRecord Start(ProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (request.ParentPid is { } parentPid && !_active.ContainsKey(parentPid))
            {
                throw new InvalidOperationException($"Parent process '{parentPid}' is not active.");
            }

            EnsureProcessLimit(request);
            ProcessId pid = ProcessId.FromInt64(_nextPid++);
            // Copy the parent's directory and environment now, while it is known to be active.
            _environments.Create(request, pid);
            // ADR 0125 section 3: the limits are inherited like the environment, at fork.
            _limits.Create(request, pid);
            _descriptors.Create(pid, request.Kind == AppKind.Terminal ? "/dev/tty" : "/dev/null");
            // ADR 0126 section 4: oom_score_adj is inherited at fork, or set at exec for a service.
            _oomScores.Create(request, pid);
            // ADR 0097 section 4: a boot service lives until shutdown, not until logout.
            CancellationTokenSource tokenSource = request.SystemScope
                ? CancellationTokenSource.CreateLinkedTokenSource(_systemRoot.Token)
                : _session.CreateLinkedCancellationSource();
            DateTimeOffset now = _clock.UtcNow;

            ProcessRecord record = new(
                pid,
                request.ParentPid,
                request.AppId,
                request.AppInstanceId,
                request.Kind,
                request.UserId,
                request.SessionId,
                ProcessState.Starting,
                request.ResourceProfile,
                serviceHealth: request.Kind == AppKind.Service ? ServiceHealth.Unknown : null,
                createdAtUtc: now,
                startedAtUtc: null,
                stoppedAtUtc: null,
                exitCode: null,
                exitReason: null);

            // ADR 0127: like fork, the child starts with its parent's niceness unless asked otherwise.
            int nice = ProcessNice.Clamp(request.Nice
                ?? (request.ParentPid is { } parent && _active.TryGetValue(parent, out Entry? parentEntry) ? parentEntry.Nice : ProcessNice.Default));
            _active[pid] = new Entry(record, tokenSource, new ProcessControlBlock(pid))
            {
                IsAuxiliary = request.Auxiliary,
                Nice = nice,
                ParentExitGracePeriod = request.ParentExitGracePeriod is { } grace && grace > TimeSpan.Zero ? grace : null,
            };
            _eventBus.Publish(new ProcessStateChangedEvent(pid, ProcessState.Created, ProcessState.Starting, now));
            return record;
        }
    }

    /// <inheritdoc />
    public ProcessRecord MarkRunning(ProcessId pid)
    {
        lock (_gate)
        {
            Entry entry = GetActiveEntry(pid);
            ProcessRecord old = entry.Record;
            if (old.State != ProcessState.Starting)
            {
                throw new InvalidOperationException($"Only a Starting process may become Running (was {old.State}).");
            }

            DateTimeOffset now = _clock.UtcNow;
            ProcessRecord updated = new(
                old.Pid, old.ParentPid, old.AppId, old.AppInstanceId, old.Kind, old.UserId, old.SessionId,
                ProcessState.Running, old.ResourceProfile, old.ServiceHealth, old.CreatedAtUtc,
                startedAtUtc: now, stoppedAtUtc: null, exitCode: null, exitReason: null);

            entry.Record = updated;
            _eventBus.Publish(new ProcessStateChangedEvent(pid, old.State, updated.State, now));
            return updated;
        }
    }

    /// <inheritdoc />
    public ProcessRecord Complete(ProcessId pid, int exitCode)
    {
        lock (_gate)
        {
            Entry entry = GetActiveEntry(pid);
            // ADR 0125 section 2: a process that ends on the SIGXCPU of its CPU limit (128 + 24) was
            // ended by that limit, as the shell reports it.
            ProcessExitReason reason = exitCode == CpuLimitExitCode
                && entry.Control.StopRequest?.Reason == ProcessExitReason.CpuLimitExceeded
                    ? ProcessExitReason.CpuLimitExceeded
                    : ProcessExitReason.Completed;
            return Finish(entry, ProcessState.Stopped, exitCode, reason);
        }
    }

    /// <inheritdoc />
    public ProcessRecord Fault(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Fault)
    {
        lock (_gate)
        {
            Entry entry = GetActiveEntry(pid);
            return Finish(entry, ProcessState.Faulted, exitCode: null, reason);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ADR 0069, section 1, in this order: the process moves to <see cref="ProcessState.Stopping"/>,
    /// its <see cref="ProcessControlBlock.StopRequested"/> is raised once (reason and deadline on the
    /// simulation clock), its token is cancelled, then the manager waits until the deadline. Past it,
    /// the process is killed: <see cref="ProcessExitReason.Timeout"/>, and its control block refuses
    /// any further continuation or gateway call (section 3). Overlapping stops share one outcome and
    /// keep the first request's reason and deadline.
    /// </remarks>
    public async Task<ProcessRecord> StopAsync(
        ProcessId pid,
        TimeSpan timeout,
        ProcessExitReason reason = ProcessExitReason.CloseRequested,
        CancellationToken cancellationToken = default)
    {
        Entry entry;
        TaskCompletionSource<ProcessRecord> stopSignal;
        lock (_gate)
        {
            entry = GetActiveEntry(pid);
            // A second stop joins the first one instead of orphaning its waiter.
            stopSignal = entry.StopSignal ??= new TaskCompletionSource<ProcessRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            TransitionToStopping(entry);
            DateTimeOffset now = _clock.UtcNow;
            entry.Control.TryRequestStop(new ProcessStopRequest(pid, reason, now, now + timeout));
        }

        // SIGTERM: delivered outside the lock, since the process's handlers may call back into the OS.
        entry.Control.RaiseStopRequested();

        lock (_gate)
        {
            // A handler may have ended the process already, which disposed its token source.
            if (_active.TryGetValue(pid, out Entry? current) && ReferenceEquals(current, entry))
            {
                entry.TokenSource.Cancel();
            }
        }

        Task delay = _clock.DelayAsync(timeout, cancellationToken);
        Task completed = await Task.WhenAny(stopSignal.Task, delay).ConfigureAwait(false);
        if (completed == stopSignal.Task)
        {
            return await stopSignal.Task.ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_active.TryGetValue(pid, out Entry? stillActive) && ReferenceEquals(stillActive, entry))
            {
                return ForceTerminal(entry, ProcessExitReason.Timeout);
            }
        }

        // Resolved concurrently between the timeout firing and re-acquiring the lock.
        return await stopSignal.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A descendant started with <see cref="ProcessStartRequest.ParentExitGracePeriod"/> is not ended
    /// here unless the kill is a <c>SIGKILL</c>: it receives a stop request (its <c>SIGTERM</c>) and
    /// is killed only if it still runs at the end of its grace period (ADR 0147).
    /// </remarks>
    public ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed)
    {
        List<(ProcessId Pid, TimeSpan GracePeriod)> graceful = [];
        ProcessRecord ended;
        lock (_gate)
        {
            Entry entry = GetActiveEntry(pid);
            KillDescendants(pid, IsSignalKill(reason), graceful);
            ended = ForceTerminal(entry, reason);
        }

        // Outside the lock: StopAsync raises the stop request, whose handlers may call back into the OS.
        foreach ((ProcessId childPid, TimeSpan gracePeriod) in graceful)
        {
            _ = StopAfterParentExitAsync(childPid, gracePeriod);
        }

        return ended;
    }

    /// <summary>
    /// The parent-death signal of a descendant that asked for one (ADR 0147, like
    /// <c>prctl(PR_SET_PDEATHSIG, SIGTERM)</c>): a stop request with <see cref="ProcessExitReason.DependencyStop"/>,
    /// then a kill at the end of <paramref name="gracePeriod"/>. Whatever the process itself started and
    /// left running is killed once it has ended.
    /// </summary>
    private async Task StopAfterParentExitAsync(ProcessId pid, TimeSpan gracePeriod)
    {
        try
        {
            await StopAsync(pid, gracePeriod, ProcessExitReason.DependencyStop).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            // Ended or stopping meanwhile.
        }

        lock (_gate)
        {
            KillDescendants(pid, signalKill: true, graceful: []);
        }
    }

    /// <inheritdoc />
    public ProcessRecord? SetServiceHealth(ProcessId pid, ServiceHealth health)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(pid, out Entry? entry) || entry.Record.Kind != AppKind.Service)
            {
                return null;
            }

            ProcessRecord old = entry.Record;
            entry.Record = new ProcessRecord(
                old.Pid, old.ParentPid, old.AppId, old.AppInstanceId, old.Kind, old.UserId, old.SessionId,
                old.State, old.ResourceProfile, health, old.CreatedAtUtc, old.StartedAtUtc,
                old.StoppedAtUtc, old.ExitCode, old.ExitReason);
            return entry.Record;
        }
    }

    /// <inheritdoc />
    public int GetNice(ProcessId pid)
    {
        lock (_gate)
        {
            return GetActiveEntry(pid).Nice;
        }
    }

    /// <inheritdoc />
    /// <remarks>No event: like <c>setpriority(2)</c>, a niceness change is not a lifecycle transition.</remarks>
    public int SetNice(ProcessId pid, int nice)
    {
        lock (_gate)
        {
            Entry entry = GetActiveEntry(pid);
            entry.Nice = ProcessNice.Clamp(nice);
            return entry.Nice;
        }
    }

    /// <summary>
    /// Cancels the token of every system-scope process (ADR 0097, section 4), as the last step of a
    /// shutdown; their stop is the service manager's job before that.
    /// </summary>
    public void CancelSystemScope() => _systemRoot.Cancel();

    /// <inheritdoc />
    public bool TryGetActive(ProcessId pid, out ProcessRecord process)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(pid, out Entry? entry))
            {
                process = entry.Record;
                return true;
            }

            process = null!;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetSingleton(string appId, out ProcessRecord process)
    {
        lock (_gate)
        {
            // ADR 0084 section 3: a renderer is not the site; only the app's own instance counts.
            Entry? match = _active.Values.FirstOrDefault(e => e.Record.AppId == appId && !e.IsAuxiliary);
            process = match?.Record!;
            return match is not null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessRecord> GetActiveProcesses()
    {
        lock (_gate)
        {
            return [.. _active.Values.Select(e => e.Record)];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessRecord> GetHistory()
    {
        lock (_gate)
        {
            return [.. _history];
        }
    }

    /// <summary>
    /// Gets the control block of one active process (ADR 0069): its stop request, killed flag and
    /// scheduler. The execution context keeps it after the process ends.
    /// </summary>
    /// <param name="pid">An active process.</param>
    /// <exception cref="KeyNotFoundException">No active process has this PID.</exception>
    public ProcessControlBlock GetControlBlock(ProcessId pid)
    {
        lock (_gate)
        {
            return GetActiveEntry(pid).Control;
        }
    }

    /// <inheritdoc />
    public CancellationToken GetCancellationToken(ProcessId pid)
    {
        lock (_gate)
        {
            return GetActiveEntry(pid).TokenSource.Token;
        }
    }

    /// <summary>
    /// Ends every active descendant of a process; with <paramref name="signalKill"/>, as killed (ADR 0069).
    /// Without it, a descendant that has a parent-exit grace period is added to <paramref name="graceful"/>
    /// instead, with its own descendants (ADR 0147).
    /// </summary>
    private void KillDescendants(ProcessId parentPid, bool signalKill, List<(ProcessId Pid, TimeSpan GracePeriod)> graceful)
    {
        List<ProcessId> children = [.. _active.Values
            .Where(e => e.Record.ParentPid == parentPid)
            .Select(e => e.Record.Pid)];

        foreach (ProcessId childPid in children)
        {
            if (_active.TryGetValue(childPid, out Entry? childEntry))
            {
                if (!signalKill && childEntry.ParentExitGracePeriod is { } gracePeriod)
                {
                    graceful.Add((childPid, gracePeriod));
                    continue;
                }

                KillDescendants(childPid, signalKill, graceful);
                if (signalKill)
                {
                    childEntry.Control.MarkKilled();
                }

                ForceTerminal(childEntry, ProcessExitReason.DependencyStop);
            }
        }
    }

    private void TransitionToStopping(Entry entry)
    {
        ProcessRecord old = entry.Record;
        if (old.State == ProcessState.Stopping)
        {
            return;
        }

        if (old.State is not (ProcessState.Starting or ProcessState.Running))
        {
            throw new InvalidOperationException($"Cannot stop a process in state {old.State}.");
        }

        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset started = old.StartedAtUtc ?? now;
        ProcessRecord updated = new(
            old.Pid, old.ParentPid, old.AppId, old.AppInstanceId, old.Kind, old.UserId, old.SessionId,
            ProcessState.Stopping, old.ResourceProfile, old.ServiceHealth, old.CreatedAtUtc,
            started, stoppedAtUtc: null, exitCode: null, exitReason: null);

        entry.Record = updated;
        _eventBus.Publish(new ProcessStateChangedEvent(old.Pid, old.State, updated.State, now));
    }

    private ProcessRecord Finish(Entry entry, ProcessState terminalState, int? exitCode, ProcessExitReason reason)
    {
        ProcessRecord old = entry.Record;
        DateTimeOffset now = _clock.UtcNow;
        ProcessRecord updated = new(
            old.Pid, old.ParentPid, old.AppId, old.AppInstanceId, old.Kind, old.UserId, old.SessionId,
            terminalState, old.ResourceProfile, old.ServiceHealth, old.CreatedAtUtc, old.StartedAtUtc,
            stoppedAtUtc: now, exitCode, reason);

        _active.Remove(old.Pid);
        _environments.Remove(old.Pid);
        _limits.Remove(old.Pid);
        _descriptors.Remove(old.Pid);
        _oomScores.Remove(old.Pid);
        entry.TokenSource.Cancel();
        entry.TokenSource.Dispose();
        AddHistory(updated);
        _eventBus.Publish(new ProcessStateChangedEvent(old.Pid, old.State, updated.State, now));
        entry.StopSignal?.TrySetResult(updated);
        return updated;
    }

    private ProcessRecord ForceTerminal(Entry entry, ProcessExitReason reason)
    {
        if (IsSignalKill(reason))
        {
            // Before the terminal event: whatever reacts to it (closing the windows) already finds
            // the process cut off from the OS.
            entry.Control.MarkKilled();
        }

        return Finish(entry, ProcessState.Stopped, exitCode: null, reason);
    }

    /// <summary>
    /// Tells whether a forced end is a <c>SIGKILL</c> (ADR 0069, section 3): <c>kill -9</c>, "Force
    /// kill", or the end of a grace period. Other forced ends (window closed, logout, app disabled)
    /// keep the OS reachable for the process's own cleanup, as before.
    /// </summary>
    /// <remarks>
    /// ADR 0125: the OS's own <c>SIGKILL</c> at a hard CPU limit or a memory limit is one too, and so
    /// is the OOM killer's (ADR 0126).
    /// </remarks>
    private static bool IsSignalKill(ProcessExitReason reason) =>
        reason is ProcessExitReason.Killed or ProcessExitReason.Timeout
            or ProcessExitReason.CpuLimitExceeded or ProcessExitReason.MemoryLimitExceeded
            or ProcessExitReason.OutOfMemory;

    /// <summary>
    /// The <c>RLIMIT_NPROC</c> check of <c>fork(2)</c> (ADR 0125, section 1): the caller's soft
    /// <c>nproc</c> limit against the live processes of the user. Called under the lock.
    /// </summary>
    /// <exception cref="ProcessLimitExceededException">The limit is reached (<c>EAGAIN</c>).</exception>
    private void EnsureProcessLimit(ProcessStartRequest request)
    {
        if (request.ResourceLimitExempt || request.SystemScope)
        {
            // ADR 0125 section 5: an elevated root (CAP_SYS_RESOURCE) and system processes.
            return;
        }

        long limit = _limits.Preview(request).NProc.Soft;
        if (limit == ResourceLimit.Unlimited)
        {
            return;
        }

        int running = _active.Values.Count(entry => entry.Record.UserId == request.UserId);
        if (running >= limit)
        {
            throw new ProcessLimitExceededException(limit);
        }
    }

    private void AddHistory(ProcessRecord record)
    {
        _history.Add(record);
        if (_history.Count > _maxHistory)
        {
            _history.RemoveAt(0);
        }
    }

    private Entry GetActiveEntry(ProcessId pid) => _active.TryGetValue(pid, out Entry? entry)
        ? entry
        : throw new KeyNotFoundException($"No active process with PID '{pid}'.");

    private sealed class Entry(ProcessRecord record, CancellationTokenSource tokenSource, ProcessControlBlock control)
    {
        public ProcessRecord Record { get; set; } = record;
        public CancellationTokenSource TokenSource { get; } = tokenSource;
        public ProcessControlBlock Control { get; } = control;
        public TaskCompletionSource<ProcessRecord>? StopSignal { get; set; }

        /// <summary>Whether the process is an auxiliary of its app (<see cref="ProcessStartRequest.Auxiliary"/>).</summary>
        public bool IsAuxiliary { get; init; }

        /// <summary>The process's niceness (ADR 0127), <c>-20</c> to <c>19</c>.</summary>
        public int Nice { get; set; }

        /// <summary>Its <see cref="ProcessStartRequest.ParentExitGracePeriod"/>, when positive (ADR 0147).</summary>
        public TimeSpan? ParentExitGracePeriod { get; init; }
    }
}
