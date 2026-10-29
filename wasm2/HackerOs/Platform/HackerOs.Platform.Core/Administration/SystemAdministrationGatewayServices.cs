using HackerOs.Platform.Core.Backup;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Power;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Platform.Core.Settings;

namespace HackerOs.Platform.Core.Administration;

/// <summary>
/// Platform services behind the administration gateways of ADR 0056, sections 8 to 11
/// (<c>HackSettings.OpenSystemAsync</c>, <c>HackPermissionAdministration</c>,
/// <c>HackServerConnection</c>, <c>HackSystem</c>, <c>HackStorageDiagnostics</c>), and the
/// <c>HackSystemBackup</c> of ADR 0099. Every service is
/// resolved lazily, on the app's first call: several depend, through the lifecycle orchestrator or
/// the session, on the very factory that receives this bundle. A <see langword="null"/> member
/// leaves its gateway unsupported (or, for the server, "not connected").
/// </summary>
/// <param name="Settings">OS settings files behind <c>OpenSystemAsync</c>.</param>
/// <param name="Permissions">Dangerous-capability administration.</param>
/// <param name="Server">Optional server connection and sync services.</param>
/// <param name="Power">Reboot and factory reset.</param>
/// <param name="Storage">Raw storage snapshot.</param>
/// <param name="AuthenticationAgent">OS authentication agent (ADR 0055, section 5); <see langword="null"/> refuses every administrator operation.</param>
/// <param name="Backup">Whole-machine backup and restore (ADR 0099); <see langword="null"/> on a host without one.</param>
public sealed record SystemAdministrationGatewayServices(
    Func<IOsSettingsService>? Settings = null,
    Func<AppPermissionAdministration>? Permissions = null,
    ServerConnectionGatewayServices? Server = null,
    Func<SystemPowerService>? Power = null,
    Func<IStorageInspector>? Storage = null,
    Func<IAdministratorAuthenticationAgent>? AuthenticationAgent = null,
    Func<SystemBackupService>? Backup = null)
{
    /// <summary>
    /// Gets the password service behind <c>HackUserAccounts</c> (ADR 0120); <see langword="null"/> on a
    /// host without local accounts. An <c>init</c> property rather than a parameter keeps the record's
    /// constructor of SDK 1.0 unchanged.
    /// </summary>
    public Func<LocalPasswordService>? Passwords { get; init; }

    /// <summary>
    /// Gets the service that adds and deletes accounts behind <c>HackUserAccounts</c> (ADR 0169);
    /// <see langword="null"/> leaves those calls unsupported.
    /// </summary>
    public Func<LocalAccountService>? Accounts { get; init; }

    /// <summary>
    /// Gets the passkey service behind <c>HackUserAccounts</c> (ADR 0168); <see langword="null"/> on a
    /// host without WebAuthn, where the session user has no passkey to manage.
    /// </summary>
    public Func<PasskeyService>? Passkeys { get; init; }

    /// <summary>
    /// Gets the service behind <c>HackComponentProviders</c> (ADR 0156); <see langword="null"/> leaves
    /// the gateway unsupported.
    /// </summary>
    public Func<ComponentProviders.ComponentProviderService>? ComponentProviders { get; init; }

    /// <summary>
    /// Gets the service behind <c>HackFileMenuEntries</c> (ADR 0159 §8); <see langword="null"/> leaves
    /// the gateway unsupported.
    /// </summary>
    public Func<ContextMenus.FileContextMenuAdministration>? FileMenuEntries { get; init; }

    /// <summary>
    /// Gets the secondary screens behind <c>HackScreens</c> (ADR 0184); <see langword="null"/> on a
    /// host without screens leaves the gateway unsupported.
    /// </summary>
    public Func<IScreenControl>? Screens { get; init; }

    /// <summary>
    /// Gets the change list behind <c>HackWhatsNew</c> (ADR 0186); <see langword="null"/> on a host
    /// that serves none leaves the gateway with an empty list.
    /// </summary>
    public Func<WhatsNew.WhatsNewService>? WhatsNew { get; init; }

    /// <summary>
    /// Gets the approval window of the dangerous capabilities (ADR 0049), which a third-party
    /// component provider goes through (ADR 0156 §3); <see langword="null"/> refuses such a choice.
    /// </summary>
    public Func<IDangerousCapabilityApprovalPrompt>? ApprovalPrompt { get; init; }
}
