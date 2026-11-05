using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Shell;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// Turns the terminal's <see cref="KazoCredentialPrompt"/> into the three questions the
/// interpreter asks (ADR 0056, section 7): <c>sudo</c>'s ticket, the one-shot approval after a
/// permission denial (ADR 0042) and the text-mode approval of dangerous capabilities (ADR 0049,
/// section 5). The terminal reads; this class verifies through the OS. Its texts are in the
/// shell's language (ADR 0104), as <c>sudo</c> prints its prompts in the locale of its process.
/// </summary>
internal sealed class ShellHostPrompts : IDangerousCapabilityApprovalPrompt
{
    private readonly KazoCredentialPrompt _prompt;
    private readonly IElevationService _elevation;
    private readonly Func<System.Globalization.CultureInfo> _language;

    /// <summary>Creates the adapter for one hosted shell.</summary>
    /// <param name="prompt">The terminal's prompt.</param>
    /// <param name="elevation">Verifies the credentials and mints tickets (ADR 0042).</param>
    /// <param name="language">The shell's current language, read at each prompt; English when <see langword="null"/>.</param>
    public ShellHostPrompts(KazoCredentialPrompt prompt, IElevationService elevation, Func<System.Globalization.CultureInfo>? language = null)
    {
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _language = language ?? (static () => Kazo.Os.AppSdk.Globalization.KazoLocale.DefaultLanguage);
    }

    /// <summary>Gets the core's texts in the shell's current language.</summary>
    private IStringLocalizer Text => Strings.For(_language());

