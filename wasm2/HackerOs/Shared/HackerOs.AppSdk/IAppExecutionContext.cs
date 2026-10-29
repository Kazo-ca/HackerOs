using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.Settings;
using HackerOs.AppSdk.Clipboard;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.AppSdk;

/// <summary>
/// Provides identity, granted policy, and scoped gateway access for one app instance.
/// </summary>
/// <remarks>
/// This context deliberately does not expose the root service provider, raw repositories,
/// unrestricted <c>IJSRuntime</c>, or any other concrete platform implementation. Every gateway
/// property is a narrow, capability-checked surface bound to this instance only. App code never
/// constructs this context itself; only the trusted platform-owned factory does
/// (`P1-EXEC-007`).
/// </remarks>
public interface IAppExecutionContext
{
    /// <summary>Gets the validated manifest for the running application.</summary>
    AppManifest Manifest { get; }

    /// <summary>Gets the unique identifier for this running instance.</summary>
    Guid InstanceId { get; }

    /// <summary>Gets the authenticated user identifier that initiated the operation.</summary>
    string UserId { get; }

    /// <summary>
    /// Gets the acting user's canonical home directory (<c>/home/{login}</c>), used by
    /// <see cref="HackExecutionContext"/> as the default working directory and by
    /// <see cref="IO.HackPath.GetFullPath(string)"/> to expand <c>~</c>, and as the root of the user settings layer
    /// (<c>~/.config/&lt;appId&gt;/</c>, ADR 0044). Defaults to
    /// <c>/home/{UserId}</c> so existing implementations keep compiling; the trusted platform
    /// factory overrides it with the principal's real home path (ADR 0043).
    /// </summary>
    string HomeDirectory => $"/home/{UserId}";

    /// <summary>
    /// Gets whether this one launch runs as full root through <c>sudo</c> (ADR 0042). The effective
    /// user is then <c>root</c>, whose home is <c>/root</c>: per-user data written on its behalf,
    /// such as the trash (ADR 0052), goes there rather than under <see cref="HomeDirectory"/>.
    /// Defaults to <see langword="false"/> so existing implementations keep compiling; the trusted
    /// platform factory sets it from the launch.
    /// </summary>
    bool IsRootElevated => false;

    /// <summary>
    /// Gets the session user's login name, read by <see cref="HackUser"/> (ADR 0056, section 1).
    /// Defaults to the process environment's user name so existing implementations keep compiling;
    /// the trusted platform factory sets it from the authenticated principal.
    /// </summary>
    string UserLoginName => Environment.UserName;

    /// <summary>
    /// Gets the session user's display name, read by <see cref="HackUser"/> (ADR 0056, section 1).
    /// Defaults to <see cref="UserLoginName"/>; the trusted platform factory sets it from the
    /// authenticated principal.
    /// </summary>
    string UserDisplayName => UserLoginName;

    /// <summary>Gets the acting user's authority for this operation.</summary>
    AppAuthority UserAuthority { get; }

    /// <summary>Gets the capabilities granted to this app instance by OS policy.</summary>
    IReadOnlySet<string> GrantedCapabilities { get; }

    /// <summary>Gets the session this app instance belongs to.</summary>
    SessionId SessionId { get; }

    /// <summary>Gets the process record hosting this app instance.</summary>
    ProcessId ProcessId { get; }

    /// <summary>
    /// Gets the cancellation token owned by this app instance's process, cancelled on close, kill,
    /// logout, or shutdown.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Raised once when the OS asks the process to stop (<c>SIGTERM</c>, ADR 0069, section 1), after
    /// it moved to <c>Stopping</c> and before <see cref="CancellationToken"/> is cancelled. The data
    /// carries the reason and the simulated deadline at which the OS kills the process. Defaults to
    /// an event that is never raised so existing implementations keep compiling; the trusted platform
    /// factory overrides it. Apps read it through <c>HackProcess.Current.StopRequested</c>.
    /// </summary>
    event EventHandler<Diagnostics.HackStopRequestedEventArgs>? StopRequested
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Gets the stop request this process already received, or <see langword="null"/> (ADR 0069), for
    /// code that subscribes to <see cref="StopRequested"/> after the fact. Defaults to
    /// <see langword="null"/>; the trusted platform factory overrides it.
    /// </summary>
    Diagnostics.HackStopRequestedEventArgs? StopRequest => null;

