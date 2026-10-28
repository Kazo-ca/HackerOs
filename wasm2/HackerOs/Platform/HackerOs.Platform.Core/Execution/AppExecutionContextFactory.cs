using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.Simulation.Abstractions;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Platform.Core.Administration;
using HackerOs.Platform.Core.Diagnostics;
using HackerOs.Platform.Core.Intents;
using HackerOs.Platform.Core.Network;
using HackerOs.Platform.Core.ServerConnection;
using HackerOs.Simulation.Abstractions.Network;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Metrics;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Time;

namespace HackerOs.Platform.Core.Execution;

/// <summary>
/// The sole trusted constructor for <see cref="IAppExecutionContext"/> instances (`P1-EXEC-007`).
/// App constructors and components never construct or elevate their own context; only platform
/// code that already holds a validated manifest, authenticated principal, and hosting process
/// record may call this factory.
/// </summary>
public sealed class AppExecutionContextFactory
{
    private readonly ICapabilityGrantRepository _grantRepository;
    private readonly IFileSystemService _fileSystem;
    private readonly IEventBus _eventBus;
    private readonly ITopicMessageBus _topicBus;
    private readonly INotificationQueue _notifications;
    private readonly IDiagnosticSink _diagnostics;
    private readonly ISimulationClock _clock;
    private readonly IProcessManager _processManager;
    private readonly Func<AppIntentDispatcher>? _intentDispatcherProvider;
    private readonly LogFileWriter? _logWriter;
    private readonly IProcessEnvironmentTable? _environments;
    private readonly Func<string> _machineName;
    private readonly Func<Shell.ShellHostService>? _shellHostProvider;
    private readonly AppCatalogGatewayServices? _appCatalog;
    private readonly ISimulatedNetworkService? _network;
    private readonly IServerConnectionService? _serverConnection;
    private readonly IProxyClient? _proxy;
    private readonly SystemMetricsServices? _metrics;
    private readonly SystemAdministrationGatewayServices? _administration;
    private readonly Clipboard.SystemClipboard _clipboard;
    private readonly Network.Local.LocalNetworkServices? _localNetwork;
    private readonly WebData.IWebSiteDatabaseStore? _webDatabases;
    private readonly Keyring.UserKeyringService? _keyring;
    private readonly Appearance.AccessibilityPreferenceService? _accessibility;

