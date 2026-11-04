using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.Administration;

/// <summary>
/// Administers the dangerous capabilities of the other apps (ADR 0056, section 9), as
/// Settings &gt; Permissions does. Replaces <c>AppPermissionAdministration</c>, which apps may no
/// longer obtain (ADR 0055).
/// </summary>
/// <remarks>
/// <para>
/// Every member requires the dangerous capability <c>permissions.manage</c>; a missing one raises
/// <see cref="KazoUnauthorizedAccessException"/>.
/// </para>
/// <para>
/// <see cref="SetPolicyAsync(KazoDangerousCapabilityPolicy, CancellationToken)"/> and <see cref="SetMachineDecisionAsync(string, string, KazoPermissionDecision, CancellationToken)"/> change machine files. Like
/// <c>pkexec</c>, the OS first asks an administrator for their password in its own secure window
/// (ADR 0055, section 5); the app never sees the password. A refusal or a cancellation raises
/// <see cref="KazoUnauthorizedAccessException"/> and nothing is written. A process already running
/// as root through <c>sudo</c> is not asked again. Every change applies at the app's next launch.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// IReadOnlyList&lt;KazoAppPermissionInfo&gt; rows = await KazoPermissionAdministration.GetOverviewAsync();
/// await KazoPermissionAdministration.SetMachineDecisionAsync("org.example.tool", "apps.manage", KazoPermissionDecision.Allow);
/// </code>
/// </example>
public static class KazoPermissionAdministration
{
    /// <summary>Reads the effective <c>dangerousCapabilities</c> policy.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>permissions.manage</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task<KazoDangerousCapabilityPolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        GetPolicyAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Reads the policy through an explicit context (a component outside the ambient scope).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static Task<KazoDangerousCapabilityPolicy> GetPolicyAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.GetPolicyAsync(cancellationToken));

    /// <summary>Lists every app declaring a dangerous capability with the state of each one.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>permissions.manage</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task<IReadOnlyList<KazoAppPermissionInfo>> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        GetOverviewAsync(KazoExecutionContext.Current, cancellationToken);

    /// <summary>Lists the apps through an explicit context (a component outside the ambient scope).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static Task<IReadOnlyList<KazoAppPermissionInfo>> GetOverviewAsync(IAppExecutionContext context, CancellationToken cancellationToken = default) =>
        Call(context, gateway => gateway.GetOverviewAsync(cancellationToken));

    /// <summary>Changes the policy for the whole machine; an administrator must authenticate.</summary>
    /// <param name="policy">New policy.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <exception cref="KazoUnauthorizedAccessException">
    /// This app lacks <c>permissions.manage</c>, or no administrator authenticated.
    /// </exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task SetPolicyAsync(KazoDangerousCapabilityPolicy policy, CancellationToken cancellationToken = default) =>
        SetPolicyAsync(KazoExecutionContext.Current, policy, cancellationToken);

    /// <summary>Changes the policy through an explicit context (a component outside the ambient scope).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="policy">New policy.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    public static Task SetPolicyAsync(
        IAppExecutionContext context, KazoDangerousCapabilityPolicy policy, CancellationToken cancellationToken = default) =>
        Call(context, async gateway =>
        {
            await gateway.SetPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <summary>
    /// Records a machine decision for one dangerous capability of one non-system app; an
    /// administrator must authenticate.
    /// </summary>
    /// <param name="appId">Non-system app identifier.</param>
    /// <param name="capability">Dangerous capability the app declares.</param>
    /// <param name="decision"><see cref="KazoPermissionDecision.Allow"/>, <see cref="KazoPermissionDecision.Deny"/>, or <see cref="KazoPermissionDecision.Ask"/> to forget the decision.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    /// <exception cref="ArgumentException">The app is unknown or a system app, or does not declare this dangerous capability.</exception>
    /// <exception cref="KazoUnauthorizedAccessException">
    /// This app lacks <c>permissions.manage</c>, or no administrator authenticated.
    /// </exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task SetMachineDecisionAsync(
        string appId, string capability, KazoPermissionDecision decision, CancellationToken cancellationToken = default) =>
        SetMachineDecisionAsync(KazoExecutionContext.Current, appId, capability, decision, cancellationToken);

    /// <summary>Records a machine decision through an explicit context (a component outside the ambient scope).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="appId">Non-system app identifier.</param>
    /// <param name="capability">Dangerous capability the app declares.</param>
    /// <param name="decision">Decision to record.</param>
    /// <param name="cancellationToken">Cancels the authentication and the write.</param>
    public static Task SetMachineDecisionAsync(
        IAppExecutionContext context,
        string appId,
        string capability,
        KazoPermissionDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return Call(context, async gateway =>
        {
            await gateway.SetMachineDecisionAsync(appId, capability, decision, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Withdraws the session user's own refusal of one capability; no administrator is needed.</summary>
    /// <param name="appId">App identifier.</param>
    /// <param name="capability">Capability identifier.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="KazoUnauthorizedAccessException">This app lacks <c>permissions.manage</c>.</exception>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static Task RemoveOwnDenialAsync(string appId, string capability, CancellationToken cancellationToken = default) =>
        RemoveOwnDenialAsync(KazoExecutionContext.Current, appId, capability, cancellationToken);

    /// <summary>Withdraws one's own refusal through an explicit context (a component outside the ambient scope).</summary>
    /// <param name="context">The calling app's execution context.</param>
    /// <param name="appId">App identifier.</param>
    /// <param name="capability">Capability identifier.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static Task RemoveOwnDenialAsync(
        IAppExecutionContext context, string appId, string capability, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return Call(context, async gateway =>
        {
            await gateway.RemoveOwnDenialAsync(appId, capability, cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    private static async Task<T> Call<T>(IAppExecutionContext context, Func<IAppPermissionAdministrationGateway, ValueTask<T>> call)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await call(context.PermissionAdministration).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw KazoGatewayErrors.ToUnauthorized(exception);
        }
    }
}