    /// <summary>Gets a capability checker scoped to this app/user, exposing no other app's grants.</summary>
    ICapabilityChecker Capabilities { get; }

    /// <summary>Gets this instance's authorized, capability-checked filesystem gateway.</summary>
    IAppFileSystemGateway FileSystem { get; }

    /// <summary>Gets this instance's typed event bus gateway.</summary>
    IAppEventGateway Events { get; }

    /// <summary>Gets this instance's authorized notification-posting gateway.</summary>
    IAppNotificationGateway Notifications { get; }

    /// <summary>
    /// Gets this instance's structured diagnostic logging gateway. Entries land in
    /// <c>/var/log/apps/&lt;appId&gt;.log</c> (ADR 0045); reading logs back is plain file access.
    /// </summary>
    IAppLoggingGateway Logging { get; }

    /// <summary>Gets this instance's read-only deterministic simulation clock gateway.</summary>
    IAppClockGateway Clock { get; }

    /// <summary>Gets this instance's authorized process/job gateway.</summary>
    IAppProcessGateway Processes { get; }

    /// <summary>
    /// Gets this instance's authorized ability to launch another installed application.
    /// Defaults to an unsupported gateway so existing <see cref="IAppExecutionContext"/>
    /// implementations that predate this member keep compiling; the trusted platform factory
    /// overrides it with a real implementation.
    /// </summary>
    IAppIntentGateway Intents => new UnsupportedAppIntentGateway();

    /// <summary>
    /// Gets the intent this process was started for (ADR 0096, section 5): an app-defined intent
    /// with its payload, or the OS intent that launched it. <see langword="null"/> for a plain launch.
    /// Defaults to <see langword="null"/> so existing implementations keep compiling; the trusted
    /// platform factory sets it from the launch.
    /// </summary>
    AppIntentDelivery? Intent => null;

    /// <summary>
    /// Raised when the OS hands an intent to this already running instance instead of starting a new
    /// one: a <see cref="AppManifest.SingleInstancePerUser"/> window app or a service, like a second
    /// <c>GApplication</c> activation (ADR 0096, section 5). Defaults to an event that is never
    /// raised; the trusted platform factory overrides it.
    /// </summary>
    event EventHandler<AppIntentDelivery>? IntentReceived
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Gets this instance's authorized directory-change watch gateway. Defaults to an unsupported
    /// gateway so existing <see cref="IAppExecutionContext"/> implementations that predate this member
    /// keep compiling; the trusted platform factory overrides it with a real implementation.
    /// </summary>
    IAppFileSystemWatchGateway Watch => new UnsupportedAppFileSystemWatchGateway();

    /// <summary>
    /// Gets this instance's authorized ability to start, stop, or reconfigure other
    /// <see cref="AppKind.Service"/> apps. Defaults to an unsupported gateway so existing
    /// <see cref="IAppExecutionContext"/> implementations that predate this member keep compiling;
    /// the trusted platform factory overrides it with a real implementation.
    /// </summary>
    IAppServiceControlGateway Services => new UnsupportedAppServiceControlGateway();

    /// <summary>
    /// Gets the channel a running service uses to tell the OS it is ready and how healthy it is
    /// (ADR 0097, section 2; <see cref="ServiceAppBase.ReportHealth"/>). Defaults to a gateway that
    /// drops every message, so contexts that predate this member keep compiling; the trusted
    /// platform factory gives a service process the real one.
    /// </summary>
    IAppServiceNotifyGateway ServiceNotify => NullAppServiceNotifyGateway.Instance;

    /// <summary>
    /// Gets this instance's permission-error notification gateway. Defaults to a no-op gateway so
    /// existing <see cref="IAppExecutionContext"/> implementations that predate this member keep
    /// compiling; the trusted platform factory overrides it with a real implementation.
    /// </summary>
    IAppPermissionErrorGateway PermissionErrors => new NoOpAppPermissionErrorGateway();