    /// <summary>Initializes the factory with every platform singleton gateways delegate to.</summary>
    /// <param name="grantRepository">Capability grants; each context's capability checker reads the app's grants for its user from it.</param>
    /// <param name="fileSystem">The OS filesystem behind the filesystem and directory-watch gateways, authorized per context.</param>
    /// <param name="eventBus">The system event bus behind <see cref="IAppExecutionContext.Events"/>.</param>
    /// <param name="topicBus">Topic messaging, shared by the event gateway and filesystem change notifications.</param>
    /// <param name="notifications">The desktop notification queue behind <see cref="IAppExecutionContext.Notifications"/>.</param>
    /// <param name="diagnostics">The diagnostic sink app log entries are forwarded to.</param>
    /// <param name="clock">The simulation clock: timestamps of every gateway and <see cref="IAppExecutionContext.Clock"/>.</param>
    /// <param name="processManager">The process table; supplies each process's cancellation token and backs <see cref="IAppExecutionContext.Processes"/>.</param>
    /// <param name="intentDispatcherProvider">
    /// Lazily resolves the app-intent dispatcher for the <see cref="IAppExecutionContext.Intents"/>
    /// gateway. Resolved lazily (not injected eagerly) because <see cref="AppIntentDispatcher"/>
    /// depends on the app lifecycle orchestrator, which itself depends on this factory to create
    /// execution contexts for newly launched processes -- an eager dependency here would be
    /// circular. Pass <see langword="null"/> (e.g. in tests) to leave app-launch unsupported.
    /// </param>
    /// <param name="logWriter">
    /// Platform log writer the <see cref="IAppExecutionContext.Logging"/> gateway appends to
    /// (<c>/var/log/apps/&lt;appId&gt;.log</c>, ADR 0045); <see langword="null"/> keeps app logs in
    /// the in-memory buffer only.
    /// </param>
    /// <param name="environments">
    /// Per-process environment table behind <see cref="IAppExecutionContext.Environment"/> (ADR 0050);
    /// when omitted, the one of an <see cref="Processes.InMemoryProcessManager"/> is used, otherwise
    /// contexts get a detached read-only view.
    /// </param>
    /// <param name="machineName">Reads the machine name (<c>/etc/hostname</c>); defaults to <c>hackeros</c>.</param>
    /// <param name="appCatalog">
    /// Services behind the <see cref="IAppExecutionContext.Catalog"/> and
    /// <see cref="IAppExecutionContext.AppManagement"/> gateways (ADR 0056, sections 2 and 3);
    /// <see langword="null"/> leaves both unsupported.
    /// </param>
    /// <param name="network">
    /// Simulated network behind <see cref="IAppExecutionContext.Network"/> (ADR 0056, section 6);
    /// <see langword="null"/> leaves contexts without a network.
    /// </param>
    /// <param name="serverConnection">Optional server connection used by the real-network fallback.</param>
    /// <param name="proxy">Optional server proxy client used by the real-network fallback.</param>
    /// <param name="shellHostProvider">
    /// Lazily resolves the service behind <see cref="IAppExecutionContext.ShellHost"/> (ADR 0056,
    /// section 7). Lazy for the same reason as <paramref name="intentDispatcherProvider"/>: the
    /// interpreter depends on the orchestrator, which depends on this factory. Pass
    /// <see langword="null"/> to leave shell hosting unsupported.
    /// </param>
    /// <param name="metrics">
    /// Counters and drive information of ADR 0057: each app's network traffic is counted for
    /// <c>/proc/net/dev</c> and its <c>/proc/&lt;pid&gt;/io</c>, and
    /// <see cref="IAppExecutionContext.Drives"/> answers <c>HackDriveInfo</c>. <see langword="null"/>
    /// counts nothing and leaves drives unsupported.
    /// </param>
    /// <param name="clipboard">
    /// The OS clipboard behind <see cref="IAppExecutionContext.Clipboard"/> (ADR 0056, section 12), shared
    /// by every context this factory creates; when omitted the factory keeps its own.
    /// </param>
    /// <param name="administration">
    /// Services behind the administration gateways (ADR 0056, sections 8 to 11):
    /// <see cref="IAppExecutionContext.SystemSettings"/>, <see cref="IAppExecutionContext.PermissionAdministration"/>,
    /// <see cref="IAppExecutionContext.ServerConnection"/>, <see cref="IAppExecutionContext.SystemPower"/>,
    /// <see cref="IAppExecutionContext.StorageDiagnostics"/> and <see cref="IAppExecutionContext.SystemBackup"/>
    /// (ADR 0099); <see langword="null"/> leaves them unsupported.
    /// </param>
    /// <param name="localNetwork">
    /// The local network stack (ADR 0074): listening ports, <c>hackdns</c> and web app activation, used by
    /// the network gateway to reach services on this machine; <see langword="null"/> leaves it out.
    /// </param>
    /// <param name="webDatabases">
    /// The browser databases of web sites behind <see cref="IAppExecutionContext.WebDatabase"/> and the
    /// storage diagnostics (ADR 0088); <see langword="null"/> leaves sites without a database.
    /// </param>
    /// <param name="keyring">
    /// The session user's keyring behind <see cref="IAppExecutionContext.Keyring"/> (ADR 0100 section 2);
    /// <see langword="null"/> leaves every app with a locked keyring.
    /// </param>
    /// <param name="accessibility">
    /// The session's accessibility preferences behind <see cref="IAppExecutionContext.Accessibility"/>
    /// (ADR 0105 section 3); <see langword="null"/> gives every app the defaults.
    /// </param>
    public AppExecutionContextFactory(
        ICapabilityGrantRepository grantRepository,
        IFileSystemService fileSystem,
        IEventBus eventBus,
        ITopicMessageBus topicBus,
        INotificationQueue notifications,
        IDiagnosticSink diagnostics,
        ISimulationClock clock,
        IProcessManager processManager,
        Func<AppIntentDispatcher>? intentDispatcherProvider = null,
        LogFileWriter? logWriter = null,
        IProcessEnvironmentTable? environments = null,
        Func<string>? machineName = null,
        AppCatalogGatewayServices? appCatalog = null,
        ISimulatedNetworkService? network = null,
        IServerConnectionService? serverConnection = null,
        IProxyClient? proxy = null,
        Func<Shell.ShellHostService>? shellHostProvider = null,
        SystemMetricsServices? metrics = null,
        Clipboard.SystemClipboard? clipboard = null,
        SystemAdministrationGatewayServices? administration = null,
        Network.Local.LocalNetworkServices? localNetwork = null,
        WebData.IWebSiteDatabaseStore? webDatabases = null,
        Keyring.UserKeyringService? keyring = null,
        Appearance.AccessibilityPreferenceService? accessibility = null)
    {
        _grantRepository = grantRepository ?? throw new ArgumentNullException(nameof(grantRepository));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _topicBus = topicBus ?? throw new ArgumentNullException(nameof(topicBus));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _intentDispatcherProvider = intentDispatcherProvider;
        _logWriter = logWriter;
        _environments = environments ?? (processManager as Processes.InMemoryProcessManager)?.Environments;
        _machineName = machineName ?? (static () => Processes.LoginEnvironment.DefaultHostName);
        _appCatalog = appCatalog;
        _network = network;
        _serverConnection = serverConnection;
        _proxy = proxy;
        _shellHostProvider = shellHostProvider;
        _metrics = metrics;
        _administration = administration;
        _clipboard = clipboard ?? new Clipboard.SystemClipboard();
        _localNetwork = localNetwork;
        _webDatabases = webDatabases;
        _keyring = keyring;
        _accessibility = accessibility;
    }

