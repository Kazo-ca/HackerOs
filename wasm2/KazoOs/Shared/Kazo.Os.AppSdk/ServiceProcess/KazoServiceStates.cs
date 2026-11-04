using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.AppSdk.ServiceProcess;

/// <summary>
/// Activation state of a <see cref="KazoServiceController"/> (ADR 0097, section 1), like
/// systemd's <c>ActiveState</c>.
/// </summary>
public enum KazoServiceActiveState
{
    /// <summary>Not running, and the last run did not fail (<c>inactive</c>).</summary>
    Inactive = 0,

    /// <summary>The process exists; its start code has not returned yet (<c>activating</c>).</summary>
    Activating = 1,

    /// <summary>Started and running (<c>active</c>).</summary>
    Active = 2,

    /// <summary>Asked to stop and finishing (<c>deactivating</c>).</summary>
    Deactivating = 3,

    /// <summary>The last run ended without being asked to (<c>failed</c>), until the next start or a reset.</summary>
    Failed = 4
}

/// <summary>Why the last run of a service ended (ADR 0097, section 1), like systemd's <c>Result</c>.</summary>
public enum KazoServiceResult
{
    /// <summary>Running, or ended as asked (<c>success</c>).</summary>
    Success = 0,

    /// <summary>Exited on its own with a non-zero code (<c>exit-code</c>).</summary>
    ExitCode = 1,

    /// <summary>An unhandled exception ended it (<c>exception</c>).</summary>
    Exception = 2,

    /// <summary>Killed (<c>kill -9</c>, "Force kill") (<c>killed</c>).</summary>
    Killed = 3,

    /// <summary>Its start, or its stop, took longer than allowed (<c>timeout</c>).</summary>
    Timeout = 4
}

/// <summary>Health a service reports about itself (ADR 0097, section 2).</summary>
public enum KazoServiceHealth
{
    /// <summary>Not reported yet: the service is not running or has not finished starting.</summary>
    Unknown = 0,

    /// <summary>Working normally.</summary>
    Healthy = 1,

    /// <summary>Running with reduced capability (a dependency is unreachable, a cache is cold…).</summary>
    Degraded = 2,

    /// <summary>Running but not doing its job.</summary>
    Unhealthy = 3
}

/// <summary>Conversions between the SDK's service states and the OS contracts, plus their systemd words.</summary>
public static class KazoServiceStates
{
    /// <summary>Gets the systemd word of an activation state (<c>active</c>, <c>failed</c>…).</summary>
    /// <param name="state">The state.</param>
    public static string ToSystemdName(KazoServiceActiveState state) => state switch
    {
        KazoServiceActiveState.Activating => "activating",
        KazoServiceActiveState.Active => "active",
        KazoServiceActiveState.Deactivating => "deactivating",
        KazoServiceActiveState.Failed => "failed",
        _ => "inactive"
    };

    /// <summary>Gets the systemd word of a result (<c>exit-code</c>, <c>timeout</c>…).</summary>
    /// <param name="result">The result.</param>
    public static string ToSystemdName(KazoServiceResult result) => result switch
    {
        KazoServiceResult.ExitCode => "exit-code",
        KazoServiceResult.Exception => "exception",
        KazoServiceResult.Killed => "killed",
        KazoServiceResult.Timeout => "timeout",
        _ => "success"
    };

    internal static KazoServiceActiveState ToPublic(ServiceActiveState state) => (KazoServiceActiveState)(int)state;

    internal static KazoServiceResult ToPublic(ServiceRunResult result) => (KazoServiceResult)(int)result;

    internal static KazoServiceHealth ToPublic(ServiceHealth health) => health switch
    {
        ServiceHealth.Healthy => KazoServiceHealth.Healthy,
        ServiceHealth.Degraded => KazoServiceHealth.Degraded,
        ServiceHealth.Unhealthy => KazoServiceHealth.Unhealthy,
        _ => KazoServiceHealth.Unknown
    };

    internal static ServiceHealth ToInternal(KazoServiceHealth health) => health switch
    {
        KazoServiceHealth.Healthy => ServiceHealth.Healthy,
        KazoServiceHealth.Degraded => ServiceHealth.Degraded,
        KazoServiceHealth.Unhealthy => ServiceHealth.Unhealthy,
        _ => ServiceHealth.Unknown
    };
}