    /// <summary>
    /// Gets this instance's live one-shot elevation ticket set (`ADR 0042`). An app grants into it
    /// only after the platform's admin-approval dialog verifies an enabled Administrator; a ticket
    /// unblocks exactly the next matching (capability, path) filesystem denial, then is consumed.
    /// Defaults to a fresh, always-empty instance so existing <see cref="IAppExecutionContext"/>
    /// implementations that predate this member keep compiling; the trusted platform factory
    /// overrides it with the same live instance <see cref="HackerOs.App.Abstractions.AppOperationContext"/> reads.
    /// </summary>
    OneShotElevationGrantSet OneShotElevation => new();

    /// <summary>
    /// Gets this instance's own process environment: arguments, working directory, environment
    /// variables, user and machine names (ADR 0050, section 6), read by <c>HackEnvironment</c>.
    /// Defaults to a detached, read-only view (home directory, <c>HOME</c> only) so existing
    /// <see cref="IAppExecutionContext"/> implementations that predate this member keep compiling;
    /// the trusted platform factory overrides it with the live process entry.
    /// </summary>
    IAppEnvironmentGateway Environment => new DetachedAppEnvironmentGateway(this);

    /// <summary>
    /// Gets the language of this instance's texts (ADR 0104, the "language gateway"): <c>en</c> or
    /// <c>fr</c>, read from its own process environment (<c>LC_ALL</c>, <c>LC_MESSAGES</c>, then
    /// <c>LANG</c>) at every access, never from the browser tab's shared thread culture.
    /// <see cref="Globalization.HackLocalization.GetLocalizer{TResource}(IAppExecutionContext?)"/>
    /// looks texts up in this culture.
    /// </summary>
    System.Globalization.CultureInfo Culture =>
        Globalization.HackLocale.ResolveLanguage(Environment.GetVariable);

    /// <summary>
    /// Gets the culture of this instance's dates and numbers (ADR 0104, section 1): the full locale
    /// of <c>LC_TIME</c> (or <c>LC_ALL</c>, then <c>LANG</c>), e.g. <c>fr-CA</c>, which can differ
    /// from <see cref="Culture"/>'s language.
    /// </summary>
    System.Globalization.CultureInfo FormatCulture =>
        Globalization.HackLocale.ResolveFormatCulture(Environment.GetVariable);

    /// <summary>
    /// Gets the time zone this instance shows its dates in (ADR 0193): the zone named by its own
    /// <c>TZ</c> variable when set, otherwise the session's zone (Settings &gt; Date and time). This
    /// default, for hosts without a session zone, falls back to the device's zone.
    /// <see cref="Globalization.HackTimeZone"/> converts the OS's UTC time with it.
    /// </summary>
    TimeZoneInfo TimeZone =>
        Globalization.HackTimeZone.Resolve(Environment.GetVariable(Globalization.HackTimeZone.Variable));

    /// <summary>
    /// Gets this instance's network stack: resolution, ping, HTTP and TCP probes, with the OS's
    /// real-network fallback (ADR 0056, section 6), read by <c>HackDns</c>, <c>HackPing</c>,
    /// <c>HackHttpClient</c> and <c>HackTcpClient</c>. Defaults to an unsupported gateway so existing
    /// <see cref="IAppExecutionContext"/> implementations that predate this member keep compiling;
    /// the trusted platform factory overrides it with a real implementation.
    /// </summary>
    Net.IAppNetworkGateway Network => new Net.UnsupportedAppNetworkGateway();

    /// <summary>
    /// Gets this instance's read access to the app catalog, behind <see cref="HackApps"/> (ADR 0056,
    /// section 2). Defaults to an unsupported gateway so existing implementations keep compiling; the
    /// trusted platform factory overrides it with a real implementation.
    /// </summary>
    IAppCatalogGateway Catalog => new UnsupportedAppCatalogGateway();

    /// <summary>
    /// Gets this instance's uninstall/reinstall gateway, behind <see cref="HackAppManagement"/>
    /// (ADR 0056, section 3; capability <c>apps.manage</c>). Defaults to an unsupported gateway so
    /// existing implementations keep compiling; the trusted platform factory overrides it.
    /// </summary>
    IAppManagementGateway AppManagement => new UnsupportedAppManagementGateway();

