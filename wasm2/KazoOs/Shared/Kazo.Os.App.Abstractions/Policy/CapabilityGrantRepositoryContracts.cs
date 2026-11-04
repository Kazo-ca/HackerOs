namespace Kazo.Os.App.Abstractions.Policy;

/// <summary>Identifies the stable outcome of one grant-repository mutation attempt.</summary>
public enum CapabilityGrantMutationStatus
{
    /// <summary>A new grant was created.</summary>
    Granted = 1,

    /// <summary>A new grant was created and it is broader than an existing active grant for the same app/user/capability.</summary>
    Expanded = 2,

    /// <summary>An existing active grant was revoked.</summary>
    Revoked = 3,

    /// <summary>No active grant exists for the requested ID.</summary>
    NotFound = 4,

    /// <summary>The acting principal lacks the authority required to mutate policy.</summary>
    AuthorityDenied = 5
}

/// <summary>Records the trusted action recorded in the capability grant audit log.</summary>
public enum CapabilityGrantAuditAction
{
    /// <summary>A grant was created.</summary>
    Granted = 1,

    /// <summary>A grant was created and expanded access relative to a prior active grant.</summary>
    Expanded = 2,

    /// <summary>A grant was revoked.</summary>
    Revoked = 3
}

/// <summary>Contains the result of one grant-repository mutation.</summary>
/// <param name="Status">Stable mutation outcome.</param>
/// <param name="Grant">Affected grant when the mutation touched one.</param>
/// <param name="PolicyRevision">Repository policy revision after the mutation attempt.</param>
public sealed record CapabilityGrantMutationResult(
    CapabilityGrantMutationStatus Status,
    CapabilityGrant? Grant,
    long PolicyRevision);

/// <summary>Records one audited grant or revocation for later inspection.</summary>
/// <param name="Action">Trusted action that produced this entry.</param>
/// <param name="Grant">Grant affected by the action.</param>
/// <param name="PolicyRevision">Repository policy revision produced by the action.</param>
/// <param name="ActingAuthority">Authority of the principal that performed the action.</param>
/// <param name="TimestampUtc">UTC time the action was recorded.</param>
public sealed record CapabilityGrantAuditEntry(
    CapabilityGrantAuditAction Action,
    CapabilityGrant Grant,
    long PolicyRevision,
    AppAuthority ActingAuthority,
    DateTimeOffset TimestampUtc);

