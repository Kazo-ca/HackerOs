using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Elevation;

/// <summary>
/// One operation that needs an administrator in addition to the app's capability (ADR 0055,
/// section 5), such as uninstalling an app for every user.
/// </summary>
/// <param name="App">App asking for the operation; shown by name and id in the prompt and logged.</param>
/// <param name="Operation">The operation in plain words, shown in the prompt ("Uninstall Calculator for all users").</param>
/// <param name="Action">Stable operation id written to <c>/var/log/auth.log</c> (<c>apps.uninstall.machine</c>).</param>
/// <param name="RequestedBy">Session user running the app, or <see langword="null"/> without a session.</param>
/// <param name="IsRootElevated">Whether the calling process already runs as root through <c>sudo</c> (ADR 0042): nothing is asked then.</param>
public sealed record AdministratorAuthenticationRequest(
    AppManifest App,
    string Operation,
    string Action,
    AuthenticatedPrincipal? RequestedBy,
    bool IsRootElevated = false);

/// <summary>Result of <see cref="IAdministratorAuthenticationAgent.AuthenticateAsync"/>.</summary>
/// <param name="Succeeded">Whether an enabled administrator authenticated (or the process was already root).</param>
/// <param name="AdministratorLogin">Login of the administrator who authenticated; <c>root</c> under <c>sudo</c>.</param>
/// <param name="FailureReason">
/// Stable reason of a failure: <c>refused</c>, <c>cancelled</c>, <c>too-many-attempts</c> or
/// <c>unavailable</c> (no surface could show the prompt).
/// </param>
public sealed record AdministratorAuthenticationResult(bool Succeeded, string? AdministratorLogin, string? FailureReason)
{
    /// <summary>Builds a failed result.</summary>
    /// <param name="reason">Stable reason code.</param>
    public static AdministratorAuthenticationResult Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// The OS authentication agent of ADR 0055, section 5, modeled on <c>polkit</c>: a gateway that
/// needs an administrator asks it, the agent shows its own prompt, verifies the credentials with
/// the <c>sudo</c> verifier (ADR 0042) and logs the outcome in <c>/var/log/auth.log</c>. The app
/// never sees the password.
/// </summary>
public interface IAdministratorAuthenticationAgent
{
    /// <summary>Asks an administrator to authorize one operation.</summary>
    /// <param name="request">What is asked, by whom.</param>
    /// <param name="cancellationToken">Cancels the prompt; the result is then a failure (<c>cancelled</c>).</param>
    Task<AdministratorAuthenticationResult> AuthenticateAsync(
        AdministratorAuthenticationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>How an administrator authentication prompt ended.</summary>
public enum AdministratorAuthenticationOutcome
{
    /// <summary>An administrator authenticated: a call to <see cref="AdministratorAuthenticationPromptRequest.AuthenticateAsync"/> succeeded.</summary>
    Approved = 0,

    /// <summary>The user chose <em>Annuler</em>, pressed Escape, or failed three times.</summary>
    Cancelled = 1,

    /// <summary>No interactive surface could show the prompt.</summary>
    Unavailable = 2
}

/// <summary>Result of one credential attempt inside an administrator authentication prompt.</summary>
/// <param name="Succeeded">Whether an enabled administrator was verified.</param>
/// <param name="RemainingAttempts">Attempts left; <c>0</c> after the third failure, and the prompt must close.</param>
/// <param name="FailureReason">Stable reason from <c>IAdminElevationVerifier</c> on a failure, else <see langword="null"/>.</param>
public sealed record AdministratorAuthenticationAttempt(bool Succeeded, int RemainingAttempts, string? FailureReason = null);

/// <summary>What an administrator authentication prompt shows and how it submits credentials.</summary>
/// <param name="App">App asking for the operation.</param>
/// <param name="Operation">The operation in plain words.</param>
/// <param name="RequestedBy">Session user running the app, or <see langword="null"/>.</param>
/// <param name="SuggestedAdministratorLogin">Login to prefill: the session user when they are an administrator.</param>
/// <param name="AuthenticateAsync">
/// Verifies an administrator's login and password (possibly another account, as with
/// <c>sudo</c>). Every attempt is logged, never the password. The prompt closes with
/// <see cref="AdministratorAuthenticationOutcome.Approved"/> on success.
/// </param>
public sealed record AdministratorAuthenticationPromptRequest(
    AppManifest App,
    string Operation,
    AuthenticatedPrincipal? RequestedBy,
    string? SuggestedAdministratorLogin,
    Func<string, string, CancellationToken, Task<AdministratorAuthenticationAttempt>> AuthenticateAsync)
{
    /// <summary>Maximum number of credential attempts before the request is cancelled.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Gets how to authenticate an administrator with one of their passkeys instead of a password
    /// (ADR 0168 section 4): takes the administrator's login; <see langword="null"/> when the host
    /// has no passkeys, and the prompt then offers the password only.
    /// </summary>
    public Func<string, CancellationToken, Task<AdministratorAuthenticationAttempt>>? AuthenticateWithPasskeyAsync { get; init; }
}

/// <summary>
/// Surface that shows the agent's prompt: the secure-desktop overlay of the platform shell. The
/// prompt decides nothing; it only submits credentials to
/// <see cref="AdministratorAuthenticationPromptRequest.AuthenticateAsync"/>.
/// </summary>
public interface IAdministratorAuthenticationPrompt
{
    /// <summary>Shows the prompt and waits until it closes.</summary>
    /// <param name="request">What to show and how to submit credentials.</param>
    /// <param name="cancellationToken">Cancels the prompt; it then ends as <see cref="AdministratorAuthenticationOutcome.Cancelled"/>.</param>
    Task<AdministratorAuthenticationOutcome> RequestAsync(
        AdministratorAuthenticationPromptRequest request, CancellationToken cancellationToken = default);
}
