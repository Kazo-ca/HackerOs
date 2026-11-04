namespace Kazo.Os.AppSdk.Administration;

/// <summary>
/// What the OS does when a non-system app declares a dangerous capability: the
/// <c>dangerousCapabilities</c> key of <c>/etc/hackeros/security.config</c> (ADR 0049, section 3).
/// </summary>
public enum HackDangerousCapabilityPolicy
{
    /// <summary><c>prompt</c> (default): an administrator approves pending capabilities at launch.</summary>
    Prompt = 0,

    /// <summary><c>block</c>: dangerous capabilities are never granted to non-system apps.</summary>
    Block = 1
}

/// <summary>A machine decision about one dangerous capability of one app (ADR 0049, section 4).</summary>
public enum HackPermissionDecision
{
    /// <summary>Write <c>allow</c> in <c>/etc/hackeros/app-permissions.config</c>.</summary>
    Allow = 0,

    /// <summary>Write <c>deny</c> in <c>/etc/hackeros/app-permissions.config</c>.</summary>
    Deny = 1,

    /// <summary>Remove the machine line: the capability is pending again and the launch asks.</summary>
    Ask = 2
}

/// <summary>State of one declared dangerous capability for the session user (ADR 0049, section 7).</summary>
public enum HackPermissionState
{
    /// <summary>The app is a system app (ADR 0048): granted without approval.</summary>
    System = 0,

    /// <summary>An administrator allowed it for the machine and the user did not refuse it.</summary>
    Allowed = 1,

    /// <summary>The session user refused it in <c>~/.config/hackeros/app-permissions.config</c>.</summary>
    DeniedByUser = 2,

    /// <summary>An administrator refused it for the machine.</summary>
    DeniedByAdministrator = 3,

    /// <summary>The policy is <c>block</c>.</summary>
    BlockedByPolicy = 4,

    /// <summary>No decision yet: the next launch asks an administrator.</summary>
    Pending = 5
}

/// <summary>One dangerous capability an app declares, with its state.</summary>
/// <param name="Capability">Exact capability identifier, such as <c>apps.manage</c>.</param>
/// <param name="Title">Short title shown to the user.</param>
/// <param name="RiskDescription">Sentence explaining the risk to an administrator.</param>
/// <param name="State">Current state for the session user.</param>
public sealed record HackCapabilityPermissionInfo(
    string Capability,
    string Title,
    string RiskDescription,
    HackPermissionState State)
{
    /// <summary>Gets whether the app receives the capability at its next launch.</summary>
    public bool IsGranted => State is HackPermissionState.System or HackPermissionState.Allowed;
}

/// <summary>One catalog app that declares at least one dangerous capability.</summary>
/// <param name="AppId">App identifier.</param>
/// <param name="AppName">Display name.</param>
/// <param name="IsSystemApp">Whether the app is a system app (its capabilities are always granted).</param>
/// <param name="Capabilities">Each declared dangerous capability with its state.</param>
public sealed record HackAppPermissionInfo(
    string AppId,
    string AppName,
    bool IsSystemApp,
    IReadOnlyList<HackCapabilityPermissionInfo> Capabilities);

/// <summary>
/// Administration of dangerous capabilities for one app instance (ADR 0056, section 9), behind
/// <see cref="HackPermissionAdministration"/>. Every member requires the dangerous capability
/// <c>permissions.manage</c>; machine changes also require an administrator through the OS
/// authentication agent, unless the process already runs as root.
/// </summary>
public interface IAppPermissionAdministrationGateway
{
    /// <summary>Reads the effective policy of <c>/etc/hackeros/security.config</c>.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<HackDangerousCapabilityPolicy> GetPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists every catalog app declaring a dangerous capability, non-system apps first, then by name.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    ValueTask<IReadOnlyList<HackAppPermissionInfo>> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the policy after an administrator authenticated.</summary>
    /// <param name="policy">New policy.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    ValueTask SetPolicyAsync(HackDangerousCapabilityPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>Records a machine decision for one capability of one non-system app, after an administrator authenticated.</summary>
    /// <param name="appId">Non-system app identifier.</param>
    /// <param name="capability">Dangerous capability the app declares.</param>
    /// <param name="decision">Decision to record.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    ValueTask SetMachineDecisionAsync(
        string appId, string capability, HackPermissionDecision decision, CancellationToken cancellationToken = default);

    /// <summary>Withdraws the session user's own refusal of one capability; no administrator is needed.</summary>
    /// <param name="appId">App identifier.</param>
    /// <param name="capability">Capability identifier.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask RemoveOwnDenialAsync(string appId, string capability, CancellationToken cancellationToken = default);
}
