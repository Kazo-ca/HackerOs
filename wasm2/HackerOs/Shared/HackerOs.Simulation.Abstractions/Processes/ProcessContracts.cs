using System.Globalization;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Simulation.Abstractions.Processes;

/// <summary>
/// Identifies one process within a single installation runtime for the life of that boot, per
/// ADR 0012. PIDs are positive, monotonically increasing, and never reused during one boot; PID
/// zero is reserved and never assigned.
/// </summary>
public readonly record struct ProcessId
{
    private ProcessId(long value) => Value = value;

    /// <summary>Gets the positive PID value.</summary>
    public long Value { get; }

    /// <summary>Creates a process identifier from a positive value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public static ProcessId FromInt64(long value) => value > 0
        ? new ProcessId(value)
        : throw new ArgumentOutOfRangeException(nameof(value), "Process IDs must be positive; zero is reserved.");

    /// <summary>Returns the PID as decimal text.</summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Identifies one running instance of an app independently of its process.</summary>
public readonly record struct AppInstanceId
{
    private AppInstanceId(Guid value) => Value = value;

    /// <summary>Gets the opaque 128-bit identifier value.</summary>
    public Guid Value { get; }

    /// <summary>Creates an app instance identifier from a non-empty value.</summary>
    public static AppInstanceId FromGuid(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("App instance IDs cannot be empty.", nameof(value))
        : new AppInstanceId(value);

    /// <summary>Returns canonical lowercase identifier text.</summary>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>Defines the ordered process lifecycle state machine from ADR 0012.</summary>
public enum ProcessState
{
    /// <summary>The process record exists but has not begun starting.</summary>
    Created,

    /// <summary>The process is initializing and has not yet reached steady-state execution.</summary>
    Starting,

    /// <summary>The process is executing normally.</summary>
    Running,

    /// <summary>The process has been asked to stop and is completing a bounded graceful shutdown.</summary>
    Stopping,

    /// <summary>The process has stopped and will never resume.</summary>
    Stopped,

    /// <summary>The process terminated abnormally from <see cref="Running"/>.</summary>
    Faulted
}

/// <summary>Defines why one process reached a terminal state, per ADR 0012.</summary>
public enum ProcessExitReason
{
    /// <summary>The process finished its work on its own.</summary>
    Completed,

    /// <summary>The user requested the process's window/command close.</summary>
    CloseRequested,

    /// <summary>An in-flight operation was cancelled.</summary>
    Cancelled,

    /// <summary>The process was forcibly killed after a graceful stop failed or was skipped.</summary>
    Killed,

    /// <summary>The owning session logged out.</summary>
    Logout,

    /// <summary>The platform shut down.</summary>
    Shutdown,

    /// <summary>The app was disabled by policy while running.</summary>
    Disabled,

    /// <summary>The graceful stop deadline elapsed and the process was force-stopped.</summary>
    Timeout,

    /// <summary>A dependency (e.g. parent process) stopped first.</summary>
    DependencyStop,

    /// <summary>The process faulted.</summary>
    Fault,

    /// <summary>
    /// The active UI platform changed (doc/wasm/dev/old/mobile-interface-platform-plan.md §6.3, <c>MOB-008</c>) and
    /// this UI instance does not carry over to the new shell.
    /// </summary>
    PlatformChanged,

    /// <summary>
    /// Another process asked this one to stop (<c>SIGTERM</c> from <c>kill</c> or the task
    /// manager's "End task", ADR 0069) and it ended within its grace period.
    /// </summary>
    Terminated,

    /// <summary>
    /// The process went over its CPU time limit (<c>RLIMIT_CPU</c>, ADR 0125): it ended on the
    /// <c>SIGXCPU</c> the OS sent at the soft limit (exit code 152), or the OS killed it
    /// (<c>SIGKILL</c>, exit code 137) at the hard limit.
    /// </summary>
    CpuLimitExceeded,

    /// <summary>
    /// The process went over its resident memory limit (<c>RLIMIT_RSS</c>, ADR 0125) and the OS
    /// killed it (<c>SIGKILL</c>, exit code 137).
    /// </summary>
    MemoryLimitExceeded,

    /// <summary>
    /// The OOM killer chose the process because the system ran out of memory, or because its
    /// runtime neared the browser's real memory limit (ADR 0126), and killed it (<c>SIGKILL</c>, exit
    /// code 137).
    /// </summary>
    OutOfMemory
}

/// <summary>Reports the self-assessed health of a long-running <see cref="AppKind.Service"/> process.</summary>
public enum ServiceHealth
{
    /// <summary>Health has not been reported yet.</summary>
    Unknown,

    /// <summary>The service is operating normally.</summary>
    Healthy,

    /// <summary>The service is operating with reduced capability.</summary>
    Degraded,

    /// <summary>The service is not functioning.</summary>
    Unhealthy
}

/// <summary>
/// Declares the bounded simulation-only CPU/memory/storage-I/O/network-I/O weight an app may
/// consume, per ADR 0012. These weights are simulation inputs only; they never reflect a real
/// hardware measurement.
/// </summary>
public sealed record ResourceProfile
{
    /// <summary>Initializes a validated resource profile.</summary>
    /// <param name="baselineCpuWeight">Steady-state CPU weight in <c>[0, 1]</c>.</param>
    /// <param name="burstCpuWeight">Peak CPU weight in <c>[0, 1]</c>, at least <paramref name="baselineCpuWeight"/>.</param>
    /// <param name="baselineMemoryWeight">Steady-state memory weight in <c>[0, 1]</c>.</param>
    /// <param name="burstMemoryWeight">Peak memory weight in <c>[0, 1]</c>, at least <paramref name="baselineMemoryWeight"/>.</param>
    /// <param name="baselineStorageIoWeight">Steady-state storage I/O weight in <c>[0, 1]</c>.</param>
    /// <param name="burstStorageIoWeight">Peak storage I/O weight in <c>[0, 1]</c>, at least <paramref name="baselineStorageIoWeight"/>.</param>
    /// <param name="baselineNetworkIoWeight">Steady-state network I/O weight in <c>[0, 1]</c>.</param>
    /// <param name="burstNetworkIoWeight">Peak network I/O weight in <c>[0, 1]</c>, at least <paramref name="baselineNetworkIoWeight"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A weight is outside <c>[0, 1]</c> or a burst weight is below its baseline.</exception>
    public ResourceProfile(
        double baselineCpuWeight,
        double burstCpuWeight,
        double baselineMemoryWeight,
        double burstMemoryWeight,
        double baselineStorageIoWeight,
        double burstStorageIoWeight,
        double baselineNetworkIoWeight,
        double burstNetworkIoWeight)
    {
        ValidatePair(baselineCpuWeight, burstCpuWeight, nameof(baselineCpuWeight), nameof(burstCpuWeight));
        ValidatePair(baselineMemoryWeight, burstMemoryWeight, nameof(baselineMemoryWeight), nameof(burstMemoryWeight));
        ValidatePair(baselineStorageIoWeight, burstStorageIoWeight, nameof(baselineStorageIoWeight), nameof(burstStorageIoWeight));
        ValidatePair(baselineNetworkIoWeight, burstNetworkIoWeight, nameof(baselineNetworkIoWeight), nameof(burstNetworkIoWeight));

        BaselineCpuWeight = baselineCpuWeight;
        BurstCpuWeight = burstCpuWeight;
        BaselineMemoryWeight = baselineMemoryWeight;
        BurstMemoryWeight = burstMemoryWeight;
        BaselineStorageIoWeight = baselineStorageIoWeight;
        BurstStorageIoWeight = burstStorageIoWeight;
        BaselineNetworkIoWeight = baselineNetworkIoWeight;
        BurstNetworkIoWeight = burstNetworkIoWeight;
    }

    /// <summary>Gets the zero-weight profile used by processes that consume no simulated resources.</summary>
    public static ResourceProfile None { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Gets the steady-state CPU weight.</summary>
    public double BaselineCpuWeight { get; }

    /// <summary>Gets the peak CPU weight.</summary>
    public double BurstCpuWeight { get; }

    /// <summary>Gets the steady-state memory weight.</summary>
    public double BaselineMemoryWeight { get; }

    /// <summary>Gets the peak memory weight.</summary>
    public double BurstMemoryWeight { get; }

    /// <summary>Gets the steady-state storage I/O weight.</summary>
    public double BaselineStorageIoWeight { get; }

    /// <summary>Gets the peak storage I/O weight.</summary>
    public double BurstStorageIoWeight { get; }

    /// <summary>Gets the steady-state network I/O weight.</summary>
    public double BaselineNetworkIoWeight { get; }

    /// <summary>Gets the peak network I/O weight.</summary>
    public double BurstNetworkIoWeight { get; }

    private static void ValidatePair(double baseline, double burst, string baselineName, string burstName)
    {
        ValidateWeight(baseline, baselineName);
        ValidateWeight(burst, burstName);

        if (burst < baseline)
        {
            throw new ArgumentOutOfRangeException(burstName, "A burst weight cannot be below its baseline weight.");
        }
    }

    private static void ValidateWeight(double weight, string name)
    {
        if (double.IsNaN(weight) || weight < 0 || weight > 1)
        {
            throw new ArgumentOutOfRangeException(name, "Resource weights must be within [0, 1].");
        }
    }
}

/// <summary>
/// Represents one immutable snapshot of a process at a point in its lifecycle, per ADR 0012.
/// A new snapshot is produced for every state transition; no snapshot is ever mutated in place.
/// </summary>
public sealed record ProcessRecord
{
    /// <summary>Initializes a validated process snapshot.</summary>
    /// <param name="pid">This process's unique PID for the current boot.</param>
    /// <param name="parentPid">The parent process's PID, or <see langword="null"/> for a root process.</param>
    /// <param name="appId">Non-empty ID of the app this process hosts.</param>
    /// <param name="appInstanceId">Identifier of this specific running instance of the app.</param>
    /// <param name="kind">Lifecycle/hosting model of the hosted app.</param>
    /// <param name="userId">User the process runs as.</param>
    /// <param name="sessionId">Session the process belongs to.</param>
    /// <param name="state">Current lifecycle state.</param>
    /// <param name="resourceProfile">Resource profile snapshot used to derive simulated resource ticks.</param>
    /// <param name="serviceHealth">Self-assessed health, meaningful only when <paramref name="kind"/> is <see cref="AppKind.Service"/>.</param>
    /// <param name="createdAtUtc">UTC simulation time this process record was created.</param>
    /// <param name="startedAtUtc">UTC simulation time the process reached <see cref="ProcessState.Running"/>, if it has.</param>
    /// <param name="stoppedAtUtc">UTC simulation time the process reached a terminal state, if it has.</param>
    /// <param name="exitCode">App-reported exit code, if the process reported one.</param>
    /// <param name="exitReason">Reason the process reached a terminal state, if it has.</param>
    /// <exception cref="ArgumentException">A required field is empty or the timestamps/terminal fields are inconsistent with <paramref name="state"/>.</exception>
    public ProcessRecord(
        ProcessId pid,
        ProcessId? parentPid,
        string appId,
        AppInstanceId appInstanceId,
        AppKind kind,
        LocalUserId userId,
        SessionId sessionId,
        ProcessState state,
        ResourceProfile resourceProfile,
        ServiceHealth? serviceHealth,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? stoppedAtUtc,
        int? exitCode,
        ProcessExitReason? exitReason)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("An app ID is required.", nameof(appId));
        }

        if (parentPid == pid)
        {
            throw new ArgumentException("A process cannot be its own parent.", nameof(parentPid));
        }

        bool isTerminal = state is ProcessState.Stopped or ProcessState.Faulted;
        if (isTerminal)
        {
            if (stoppedAtUtc is null)
            {
                throw new ArgumentException($"A {state} process requires a stop timestamp.", nameof(stoppedAtUtc));
            }

            if (exitReason is null)
            {
                throw new ArgumentException($"A {state} process requires an exit reason.", nameof(exitReason));
            }
        }
        else if (stoppedAtUtc is not null || exitReason is not null || exitCode is not null)
        {
            throw new ArgumentException("Only a terminal process may carry stop timestamp, exit reason, or exit code.");
        }

        if (startedAtUtc is { } started && started < createdAtUtc)
        {
            throw new ArgumentException("Start time cannot precede creation.", nameof(startedAtUtc));
        }

        if (stoppedAtUtc is { } stopped && stopped < createdAtUtc)
        {
            throw new ArgumentException("Stop time cannot precede creation.", nameof(stoppedAtUtc));
        }

        if (state is ProcessState.Running or ProcessState.Stopping && startedAtUtc is null)
        {
            throw new ArgumentException($"A {state} process requires a start timestamp.", nameof(startedAtUtc));
        }

        Pid = pid;
        ParentPid = parentPid;
        AppId = appId;
        AppInstanceId = appInstanceId;
        Kind = kind;
        UserId = userId;
        SessionId = sessionId;
        State = state;
        ResourceProfile = resourceProfile;
        ServiceHealth = serviceHealth;
        CreatedAtUtc = createdAtUtc;
        StartedAtUtc = startedAtUtc;
        StoppedAtUtc = stoppedAtUtc;
        ExitCode = exitCode;
        ExitReason = exitReason;
    }

    /// <summary>Gets this process's unique PID for the current boot.</summary>
    public ProcessId Pid { get; }

    /// <summary>Gets the parent process's PID, or <see langword="null"/> for a root process.</summary>
    public ProcessId? ParentPid { get; }

    /// <summary>Gets the ID of the app this process hosts.</summary>
    public string AppId { get; }

    /// <summary>Gets the identifier of this specific running instance of the app.</summary>
    public AppInstanceId AppInstanceId { get; }

    /// <summary>Gets the lifecycle/hosting model of the hosted app.</summary>
    public AppKind Kind { get; }

    /// <summary>Gets the user the process runs as.</summary>
    public LocalUserId UserId { get; }

    /// <summary>Gets the session the process belongs to.</summary>
    public SessionId SessionId { get; }

    /// <summary>Gets the current lifecycle state.</summary>
    public ProcessState State { get; }

    /// <summary>Gets the resource profile snapshot used to derive simulated resource ticks.</summary>
    public ResourceProfile ResourceProfile { get; }

    /// <summary>Gets the self-assessed health, meaningful only for <see cref="AppKind.Service"/> apps.</summary>
    public ServiceHealth? ServiceHealth { get; }

    /// <summary>Gets the UTC simulation time this process record was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets the UTC simulation time the process reached <see cref="ProcessState.Running"/>, if it has.</summary>
    public DateTimeOffset? StartedAtUtc { get; }

    /// <summary>Gets the UTC simulation time the process reached a terminal state, if it has.</summary>
    public DateTimeOffset? StoppedAtUtc { get; }

    /// <summary>Gets the app-reported exit code, if the process reported one.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets the reason the process reached a terminal state, if it has.</summary>
    public ProcessExitReason? ExitReason { get; }

    /// <summary>Gets whether this process has reached a terminal state.</summary>
    public bool IsTerminal => State is ProcessState.Stopped or ProcessState.Faulted;
}
