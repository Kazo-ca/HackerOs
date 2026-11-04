using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>One app and the state of each dangerous capability it declares.</summary>
/// <param name="Manifest">App manifest.</param>
/// <param name="IsSystemApp">Whether the app is a system app (ADR 0048).</param>
/// <param name="Capabilities">State of each declared dangerous capability.</param>
public sealed record AppPermissionOverview(
    AppManifest Manifest,
    bool IsSystemApp,
    IReadOnlyList<DangerousCapabilityStatus> Capabilities);

/// <summary>Result of an administrator action of the Settings permissions page.</summary>
/// <param name="Succeeded">Whether the administrator was verified and the file written.</param>
/// <param name="FailureReason">Stable failure code from <see cref="IAdminElevationVerifier"/>, else <see langword="null"/>.</param>
public sealed record AppPermissionAdministrationResult(bool Succeeded, string? FailureReason = null);

/// <summary>
/// Backs Settings &gt; Applications &gt; Permissions (ADR 0049 section 7): lists every app that
/// declares a dangerous capability with its state, and carries out the changes.
/// </summary>
/// <remarks>
/// A machine change (allow, deny or reset in <c>/etc/hackeros/app-permissions.config</c>, or the
/// <c>dangerousCapabilities</c> policy) first verifies an enabled administrator's credentials, as the
/// approval prompt of ADR 0042 does, then writes as an audited system operation and logs to
/// <c>/var/log/auth.log</c>. Withdrawing one's own <c>deny</c> needs no administrator. Every change
/// takes effect at the app's next launch.
/// </remarks>
public sealed class AppPermissionAdministration
{
    private readonly AppCatalog _catalog;
    private readonly IAppPermissionStore _permissions;
    private readonly IOsSettingsService _settings;
    private readonly IAdminElevationVerifier _verifier;
    private readonly IAuditLog _auditLog;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="catalog">App catalog.</param>
    /// <param name="permissions">Remembered decisions.</param>
    /// <param name="settings">OS settings files.</param>
    /// <param name="verifier">Administrator credential verifier (ADR 0042).</param>
    /// <param name="auditLog">Audit log writing <c>/var/log/auth.log</c>.</param>
    /// <param name="clock">Clock for audit timestamps.</param>
    public AppPermissionAdministration(
        AppCatalog catalog,
        IAppPermissionStore permissions,
        IOsSettingsService settings,
        IAdminElevationVerifier verifier,
        IAuditLog auditLog,
        Func<DateTimeOffset>? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Reads the effective <c>dangerousCapabilities</c> policy.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public ValueTask<DangerousCapabilityPolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        SecuritySettingsDocuments.ReadPolicyAsync(_settings, cancellationToken);

    /// <summary>Lists every catalog app declaring a dangerous capability, non-system apps first, then by name.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task<IReadOnlyList<AppPermissionOverview>> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        DangerousCapabilityPolicy policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        AppPermissionDecisions decisions = await _permissions.ReadAsync(cancellationToken).ConfigureAwait(false);
        return [.. _catalog.Manifests.Values
            .Where(DangerousCapabilityEvaluator.DeclaresDangerousCapabilities)
            .Select(manifest =>
            {
                bool isSystemApp = _catalog.IsSystemApp(manifest.Id);
                return new AppPermissionOverview(
                    manifest,
                    isSystemApp,
                    DangerousCapabilityEvaluator.Evaluate(manifest, isSystemApp, policy, decisions));
            })
            .OrderBy(overview => overview.IsSystemApp)
            .ThenBy(overview => overview.Manifest.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Records a machine decision for one capability of one non-system app, after verifying an
    /// administrator: <c>allow</c>, <c>deny</c>, or <see langword="null"/> to remove the line (the
    /// capability becomes pending again).
    /// </summary>
    /// <param name="appId">Non-system app identifier.</param>
    /// <param name="capability">Dangerous capability it declares.</param>
    /// <param name="decision">Decision, or <see langword="null"/> to reset.</param>
    /// <param name="adminLogin">Administrator login.</param>
    /// <param name="password">Administrator password; never logged.</param>
    /// <param name="requestedBy">Session user acting in Settings.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <exception cref="ArgumentException">The app is unknown or a system app, or the capability is not a declared dangerous one.</exception>
    public async Task<AppPermissionAdministrationResult> SetMachineDecisionAsync(
        string appId,
        string capability,
        AppPermissionDecision? decision,
        string adminLogin,
        string password,
        AuthenticatedPrincipal requestedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);
        RequireDeclaredDangerous(appId, capability);

        AdminElevationVerificationResult verification = await _verifier
            .VerifyAsync(adminLogin ?? string.Empty, password ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        string admin = verification.LoginName ?? (string.IsNullOrWhiteSpace(adminLogin) ? "-" : adminLogin);
        AuditOutcome outcome = verification.Success ? AuditOutcome.Success : AuditOutcome.Denied;

        if (verification.Success)
        {
            await _permissions
                .WriteMachineDecisionsAsync(appId, [capability], decision, cancellationToken)
                .ConfigureAwait(false);
        }

        RecordMachineDecision(appId, capability, decision, admin, requestedBy, outcome, verification.FailureReason);
        return new AppPermissionAdministrationResult(verification.Success, verification.FailureReason);
    }

    /// <summary>
    /// Records a machine decision once an administrator already authenticated through the OS
    /// authentication agent (ADR 0055 section 5, <c>HackPermissionAdministration</c>): writes
    /// <c>/etc/hackeros/app-permissions.config</c> as an audited system operation and logs the
    /// change to <c>/var/log/auth.log</c>. The caller is responsible for the authentication.
    /// </summary>
    /// <param name="appId">Non-system app identifier.</param>
    /// <param name="capability">Dangerous capability it declares.</param>
    /// <param name="decision">Decision, or <see langword="null"/> to reset.</param>
    /// <param name="administratorLogin">Administrator the agent authenticated (<c>root</c> under <c>sudo</c>).</param>
    /// <param name="requestedBy">Session user acting in the calling app.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ArgumentException">The app is unknown or a system app, or the capability is not a declared dangerous one.</exception>
    public async Task ApplyMachineDecisionAsync(
        string appId,
        string capability,
        AppPermissionDecision? decision,
        string administratorLogin,
        AuthenticatedPrincipal requestedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorLogin);
        RequireDeclaredDangerous(appId, capability);
        await _permissions.WriteMachineDecisionsAsync(appId, [capability], decision, cancellationToken).ConfigureAwait(false);
        RecordMachineDecision(appId, capability, decision, administratorLogin, requestedBy, AuditOutcome.Success, failureReason: null);
    }

    /// <summary>Validates a machine decision without writing anything, so a caller can refuse before asking for a password.</summary>
    /// <param name="appId">Non-system app identifier.</param>
    /// <param name="capability">Dangerous capability it declares.</param>
    /// <exception cref="ArgumentException">The app is unknown or a system app, or the capability is not a declared dangerous one.</exception>
    public void ValidateMachineDecision(string appId, string capability) => RequireDeclaredDangerous(appId, capability);

    /// <summary>Withdraws the session user's own refusal of one capability (no administrator needed).</summary>
    /// <param name="appId">App identifier.</param>
    /// <param name="capability">Capability identifier.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public ValueTask RemoveOwnDenialAsync(string appId, string capability, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return _permissions.RemoveUserDenialAsync(appId, capability, cancellationToken);
    }

    /// <summary>Changes <c>dangerousCapabilities</c> in <c>/etc/hackeros/security.config</c> after verifying an administrator.</summary>
    /// <param name="policy">New policy.</param>
    /// <param name="adminLogin">Administrator login.</param>
    /// <param name="password">Administrator password; never logged.</param>
    /// <param name="requestedBy">Session user acting in Settings.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<AppPermissionAdministrationResult> SetPolicyAsync(
        DangerousCapabilityPolicy policy,
        string adminLogin,
        string password,
        AuthenticatedPrincipal requestedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);
        AdminElevationVerificationResult verification = await _verifier
            .VerifyAsync(adminLogin ?? string.Empty, password ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        if (verification.Success)
        {
            await WritePolicyAsync(policy, cancellationToken).ConfigureAwait(false);
        }

        RecordPolicy(policy, verification.LoginName ?? (string.IsNullOrWhiteSpace(adminLogin) ? "-" : adminLogin), requestedBy,
            verification.Success ? AuditOutcome.Success : AuditOutcome.Denied);
        return new AppPermissionAdministrationResult(verification.Success, verification.FailureReason);
    }

