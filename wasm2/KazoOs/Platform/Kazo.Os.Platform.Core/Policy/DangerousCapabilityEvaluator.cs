using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>State of one declared dangerous capability for one app and one user (ADR 0049 section 7).</summary>
public enum DangerousCapabilityState
{
    /// <summary>The app is a system app (ADR 0048): granted without approval.</summary>
    System = 0,

    /// <summary>A machine <c>allow</c> exists, the policy is <c>prompt</c> and the user did not refuse it.</summary>
    Allowed = 1,

    /// <summary>The session user refused it in <c>~/.config/kazoos/app-permissions.config</c>.</summary>
    DeniedByUser = 2,

    /// <summary>An administrator refused it machine-wide in <c>/etc/kazoos/app-permissions.config</c>.</summary>
    DeniedByAdministrator = 3,

    /// <summary><c>dangerousCapabilities=block</c> in <c>/etc/kazoos/security.config</c>.</summary>
    BlockedByPolicy = 4,

    /// <summary>No machine decision and no refusal by the user: the launch gate asks.</summary>
    Pending = 5
}

/// <summary>One declared dangerous capability and its current state.</summary>
/// <param name="Description">Title and risk sentence from <see cref="AppCapabilities.Describe"/>.</param>
/// <param name="State">Current state.</param>
public sealed record DangerousCapabilityStatus(CapabilityDescription Description, DangerousCapabilityState State)
{
    /// <summary>Gets the capability identifier.</summary>
    public string Capability => Description.Capability;

    /// <summary>Gets whether the app receives the capability at its next launch.</summary>
    public bool IsGranted => State is DangerousCapabilityState.System or DangerousCapabilityState.Allowed;
}

/// <summary>
/// Pure decision table of ADR 0049 sections 2 to 4: which declared dangerous capabilities of an
/// app are granted, refused or still pending.
/// </summary>
/// <remarks>
/// For a non-system app the first matching rule wins:
/// <list type="number">
/// <item>policy <c>block</c> → <see cref="DangerousCapabilityState.BlockedByPolicy"/> (existing <c>allow</c> lines are ignored);</item>
/// <item>machine <c>deny</c> → <see cref="DangerousCapabilityState.DeniedByAdministrator"/>;</item>
/// <item>user <c>deny</c> → <see cref="DangerousCapabilityState.DeniedByUser"/> (it beats a machine <c>allow</c>);</item>
/// <item>machine <c>allow</c> → <see cref="DangerousCapabilityState.Allowed"/>;</item>
/// <item>otherwise <see cref="DangerousCapabilityState.Pending"/>.</item>
/// </list>
/// A system app gets <see cref="DangerousCapabilityState.System"/> for every dangerous capability:
/// decision lines naming it are ignored without effect.
/// </remarks>
public static class DangerousCapabilityEvaluator
{
    /// <summary>Evaluates every dangerous capability <paramref name="manifest"/> declares, in manifest order.</summary>
    /// <param name="manifest">App manifest.</param>
    /// <param name="isSystemApp">Whether the app is a system app of the host (<see cref="AppCatalog.IsSystemApp"/>).</param>
    /// <param name="policy">Effective <c>dangerousCapabilities</c> policy.</param>
    /// <param name="decisions">Parsed machine and user decisions.</param>
    public static IReadOnlyList<DangerousCapabilityStatus> Evaluate(
        AppManifest manifest,
        bool isSystemApp,
        DangerousCapabilityPolicy policy,
        AppPermissionDecisions decisions)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(decisions);

        List<DangerousCapabilityStatus> statuses = [];
        foreach (string capability in manifest.Capabilities.Distinct(StringComparer.Ordinal))
        {
            if (!AppCapabilities.IsDangerous(capability))
            {
                continue;
            }

            statuses.Add(new DangerousCapabilityStatus(
                AppCapabilities.Describe(capability),
                isSystemApp ? DangerousCapabilityState.System : EvaluateOne(manifest.Id, capability, policy, decisions)));
        }

        return statuses;
    }

    /// <summary>Gets whether <paramref name="manifest"/> declares at least one dangerous capability.</summary>
    /// <param name="manifest">App manifest.</param>
    public static bool DeclaresDangerousCapabilities(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Capabilities.Any(AppCapabilities.IsDangerous);
    }

    private static DangerousCapabilityState EvaluateOne(
        string appId, string capability, DangerousCapabilityPolicy policy, AppPermissionDecisions decisions)
    {
        if (policy == DangerousCapabilityPolicy.Block)
        {
            return DangerousCapabilityState.BlockedByPolicy;
        }

        AppPermissionDecision? machine = decisions.GetMachineDecision(appId, capability);
        if (machine == AppPermissionDecision.Deny)
        {
            return DangerousCapabilityState.DeniedByAdministrator;
        }

        if (decisions.IsDeniedByUser(appId, capability))
        {
            return DangerousCapabilityState.DeniedByUser;
        }

        return machine == AppPermissionDecision.Allow
            ? DangerousCapabilityState.Allowed
            : DangerousCapabilityState.Pending;
    }
}
