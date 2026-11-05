using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Simulation.Abstractions.Gateways;

/// <summary>
/// High-level activation state of one <see cref="AppKind.Service"/> app (ADR 0097, section 1), with
/// the values of systemd's <c>ActiveState</c>.
/// </summary>
public enum ServiceActiveState
{
    /// <summary>No process runs the service and its last run did not fail (<c>inactive</c>).</summary>
    Inactive = 0,

    /// <summary>The process exists and the service's start code has not returned yet (<c>activating</c>).</summary>
    Activating = 1,

    /// <summary>The service started and runs (<c>active</c>).</summary>
    Active = 2,

    /// <summary>The service was asked to stop and is finishing (<c>deactivating</c>).</summary>
    Deactivating = 3,

    /// <summary>
    /// The last run ended without being asked to (<c>failed</c>); kept until the next start or
    /// <c>systemctl reset-failed</c>.
    /// </summary>
    Failed = 4
}

/// <summary>
/// Why the last run of a service ended (ADR 0097, section 1), with the words of systemd's
/// <c>Result</c> property.
/// </summary>
public enum ServiceRunResult
{
    /// <summary>The service runs, or its last run ended as asked (<c>success</c>).</summary>
    Success = 0,

    /// <summary>The process exited on its own with a non-zero code (<c>exit-code</c>).</summary>
    ExitCode = 1,

    /// <summary>An unhandled exception ended the process (<c>exception</c>; systemd says <c>core-dump</c>).</summary>
    Exception = 2,

    /// <summary>The process was killed (<c>kill -9</c>, "Force kill"; systemd says <c>signal</c>).</summary>
    Killed = 3,

    /// <summary>
    /// A deadline passed: the start took longer than the start timeout, or a stop request was
    /// not honored within its grace period (<c>timeout</c>).
    /// </summary>
    Timeout = 4
}

/// <summary>
/// One snapshot of a service's state for <c>systemctl status</c> and the task manager (ADR 0097,
/// sections 1 and 2).
/// </summary>
/// <param name="ActiveState">Activation state.</param>
/// <param name="Result">Why the last run ended; <see cref="ServiceRunResult.Success"/> while it runs.</param>
/// <param name="Health">Health reported by the service (<c>Unknown</c> until it started).</param>
/// <param name="StatusText">Free text the service gave with its health (<c>sd_notify STATUS=</c>), if any.</param>
/// <param name="StateChangedAtUtc">Simulation time of the last state change (<c>since …</c>), if the service ever ran.</param>
/// <param name="MainPid">PID of the running service process, if any.</param>
/// <param name="ExitCode">Exit code of the last run, when the process reported one.</param>
public sealed record ServiceStatusSnapshot(
    ServiceActiveState ActiveState,
    ServiceRunResult Result,
    ServiceHealth Health,
    string? StatusText,
    DateTimeOffset? StateChangedAtUtc,
    long? MainPid,
    int? ExitCode)
{
    /// <summary>Gets the state of a service that never ran during this boot.</summary>
    public static ServiceStatusSnapshot NeverStarted { get; } =
        new(ServiceActiveState.Inactive, ServiceRunResult.Success, ServiceHealth.Unknown, null, null, null, null);
}

/// <summary>
/// One message from a running service to the OS (ADR 0097, section 2), like <c>sd_notify</c>:
/// <c>READY=1</c> and <c>STATUS=…</c>.
/// </summary>
/// <param name="Ready">
/// <see langword="true"/> when the service's start code returned: the service moves from
/// <c>activating</c> to <c>active</c>.
/// </param>
/// <param name="Health">The health the service reports, or <see langword="null"/> to keep the current one.</param>
/// <param name="StatusText">The status text, or <see langword="null"/> to keep the current one; empty clears it.</param>
public sealed record ServiceNotification(bool Ready = false, ServiceHealth? Health = null, string? StatusText = null);

/// <summary>
/// Lets a running service tell the OS how it is doing (ADR 0097, section 2). The OS only accepts
/// the messages of the service process itself: any other process's notifications are ignored, as
/// systemd drops <c>sd_notify</c> from a process that is not the unit's main PID.
/// </summary>
public interface IAppServiceNotifyGateway
{
    /// <summary>Sends one notification; never throws.</summary>
    /// <param name="notification">What changed.</param>
    void Notify(ServiceNotification notification);
}

/// <summary>Notification gateway of a context that is not a service: every message is dropped.</summary>
public sealed class NullAppServiceNotifyGateway : IAppServiceNotifyGateway
{
    /// <summary>Gets the shared instance.</summary>
    public static NullAppServiceNotifyGateway Instance { get; } = new();

    private NullAppServiceNotifyGateway()
    {
    }

    /// <inheritdoc />
    public void Notify(ServiceNotification notification)
    {
    }
}
