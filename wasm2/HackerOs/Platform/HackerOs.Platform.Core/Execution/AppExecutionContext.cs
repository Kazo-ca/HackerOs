using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Shell;
using HackerOs.AppSdk.Net;
using HackerOs.AppSdk.Settings;
using HackerOs.AppSdk.Clipboard;
using HackerOs.AppSdk.Diagnostics;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Execution;

/// <summary>
/// Trusted, immutable implementation of <see cref="IAppExecutionContext"/> constructed only by
/// <see cref="AppExecutionContextFactory"/> (`P1-EXEC-007`). App code never constructs this type.
/// </summary>
internal sealed class AppExecutionContext : IAppExecutionContext
{
    /// <summary>Initializes a fully wired app execution context.</summary>
    internal AppExecutionContext(
        AppManifest manifest,
        Guid instanceId,
        string userId,
        AppAuthority userAuthority,
        IReadOnlySet<string> grantedCapabilities,
        SessionId sessionId,
        ProcessId processId,
        CancellationToken cancellationToken,
        ICapabilityChecker capabilities,
        IAppFileSystemGateway fileSystem,
        IAppEventGateway events,
        IAppNotificationGateway notifications,
        IAppLoggingGateway logging,
        IAppClockGateway clock,
        IAppProcessGateway processes,
        IAppIntentGateway intents,
        IAppFileSystemWatchGateway watch,
        IAppPermissionErrorGateway permissionErrors,
        IAppServiceControlGateway services,
        OneShotElevationGrantSet oneShotElevation,
        string? homeDirectory = null,
        IAppEnvironmentGateway? environment = null,
        bool isRootElevated = false,
        string? userLoginName = null,
        string? userDisplayName = null,
        IAppCatalogGateway? catalog = null,
        IAppManagementGateway? appManagement = null,
        IAppNetworkGateway? network = null,
        IAppDriveGateway? drives = null,
        IAppClipboardGateway? clipboard = null,
        IAppSystemSettingsGateway? systemSettings = null,
        IAppPermissionAdministrationGateway? permissionAdministration = null,
        IAppServerConnectionGateway? serverConnection = null,
        IAppSystemPowerGateway? systemPower = null,
        IAppStorageDiagnosticsGateway? storageDiagnostics = null,
        Processes.ProcessControlBlock? control = null,
        HackerOs.AppSdk.WebData.IAppWebDatabaseGateway? webDatabase = null,
        HackerOs.AppSdk.Keyring.IAppKeyringGateway? keyring = null,
        HackerOs.AppSdk.Accessibility.IAppAccessibilityGateway? accessibility = null)
    {
        // ADR 0069: a context built without the process manager's block (tests) is never stopped or killed.
        Control = control ?? new Processes.ProcessControlBlock(processId);
        Control.StopRequested += OnStopRequested;
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        InstanceId = instanceId;
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        UserId = userId;
        UserAuthority = userAuthority;
        GrantedCapabilities = grantedCapabilities ?? throw new ArgumentNullException(nameof(grantedCapabilities));
        SessionId = sessionId;
        ProcessId = processId;
        CancellationToken = cancellationToken;
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        Events = events ?? throw new ArgumentNullException(nameof(events));
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Processes = processes ?? throw new ArgumentNullException(nameof(processes));
        Intents = intents ?? throw new ArgumentNullException(nameof(intents));
        Watch = watch ?? throw new ArgumentNullException(nameof(watch));
        PermissionErrors = permissionErrors ?? throw new ArgumentNullException(nameof(permissionErrors));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        OneShotElevation = oneShotElevation ?? throw new ArgumentNullException(nameof(oneShotElevation));
        HomeDirectory = homeDirectory ?? $"/home/{userId}";
        Environment = environment
            ?? new DetachedAppEnvironmentGateway(manifest.Id, HomeDirectory, userId, static () => "hackeros");
        IsRootElevated = isRootElevated;
        UserLoginName = userLoginName ?? Environment.UserName;
        UserDisplayName = userDisplayName ?? UserLoginName;
        Catalog = catalog ?? UnsupportedAppGateways.Catalog;
        AppManagement = appManagement ?? UnsupportedAppGateways.Management;
        Network = network ?? UnsupportedNetworkGateway.Instance;
        Drives = drives ?? UnsupportedDriveGateway.Instance;
        SystemSettings = systemSettings ?? UnsupportedAdministrationGateways.SystemSettings;
        PermissionAdministration = permissionAdministration ?? UnsupportedAdministrationGateways.Permissions;
        ServerConnection = serverConnection ?? UnsupportedAdministrationGateways.ServerConnection;
        SystemPower = systemPower ?? UnsupportedAdministrationGateways.SystemPower;
        StorageDiagnostics = storageDiagnostics ?? UnsupportedAdministrationGateways.StorageDiagnostics;
        Clipboard = clipboard ?? new AppClipboardGateway(new Clipboard.SystemClipboard(), capabilities, sessionId);
        WebDatabase = webDatabase ?? new WebData.AppWebDatabaseGateway(null, manifest);
        Keyring = keyring ?? HackerOs.AppSdk.Keyring.UnsupportedAppKeyringGateway.Instance;
        Accessibility = accessibility ?? HackerOs.AppSdk.Accessibility.DefaultAppAccessibilityGateway.Instance;
    }