    /// <summary>
    /// Gets this instance's access to the OS's own settings documents, behind
    /// <see cref="HackSettings.OpenSystemAsync(string, CancellationToken)"/> (ADR 0056, section 8; no
    /// capability, file permissions decide). Defaults to an unsupported gateway so existing
    /// implementations keep compiling; the trusted platform factory overrides it.
    /// </summary>
    IAppSystemSettingsGateway SystemSettings => UnsupportedAdministrationGateways.SystemSettings;

    /// <summary>
    /// Gets this instance's dangerous-capability administration gateway, behind
    /// <see cref="HackPermissionAdministration"/> (ADR 0056, section 9; capability
    /// <c>permissions.manage</c>). Defaults to an unsupported gateway; the trusted platform factory
    /// overrides it.
    /// </summary>
    IAppPermissionAdministrationGateway PermissionAdministration => UnsupportedAdministrationGateways.Permissions;

    /// <summary>
    /// Gets this instance's optional-server connection gateway, behind
    /// <see cref="HackServerConnection"/> (ADR 0056, section 10; capability
    /// <c>server.connection.manage</c>). Defaults to an unsupported gateway; the trusted platform
    /// factory overrides it.
    /// </summary>
    IAppServerConnectionGateway ServerConnection => UnsupportedAdministrationGateways.ServerConnection;

    /// <summary>
    /// Gets this instance's reboot and factory-reset gateway, behind <see cref="HackSystem"/>
    /// (ADR 0056, section 11; capability <c>system.power</c> plus an administrator). Named
    /// <c>SystemPower</c> rather than <c>System</c> so it never hides the <c>System</c> namespace in
    /// implementing classes. Defaults to an unsupported gateway; the trusted platform factory
    /// overrides it.
    /// </summary>
    IAppSystemPowerGateway SystemPower => UnsupportedAdministrationGateways.SystemPower;

    /// <summary>
    /// Gets this instance's raw storage inspection gateway, behind
    /// <see cref="HackStorageDiagnostics"/> (ADR 0056, section 11; capability
    /// <c>system.storage.inspect</c>). Defaults to an unsupported gateway; the trusted platform
    /// factory overrides it.
    /// </summary>
    IAppStorageDiagnosticsGateway StorageDiagnostics => UnsupportedAdministrationGateways.StorageDiagnostics;

    /// <summary>
    /// Gets this instance's whole-machine backup gateway, behind <see cref="HackSystemBackup"/>
    /// (ADR 0099, section 1; capability <c>system.backup</c> plus an administrator). Defaults to an
    /// unsupported gateway; the trusted platform factory overrides it.
    /// </summary>
    IAppSystemBackupGateway SystemBackup => UnsupportedAdministrationGateways.SystemBackup;

    /// <summary>
    /// Gets this instance's local accounts gateway, behind <see cref="HackUserAccounts"/> (ADR 0120;
    /// own password without capability, another account's with <c>users.manage</c> plus an
    /// administrator). Defaults to an unsupported gateway; the trusted platform factory overrides it.
    /// </summary>
    IAppUserAccountsGateway UserAccounts => UnsupportedAdministrationGateways.UserAccounts;

    /// <summary>
    /// Gets this instance's gateway choosing which app provides the OS's file view and file dialogs,
    /// behind <see cref="HackComponentProviders"/> (ADR 0156; capability <c>apps.manage</c> plus an
    /// administrator). Defaults to an unsupported gateway; the trusted platform factory overrides it.
    /// </summary>
    IAppComponentProviderAdministrationGateway ComponentProviderAdministration => UnsupportedAdministrationGateways.ComponentProviders;

    /// <summary>
    /// Gets this instance's gateway listing and hiding the entries apps added to the file menus, behind
    /// <see cref="HackFileMenuEntries"/> (ADR 0159 §8; capability <c>apps.manage</c>). Defaults to an
    /// unsupported gateway; the trusted platform factory overrides it.
    /// </summary>
    IAppFileMenuEntryAdministrationGateway FileMenuEntryAdministration => UnsupportedAdministrationGateways.FileMenuEntries;

    /// <summary>
    /// Gets this instance's gateway to the secondary screens of the session, behind
    /// <see cref="HackScreens"/> (ADR 0184; capability <c>screens.manage</c>). Defaults to an
    /// unsupported gateway; the trusted platform factory overrides it on a host with screens.
    /// </summary>
    IAppScreensGateway Screens => UnsupportedAdministrationGateways.Screens;