    /// <summary>
    /// <c>sudo</c>'s credential window (ADR 0051, section 4): an administrator's credentials mint
    /// a single-use <c>SudoRoot</c> ticket. A wrong password prints "Sorry, try again." as
    /// <c>sudo</c> does, then the interpreter reports the failure.
    /// </summary>
    public async Task<ShellElevation?> RequestSudoAsync(string description, CancellationToken cancellationToken)
    {
        KazoCredential? credential = await _prompt.ReadCredentialAsync(
            new KazoCredentialRequest(Text["Prompt.Sudo.Title"], description), cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return null;
        }

        ElevationRequestResult result = await _elevation
            .RequestSudoAsync(credential.LoginName, credential.Password, cancellationToken).ConfigureAwait(false);
        if (result is { Success: true, Token: Guid token })
        {
            return new ShellElevation(token, ElevationScope.SudoRoot);
        }

        await _prompt.WriteLineAsync(Text["Prompt.SorryTryAgain"], KazoPromptLineKind.Error, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// ADR 0042: offers an administrator's one-shot approval when a lone command failed on a
    /// permission denial; the interpreter reruns it with the ticket.
    /// </summary>
    public async Task<ShellElevation?> RequestPermissionApprovalAsync(ShellPermissionDenial denial, CancellationToken cancellationToken)
    {
        FileSystemError denied = denial.Error;
        KazoCredential? credential = await _prompt.ReadCredentialAsync(
            new KazoCredentialRequest(
                Text["Prompt.PermissionApproval.Title"],
                Text["Prompt.PermissionApproval.Description", denial.CommandName, denied.Code, denied.Path?.ToString() ?? string.Empty]),
            cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return null;
        }

        ElevationRequestResult result = await _elevation.RequestCapabilityApprovalAsync(
            credential.LoginName, credential.Password, denied.RequiredCapability!, denied.Path, cancellationToken).ConfigureAwait(false);
        if (result is { Success: true, Token: Guid token })
        {
            return new ShellElevation(token, ElevationScope.CapabilityApproval);
        }

        await _prompt.WriteLineAsync(Text["Prompt.SorryTryAgain"], KazoPromptLineKind.Error, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Text mode of the dangerous-capability approval (ADR 0049, section 5) for a third-party
    /// command: a warning banner and the capability list in the terminal, a yes / no / cancel
    /// choice, then an administrator's name and password, like <c>sudo</c>. The prompt decides
    /// nothing: credentials go to <see cref="DangerousCapabilityApprovalRequest.ApproveAsync"/>,
    /// which verifies them, writes <c>/etc/kazoos/app-permissions.config</c> and logs to
    /// <c>/var/log/auth.log</c>.
    /// </summary>
    public async Task<DangerousCapabilityApprovalOutcome> RequestAsync(
        DangerousCapabilityApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AppManifest app = request.Manifest;
        IStringLocalizer text = Text;

        await WriteAsync(text["Prompt.Dangerous.Banner"], KazoPromptLineKind.Warning).ConfigureAwait(false);
        await WriteAsync(
            text["Prompt.Dangerous.Request", app.Terminal?.Name ?? app.Name, app.Id, app.PublisherId, app.Version],
            KazoPromptLineKind.Warning).ConfigureAwait(false);
        foreach (CapabilityDescription capability in request.Capabilities.Select(description =>
                     Kazo.Os.Platform.Core.Policy.CapabilityText.Localize(description, _language())))
        {
            await WriteAsync($"  - {capability.Title} [{capability.Capability}]", KazoPromptLineKind.Warning).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(capability.RiskDescription))
            {
                await WriteAsync($"    {capability.RiskDescription}", KazoPromptLineKind.Warning).ConfigureAwait(false);
            }
        }

        await WriteAsync(text["Prompt.Dangerous.AuditNotice"], KazoPromptLineKind.Info).ConfigureAwait(false);

        KazoConfirmation choice = await _prompt.ConfirmAsync(new KazoConfirmationRequest(
            text["Prompt.Dangerous.Title"],
            text["Prompt.Dangerous.Question", app.Name]), cancellationToken).ConfigureAwait(false);

        if (choice == KazoConfirmation.No)
        {
            await WriteAsync(text["Prompt.Dangerous.Refused"], KazoPromptLineKind.Info).ConfigureAwait(false);
            return DangerousCapabilityApprovalOutcome.Refused;
        }

        if (choice != KazoConfirmation.Yes)
        {
            await WriteAsync(text["Prompt.Dangerous.Dismissed"], KazoPromptLineKind.Info).ConfigureAwait(false);
            return DangerousCapabilityApprovalOutcome.Dismissed;
        }

        while (true)
        {
            KazoCredential? credential = await _prompt.ReadCredentialAsync(new KazoCredentialRequest(
                text["Prompt.Dangerous.Title"],
                text["Prompt.Dangerous.CredentialDescription", app.Name]), cancellationToken).ConfigureAwait(false);
            if (credential is null)
            {
                await WriteAsync(text["Prompt.Dangerous.Dismissed"], KazoPromptLineKind.Info).ConfigureAwait(false);
                return DangerousCapabilityApprovalOutcome.Dismissed;
            }

            DangerousCapabilityApprovalAttempt attempt = await request.ApproveAsync(
                credential.LoginName, credential.Password, cancellationToken).ConfigureAwait(false);
            if (attempt.Succeeded)
            {
                await WriteAsync(text["Prompt.Dangerous.Approved"], KazoPromptLineKind.Info).ConfigureAwait(false);
                return DangerousCapabilityApprovalOutcome.Approved;
            }

            if (attempt.RemainingAttempts <= 0)
            {
                await WriteAsync(
                    text["Prompt.Dangerous.TooManyAttempts", DangerousCapabilityApprovalRequest.MaxAttempts],
                    KazoPromptLineKind.Error).ConfigureAwait(false);
                return DangerousCapabilityApprovalOutcome.Dismissed;
            }

            await WriteAsync(text["Prompt.SorryTryAgain"], KazoPromptLineKind.Error).ConfigureAwait(false);
        }

        Task WriteAsync(string text, KazoPromptLineKind kind) => _prompt.WriteLineAsync(text, kind, cancellationToken);
    }
}