    /// <inheritdoc />
    public HackerOs.AppSdk.Accessibility.IAppAccessibilityGateway Accessibility { get => Guarded(field); }

    /// <inheritdoc />
    public HackerOs.AppSdk.Keyring.IAppKeyringGateway Keyring { get => Guarded(field); }

    /// <inheritdoc />
    public HackerOs.AppSdk.WebData.IAppWebDatabaseGateway WebDatabase { get => Guarded(field); }

    /// <inheritdoc />
    public IAppSystemSettingsGateway SystemSettings { get => Guarded(field); }

    /// <inheritdoc />
    public IAppPermissionAdministrationGateway PermissionAdministration { get => Guarded(field); }

    /// <inheritdoc />
    public IAppServerConnectionGateway ServerConnection { get => Guarded(field); }

    /// <inheritdoc />
    public IAppSystemPowerGateway SystemPower { get => Guarded(field); }

    /// <inheritdoc />
    public IAppStorageDiagnosticsGateway StorageDiagnostics { get => Guarded(field); }

    /// <summary>
    /// Gets or sets the session's time zone, read at every access (ADR 0193 section 4), or
    /// <see langword="null"/> for the device's zone.
    /// </summary>
    internal Func<TimeZoneInfo>? SessionTimeZone { get; set; }

    /// <inheritdoc />
    /// <remarks>The process's own <c>TZ</c> wins, then the session's zone, so a change in Settings applies at once.</remarks>
    public TimeZoneInfo TimeZone =>
        HackerOs.AppSdk.Globalization.HackTimeZone.TryFind(
            Environment.GetVariable(HackerOs.AppSdk.Globalization.HackTimeZone.Variable), out TimeZoneInfo? zone)
            ? zone
            : SessionTimeZone?.Invoke() ?? HackerOs.AppSdk.Globalization.HackTimeZone.Device;

    /// <inheritdoc />
    public IAppSystemBackupGateway SystemBackup { get => Guarded(field); internal set; } = UnsupportedAdministrationGateways.SystemBackup;

    /// <inheritdoc />
    public IAppUserAccountsGateway UserAccounts { get => Guarded(field); internal set; } = UnsupportedAdministrationGateways.UserAccounts;

    /// <inheritdoc />
    public IAppComponentProviderAdministrationGateway ComponentProviderAdministration { get => Guarded(field); internal set; } = UnsupportedAdministrationGateways.ComponentProviders;

    /// <inheritdoc />
    public IAppFileMenuEntryAdministrationGateway FileMenuEntryAdministration { get => Guarded(field); internal set; } = UnsupportedAdministrationGateways.FileMenuEntries;

    /// <inheritdoc />
    public IAppScreensGateway Screens { get => Guarded(field); internal set; } = UnsupportedAdministrationGateways.Screens;

    /// <inheritdoc />
    public HackerOs.AppSdk.WhatsNew.IAppWhatsNewGateway WhatsNew { get => Guarded(field); internal set; } = UnsupportedAdministrationGateways.WhatsNew;

    /// <inheritdoc />
    public string UserLoginName { get; }

    /// <inheritdoc />
    public string UserDisplayName { get; }

    /// <inheritdoc />
    public IAppCatalogGateway Catalog { get => Guarded(field); }

    /// <inheritdoc />
    public IAppManagementGateway AppManagement { get => Guarded(field); }

    /// <inheritdoc />
    public IAppNetworkGateway Network { get => Guarded(field); }

    /// <inheritdoc />
    public IAppDriveGateway Drives { get => Guarded(field); }

    /// <inheritdoc />
    public HackerOs.AppSdk.Diagnostics.IAppCrossOriginIsolationGateway CrossOriginIsolation { get => Guarded(field); internal set; } = UnsupportedCrossOriginIsolationGateway.Instance;

    /// <inheritdoc />
    public IAppClipboardGateway Clipboard { get => Guarded(field); }

    /// <inheritdoc />
    public IAppEnvironmentGateway Environment { get => Guarded(field); }

    /// <inheritdoc />
    public bool IsRootElevated { get; }

    /// <inheritdoc />
    public string HomeDirectory { get; }

    /// <inheritdoc />
    public AppManifest Manifest { get; }

    /// <inheritdoc />
    public Guid InstanceId { get; }

