using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Administration;

/// <summary>
/// Trusted <see cref="IAppPermissionAdministrationGateway"/> (ADR 0056, section 9) over
/// <see cref="AppPermissionAdministration"/>.
/// </summary>
/// <remarks>
/// Every call requires <c>permissions.manage</c>. A machine change is validated first (a system app
/// or an undeclared capability is refused before any password is asked), then the OS
/// authentication agent asks an administrator; once one authenticated, the OS writes the
/// <c>/etc/kazoos</c> file itself as an audited system operation, as <c>pkexec</c> runs the helper
/// as root. The app never sees the password and its own rights are never widened.
/// </remarks>
internal sealed class AppPermissionAdministrationGateway : IAppPermissionAdministrationGateway
{
    private readonly Func<AppPermissionAdministration> _administration;
    private readonly ICapabilityChecker _capabilities;
    private readonly AdministratorGate _gate;

    /// <summary>Creates the gateway for one app instance.</summary>
    public AppPermissionAdministrationGateway(
        Func<AppPermissionAdministration> administration, ICapabilityChecker capabilities, AdministratorGate gate)
    {
        _administration = administration ?? throw new ArgumentNullException(nameof(administration));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <inheritdoc />
    public async ValueTask<KazoDangerousCapabilityPolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.PermissionsManage);
        return ToPublic(await _administration().GetPolicyAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoAppPermissionInfo>> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.PermissionsManage);
        IReadOnlyList<AppPermissionOverview> overview = await _administration().GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        // The Settings app shows these texts: they follow its process's language (ADR 0104).
        System.Globalization.CultureInfo language = _gate.Language;
        return [.. overview.Select(app => new KazoAppPermissionInfo(
            app.Manifest.Id,
            app.Manifest.Name,
            app.IsSystemApp,
            [.. app.Capabilities.Select(status => Describe(status, language))]))];

        static KazoCapabilityPermissionInfo Describe(DangerousCapabilityStatus status, System.Globalization.CultureInfo language)
        {
            CapabilityDescription description = CapabilityText.Localize(status.Description, language);
            return new KazoCapabilityPermissionInfo(
                status.Capability,
                description.Title,
                description.RiskDescription ?? string.Empty,
                ToPublic(status.State));
        }
    }

    /// <inheritdoc />
    public async ValueTask SetPolicyAsync(KazoDangerousCapabilityPolicy policy, CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.PermissionsManage);
        string admin = await _gate.AuthenticateAsync(
            policy == KazoDangerousCapabilityPolicy.Block
                ? _gate.Text["Admin.Policy.Block"]
                : _gate.Text["Admin.Policy.Prompt"],
            "permissions.policy",
            SecuritySettingsDocuments.Document.SystemPath.Value,
            cancellationToken).ConfigureAwait(false);
        await _administration()
            .ApplyPolicyAsync(ToInternal(policy), admin, _gate.Principal, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SetMachineDecisionAsync(
        string appId, string capability, KazoPermissionDecision decision, CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.PermissionsManage);
        AppPermissionAdministration administration = _administration();
        administration.ValidateMachineDecision(appId, capability);
        string textKey = decision switch
        {
            KazoPermissionDecision.Allow => "Admin.Decision.Allow",
            KazoPermissionDecision.Deny => "Admin.Decision.Deny",
            _ => "Admin.Decision.Ask"
        };
        string admin = await _gate.AuthenticateAsync(
            _gate.Text[textKey, CapabilityText.Describe(capability, _gate.Language).Title, capability, appId],
            "permissions.machine-decision",
            AppPermissionsDocument.MachineFile.Resolve(null).Value,
            cancellationToken).ConfigureAwait(false);
        await administration
            .ApplyMachineDecisionAsync(appId, capability, ToInternal(decision), admin, _gate.Principal, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RemoveOwnDenialAsync(string appId, string capability, CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.PermissionsManage);
        await _administration().RemoveOwnDenialAsync(appId, capability, cancellationToken).ConfigureAwait(false);
    }

    private static KazoDangerousCapabilityPolicy ToPublic(DangerousCapabilityPolicy policy) =>
        policy == DangerousCapabilityPolicy.Block ? KazoDangerousCapabilityPolicy.Block : KazoDangerousCapabilityPolicy.Prompt;

    private static DangerousCapabilityPolicy ToInternal(KazoDangerousCapabilityPolicy policy) =>
        policy == KazoDangerousCapabilityPolicy.Block ? DangerousCapabilityPolicy.Block : DangerousCapabilityPolicy.Prompt;

    private static AppPermissionDecision? ToInternal(KazoPermissionDecision decision) => decision switch
    {
        KazoPermissionDecision.Allow => AppPermissionDecision.Allow,
        KazoPermissionDecision.Deny => AppPermissionDecision.Deny,
        KazoPermissionDecision.Ask => null,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown permission decision.")
    };

    private static KazoPermissionState ToPublic(DangerousCapabilityState state) => state switch
    {
        DangerousCapabilityState.System => KazoPermissionState.System,
        DangerousCapabilityState.Allowed => KazoPermissionState.Allowed,
        DangerousCapabilityState.DeniedByUser => KazoPermissionState.DeniedByUser,
        DangerousCapabilityState.DeniedByAdministrator => KazoPermissionState.DeniedByAdministrator,
        DangerousCapabilityState.BlockedByPolicy => KazoPermissionState.BlockedByPolicy,
        _ => KazoPermissionState.Pending
    };
}