    /// <summary>
    /// Gets this instance's ability to host interactive shells (<c>shell.host</c>, ADR 0056
    /// section 7), read by <see cref="Shell.HackShell"/>. Defaults to an unsupported gateway so
    /// existing <see cref="IAppExecutionContext"/> implementations that predate this member keep
    /// compiling; the trusted platform factory overrides it with a real implementation.
    /// </summary>
    Shell.IAppShellHostGateway ShellHost => new UnsupportedAppShellHostGateway();

    /// <summary>
    /// Gets this instance's view of the mounted disks' space, behind <see cref="IO.HackDriveInfo"/>
    /// (ADR 0057, section 7; no capability). Defaults to an unsupported gateway so existing
    /// implementations keep compiling; the trusted platform factory overrides it.
    /// </summary>
    IO.IAppDriveGateway Drives => new UnsupportedAppDriveGateway();

    /// <summary>
    /// Gets the OS's cross-origin isolation, behind <see cref="Diagnostics.HackCrossOriginIsolation"/>
    /// (ADR 0146; no capability). Defaults to an unsupported gateway so existing implementations keep
    /// compiling; the trusted platform factory overrides it.
    /// </summary>
    Diagnostics.IAppCrossOriginIsolationGateway CrossOriginIsolation => new UnsupportedAppCrossOriginIsolationGateway();

    /// <summary>
    /// Gets this instance's view of the session clipboard, behind <see cref="HackClipboard"/> (ADR 0056,
    /// section 12; capabilities <c>clipboard.read</c> and <c>clipboard.write</c>). Defaults to an
    /// unsupported gateway so existing implementations keep compiling; the trusted platform factory
    /// overrides it.
    /// </summary>
    IAppClipboardGateway Clipboard => new UnsupportedAppClipboardGateway();

    /// <summary>
    /// Gets this instance's browser database, behind <c>HackWebDb</c> of the web SDK (ADR 0073 section 3,
    /// ADR 0088): the IndexedDB database <c>hackweb.&lt;app id&gt;</c> of the host browser. Only an app of
    /// kind <c>Web</c> gets a working one. Defaults to an unsupported gateway so existing implementations
    /// keep compiling; the trusted platform factory overrides it.
    /// </summary>
    WebData.IAppWebDatabaseGateway WebDatabase => UnsupportedAppWebDatabaseGateway.Instance;

    /// <summary>
    /// Gets the instance's standard streams (ADR 0064), behind <see cref="HackConsole"/>: the
    /// terminal, pipe or file of the shell that started it, or <c>/dev/null</c> when it was
    /// started from the desktop. Defaults to a detached console so existing implementations keep
    /// compiling.
    /// </summary>
    IAppConsoleGateway Console => DetachedAppConsoleGateway.Instance;

    /// <summary>
    /// Gets the session user's keyring as this app sees it, behind <see cref="Keyring.HackKeyring"/>
    /// (ADR 0100 section 2; own entries without capability, every app's with <c>keyring.read-all</c>).
    /// Defaults to a locked gateway so existing implementations keep compiling; the trusted platform
    /// factory overrides it.
    /// </summary>
    HackerOs.AppSdk.Keyring.IAppKeyringGateway Keyring => HackerOs.AppSdk.Keyring.UnsupportedAppKeyringGateway.Instance;

    /// <summary>
    /// Gets the session's effective accessibility preferences, behind
    /// <see cref="Accessibility.HackAccessibility"/> (ADR 0105 section 3; no capability). Defaults to a
    /// gateway answering the defaults so existing implementations keep compiling; the trusted platform
    /// factory overrides it.
    /// </summary>
    HackerOs.AppSdk.Accessibility.IAppAccessibilityGateway Accessibility => HackerOs.AppSdk.Accessibility.DefaultAppAccessibilityGateway.Instance;

    /// <summary>
    /// Gets the app's background tasks, behind <see cref="Threading.HackBackground"/> (ADR 0128). The
    /// trusted platform factory runs each one as a child process (in a Web Worker when the host has
    /// some); this default runs it at once on the calling thread, with the same serialization, so an
    /// app's unit tests exercise the same contract.
    /// </summary>
    HackerOs.AppSdk.Threading.IAppBackgroundGateway Background => new HackerOs.AppSdk.Threading.InlineAppBackgroundGateway(this);
}

