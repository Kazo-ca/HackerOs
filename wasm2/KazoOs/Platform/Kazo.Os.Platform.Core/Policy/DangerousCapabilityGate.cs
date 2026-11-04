using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>
/// Default <see cref="IDangerousCapabilityGate"/> (ADR 0049 sections 2 to 5 and 8).
/// </summary>
/// <remarks>
/// <para>
/// The gate is the only code that grants a dangerous capability to a non-system app. At login,
/// <see cref="CleanProfileCapabilityGrantSeeder"/> withholds them; at each launch the gate reads
/// <c>security.config</c> and both <c>app-permissions.config</c> files, asks when needed, then grants
/// (source <see cref="CapabilityGrantSource.AdministratorApproval"/>) what is now allowed and revokes
/// what no longer is. A change in the files therefore takes effect at the app's next launch, while
/// a running instance keeps the capabilities frozen at its own launch (ADR 0042).
/// </para>
/// <para>
/// Credentials are verified by <see cref="IAdminElevationVerifier"/> inside
/// <see cref="DangerousCapabilityApprovalRequest.ApproveAsync"/>, so the prompt surface never decides
/// anything itself and cannot forge an approval. After the third failure further attempts are
/// refused without verification and nothing is written.
/// </para>
/// </remarks>
public sealed class DangerousCapabilityGate : IDangerousCapabilityGate
{
    private readonly AppCatalog _catalog;
    private readonly ICapabilityGrantRepository _grants;
    private readonly IAppPermissionStore _permissions;
    private readonly IOsSettingsService _settings;
    private readonly IAdminElevationVerifier _verifier;
    private readonly IAuditLog _auditLog;
    private readonly IDangerousCapabilityApprovalPrompt? _defaultPrompt;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates the gate.</summary>
    /// <param name="catalog">Catalog deciding which apps are system apps (ADR 0048).</param>
    /// <param name="grants">Capability grant repository aligned with the decisions at each launch.</param>
    /// <param name="permissions">Remembered decisions.</param>
    /// <param name="settings">OS settings, for <c>security.config</c>.</param>
    /// <param name="verifier">Administrator credential verifier (ADR 0042).</param>
    /// <param name="auditLog">Audit log writing <c>/var/log/auth.log</c> (ADR 0045).</param>
    /// <param name="defaultPrompt">Host prompt (the overlay), or <see langword="null"/> when the host has none.</param>
    /// <param name="clock">Clock for audit timestamps; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public DangerousCapabilityGate(
        AppCatalog catalog,
        ICapabilityGrantRepository grants,
        IAppPermissionStore permissions,
        IOsSettingsService settings,
        IAdminElevationVerifier verifier,
        IAuditLog auditLog,
        IDangerousCapabilityApprovalPrompt? defaultPrompt = null,
        Func<DateTimeOffset>? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _defaultPrompt = defaultPrompt;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<DangerousCapabilityGateResult> EnsureDecidedAsync(
        AppManifest manifest,
        AuthenticatedPrincipal principal,
        IDangerousCapabilityApprovalPrompt? prompt = null,
        bool interactive = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(principal);

        bool isSystemApp = _catalog.IsSystemApp(manifest.Id);
        if (!DangerousCapabilityEvaluator.DeclaresDangerousCapabilities(manifest))
        {
            return DangerousCapabilityGateResult.NotApplicable(isSystemApp);
        }

        DangerousCapabilityPolicy policy = await SecuritySettingsDocuments
            .ReadPolicyAsync(_settings, cancellationToken)
            .ConfigureAwait(false);
        if (isSystemApp)
        {
            // System apps get their dangerous capabilities from the login seeder (SystemPolicy).
            return new DangerousCapabilityGateResult(
                DangerousCapabilityEvaluator.Evaluate(manifest, true, policy, AppPermissionDecisions.Empty), policy, true);
        }

        AppPermissionDecisions decisions = await _permissions.ReadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DangerousCapabilityStatus> statuses =
            DangerousCapabilityEvaluator.Evaluate(manifest, false, policy, decisions);
        DangerousCapabilityApprovalOutcome? outcome = null;

        if (policy == DangerousCapabilityPolicy.Block)
        {
            // Nothing is asked: the app starts without its dangerous capabilities. A capability the
            // user or the administrator already refused needs no new entry.
            string[] blocked = [.. statuses
                .Where(status => decisions.GetMachineDecision(manifest.Id, status.Capability) != AppPermissionDecision.Deny
                    && !decisions.IsDeniedByUser(manifest.Id, status.Capability))
                .Select(status => status.Capability)];
            if (blocked.Length > 0)
            {
                Record(principal.UserId.ToString(), "capability.blocked", manifest.Id, AuditOutcome.Denied, new()
                {
                    ["app"] = manifest.Id,
                    ["capabilities"] = string.Join(",", blocked),
                    ["user"] = principal.LoginName.Value
                });
            }
        }
        else
        {
            string[] pending = [.. statuses
                .Where(status => status.State == DangerousCapabilityState.Pending)
                .Select(status => status.Capability)];
            if (pending.Length > 0)
            {
                outcome = await AskAsync(manifest, principal, pending, prompt ?? _defaultPrompt, interactive, cancellationToken)
                    .ConfigureAwait(false);
                if (outcome is DangerousCapabilityApprovalOutcome.Approved or DangerousCapabilityApprovalOutcome.Refused)
                {
                    decisions = await _permissions.ReadAsync(cancellationToken).ConfigureAwait(false);
                    statuses = DangerousCapabilityEvaluator.Evaluate(manifest, false, policy, decisions);
                }
            }
        }

        AlignGrants(manifest.Id, principal.UserId.ToString(), statuses);
        return new DangerousCapabilityGateResult(statuses, policy, false, outcome);
    }