/// <summary>Identifies a durable grant mutation that lost an optimistic revision race.</summary>
public sealed class CapabilityGrantConflictException : Exception
{
    /// <summary>Initializes a policy-revision conflict.</summary>
    public CapabilityGrantConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Persists capability policy through cancellation-aware durable operations.</summary>
public interface IPersistentCapabilityGrantRepository
{
    /// <summary>Gets the current durable policy revision.</summary>
    ValueTask<long> GetCurrentPolicyRevisionAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Creates and audits one immutable capability grant atomically.</summary>
    ValueTask<CapabilityGrantMutationResult> GrantAsync(
        string appId,
        string userId,
        string capability,
        CapabilityGrantSource source,
        AppAuthority actingAuthority,
        IEnumerable<CapabilityConstraint>? constraints = null,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes and audits one active capability grant atomically.</summary>
    ValueTask<CapabilityGrantMutationResult> RevokeAsync(
        CapabilityGrantId grantId,
        AppAuthority actingAuthority,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates one capability grant under a caller-supplied ID, atomically and audited,
    /// bypassing the always-mint-a-new-ID behavior of <see cref="GrantAsync"/>. Used exclusively by
    /// the optional server's Grants domain sync pull adapter (ADR 0031) to apply a server-issued
    /// grant record under the server's own <c>RecordId</c>, so a later re-import of the same record
    /// (e.g. a revocation) updates the existing row instead of creating a duplicate.
    /// </summary>
    /// <param name="id">Caller-supplied grant identity — the server's sync <c>RecordId</c>.</param>
    /// <param name="appId">Exact app ID receiving the grant.</param>
    /// <param name="userId">Exact user ID receiving the grant.</param>
    /// <param name="capability">Exact known capability identifier.</param>
    /// <param name="source">Trusted grant source.</param>
    /// <param name="constraints">Optional structured resource constraints.</param>
    /// <param name="isRevoked">Whether the imported grant is currently revoked.</param>
    /// <param name="actingAuthority">Authority of the principal requesting the mutation.</param>
    /// <param name="cancellationToken">Cancels the durable write.</param>
    /// <returns>The mutation result.</returns>
    ValueTask<CapabilityGrantMutationResult> ImportAsync(
        CapabilityGrantId id,
        string appId,
        string userId,
        string capability,
        CapabilityGrantSource source,
        IEnumerable<CapabilityConstraint>? constraints,
        bool isRevoked,
        AppAuthority actingAuthority,
        CancellationToken cancellationToken = default);

    /// <summary>Evaluates durable deny-by-default policy for one requested resource.</summary>
    ValueTask<CapabilityPolicyEvaluation> EvaluateAsync(
        string appId,
        string userId,
        string capability,
        AppAuthority actingAuthority,
        AppAuthority requiredAuthority,
        CapabilityResourceCandidate? resourceCandidate = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores immutable exact capability grants, evaluates deny-by-default policy decisions,
/// and records an audit trail of every grant/revoke mutation.
/// </summary>
public interface ICapabilityGrantRepository
{
    /// <summary>Gets the current policy revision.</summary>
    long CurrentPolicyRevision { get; }

    /// <summary>Gets every recorded audit entry in chronological order.</summary>
    IReadOnlyList<CapabilityGrantAuditEntry> AuditLog { get; }

    /// <summary>
    /// Creates a new capability grant. Requires Administrator or System acting authority.
    /// </summary>
    /// <param name="appId">Exact app ID receiving the grant.</param>
    /// <param name="userId">Exact user ID receiving the grant.</param>
    /// <param name="capability">Exact known capability identifier.</param>
    /// <param name="source">Trusted grant source.</param>
    /// <param name="actingAuthority">Authority of the principal requesting the mutation.</param>
    /// <param name="constraints">Optional structured resource constraints.</param>
    /// <returns>The mutation result, including whether the grant expanded prior access.</returns>
    CapabilityGrantMutationResult Grant(
        string appId,
        string userId,
        string capability,
        CapabilityGrantSource source,
        AppAuthority actingAuthority,
        IEnumerable<CapabilityConstraint>? constraints = null);

    /// <summary>
    /// Revokes an active capability grant. Requires Administrator or System acting authority.
    /// </summary>
    /// <param name="grantId">Identifier of the grant to revoke.</param>
    /// <param name="actingAuthority">Authority of the principal requesting the mutation.</param>
    /// <returns>The mutation result.</returns>
    CapabilityGrantMutationResult Revoke(CapabilityGrantId grantId, AppAuthority actingAuthority);

    /// <summary>
    /// Evaluates a deny-by-default capability policy decision for one requested resource.
    /// </summary>
    /// <param name="appId">Exact acting app ID.</param>
    /// <param name="userId">Exact acting user ID.</param>
    /// <param name="capability">Exact requested capability identifier.</param>
    /// <param name="actingAuthority">Effective authority of the acting operation.</param>
    /// <param name="requiredAuthority">Minimum authority policy requires for this operation, independent of the capability grant itself.</param>
    /// <param name="resourceCandidate">Optional candidate resource checked against structured grant constraints.</param>
    /// <returns>The stable, explicit policy evaluation.</returns>
    CapabilityPolicyEvaluation Evaluate(
        string appId,
        string userId,
        string capability,
        AppAuthority actingAuthority,
        AppAuthority requiredAuthority,
        CapabilityResourceCandidate? resourceCandidate = null);
}

/// <summary>Identifies the closed kind of resource candidate checked against grant constraints.</summary>
public enum CapabilityResourceCandidateKind
{
    /// <summary>A canonical virtual path.</summary>
    VirtualPath = 1,

    /// <summary>A normalized network host.</summary>
    NetworkHost = 2,

    /// <summary>A single network port.</summary>
    NetworkPort = 3
}

/// <summary>Represents one concrete resource an operation is attempting to access.</summary>
public abstract record CapabilityResourceCandidate
{
    /// <summary>Gets the closed resource candidate kind.</summary>
    public abstract CapabilityResourceCandidateKind Kind { get; }
}

/// <summary>A candidate virtual path resource.</summary>
public sealed record VirtualPathResourceCandidate(VirtualPath Path) : CapabilityResourceCandidate
{
    /// <inheritdoc />
    public override CapabilityResourceCandidateKind Kind => CapabilityResourceCandidateKind.VirtualPath;
}

/// <summary>A candidate network host resource.</summary>
public sealed record NetworkHostResourceCandidate(string Host) : CapabilityResourceCandidate
{
    /// <inheritdoc />
    public override CapabilityResourceCandidateKind Kind => CapabilityResourceCandidateKind.NetworkHost;
}

/// <summary>A candidate network port resource.</summary>
public sealed record NetworkPortResourceCandidate(ushort Port) : CapabilityResourceCandidate
{
    /// <inheritdoc />
    public override CapabilityResourceCandidateKind Kind => CapabilityResourceCandidateKind.NetworkPort;
}