/// <summary>
/// Defaults of the administration gateways (ADR 0056, sections 8 to 11) for contexts that don't wire
/// them up: every call throws <see cref="NotSupportedException"/>.
/// </summary>
public static class UnsupportedAdministrationGateways
{
    private const string Message = "This IAppExecutionContext implementation does not provide this administration gateway.";

    /// <summary>Unsupported <see cref="IAppSystemSettingsGateway"/>.</summary>
    public static IAppSystemSettingsGateway SystemSettings { get; } = new UnsupportedSystemSettings();

    /// <summary>Unsupported <see cref="IAppPermissionAdministrationGateway"/>.</summary>
    public static IAppPermissionAdministrationGateway Permissions { get; } = new UnsupportedPermissions();

    /// <summary>Unsupported <see cref="IAppServerConnectionGateway"/>.</summary>
    public static IAppServerConnectionGateway ServerConnection { get; } = new UnsupportedServerConnection();

    /// <summary>Unsupported <see cref="IAppSystemPowerGateway"/>.</summary>
    public static IAppSystemPowerGateway SystemPower { get; } = new UnsupportedSystemPower();

    /// <summary>Unsupported <see cref="IAppStorageDiagnosticsGateway"/>.</summary>
    public static IAppStorageDiagnosticsGateway StorageDiagnostics { get; } = new UnsupportedStorageDiagnostics();

    /// <summary>Unsupported <see cref="IAppSystemBackupGateway"/>.</summary>
    public static IAppSystemBackupGateway SystemBackup { get; } = new UnsupportedSystemBackup();

    /// <summary>Unsupported <see cref="IAppUserAccountsGateway"/>.</summary>
    public static IAppUserAccountsGateway UserAccounts { get; } = new UnsupportedUserAccounts();

    /// <summary>Unsupported <see cref="IAppComponentProviderAdministrationGateway"/>.</summary>
    public static IAppComponentProviderAdministrationGateway ComponentProviders { get; } = new UnsupportedComponentProviders();

    /// <summary>Unsupported <see cref="IAppFileMenuEntryAdministrationGateway"/>.</summary>
    public static IAppFileMenuEntryAdministrationGateway FileMenuEntries { get; } = new UnsupportedFileMenuEntries();

    /// <summary>Unsupported <see cref="IAppScreensGateway"/>: a host without secondary screens.</summary>
    public static IAppScreensGateway Screens { get; } = new UnsupportedScreens();

    private sealed class UnsupportedScreens : IAppScreensGateway
    {
        public bool IsSupported => false;

        public HackScreensState GetState() => throw new NotSupportedException(Message);