    /// <summary>
    /// Gets the service behind <see cref="IAppExecutionContext.Background"/> (ADR 0128): each task
    /// becomes a child process of the app. Lazy because the service reaches the orchestrator (the app's
    /// descriptor) and the remote host, which depend on this factory. <see langword="null"/> keeps the
    /// SDK's default, which runs the task at once on the calling thread.
    /// </summary>
    public Func<BackgroundJobService>? BackgroundJobs { get; init; }

    /// <summary>
    /// Creates a new trusted context for one running app instance, wiring every scoped gateway to
    /// the acting app/user/process.
    /// </summary>
    /// <param name="manifest">Validated manifest of the running application.</param>
    /// <param name="principal">Authenticated principal that owns the hosting session.</param>
    /// <param name="process">Process record hosting this app instance; must already be active.</param>
    /// <param name="grantedCapabilities">Exact capabilities trusted policy granted for this instance.</param>
    /// <param name="instanceId">Explicit instance ID, or <see langword="null"/> to generate one.</param>
    /// <param name="isRootElevated">
    /// Whether this one launch was elevated to full unrestricted root via <c>sudo</c> (`ADR 0042`),
    /// after a caller-supplied elevation ticket was already consumed.
    /// </param>
    /// <param name="oneShotElevation">
    /// A live one-shot elevation ticket set to bind into this instance, when the caller pre-filled
    /// one from an already-consumed capability-approval ticket; otherwise a fresh empty set is used
    /// so an app can still receive a same-instance admin-approval grant later.
    /// </param>
    public IAppExecutionContext Create(
        AppManifest manifest,
        AuthenticatedPrincipal principal,
        ProcessRecord process,
        IReadOnlySet<string> grantedCapabilities,
        Guid? instanceId = null,
        bool isRootElevated = false,
        OneShotElevationGrantSet? oneShotElevation = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(grantedCapabilities);

        string userId = principal.UserId.ToString();
        string userName = principal.LoginName.Value;
        string[] groupIds = [.. principal.GroupIds.Select(g => g.ToString())];

        AppOperationContext operationContext = new()
        {
            AppId = manifest.Id,
            UserId = userId,
            UserName = userName,
            ProcessId = process.Pid.Value,
            UserAuthority = principal.Authority,
            GrantedCapabilities = grantedCapabilities,
            IsSystemOperation = false,
            IsRootElevated = isRootElevated,
            OneShotElevation = oneShotElevation ?? new OneShotElevationGrantSet()
        };

        ICapabilityChecker capabilities = new AppCapabilityChecker(
            _grantRepository, manifest.Id, userId, principal.Authority);

        CancellationToken cancellationToken = _processManager.GetCancellationToken(process.Pid);

        IAppIntentGateway intents = _intentDispatcherProvider is not null
            ? new AppIntentGateway(_intentDispatcherProvider, manifest.Id, userId, principal)
            : UnsupportedIntentGateway.Instance;

        IAppServiceControlGateway services = _intentDispatcherProvider is not null
            ? new AppServiceControlGateway(_intentDispatcherProvider, manifest.Id, userId, principal)
            : UnsupportedServiceControlGateway.Instance;

        IAppEnvironmentGateway environment = _environments is null
            ? new DetachedAppEnvironmentGateway(manifest.Id, principal.HomePath, userName, _machineName)
            : new AppEnvironmentGateway(_environments, process.Pid, userName, _machineName, principal.HomePath);

        // The process's language (ADR 0104), read at each use: the OS texts it asks for (the
        // operation shown in an administrator prompt) follow its LANG, as its own texts do.
        Func<System.Globalization.CultureInfo> language = () => HackerOs.AppSdk.Globalization.HackLocale.ResolveLanguage(environment.GetVariable);
        AppPermissionErrorGateway permissionErrors = new();
        AppEventGateway events = new(_eventBus, _topicBus, manifest.Id, userId, process.Pid.ToString());
        AppClockGateway clock = new(_clock);
        AppCatalogGateway? catalogGateway = _appCatalog is null
            ? null
            : new AppCatalogGateway(
                _appCatalog.Catalog, _appCatalog.Enablement, _appCatalog.FileAssociations, _appCatalog.Installation, principal, capabilities);
        AppManagementGateway? managementGateway = _appCatalog?.Installation is null
            ? null
            : new AppManagementGateway(
                _appCatalog.Catalog,
                _appCatalog.Installation,
                _appCatalog.AuthenticationAgent,
                capabilities,
                manifest,
                principal,
                isRootElevated,
                _fileSystem,
                _clock,
                language,
                _appCatalog.Assets);
        AppClipboardGateway clipboardGateway = new(_clipboard, capabilities, process.SessionId, process.Pid);

        AppFileSystemGateway fileSystem = new(
            _fileSystem, _clock, operationContext, groupIds, notifyPermissionError: permissionErrors.Raise, kernelMemory: _metrics?.KernelMemory)
        {
            // ADR 0125: every open of the app counts toward its process's nofile limit.
            Descriptors = (_processManager as Processes.InMemoryProcessManager)?.FileDescriptors
        };
        AdministrationGateways administration = CreateAdministrationGateways(manifest, principal, isRootElevated, capabilities, fileSystem, language);

        AppAccessibilityGateway? accessibilityGateway = _accessibility is null ? null : new AppAccessibilityGateway(_accessibility);
        AppExecutionContext context = new(
            manifest,
            instanceId ?? process.AppInstanceId.Value,
            userId,
            principal.Authority,
            grantedCapabilities,
            process.SessionId,
            process.Pid,
            cancellationToken,
            capabilities,
            fileSystem,
            events,
            new AppNotificationGateway(_notifications, _clock, capabilities, manifest.Id, principal.UserId),
            new AppLoggingGateway(
                _diagnostics, _clock, manifest.Id, _logWriter, process.Pid.Value,
                isSystemApp: _appCatalog?.Catalog.IsSystemApp(manifest.Id) == true,
                isWebApp: manifest.Kind == AppKind.Web),
            clock,
            new AppProcessGateway(_processManager, capabilities, process.Pid)
            {
                // ADR 0125 section 4: setrlimit/prlimit, under the caller's identity and elevation.
                ResourceLimits = (_processManager as Processes.InMemoryProcessManager)?.ResourceLimits,
                IsRootElevated = isRootElevated
            },
            intents,
            new AppFileSystemWatchGateway(_fileSystem, _topicBus, _clock, operationContext, groupIds, process.Pid.ToString()),
            permissionErrors,
            services,
            operationContext.OneShotElevation,
            principal.HomePath,
            environment,
            isRootElevated,
            userName,
            principal.DisplayName,
            catalogGateway,
            managementGateway,
            _network is null
                ? null
                : new AppNetworkGateway(
                    _network,
                    capabilities,
                    manifest.Id,
                    _serverConnection,
                    _proxy,
                    _metrics?.Network,
                    _metrics?.ProcessIo,
                    process.Pid,
                    _metrics?.KernelMemory,
                    _localNetwork is null ? null : new Network.Local.LocalNetworkCaller(_localNetwork, manifest, principal, isRootElevated)),
            _metrics?.Drives,
            clipboardGateway,
            administration.SystemSettings,
            administration.Permissions,
            administration.ServerConnection,
            administration.SystemPower,
            administration.StorageDiagnostics,
            // ADR 0069: the process manager's block carries the stop request and the kill to the context.
            (_processManager as Processes.InMemoryProcessManager)?.GetControlBlock(process.Pid),
            // ADR 0088: hackweb.<app id>, for web apps only.
            new WebData.AppWebDatabaseGateway(_webDatabases, manifest),
            // ADR 0100 section 2: the session user's keyring, confined to this app's entries.
            _keyring is null ? null : new Keyring.AppKeyringGateway(_keyring, principal.UserId, manifest.Id, capabilities),
            // ADR 0105 section 3: the session's accessibility preferences, no capability needed.
            accessibilityGateway);

        // ADR 0043: callbacks these gateways invoke on the app's behalf re-enter its ambient context.
        events.BindAmbientContext(context);
        clock.BindAmbientContext(context);
        catalogGateway?.BindAmbientContext(context);
        accessibilityGateway?.BindAmbientContext(context);
        administration.SystemSettings?.BindAmbientContext(context);
        if (administration.SystemBackup is not null)
        {
            context.SystemBackup = administration.SystemBackup;
        }

        if (_metrics?.CrossOriginIsolation is { } isolation)
        {
            // ADR 0146: the page's isolation, no capability, set after construction like the backup gateway.
            context.CrossOriginIsolation = isolation;
        }

        if (administration.UserAccounts is not null)
        {
            // ADR 0120: accounts and passwords, set after construction like the backup gateway.
            context.UserAccounts = administration.UserAccounts;
        }

        if (administration.ComponentProviders is not null)
        {
            // ADR 0156: who provides the file view and the file dialogs, set like the backup gateway.
            context.ComponentProviderAdministration = administration.ComponentProviders;
        }

        if (administration.FileMenuEntries is not null)
        {
            // ADR 0159 §8: the entries apps added to the file menus, set like the backup gateway.
            context.FileMenuEntryAdministration = administration.FileMenuEntries;
        }

        if (_administration?.Screens is { } screens)
        {
            // ADR 0184: the secondary screens, for the windows this instance owns, set like the backup gateway.
            AppScreensGateway screensGateway = new(screens, capabilities, context.InstanceId);
            screensGateway.BindAmbientContext(context);
            context.Screens = screensGateway;
        }

        if (_shellHostProvider is not null)
        {
            // ADR 0056 section 7: the shell is a child process of this one, with this instance's
            // capabilities for its own redirections and the same root elevation, if any.
            context.ShellHost = new AppShellHostGateway(
                _shellHostProvider,
                capabilities,
                principal,
                () => context,
                shellProcess => Create(
                    Shell.ShellHostService.ShellManifest, principal, shellProcess, grantedCapabilities,
                    shellProcess.AppInstanceId.Value, isRootElevated));
        }

        if (BackgroundJobs is { } backgroundJobs)
        {
            // ADR 0128 section 3: a task is a child process of this one, with this instance's identity,
            // capabilities and root elevation, like a helper the app would fork.
            context.Background = new AppBackgroundGateway(
                backgroundJobs,
                () => context,
                jobProcess => Create(
                    manifest, principal, jobProcess, grantedCapabilities, jobProcess.AppInstanceId.Value, isRootElevated));
        }

        clipboardGateway.BindAmbientContext(context);
        return context;
    }

