namespace Kazo.Os.App.Abstractions.Policy;

/// <summary>Identifies what a minted elevation ticket authorizes.</summary>
public enum ElevationScope
{
    /// <summary>Full unrestricted root bypass for exactly one launched terminal command (`sudo`).</summary>
    SudoRoot,

    /// <summary>Unblocks one specific (capability, path) denial, once.</summary>
    CapabilityApproval
}

/// <summary>A single-use, short-lived authorization minted after verifying an enabled Administrator's credentials.</summary>
public sealed record ElevationTicket(
    Guid Token,
    ElevationScope Scope,
    string AuthorizedByUserId,
    string AuthorizedByLoginName,
    DateTimeOffset IssuedAtUtc,
    string? Capability = null,
    VirtualPath? Path = null);

/// <summary>Mints and single-consumes short-lived elevation tickets.</summary>
public interface IElevationTicketStore
{
    /// <summary>Mints a new single-use ticket for the given scope.</summary>
    ElevationTicket Issue(
        ElevationScope scope,
        string authorizedByUserId,
        string authorizedByLoginName,
        string? capability = null,
        VirtualPath? path = null);

    /// <summary>Atomically consumes a ticket exactly once, when it exists, matches the expected scope, and has not expired.</summary>
    bool TryConsume(Guid token, ElevationScope expectedScope, out ElevationTicket? ticket);
}

/// <summary>Contains the outcome of verifying an enabled Administrator's credentials.</summary>
public sealed record AdminElevationVerificationResult(bool Success, string? UserId, string? LoginName, string? FailureReason)
{
    /// <summary>Creates a failed verification result with a stable reason code.</summary>
    public static AdminElevationVerificationResult Failed(string reason) => new(false, null, null, reason);
}

/// <summary>Verifies that a login belongs to an enabled Administrator, without mutating session state.</summary>
public interface IAdminElevationVerifier
{
    /// <summary>Verifies the given login/password against an enabled Administrator account.</summary>
    Task<AdminElevationVerificationResult> VerifyAsync(
        string loginName, string password, CancellationToken cancellationToken = default);
}

/// <summary>Contains the outcome of requesting an elevation ticket.</summary>
public sealed record ElevationRequestResult(bool Success, Guid? Token, string? AuthorizedByLoginName, string? FailureReason);

/// <summary>Ties admin-credential verification and ticket minting together for callers needing a portable token.</summary>
public interface IElevationService
{
    /// <summary>Verifies the given Administrator credentials and, on success, mints a <see cref="ElevationScope.SudoRoot"/> ticket.</summary>
    Task<ElevationRequestResult> RequestSudoAsync(
        string adminLoginName, string password, CancellationToken cancellationToken = default);

    /// <summary>Verifies the given Administrator credentials and, on success, mints a <see cref="ElevationScope.CapabilityApproval"/> ticket.</summary>
    Task<ElevationRequestResult> RequestCapabilityApprovalAsync(
        string adminLoginName, string password, string capability, VirtualPath? path,
        CancellationToken cancellationToken = default);
}
