using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>How an approval prompt ended (ADR 0049 section 5, step 3).</summary>
public enum DangerousCapabilityApprovalOutcome
{
    /// <summary>An administrator authorized: <see cref="DangerousCapabilityApprovalRequest.ApproveAsync"/> succeeded.</summary>
    Approved = 0,

    /// <summary>The user chose <em>Refuser</em>: <c>deny</c> lines go to the user's file.</summary>
    Refused = 1,

    /// <summary>Closed without choosing (Escape, three failed passwords): nothing is written.</summary>
    Dismissed = 2,

    /// <summary>No interactive surface could show the prompt: the decision is deferred.</summary>
    Unavailable = 3
}

/// <summary>Result of one administrator credential attempt inside an approval prompt.</summary>
/// <param name="Succeeded">Whether an enabled administrator was verified and the decision recorded.</param>
/// <param name="RemainingAttempts">
/// Attempts left; <c>0</c> after the third failure, and the prompt must then close with
/// <see cref="DangerousCapabilityApprovalOutcome.Dismissed"/>.
/// </param>
/// <param name="FailureReason">Stable reason code of a failure (from <see cref="Kazo.Os.App.Abstractions.Policy.IAdminElevationVerifier"/>), else <see langword="null"/>.</param>
public sealed record DangerousCapabilityApprovalAttempt(bool Succeeded, int RemainingAttempts, string? FailureReason = null);

/// <summary>
/// What an approval prompt shows and how it submits administrator credentials.
/// </summary>
/// <param name="Manifest">App asking for the capabilities (name, id, publisher, version).</param>
/// <param name="Capabilities">Pending dangerous capabilities, each with its title and risk sentence.</param>
/// <param name="RequestedBy">Session user who launched the app.</param>
/// <param name="SuggestedAdministratorLogin">
/// Login to prefill: the session user when they are an administrator, else <see langword="null"/>.
/// Even an administrator must type their password (ADR 0049, D24).
/// </param>
/// <param name="ApproveAsync">
/// Verifies an administrator's login and password (possibly another account than the session
/// user's, as with <c>sudo</c>). On success the gate has already written the machine <c>allow</c>
/// lines and logged <c>capability.approve</c>; the prompt then closes with
/// <see cref="DangerousCapabilityApprovalOutcome.Approved"/>. Every attempt is logged, never the password.
/// </param>
public sealed record DangerousCapabilityApprovalRequest(
    AppManifest Manifest,
    IReadOnlyList<CapabilityDescription> Capabilities,
    AuthenticatedPrincipal RequestedBy,
    string? SuggestedAdministratorLogin,
    Func<string, string, CancellationToken, Task<DangerousCapabilityApprovalAttempt>> ApproveAsync)
{
    /// <summary>Maximum number of credential attempts before the request is cancelled.</summary>
    public const int MaxAttempts = 3;
}

/// <summary>
/// Shows the administrator approval for pending dangerous capabilities (ADR 0049 section 5): the
/// secure-desktop overlay for window and service apps launched from the desktop, or the text mode
/// of the terminal for a command.
/// </summary>
public interface IDangerousCapabilityApprovalPrompt
{
    /// <summary>Shows the prompt and waits for the user's choice.</summary>
    /// <param name="request">What to show and how to submit credentials.</param>
    /// <param name="cancellationToken">Cancels the prompt; it then ends as <see cref="DangerousCapabilityApprovalOutcome.Dismissed"/>.</param>
    Task<DangerousCapabilityApprovalOutcome> RequestAsync(
        DangerousCapabilityApprovalRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What the launch gate decided for one launch.</summary>
/// <param name="Statuses">Final state of every declared dangerous capability (empty when none).</param>
/// <param name="Policy">Effective <c>dangerousCapabilities</c> policy read for this launch.</param>
/// <param name="IsSystemApp">Whether the app is a system app.</param>
/// <param name="PromptOutcome">How the prompt ended, or <see langword="null"/> when none was shown.</param>
public sealed record DangerousCapabilityGateResult(
    IReadOnlyList<DangerousCapabilityStatus> Statuses,
    DangerousCapabilityPolicy Policy,
    bool IsSystemApp,
    DangerousCapabilityApprovalOutcome? PromptOutcome = null)
{
    /// <summary>Gets a result for an app that declares no dangerous capability.</summary>
    public static DangerousCapabilityGateResult NotApplicable(bool isSystemApp) =>
        new([], DangerousCapabilityPolicy.Prompt, isSystemApp);

    /// <summary>
    /// Gets whether the one-shot approval of ADR 0042 must be refused for this app's dangerous
    /// capabilities: policy <c>block</c> on a non-system app (<c>sudo</c> is not concerned).
    /// </summary>
    public bool BlocksOneShotApproval => !IsSystemApp && Policy == DangerousCapabilityPolicy.Block;
}

/// <summary>
/// Launch gate of ADR 0049 section 5, called by <c>AppLifecycleOrchestrator.LaunchAsync</c> before
/// the process is created.
/// </summary>
public interface IDangerousCapabilityGate
{
    /// <summary>
    /// Makes sure every dangerous capability <paramref name="manifest"/> declares is decided for this
    /// launch: logs <c>capability.blocked</c> under policy <c>block</c>, asks through the prompt when
    /// one is pending under <c>prompt</c> (or logs <c>capability.deferred</c> without an interactive
    /// session), then aligns the capability grants (source <c>AdministratorApproval</c>) with the
    /// final decisions so the new instance's capabilities are frozen at launch (ADR 0042).
    /// </summary>
    /// <param name="manifest">App being launched.</param>
    /// <param name="principal">Session user launching it.</param>
    /// <param name="prompt">
    /// Prompt to use (the terminal passes its text mode), or <see langword="null"/> for the host's
    /// default (the overlay).
    /// </param>
    /// <param name="interactive">
    /// <see langword="false"/> for a launch nobody is watching (services auto-started with the
    /// session): no prompt, <c>capability.deferred</c> instead.
    /// </param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    Task<DangerousCapabilityGateResult> EnsureDecidedAsync(
        AppManifest manifest,
        AuthenticatedPrincipal principal,
        IDangerousCapabilityApprovalPrompt? prompt = null,
        bool interactive = true,
        CancellationToken cancellationToken = default);
}