    /// <summary>Builds the administration gateways of ADR 0056 (sections 8 to 11) for one app instance.</summary>
    private AdministrationGateways CreateAdministrationGateways(
        AppManifest manifest,
        AuthenticatedPrincipal principal,
        bool isRootElevated,
        ICapabilityChecker capabilities,
        AppFileSystemGateway fileSystem,
        Func<System.Globalization.CultureInfo> language)
    {
        if (_administration is null)
        {
            return new AdministrationGateways(null, null, null, null, null, null, null, null, null);
        }

        AdministratorGate gate = new(_administration.AuthenticationAgent, manifest, principal, isRootElevated, language);
        return new AdministrationGateways(
            _administration.Settings is { } settings ? new AppSystemSettingsGateway(settings, fileSystem) : null,
            _administration.Permissions is { } permissions ? new AppPermissionAdministrationGateway(permissions, capabilities, gate) : null,
            new AppServerConnectionGateway(_administration.Server, capabilities, language),
            new AppSystemPowerGateway(_administration.Power, capabilities, gate),
            new AppStorageDiagnosticsGateway(_administration.Storage, capabilities, _webDatabases),
            new AppSystemBackupGateway(_administration.Backup, capabilities, gate),
            new AppUserAccountsGateway(_administration.Passwords, capabilities, gate)
            {
                Accounts = _administration.Accounts,
                Passkeys = _administration.Passkeys
            },
            new AppComponentProviderAdministrationGateway(_administration.ComponentProviders, _administration.ApprovalPrompt, capabilities, gate),
            new AppFileMenuEntryAdministrationGateway(_administration.FileMenuEntries, capabilities, language));
    }

