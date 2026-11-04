using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;

namespace Kazo.Os.Platform.Core.Elevation;

/// <summary>
/// Verifies live Administrator credentials and mints the resulting single-use elevation ticket in
/// one call, for callers (the terminal's <c>sudo</c> built-in, and a terminal command's
/// permission-denied retry) that need a portable token carried through <see cref="ExecuteCommandIntent"/>
/// to a later, separate <c>AppLifecycleOrchestrator.LaunchAsync</c> call (`ADR 0042`).
/// </summary>
public sealed class ElevationService : IElevationService
{
    private readonly IAdminElevationVerifier _verifier;
    private readonly IElevationTicketStore _tickets;

    /// <summary>Initializes the service over a credential verifier and ticket store.</summary>
    public ElevationService(IAdminElevationVerifier verifier, IElevationTicketStore tickets)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
    }

    /// <inheritdoc />
    public async Task<ElevationRequestResult> RequestSudoAsync(
        string adminLoginName, string password, CancellationToken cancellationToken = default)
    {
        AdminElevationVerificationResult result = await _verifier.VerifyAsync(adminLoginName, password, cancellationToken);
        if (!result.Success)
        {
            return new ElevationRequestResult(false, null, null, result.FailureReason);
        }

        ElevationTicket ticket = _tickets.Issue(ElevationScope.SudoRoot, result.UserId!, result.LoginName!);
        return new ElevationRequestResult(true, ticket.Token, result.LoginName, null);
    }

    /// <inheritdoc />
    public async Task<ElevationRequestResult> RequestCapabilityApprovalAsync(
        string adminLoginName, string password, string capability, VirtualPath? path,
        CancellationToken cancellationToken = default)
    {
        AdminElevationVerificationResult result = await _verifier.VerifyAsync(adminLoginName, password, cancellationToken);
        if (!result.Success)
        {
            return new ElevationRequestResult(false, null, null, result.FailureReason);
        }

        ElevationTicket ticket = _tickets.Issue(
            ElevationScope.CapabilityApproval, result.UserId!, result.LoginName!, capability, path);
        return new ElevationRequestResult(true, ticket.Token, result.LoginName, null);
    }
}