    /// <summary>
    /// Changes the policy once an administrator already authenticated through the OS authentication
    /// agent (ADR 0055 section 5): writes <c>/etc/hackeros/security.config</c> as an audited system
    /// operation and logs the change. The caller is responsible for the authentication.
    /// </summary>
    /// <param name="policy">New policy.</param>
    /// <param name="administratorLogin">Administrator the agent authenticated.</param>
    /// <param name="requestedBy">Session user acting in the calling app.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task ApplyPolicyAsync(
        DangerousCapabilityPolicy policy,
        string administratorLogin,
        AuthenticatedPrincipal requestedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorLogin);
        await WritePolicyAsync(policy, cancellationToken).ConfigureAwait(false);
        RecordPolicy(policy, administratorLogin, requestedBy, AuditOutcome.Success);
    }

    private async Task WritePolicyAsync(DangerousCapabilityPolicy policy, CancellationToken cancellationToken)
    {
        OsSettingsFile file = new(OsSettingsLocation.System, $"{SecuritySettingsDocuments.DocumentId}.config");
        string? content = await _settings.ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
        string edited = ConfigDocumentEditor.SetValue(
            content ?? SecuritySettingsDocuments.DefaultContent,
            group: null,
            SecuritySettingsDocuments.DangerousCapabilitiesKey,
            SecuritySettingsDocuments.ToValue(policy));

        // writer: null on a System file = audited system operation, the administrator being verified.
        await _settings.WriteFileAsync(file, edited, writer: null, cancellationToken).ConfigureAwait(false);
    }

    private void RecordPolicy(DangerousCapabilityPolicy policy, string admin, AuthenticatedPrincipal requestedBy, AuditOutcome outcome) =>
        Record(requestedBy, "capability.policy", SecuritySettingsDocuments.Document.SystemPath.Value, outcome, new()
        {
            ["dangerousCapabilities"] = SecuritySettingsDocuments.ToValue(policy),
            ["admin"] = admin
        });

    private void RecordMachineDecision(
        string appId,
        string capability,
        AppPermissionDecision? decision,
        string admin,
        AuthenticatedPrincipal requestedBy,
        AuditOutcome outcome,
        string? failureReason)
    {
        if (decision == AppPermissionDecision.Allow)
        {
            Record(requestedBy, "capability.approve", appId, outcome, new()
            {
                ["app"] = appId,
                ["capabilities"] = capability,
                ["requestedBy"] = requestedBy.LoginName.Value,
                ["admin"] = admin,
                ["outcome"] = outcome == AuditOutcome.Success ? "success" : $"failure:{failureReason ?? "unknown"}"
            });
        }
        else
        {
            Record(requestedBy, "capability.revoke", appId, outcome, new()
            {
                ["app"] = appId,
                ["capability"] = capability,
                ["admin"] = admin,
                ["decision"] = decision is null ? "reset" : AppPermissionsDocument.DenyValue
            });
        }
    }

    private void RequireDeclaredDangerous(string appId, string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        if (!_catalog.Manifests.TryGetValue(appId, out AppManifest? manifest) || _catalog.IsSystemApp(appId))
        {
            throw new ArgumentException($"'{appId}' is not a non-system catalog app.", nameof(appId));
        }

        if (!AppCapabilities.IsDangerous(capability) || !manifest.Capabilities.Contains(capability, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{appId}' does not declare the dangerous capability '{capability}'.", nameof(capability));
        }
    }

    private void Record(
        AuthenticatedPrincipal actor, string action, string subject, AuditOutcome outcome, Dictionary<string, string> properties) =>
        _auditLog.Record(new AuditEntry(_clock(), Guid.NewGuid(), actor.UserId.ToString(), action, subject, outcome, properties));
}