    private sealed record AdministrationGateways(
        AppSystemSettingsGateway? SystemSettings,
        AppPermissionAdministrationGateway? Permissions,
        AppServerConnectionGateway? ServerConnection,
        AppSystemPowerGateway? SystemPower,
        AppStorageDiagnosticsGateway? StorageDiagnostics,
        AppSystemBackupGateway? SystemBackup,
        AppUserAccountsGateway? UserAccounts,
        AppComponentProviderAdministrationGateway? ComponentProviders,
        AppFileMenuEntryAdministrationGateway? FileMenuEntries);

    private sealed class UnsupportedIntentGateway : IAppIntentGateway
    {
        public static readonly UnsupportedIntentGateway Instance = new();

        public ValueTask<AppIntentLaunchResult> LaunchAsync(
            string appId, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "This execution context factory was not configured with an app-intent dispatcher.");

        public ValueTask<AppIntentOpenFileResult> OpenFileAsync(
            VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "This execution context factory was not configured with an app-intent dispatcher.");
    }

    private sealed class UnsupportedServiceControlGateway : IAppServiceControlGateway
    {
        public static readonly UnsupportedServiceControlGateway Instance = new();

        public ValueTask<ServiceControlResult> StartAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "This execution context factory was not configured with an app-intent dispatcher.");

