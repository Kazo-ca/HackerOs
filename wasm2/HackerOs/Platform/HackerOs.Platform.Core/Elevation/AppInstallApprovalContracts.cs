using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Elevation;

/// <summary>What an administrator approves in the second step of ADR 0189 §4.</summary>
public enum AppInstallApprovalOperation
{
    /// <summary>A new app.</summary>
    Install = 0,

    /// <summary>A new version of an installed app.</summary>
    Update = 1,

    /// <summary>The removal of an installed app.</summary>
    Remove = 2
}

/// <summary>
/// Step 2 of ADR 0189 §4: after an administrator signed in (step 1), the window shows the app, its
/// source and trust level, and the capabilities it asks for; the administrator approves or refuses.
/// </summary>
/// <param name="AppId">App identifier.</param>
/// <param name="Name">App name.</param>
/// <param name="Version">Version installed, updated to or removed.</param>
/// <param name="PreviousVersion">Version an update replaces, or <see langword="null"/>.</param>
/// <param name="SizeBytes">Size of the package's files, or <see langword="null"/> for a removal.</param>
/// <param name="Source">The source, such as <c>github:Kazo-ca/HackerOs-App-Webamp@1.0.0</c>.</param>
/// <param name="Trust">Trust level of the source (ADR 0189 §2).</param>
/// <param name="Capabilities">Capabilities the app declares; dangerous ones are shown first (ADR 0049).</param>
/// <param name="Operation">Install, update or removal.</param>
/// <param name="SystemWide">Whether it is for every user (<c>/usr/lib/hackeros/apps</c>) or for the requesting user only.</param>
/// <param name="AdministratorLogin">The administrator who signed in at step 1.</param>
/// <param name="RequestedBy">Session user who asked, or <see langword="null"/>.</param>
/// <param name="Caller">App that asked (the Store, <c>hackpkg</c>, Settings).</param>
public sealed record AppInstallApprovalRequest(
    string AppId,
    string Name,
    string Version,
    string? PreviousVersion,
    long? SizeBytes,
    string Source,
    AppSourceTrust Trust,
    IReadOnlyList<string> Capabilities,
    AppInstallApprovalOperation Operation,
    bool SystemWide,
    string AdministratorLogin,
    AuthenticatedPrincipal? RequestedBy,
    AppManifest Caller)
{
    /// <summary>Gets why a personal install needs an administrator, when the block list stopped it (ADR 0189 §5).</summary>
    public AppBlockReason? BlockReason { get; init; }
}

/// <summary>How the approval window ended.</summary>
public enum AppInstallApprovalOutcome
{
    /// <summary>The administrator approved.</summary>
    Approved = 0,

    /// <summary>The administrator refused, pressed Escape or closed the window.</summary>
    Refused = 1,

    /// <summary>No interactive surface could show the window: nothing is installed.</summary>
    Unavailable = 2
}

/// <summary>
/// The window of step 2 (ADR 0189 §4), drawn by the platform shell above every window like the
/// authentication prompt (ADR 0055 §5); shared by the Store, <c>hackpkg</c> and Settings.
/// </summary>
public interface IAppInstallApprovalPrompt
{
    /// <summary>Shows the window and waits until it closes.</summary>
    /// <param name="request">What to show.</param>
    /// <param name="cancellationToken">Cancels the window; it then ends as <see cref="AppInstallApprovalOutcome.Refused"/>.</param>
    Task<AppInstallApprovalOutcome> RequestAsync(AppInstallApprovalRequest request, CancellationToken cancellationToken = default);
}