        public ValueTask<IAsyncDisposable> WatchAsync(Func<ValueTask> onChanged, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<HackScreenOpenStatus> OpenScreenAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<int> RestoreScreensAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask CloseScreenAsync(int number, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask IdentifyAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<bool> ToggleFullScreenAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<HackScreenPermission> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }

    private sealed class UnsupportedFileMenuEntries : IAppFileMenuEntryAdministrationGateway
    {
        public ValueTask<IReadOnlyList<HackFileMenuEntryInfo>> GetEntriesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask SetHiddenAsync(string appId, string name, bool hidden, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }

    private sealed class UnsupportedComponentProviders : IAppComponentProviderAdministrationGateway
    {
        public ValueTask<IReadOnlyList<HackComponentContractInfo>> GetOverviewAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask SetProviderAsync(IReadOnlyList<string> contracts, string? appId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }

    private sealed class UnsupportedUserAccounts : IAppUserAccountsGateway
    {
        public ValueTask<IReadOnlyList<HackUserAccount>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask ResetPasswordAsync(string loginName, string? newPassword, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }

    private sealed class UnsupportedSystemSettings : IAppSystemSettingsGateway
    {
        public AppSystemSettingsDocumentInfo Describe(string documentId) => throw new NotSupportedException(Message);

        public ValueTask<HackerOs.Simulation.Abstractions.Settings.LayeredConfigSnapshot> ReadAsync(
            string documentId, CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

        public ValueTask SetAsync(
            string documentId,
            IReadOnlyList<KeyValuePair<string, string>> values,
            HackerOs.Simulation.Abstractions.Settings.SettingLayer target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

        public ValueTask ResetAsync(
            string documentId,
            string qualifiedKey,
            HackerOs.Simulation.Abstractions.Settings.SettingLayer target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

        public ValueTask<IAsyncDisposable> WatchAsync(
            string documentId, Func<ValueTask> onChanged, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }

    private sealed class UnsupportedPermissions : IAppPermissionAdministrationGateway
    {
        public ValueTask<HackDangerousCapabilityPolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<IReadOnlyList<HackAppPermissionInfo>> GetOverviewAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask SetPolicyAsync(HackDangerousCapabilityPolicy policy, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask SetMachineDecisionAsync(
            string appId, string capability, HackPermissionDecision decision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask RemoveOwnDenialAsync(string appId, string capability, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }

    private sealed class UnsupportedServerConnection : IAppServerConnectionGateway
    {
        public ValueTask<HackServerConnectionState> GetStateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<HackServerConnectionState> ConnectAsync(
            Uri serverUrl, string login, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<HackServerConnectionState> CreateAccountAsync(
            Uri serverUrl, string login, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

        public ValueTask SyncAsync(HackSyncDomain domain, HackSyncDirection direction, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<IReadOnlyList<HackSyncConflict>> GetConflictsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask ShowConflictsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

        public ValueTask<HackSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask SetSyncPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<HackSyncStatus> SyncNowAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }

    private sealed class UnsupportedSystemPower : IAppSystemPowerGateway
    {
        public ValueTask RebootAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);

        public ValueTask FactoryResetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Message);
    }

    private sealed class UnsupportedSystemBackup : IAppSystemBackupGateway
    {
        public bool IsSupported => false;

        public ValueTask<HackBackupInfo> CreateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<HackBackupInfo?> RestoreAsync(HackBackupRestoreMode mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }

    private sealed class UnsupportedStorageDiagnostics : IAppStorageDiagnosticsGateway
    {
        public ValueTask<HackStorageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }
}

/// <summary>Default <see cref="IAppExecutionContext.WebDatabase"/> for contexts that don't wire one up.</summary>
file sealed class UnsupportedAppWebDatabaseGateway : WebData.IAppWebDatabaseGateway
{
    public static UnsupportedAppWebDatabaseGateway Instance { get; } = new();

    public string DatabaseName => throw Unsupported();

    public ValueTask OpenAsync(WebData.HackWebDbSchemaDefinition schema, CancellationToken cancellationToken = default) => throw Unsupported();

    public ValueTask<IReadOnlyList<string?>> ExecuteAsync(WebData.HackWebDbRequest request, CancellationToken cancellationToken = default) => throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new("This IAppExecutionContext implementation does not provide a browser database (only web apps have one).");
}

/// <summary>Default <see cref="IAppExecutionContext.Clipboard"/> for contexts that don't wire one up.</summary>
file sealed class UnsupportedAppClipboardGateway : IAppClipboardGateway
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public ValueTask<string?> GetTextAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    public ValueTask SetTextAsync(string text, CancellationToken cancellationToken = default) => throw Unsupported();

    public ValueTask<HackClipboardFileList?> GetFileDropListAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    public ValueTask SetFileDropListAsync(HackClipboardFileList files, CancellationToken cancellationToken = default) => throw Unsupported();

    public ValueTask ClearAsync(CancellationToken cancellationToken = default) => throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new("This IAppExecutionContext implementation does not provide a Clipboard gateway.");
}
/// <summary>Default <see cref="IAppExecutionContext.Drives"/> for contexts that don't wire one up.</summary>
file sealed class UnsupportedAppDriveGateway : IO.IAppDriveGateway
{
    public ValueTask<IReadOnlyList<IO.HackDriveSnapshot>> GetDrivesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a Drives gateway.");
}

/// <summary>Default <see cref="IAppExecutionContext.CrossOriginIsolation"/> for contexts that don't wire one up.</summary>
file sealed class UnsupportedAppCrossOriginIsolationGateway : Diagnostics.IAppCrossOriginIsolationGateway
{
    public ValueTask<Diagnostics.HackCrossOriginIsolationStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a CrossOriginIsolation gateway.");
}

/// <summary>Default <see cref="IAppExecutionContext.Catalog"/> for contexts that don't wire one up.</summary>
file sealed class UnsupportedAppCatalogGateway : IAppCatalogGateway
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public ValueTask<IReadOnlyList<HackAppInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a Catalog gateway.");

    public ValueTask<IReadOnlyList<HackAppInfo>> GetHandlersAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a Catalog gateway.");
}

/// <summary>Default <see cref="IAppExecutionContext.AppManagement"/> for contexts that don't wire one up.</summary>
file sealed class UnsupportedAppManagementGateway : IAppManagementGateway
{
    public ValueTask<IReadOnlyList<HackAppInstallInfo>> GetAllAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide an AppManagement gateway.");

    public ValueTask<IReadOnlyList<string>> UninstallAsync(
        string appId, HackInstallScope scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide an AppManagement gateway.");

    public ValueTask<IReadOnlyList<string>> ReinstallAsync(
        string appId, HackInstallScope scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide an AppManagement gateway.");
}

/// <summary>Default <see cref="IAppExecutionContext.ShellHost"/> for contexts that don't wire one up.</summary>
file sealed class UnsupportedAppShellHostGateway : Shell.IAppShellHostGateway
{
    public Task<Shell.IHackShell> StartAsync(Shell.HackShellOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a ShellHost gateway.");
}

/// <summary>Default <see cref="IAppExecutionContext.Environment"/> for contexts with no live process environment.</summary>
file sealed class DetachedAppEnvironmentGateway(IAppExecutionContext context) : IAppEnvironmentGateway
{
    public string CurrentDirectory => context.HomeDirectory;

    public string UserName => context.UserId;

    public string MachineName => "hackeros";

    public IReadOnlyList<string> GetCommandLineArgs() => [context.Manifest.Id];

    public string? GetVariable(string name) => name == "HOME" ? context.HomeDirectory : null;

    public IReadOnlyDictionary<string, string> GetVariables() =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = context.HomeDirectory };

    public void SetCurrentDirectory(VirtualPath directory) =>
        throw new NotSupportedException("This IAppExecutionContext implementation has no live process environment.");

    public void SetVariable(string name, string? value) =>
        throw new NotSupportedException("This IAppExecutionContext implementation has no live process environment.");
}

/// <summary>Default <see cref="IAppExecutionContext.PermissionErrors"/> for contexts that don't wire one up.</summary>
file sealed class NoOpAppPermissionErrorGateway : IAppPermissionErrorGateway
{
    public event EventHandler<AppPermissionErrorEventArgs>? PermissionDenied
    {
        add { }
        remove { }
    }
}

/// <summary>Default <see cref="IAppExecutionContext.Intents"/> for contexts that don't wire one up.</summary>
file sealed class UnsupportedAppIntentGateway : IAppIntentGateway
{
    public ValueTask<AppIntentLaunchResult> LaunchAsync(
        string appId, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide an Intents gateway.");

    public ValueTask<AppIntentOpenFileResult> OpenFileAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide an Intents gateway.");
}

/// <summary>Default <see cref="IAppExecutionContext.Watch"/> for contexts that don't wire one up.</summary>
file sealed class UnsupportedAppFileSystemWatchGateway : IAppFileSystemWatchGateway
{
    public ValueTask<ITopicChannelSubscription<FileSystemChangeEvent>> WatchAsync(
        VirtualPath path, FileSystemWatchScope scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a Watch gateway.");
}

/// <summary>Default <see cref="IAppExecutionContext.Services"/> for contexts that don't wire one up.</summary>
file sealed class UnsupportedAppServiceControlGateway : IAppServiceControlGateway
{
    public ValueTask<ServiceControlResult> StartAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a Services gateway.");

    public ValueTask<ServiceControlResult> StopAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a Services gateway.");

    public ValueTask<ServiceStartMode> GetStartModeAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a Services gateway.");

    public ValueTask<ServiceControlResult> SetStartModeAsync(
        string serviceAppId, ServiceStartMode mode, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This IAppExecutionContext implementation does not provide a Services gateway.");
}