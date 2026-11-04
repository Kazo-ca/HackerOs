namespace Kazo.Os.App.Abstractions.Policy;

/// <summary>Identifies the stable outcome of one capability policy evaluation.</summary>
public enum CapabilityPolicyEvaluationReason
{
    /// <summary>No matching exact grant exists; this is the deny-by-default outcome.</summary>
    Missing = 0,

    /// <summary>A matching active grant permits the requested operation.</summary>
    Granted = 1,

    /// <summary>A matching grant exists but has been revoked.</summary>
    Revoked = 2,

    /// <summary>A matching grant does not include the requested resource.</summary>
    Constrained = 3,

    /// <summary>The acting principal lacks the authority required by policy.</summary>
    AuthorityDenied = 4
}

/// <summary>Contains one deny-by-default capability policy decision.</summary>
public readonly record struct CapabilityPolicyEvaluation
{
    private CapabilityPolicyEvaluation(
        bool granted,
        CapabilityPolicyEvaluationReason reason,
        long policyRevision,
        CapabilityGrantId? grantId)
    {
        Granted = granted;
        Reason = reason;
        PolicyRevision = policyRevision;
        GrantId = grantId;
    }

    /// <summary>Gets whether policy granted the requested capability and resource.</summary>
    public bool Granted { get; }

    /// <summary>Gets the stable reason for the decision.</summary>
    public CapabilityPolicyEvaluationReason Reason { get; }

    /// <summary>Gets the policy revision used for evaluation, or zero before evaluation.</summary>
    public long PolicyRevision { get; }

    /// <summary>Gets the matching grant ID when one participated in the decision.</summary>
    public CapabilityGrantId? GrantId { get; }

    /// <summary>Creates a granted decision from the exact matching grant.</summary>
    public static CapabilityPolicyEvaluation Permit(CapabilityGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new CapabilityPolicyEvaluation(
            true,
            CapabilityPolicyEvaluationReason.Granted,
            grant.PolicyRevision,
            grant.Id);
    }

    /// <summary>Creates a missing-grant denial at the evaluated policy revision.</summary>
    public static CapabilityPolicyEvaluation DenyMissing(long policyRevision)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(policyRevision, 1);
        return new CapabilityPolicyEvaluation(
            false,
            CapabilityPolicyEvaluationReason.Missing,
            policyRevision,
            null);
    }

    /// <summary>Creates a denial caused by one matching grant.</summary>
    public static CapabilityPolicyEvaluation Deny(
        CapabilityGrant grant,
        CapabilityPolicyEvaluationReason reason)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (reason is not CapabilityPolicyEvaluationReason.Revoked
            and not CapabilityPolicyEvaluationReason.Constrained
            and not CapabilityPolicyEvaluationReason.AuthorityDenied)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "Grant denials require a revoked, constrained, or authority-denied reason.");
        }

        return new CapabilityPolicyEvaluation(false, reason, grant.PolicyRevision, grant.Id);
    }
}