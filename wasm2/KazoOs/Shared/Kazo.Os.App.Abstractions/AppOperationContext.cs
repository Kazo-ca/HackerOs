namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Captures the trusted identity and grants applied to one platform operation.
/// </summary>
/// <remarks>
/// Platform code creates this context. App-facing gateways are pre-scoped and do
/// not accept caller-created contexts as proof of authorization.
/// </remarks>
public sealed record AppOperationContext
{
    /// <summary>Gets the application on whose behalf the operation runs.</summary>
    public required string AppId { get; init; }

    /// <summary>Gets the authenticated acting user identifier.</summary>
    public required string UserId { get; init; }

    /// <summary>Gets the authenticated acting username/login name.</summary>
    public string? UserName { get; init; }

    /// <summary>
    /// Gets the raw PID of the process performing the operation, when it runs inside one (ADR 0046).
    /// </summary>
    /// <remarks>
    /// Kept as a plain <see cref="long"/> because App Abstractions does not reference the
    /// simulation <c>ProcessId</c> type. <c>/proc/self</c> resolves through this value, and is
    /// absent when it is <see langword="null"/> (platform-owned operations such as seeding).
    /// </remarks>
    public long? ProcessId { get; init; }

    /// <summary>Gets the acting user's authority.</summary>
    public required AppAuthority UserAuthority { get; init; }

    /// <summary>Gets exact capabilities granted by trusted OS policy.</summary>
    public required IReadOnlySet<string> GrantedCapabilities { get; init; }

    /// <summary>
    /// Gets whether this is an explicit, audited OS-owned operation rather than user-driven app UI.
    /// </summary>
    public bool IsSystemOperation { get; init; }

    /// <summary>
    /// Gets whether this one launched command was elevated to full unrestricted root via <c>sudo</c>
    /// (`ADR 0042`). Unlike <see cref="IsSystemOperation"/>, this is set only for one user-invoked
    /// terminal command after live Administrator re-authentication; it is never used for trusted
    /// platform-owned operations.
    /// </summary>
    public bool IsRootElevated { get; init; }

    /// <summary>
    /// Gets this operation's live one-shot elevation tickets, minted only after an admin-approval
    /// dialog verifies an enabled Administrator. Never null so callers can grant into it without a
    /// null check; empty by default.
    /// </summary>
    public OneShotElevationGrantSet OneShotElevation { get; init; } = new();

    /// <summary>Gets the authority used for this operation.</summary>
    public AppAuthority EffectiveAuthority =>
        IsSystemOperation || IsRootElevated ? AppAuthority.System : UserAuthority;

    /// <summary>Determines whether an exact capability was granted.</summary>
    /// <param name="capability">Capability required by the operation.</param>
    /// <returns><see langword="true"/> when trusted policy granted it exactly, or the operation is root-elevated.</returns>
    public bool HasCapability(string capability) =>
        IsRootElevated || AppCapabilityPolicy.IsGranted(GrantedCapabilities, capability);
}