using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Administration;

/// <summary>
/// Asks the OS authentication agent (ADR 0055, section 5) for one administrator operation on
/// behalf of one app instance, and turns a refusal into <see cref="HackUnauthorizedAccessException"/>.
/// Shared by the administration gateways of ADR 0056 (sections 9 and 11).
/// </summary>
internal sealed class AdministratorGate
{
    private readonly Func<IAdministratorAuthenticationAgent>? _agent;
    private readonly AppManifest _caller;
    private readonly AuthenticatedPrincipal _principal;
    private readonly bool _isRootElevated;
    private readonly Func<System.Globalization.CultureInfo> _language;

    /// <summary>Creates the gate for one app instance.</summary>
    /// <param name="agent">Lazily resolved agent; <see langword="null"/> refuses every operation.</param>
    /// <param name="caller">Manifest of the app asking.</param>
    /// <param name="principal">Session user running the app.</param>
    /// <param name="isRootElevated">Whether the app runs as root through <c>sudo</c>: the agent then asks nothing.</param>
    /// <param name="language">
    /// Language of the calling process (ADR 0104), read at each request: the operation shown in the
    /// prompt is written in it, as a polkit action message is translated for its caller. English when
    /// <see langword="null"/>.
    /// </param>
    public AdministratorGate(
        Func<IAdministratorAuthenticationAgent>? agent,
        AppManifest caller,
        AuthenticatedPrincipal principal,
        bool isRootElevated,
        Func<System.Globalization.CultureInfo>? language = null)
    {
        _agent = agent;
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _isRootElevated = isRootElevated;
        _language = language ?? (static () => Kazo.Os.AppSdk.Globalization.HackLocale.DefaultLanguage);
    }

    /// <summary>Gets the session user running the app.</summary>
    public AuthenticatedPrincipal Principal => _principal;

    /// <summary>Gets the core's texts in the calling process's language, for the operation descriptions.</summary>
    public Microsoft.Extensions.Localization.IStringLocalizer Text => Strings.For(_language());

    /// <summary>Gets the calling process's language, for texts built outside <see cref="Text"/> (capability titles).</summary>
    public System.Globalization.CultureInfo Language => _language();

    /// <summary>Asks an administrator to authorize one operation; returns the administrator's login.</summary>
    /// <param name="operation">The operation in plain words, shown in the prompt.</param>
    /// <param name="action">Stable operation id written to <c>/var/log/auth.log</c>.</param>
    /// <param name="subject">Path or resource named in the denial.</param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    /// <exception cref="HackUnauthorizedAccessException">No agent, or no administrator authenticated.</exception>
    public async Task<string> AuthenticateAsync(string operation, string action, string subject, CancellationToken cancellationToken)
    {
        IAdministratorAuthenticationAgent agent = _agent?.Invoke()
            ?? throw new HackUnauthorizedAccessException("No administrator authentication agent is available on this host.", path: subject);
        AdministratorAuthenticationResult result = await agent.AuthenticateAsync(
            new AdministratorAuthenticationRequest(_caller, operation, action, _principal, _isRootElevated),
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? result.AdministratorLogin ?? "root"
            : throw new HackUnauthorizedAccessException($"Administrator authentication failed ({result.FailureReason}).", path: subject);
    }
}