        public ValueTask<ServiceControlResult> StopAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "This execution context factory was not configured with an app-intent dispatcher.");

        public ValueTask<ServiceStartMode> GetStartModeAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "This execution context factory was not configured with an app-intent dispatcher.");

        public ValueTask<ServiceControlResult> SetStartModeAsync(
            string serviceAppId, ServiceStartMode mode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "This execution context factory was not configured with an app-intent dispatcher.");
    }
}

/// <summary>
/// Platform services behind the app catalog gateways (ADR 0056, sections 2 and 3). The
/// installation service, the file association resolver and the authentication agent are resolved
/// lazily: the installation service depends, through the lifecycle orchestrator, on the very
/// factory that receives this bundle.
/// </summary>
/// <param name="Catalog">Catalog of this build, with its system app list.</param>
/// <param name="Enablement">Launch checkpoint deciding which apps are installed (ADR 0048).</param>
/// <param name="FileAssociations">Resolves "Open with" candidates; <see langword="null"/> leaves <c>GetHandlersAsync</c> unsupported.</param>
/// <param name="Installation">Uninstall/reinstall service; <see langword="null"/> leaves <c>AppManagement</c> unsupported and <c>Changed</c> silent.</param>
/// <param name="AuthenticationAgent">OS authentication agent (ADR 0055, section 5); <see langword="null"/> refuses every machine-scope change.</param>
/// <param name="Assets">Asset integrity checks (ADR 0098 §5): Settings lists the apps it found damaged; <see langword="null"/> reports none.</param>
public sealed record AppCatalogGatewayServices(
    AppCatalog Catalog,
    Lifecycle.IAppEnablementRegistry Enablement,
    Func<Intents.FileAssociationResolver>? FileAssociations = null,
    Func<Apps.IAppInstallationService>? Installation = null,
    Func<Elevation.IAdministratorAuthenticationAgent>? AuthenticationAgent = null,
    Func<Apps.AppAssetStore>? Assets = null);

/// <summary>
/// Services behind the measurements of ADR 0057 that the factory hands to each context.
/// </summary>
/// <param name="Network">Interface counters charged with each app's traffic (<c>/proc/net/dev</c>).</param>
/// <param name="ProcessIo">Per-process I/O accounting charged with each app's traffic (<c>/proc/&lt;pid&gt;/io</c>).</param>
/// <param name="Drives">Drive information behind <see cref="IAppExecutionContext.Drives"/>; shared by every context.</param>
/// <param name="KernelMemory">
/// Ledger charged with the memory the OS holds for each app (write-stream buffers, sockets in
/// flight; ADR 0068); <see langword="null"/> or not tracking when memory is not measured.
/// </param>
public sealed record SystemMetricsServices(
    INetworkInterfaceStatistics? Network = null,
    IProcessIoAccounting? ProcessIo = null,
    HackerOs.AppSdk.IO.IAppDriveGateway? Drives = null,
    IKernelMemoryLedger? KernelMemory = null)
{
    /// <summary>
    /// Gets the page's cross-origin isolation behind <see cref="IAppExecutionContext.CrossOriginIsolation"/>
    /// (ADR 0146); shared by every context. <see langword="null"/> leaves it unsupported.
    /// </summary>
    public HackerOs.AppSdk.Diagnostics.IAppCrossOriginIsolationGateway? CrossOriginIsolation { get; init; }
}