    private async Task<DangerousCapabilityApprovalOutcome> AskAsync(
        AppManifest manifest,
        AuthenticatedPrincipal principal,
        IReadOnlyList<string> pending,
        IDangerousCapabilityApprovalPrompt? prompt,
        bool interactive,
        CancellationToken cancellationToken)
    {
        string capabilityList = string.Join(",", pending);
        if (!interactive || prompt is null)
        {
            RecordDeferred(manifest.Id, capabilityList);
            return DangerousCapabilityApprovalOutcome.Unavailable;
        }

        int failures = 0;
        bool approved = false;
        SemaphoreSlim attemptGate = new(1, 1);

        async Task<DangerousCapabilityApprovalAttempt> ApproveAsync(string login, string password, CancellationToken ct)
        {
            await attemptGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (approved)
                {
                    return new DangerousCapabilityApprovalAttempt(true, DangerousCapabilityApprovalRequest.MaxAttempts - failures);
                }

                if (failures >= DangerousCapabilityApprovalRequest.MaxAttempts)
                {
                    return new DangerousCapabilityApprovalAttempt(false, 0, "too-many-attempts");
                }

                AdminElevationVerificationResult verification = await _verifier
                    .VerifyAsync(login ?? string.Empty, password ?? string.Empty, ct)
                    .ConfigureAwait(false);
                if (!verification.Success)
                {
                    failures++;
                    RecordApprove(manifest.Id, capabilityList, principal, login ?? string.Empty, verification.FailureReason, AuditOutcome.Denied);
                    return new DangerousCapabilityApprovalAttempt(
                        false, DangerousCapabilityApprovalRequest.MaxAttempts - failures, verification.FailureReason);
                }

                // Audited system context (ADR 0002): succeeds even when the session user is not an
                // administrator, since an enabled administrator has just been verified.
                await _permissions
                    .WriteMachineDecisionsAsync(manifest.Id, pending, AppPermissionDecision.Allow, ct)
                    .ConfigureAwait(false);
                approved = true;
                RecordApprove(manifest.Id, capabilityList, principal, verification.LoginName ?? login ?? string.Empty, null, AuditOutcome.Success);
                return new DangerousCapabilityApprovalAttempt(true, DangerousCapabilityApprovalRequest.MaxAttempts - failures);
            }
            finally
            {
                attemptGate.Release();
            }
        }

        DangerousCapabilityApprovalRequest request = new(
            manifest,
            [.. pending.Select(AppCapabilities.Describe)],
            principal,
            AppAuthorityPolicy.Satisfies(principal.Authority, AppAuthority.Administrator) ? principal.LoginName.Value : null,
            ApproveAsync);

        DangerousCapabilityApprovalOutcome outcome;
        try
        {
            outcome = await prompt.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = DangerousCapabilityApprovalOutcome.Dismissed;
        }

        // The prompt surface cannot forge an approval: only a verified attempt counts.
        if (approved)
        {
            return DangerousCapabilityApprovalOutcome.Approved;
        }

        switch (outcome)
        {
            case DangerousCapabilityApprovalOutcome.Refused:
                await _permissions.WriteUserDenialsAsync(manifest.Id, pending, cancellationToken).ConfigureAwait(false);
                Record(principal.UserId.ToString(), "capability.refuse", manifest.Id, AuditOutcome.Denied, new()
                {
                    ["app"] = manifest.Id,
                    ["capabilities"] = capabilityList,
                    ["user"] = principal.LoginName.Value
                });
                return DangerousCapabilityApprovalOutcome.Refused;
            case DangerousCapabilityApprovalOutcome.Unavailable:
                RecordDeferred(manifest.Id, capabilityList);
                return DangerousCapabilityApprovalOutcome.Unavailable;
            default:
                // Closed without choosing: nothing written, the question comes back next launch.
                return DangerousCapabilityApprovalOutcome.Dismissed;
        }
    }

    /// <summary>Grants what is allowed and revokes what is no longer allowed, for this user.</summary>
    private void AlignGrants(string appId, string userId, IReadOnlyList<DangerousCapabilityStatus> statuses)
    {
        foreach (DangerousCapabilityStatus status in statuses)
        {
            CapabilityPolicyEvaluation evaluation = _grants.Evaluate(
                appId, userId, status.Capability, AppAuthority.System, AppAuthority.User);
            if (status.IsGranted && !evaluation.Granted)
            {
                _grants.Grant(appId, userId, status.Capability, CapabilityGrantSource.AdministratorApproval, AppAuthority.System);
            }
            else if (!status.IsGranted && evaluation is { Granted: true, GrantId: CapabilityGrantId grantId })
            {
                _grants.Revoke(grantId, AppAuthority.System);
            }
        }
    }

    private void RecordApprove(
        string appId, string capabilities, AuthenticatedPrincipal principal, string admin, string? failureReason, AuditOutcome outcome) =>
        Record(principal.UserId.ToString(), "capability.approve", appId, outcome, new()
        {
            ["app"] = appId,
            ["capabilities"] = capabilities,
            ["requestedBy"] = principal.LoginName.Value,
            ["admin"] = string.IsNullOrWhiteSpace(admin) ? "-" : admin,
            ["outcome"] = outcome == AuditOutcome.Success ? "success" : $"failure:{failureReason ?? "unknown"}"
        });

    private void RecordDeferred(string appId, string capabilities) =>
        Record("system", "capability.deferred", appId, AuditOutcome.Denied, new()
        {
            ["app"] = appId,
            ["capabilities"] = capabilities
        });

    private void Record(string actor, string action, string subject, AuditOutcome outcome, Dictionary<string, string> properties) =>
        _auditLog.Record(new AuditEntry(_clock(), Guid.NewGuid(), actor, action, subject, outcome, properties));
}
