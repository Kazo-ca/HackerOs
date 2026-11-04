using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Evaluates capability policy for one bound app/user/authority by delegating to the trusted
/// grant repository, without exposing the repository itself or any other app's grants.
/// </summary>
public sealed class AppCapabilityChecker : ICapabilityChecker
{
    private readonly ICapabilityGrantRepository _repository;
    private readonly string _appId;
    private readonly string _userId;
    private readonly AppAuthority _actingAuthority;

    /// <summary>Initializes a capability checker bound to one app/user/authority.</summary>
    public AppCapabilityChecker(
        ICapabilityGrantRepository repository,
        string appId,
        string userId,
        AppAuthority actingAuthority)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        _appId = appId;
        _userId = userId;
        _actingAuthority = actingAuthority;
    }

    /// <inheritdoc />
    public CapabilityPolicyEvaluation Evaluate(
        string capability,
        AppAuthority requiredAuthority = AppAuthority.User,
        CapabilityResourceCandidate? resourceCandidate = null) =>
        _repository.Evaluate(_appId, _userId, capability, _actingAuthority, requiredAuthority, resourceCandidate);

    /// <inheritdoc />
    public void Require(
        string capability,
        AppAuthority requiredAuthority = AppAuthority.User,
        CapabilityResourceCandidate? resourceCandidate = null)
    {
        CapabilityPolicyEvaluation evaluation = Evaluate(capability, requiredAuthority, resourceCandidate);
        if (!evaluation.Granted)
        {
            throw new AppGatewayAccessDeniedException(capability, evaluation);
        }
    }
}
