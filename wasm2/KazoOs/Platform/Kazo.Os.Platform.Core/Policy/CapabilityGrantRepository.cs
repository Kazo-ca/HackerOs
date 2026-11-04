using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>
/// Stores capability grants in memory, evaluates deny-by-default policy decisions, and
/// records an audit trail of every grant/revoke mutation.
/// </summary>
/// <remarks>
/// Granting and revoking are themselves protected policy actions and require Administrator
/// or explicit audited System acting authority, matching the authority hierarchy enforced by
/// <see cref="AppAuthorityPolicy"/>.
/// </remarks>
public sealed class CapabilityGrantRepository : ICapabilityGrantRepository
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, CapabilityGrant> _activeGrants = new();
    private readonly Dictionary<Guid, CapabilityGrant> _revokedGrants = new();
    private readonly List<CapabilityGrantAuditEntry> _auditLog = new();
    private readonly Func<DateTimeOffset> _clock;
    private long _policyRevision;

    /// <summary>Initializes an empty repository.</summary>
    /// <param name="clock">Deterministic UTC clock used for audit timestamps; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public CapabilityGrantRepository(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _policyRevision = 0;
    }

    /// <inheritdoc />
    public long CurrentPolicyRevision
    {
        get
        {
            lock (_sync)
            {
                return _policyRevision;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CapabilityGrantAuditEntry> AuditLog
    {
        get
        {
            lock (_sync)
            {
                return _auditLog.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public CapabilityGrantMutationResult Grant(
        string appId,
        string userId,
        string capability,
        CapabilityGrantSource source,
        AppAuthority actingAuthority,
        IEnumerable<CapabilityConstraint>? constraints = null)
    {
        lock (_sync)
        {
            if (!AppAuthorityPolicy.Satisfies(actingAuthority, AppAuthority.Administrator))
            {
                return new CapabilityGrantMutationResult(
                    CapabilityGrantMutationStatus.AuthorityDenied,
                    null,
                    _policyRevision);
            }

            CapabilityConstraint[] copiedConstraints = constraints?.ToArray() ?? [];
            bool isExpansion = _activeGrants.Values.Any(existing =>
                existing.AppId == appId
                && existing.UserId == userId
                && existing.Capability == capability
                && IsBroaderOrEqual(copiedConstraints, existing.Constraints)
                && !IsBroaderOrEqual(existing.Constraints, copiedConstraints));

            _policyRevision++;
            CapabilityGrant grant = new(
                CapabilityGrantId.FromGuid(Guid.NewGuid()),
                appId,
                userId,
                capability,
                _policyRevision,
                source,
                copiedConstraints);
            _activeGrants[grant.Id.Value] = grant;

            CapabilityGrantAuditAction action = isExpansion
                ? CapabilityGrantAuditAction.Expanded
                : CapabilityGrantAuditAction.Granted;
            _auditLog.Add(new CapabilityGrantAuditEntry(action, grant, _policyRevision, actingAuthority, _clock()));

            return new CapabilityGrantMutationResult(
                isExpansion ? CapabilityGrantMutationStatus.Expanded : CapabilityGrantMutationStatus.Granted,
                grant,
                _policyRevision);
        }
    }

    /// <inheritdoc />
    public CapabilityGrantMutationResult Revoke(CapabilityGrantId grantId, AppAuthority actingAuthority)
    {
        lock (_sync)
        {
            if (!AppAuthorityPolicy.Satisfies(actingAuthority, AppAuthority.Administrator))
            {
                return new CapabilityGrantMutationResult(
                    CapabilityGrantMutationStatus.AuthorityDenied,
                    null,
                    _policyRevision);
            }

            if (!_activeGrants.Remove(grantId.Value, out CapabilityGrant? grant))
            {
                return new CapabilityGrantMutationResult(
                    CapabilityGrantMutationStatus.NotFound,
                    null,
                    _policyRevision);
            }

            _revokedGrants[grantId.Value] = grant;
            _policyRevision++;
            _auditLog.Add(new CapabilityGrantAuditEntry(
                CapabilityGrantAuditAction.Revoked,
                grant,
                _policyRevision,
                actingAuthority,
                _clock()));

            return new CapabilityGrantMutationResult(CapabilityGrantMutationStatus.Revoked, grant, _policyRevision);
        }
    }

    /// <inheritdoc />
    public CapabilityPolicyEvaluation Evaluate(
        string appId,
        string userId,
        string capability,
        AppAuthority actingAuthority,
        AppAuthority requiredAuthority,
        CapabilityResourceCandidate? resourceCandidate = null)
    {
        lock (_sync)
        {
            CapabilityGrant? matchingActive = _activeGrants.Values.FirstOrDefault(grant =>
                grant.AppId == appId && grant.UserId == userId && grant.Capability == capability
                && Allows(grant, resourceCandidate));

            if (matchingActive is not null)
            {
                return AppAuthorityPolicy.Satisfies(actingAuthority, requiredAuthority)
                    ? CapabilityPolicyEvaluation.Permit(matchingActive)
                    : CapabilityPolicyEvaluation.Deny(matchingActive, CapabilityPolicyEvaluationReason.AuthorityDenied);
            }

            CapabilityGrant? constrainedActive = _activeGrants.Values.FirstOrDefault(grant =>
                grant.AppId == appId && grant.UserId == userId && grant.Capability == capability);
            if (constrainedActive is not null)
            {
                return CapabilityPolicyEvaluation.Deny(constrainedActive, CapabilityPolicyEvaluationReason.Constrained);
            }

            CapabilityGrant? revoked = _revokedGrants.Values.FirstOrDefault(grant =>
                grant.AppId == appId && grant.UserId == userId && grant.Capability == capability);
            if (revoked is not null)
            {
                return CapabilityPolicyEvaluation.Deny(revoked, CapabilityPolicyEvaluationReason.Revoked);
            }

            return CapabilityPolicyEvaluation.DenyMissing(Math.Max(_policyRevision, 1));
        }
    }

    private static bool Allows(CapabilityGrant grant, CapabilityResourceCandidate? resourceCandidate)
    {
        if (resourceCandidate is null)
        {
            return grant.Constraints.Count == 0;
        }

        return resourceCandidate switch
        {
            VirtualPathResourceCandidate pathCandidate => grant.Constraints
                .OfType<VirtualPathCapabilityConstraint>()
                .Any(constraint => constraint.Allows(pathCandidate.Path)),
            NetworkHostResourceCandidate hostCandidate => grant.Constraints
                .OfType<NetworkHostCapabilityConstraint>()
                .Any(constraint => string.Equals(constraint.Host, hostCandidate.Host, StringComparison.Ordinal)),
            NetworkPortResourceCandidate portCandidate => grant.Constraints
                .OfType<NetworkPortCapabilityConstraint>()
                .Any(constraint =>
                    portCandidate.Port >= constraint.MinimumPort && portCandidate.Port <= constraint.MaximumPort),
            _ => false
        };
    }

    /// <summary>
    /// Determines whether <paramref name="candidate"/> constraints allow every resource
    /// <paramref name="baseline"/> allows, used to detect grant expansion on re-grant.
    /// </summary>
    private static bool IsBroaderOrEqual(
        IReadOnlyList<CapabilityConstraint> candidate,
        IReadOnlyList<CapabilityConstraint> baseline)
    {
        if (baseline.Count == 0)
        {
            // An unconstrained baseline is already maximally broad; nothing exceeds it.
            return candidate.Count == 0;
        }

        if (candidate.Count == 0)
        {
            // An unconstrained candidate allows every resource, including everything the baseline allows.
            return true;
        }

        foreach (CapabilityConstraint baselineConstraint in baseline)
        {
            CapabilityConstraint? candidateConstraint = candidate.FirstOrDefault(
                constraint => constraint.Kind == baselineConstraint.Kind);
            if (candidateConstraint is null || !IsBroaderOrEqual(candidateConstraint, baselineConstraint))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBroaderOrEqual(CapabilityConstraint candidate, CapabilityConstraint baseline) =>
        (candidate, baseline) switch
        {
            (VirtualPathCapabilityConstraint c, VirtualPathCapabilityConstraint b) =>
                c.Allows(b.Path) && (c.IncludeDescendants || !b.IncludeDescendants),
            (NetworkPortCapabilityConstraint c, NetworkPortCapabilityConstraint b) =>
                c.MinimumPort <= b.MinimumPort && c.MaximumPort >= b.MaximumPort,
            (NetworkHostCapabilityConstraint c, NetworkHostCapabilityConstraint b) =>
                string.Equals(c.Host, b.Host, StringComparison.Ordinal),
            _ => false
        };
}