    /// <inheritdoc />
    public string UserId { get; }

    /// <inheritdoc />
    public AppAuthority UserAuthority { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> GrantedCapabilities { get; }

    /// <inheritdoc />
    public SessionId SessionId { get; }

    /// <inheritdoc />
    public ProcessId ProcessId { get; }

    /// <inheritdoc />
    public CancellationToken CancellationToken { get; }

    /// <inheritdoc />
    public ICapabilityChecker Capabilities { get; }

    /// <inheritdoc />
    public IAppFileSystemGateway FileSystem { get => Guarded(field); }

    /// <inheritdoc />
    public IAppEventGateway Events { get => Guarded(field); }

    /// <inheritdoc />
    public IAppNotificationGateway Notifications { get => Guarded(field); }

    /// <inheritdoc />
    public IAppLoggingGateway Logging { get => Guarded(field); }


    /// <inheritdoc />
    public IAppClockGateway Clock { get => Guarded(field); }

    /// <inheritdoc />
    public IAppProcessGateway Processes { get => Guarded(field); }

    /// <inheritdoc />
    public IAppIntentGateway Intents { get => Guarded(field); }

    /// <inheritdoc />
    public IAppFileSystemWatchGateway Watch { get => Guarded(field); }

    /// <inheritdoc />
    public IAppPermissionErrorGateway PermissionErrors { get; }

    /// <inheritdoc />
    public IAppServiceControlGateway Services { get => Guarded(field); }

    /// <inheritdoc />
    public OneShotElevationGrantSet OneShotElevation { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Bound by <see cref="AppExecutionContextFactory"/> right after construction, because the
    /// gateway needs this context (the shell's parent process); unsupported until then.
    /// </remarks>
    public IAppShellHostGateway ShellHost { get => Guarded(field); internal set; } = UnsupportedShellHostGateway.Instance;

    /// <inheritdoc />
    /// <remarks>
    /// Bound by <c>AppLifecycleOrchestrator</c> when the launch request carries streams (a shell
    /// started the instance, ADR 0064); detached otherwise.
    /// </remarks>
    public IAppConsoleGateway Console { get => Guarded(field); internal set; } = DetachedAppConsoleGateway.Instance;

    /// <inheritdoc />
    public HackerOs.AppSdk.Threading.IAppBackgroundGateway Background
    {
        get => Guarded(field ??= new HackerOs.AppSdk.Threading.InlineAppBackgroundGateway(this));
        internal set;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Bound by <c>AppLifecycleOrchestrator</c> when it starts a service (ADR 0097, section 2): the
    /// messages reach the service's state table under this process's PID. Every other process keeps
    /// the gateway that drops them.
    /// </remarks>
    public IAppServiceNotifyGateway ServiceNotify { get => Guarded(field); internal set; } = NullAppServiceNotifyGateway.Instance;

    /// <summary>
    /// Gets the kernel-side block of this instance's process (ADR 0069): stop request, killed flag and
    /// scheduler. It outlives the process, so leftover code of a killed process still finds it.
    /// </summary>
    internal Processes.ProcessControlBlock Control { get; }

    /// <summary>Gets whether the OS killed this instance's process.</summary>
    internal bool IsKilled => Control.IsKilled;

    /// <inheritdoc />
    public event EventHandler<HackStopRequestedEventArgs>? StopRequested;

    /// <inheritdoc />
    public HackStopRequestedEventArgs? StopRequest { get; private set; }

    /// <inheritdoc />
    /// <remarks>Set by <c>AppLifecycleOrchestrator</c> from the launch request (ADR 0096, section 5).</remarks>
    public AppIntentDelivery? Intent { get; internal set; }

    /// <inheritdoc />
    public event EventHandler<AppIntentDelivery>? IntentReceived;

    /// <summary>Gets whether some code of the app listens for <see cref="IntentReceived"/> (a worker session forwards it).</summary>
    internal bool HasIntentListeners => IntentReceived is not null;

    /// <summary>
    /// Hands an intent to this running instance (ADR 0096, section 5): the second activation of a
    /// single-instance app. Runs the handlers inside the app's ambient context, like the stop
    /// request; a handler that throws is logged and does not keep the others from running.
    /// </summary>
    /// <param name="delivery">The intent and its activation arguments.</param>
    internal void DeliverIntent(AppIntentDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (Control.IsKilled || IntentReceived is not { } handlers)
        {
            return;
        }

        using IDisposable scope = HackExecutionContext.Enter(this);
        foreach (EventHandler<AppIntentDelivery> handler in handlers.GetInvocationList().Cast<EventHandler<AppIntentDelivery>>())
        {
            try
            {
                handler(this, delivery);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Logging.Log(
                    HackerOs.Simulation.Abstractions.Diagnostics.DiagnosticSeverity.Error,
                    $"An IntentReceived handler threw {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Every OS gateway refuses a killed process (ADR 0069, section 3): its leftover code is cut off,
    /// like the threads of a Linux process after <c>SIGKILL</c>. Identity, the token and the
    /// permission-error channel stay readable, for the OS's own cleanup.
    /// </summary>
    private T Guarded<T>(T gateway)
    {
        Control.ThrowIfKilled();
        return gateway;
    }

    /// <summary>
    /// Delivers the OS's stop request to the app (ADR 0069, section 1), inside the app's own ambient
    /// context so the handlers can use the SDK. A handler that throws is logged and does not keep the
    /// others from running.
    /// </summary>
    private void OnStopRequested(ProcessStopRequest request)
    {
        HackStopRequestedEventArgs args = new(request.Reason, request.RequestedAtUtc, request.Deadline);
        StopRequest = args;
        if (StopRequested is not { } handlers)
        {
            return;
        }

        using IDisposable scope = HackExecutionContext.Enter(this);
        foreach (EventHandler<HackStopRequestedEventArgs> handler in handlers.GetInvocationList().Cast<EventHandler<HackStopRequestedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Logging.Log(
                    HackerOs.Simulation.Abstractions.Diagnostics.DiagnosticSeverity.Error,
                    $"A StopRequested handler threw {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    /// <summary>Shell host of a context whose factory has no shell service (tests, legacy hosts).</summary>
    internal sealed class UnsupportedShellHostGateway : IAppShellHostGateway
    {
        public static readonly UnsupportedShellHostGateway Instance = new();

        public Task<IHackShell> StartAsync(HackShellOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This execution context factory was not configured with a shell host service.");
    }

    /// <summary>Drive gateway of a context built without a mount table (tests).</summary>
    private sealed class UnsupportedDriveGateway : IAppDriveGateway
    {
        public static readonly UnsupportedDriveGateway Instance = new();

        public ValueTask<IReadOnlyList<HackDriveSnapshot>> GetDrivesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This execution context has no drive information.");
    }

    /// <summary>Isolation gateway of a context built without the page's status (tests).</summary>
    private sealed class UnsupportedCrossOriginIsolationGateway : HackerOs.AppSdk.Diagnostics.IAppCrossOriginIsolationGateway
    {
        public static readonly UnsupportedCrossOriginIsolationGateway Instance = new();

        public ValueTask<HackerOs.AppSdk.Diagnostics.HackCrossOriginIsolationStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This execution context has no cross-origin isolation information.");
    }

    /// <summary>Network gateway of a context built without a simulated network (tests).</summary>
    private sealed class UnsupportedNetworkGateway : IAppNetworkGateway
    {
        public static readonly UnsupportedNetworkGateway Instance = new();

        private const string Reason = "This execution context factory was not configured with a network.";

        public ValueTask<HackNetworkResult<HackHostEntry>> ResolveAsync(string hostName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Reason);

        public ValueTask<HackNetworkResult<HackHostEntry>> ReverseLookupAsync(
            System.Net.IPAddress address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Reason);

        public ValueTask<HackNetworkResult<HackPingReply>> PingAsync(
            string hostNameOrAddress, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Reason);

        public ValueTask<HackNetworkResult<HackHttpResponse>> SendAsync(
            HackHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Reason);

        public ValueTask<HackNetworkResult<IReadOnlyList<HackPortProbeResult>>> ProbeTcpAsync(
            string hostNameOrAddress, int firstPort, int lastPort, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Reason);

        public ValueTask<HackNetworkResult<HackOsFingerprint?>> DetectOperatingSystemAsync(
            string hostNameOrAddress, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Reason);
    }
}

/// <summary>Gateways used when the factory was built without the app catalog services (tests, minimal hosts).</summary>
internal static class UnsupportedAppGateways
{
    private const string Message = "This execution context factory was not configured with the app catalog services.";

    /// <summary>Catalog gateway whose reads throw <see cref="NotSupportedException"/>.</summary>
    public static readonly IAppCatalogGateway Catalog = new UnsupportedCatalog();

    /// <summary>Management gateway whose calls throw <see cref="NotSupportedException"/>.</summary>
    public static readonly IAppManagementGateway Management = new UnsupportedManagement();

    private sealed class UnsupportedCatalog : IAppCatalogGateway
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ValueTask<IReadOnlyList<HackAppInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<IReadOnlyList<HackAppInfo>> GetHandlersAsync(
            VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }

    private sealed class UnsupportedManagement : IAppManagementGateway
    {
        public ValueTask<IReadOnlyList<HackAppInstallInfo>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<IReadOnlyList<string>> UninstallAsync(
            string appId, HackInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);

        public ValueTask<IReadOnlyList<string>> ReinstallAsync(
            string appId, HackInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(Message);
    }
}
