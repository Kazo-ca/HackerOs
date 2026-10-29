using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Infrastructure.Browser.Catalog;
using HackerOs.Infrastructure.Browser.FileSystem;
using HackerOs.Infrastructure.Browser.Interop;
using HackerOs.Infrastructure.Browser.Policy;
using HackerOs.Infrastructure.Browser.ServerConnection;
using HackerOs.Infrastructure.Browser.Sessions;
using HackerOs.Infrastructure.Browser.Settings;
using HackerOs.Infrastructure.Browser.Sync;
using HackerOs.Platform.Core.ServerConnection;
using HackerOs.Ecosystem.Administration;
using HackerOs.Platform.Core.Administration;
using HackerOs.Platform.Core.Power;
using HackerOs.Platform.Core.Backup;
using HackerOs.Platform.Core.Storage;
using HackerOs.Infrastructure.Browser.Storage;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core;
using HackerOs.Platform.WorkerHost.Host;
using HackerOs.Simulation.Abstractions.Workers;
using HackerOs.Platform.Core.Diagnostics;
using HackerOs.Platform.Core.Discovery;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.Isolation;
using HackerOs.Platform.Core.Keyring;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.FileSystem.HostFolders;
using HackerOs.Platform.Core.FileSystem.Virtual;
using HackerOs.Platform.Core.Intents;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Platform.Core.Metrics;
using HackerOs.Platform.Core.Network;
using HackerOs.Platform.Core.Network.Local;
using HackerOs.Platform.Core.Notifications;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Globalization;
using HackerOs.Platform.Core.Processes;
using HackerOs.Platform.Core.Processes.Accounting;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Shell;
using HackerOs.Platform.Core.Time;
using HackerOs.Platform.Blazor.Components;
using HackerOs.Platform.Blazor.Dialogs;
using HackerOs.Platform.Blazor.Display;
using HackerOs.Platform.Blazor.Security;
using HackerOs.Platform.Blazor.Shell;
using HackerOs.Platform.Blazor.Sites;
using HackerOs.AppSdk.Web.Blazor.Sites;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Simulation.Abstractions;
using HackerOs.Windowing.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HackerOs.Windowing.Abstractions;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Metrics;
using HackerOs.Simulation.Abstractions.Network;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.ServerConnection;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Sync;
using HackerOs.Simulation.Abstractions.Time;
using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.Blazor.Components;
using HackerOs.AppSdk.Blazor.Display;
using Microsoft.JSInterop;
using HackerOs.AppSdk.Icons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;

namespace HackerOs.Ecosystem;

/// <summary>Registers the process-wide HackerOS ecosystem service graph.</summary>
public static class EcosystemServiceCollectionExtensions
{
    /// <summary>
    /// Adds persistent browser infrastructure and the process-wide simulation services.
    /// Asynchronous storage reconciliation and session startup remain boot responsibilities.
    /// </summary>
    public static IServiceCollection AddHackerOsEcosystem(
        this IServiceCollection services,
        AppCatalog? catalog = null,
        Func<IServiceProvider, IReadOnlyDictionary<string, AppDescriptor>>? descriptorProvider = null,
        Func<IServiceProvider, IAppDescriptorLoader?>? descriptorLoaderProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.AddMudServices();

        services.AddSingleton(TimeProvider.System);
        // One ManualSimulationClock, driven in live hosts by SimulationClockDriver (ADR 0012);
        // the boot sequence starts the driver.
        services.AddSingleton(_ => new ManualSimulationClock(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(100)));
        services.AddSingleton<ISimulationClock>(provider => provider.GetRequiredService<ManualSimulationClock>());
        services.AddSingleton(provider => new SimulationClockDriver(
            provider.GetRequiredService<ManualSimulationClock>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISimulationRandom>(_ => new SeededSimulationRandom(1));

        services.AddSingleton<IDiagnosticRedactor>(_ => new SensitiveKeyDiagnosticRedactor());
        services.AddSingleton<IEventBus>(_ => new InMemoryEventBus());
        // In-memory buffer of recent entries for live viewers and the boot error screen. Logs
        // themselves are files under /var/log, written only by LogFileWriter (ADR 0045).
        services.AddSingleton<IDiagnosticSink>(provider => new EventPublishingDiagnosticSink(
            new BoundedDiagnosticSink(1024),
            provider.GetRequiredService<IEventBus>()));
        services.AddSingleton(provider => new LogFileWriter(
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetRequiredService<IDiagnosticSink>(),
            provider.GetRequiredService<IEventBus>()));
        services.AddSingleton<IAuditLog>(provider => new FileAuditLog(
            provider.GetRequiredService<LogFileWriter>(),
            provider.GetRequiredService<IDiagnosticRedactor>()));
        // /var/log/audit.log (ADR 0100 section 3): FileSystemService hands it every protected write.
        services.AddSingleton<IFileSystemAuditSink>(provider => new FileSystemAuditLog(
            provider.GetRequiredService<LogFileWriter>(),
            provider.GetRequiredService<ISimulationClock>()));
        // Every dependency is a singleton, so the host's root ILoggerFactory can build this provider
        // before any Blazor scope exists. ILogger<T> entries land in /var/log/syslog.
        services.AddSingleton<ILoggerProvider>(provider => new HackerOsDiagnosticLoggerProvider(
            provider.GetRequiredService<IDiagnosticSink>(),
            provider.GetRequiredService<LogFileWriter>(),
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetRequiredService<IDiagnosticRedactor>()));
        // Keep this provider scoped to HackerOS's own log categories: the ASP.NET Core host
        // (Kestrel, hosting lifetime, HTTPS redirection, etc.) also resolves every registered
        // ILoggerProvider, and its framework-internal chatter isn't a HackerOS diagnostic event.
        services.AddLogging(builder =>
        {
            builder.AddFilter<HackerOsDiagnosticLoggerProvider>("Microsoft", LogLevel.None);
            builder.AddFilter<HackerOsDiagnosticLoggerProvider>("System", LogLevel.None);
        });

        services.AddScoped<HostExceptionReporter>();
        services.AddSingleton<INotificationQueue>(_ => new InMemoryNotificationQueue(100));

        // IJSRuntime is scoped per Blazor Server circuit (WASM hosts have exactly one implicit
        // scope for the app's lifetime, so this is a no-op behavior change there). Everything in
        // this file that construct-injects IJSRuntime — directly or transitively — must be Scoped,
        // not Singleton: a singleton is constructed once, on first resolution, and Blazor Web Apps
        // construct+inject root/child components (running their [Inject] properties) during the
        // static-SSR pass of the initial HTTP response even when that component's own @rendermode
        // has prerender:false — prerender:false only skips calling its lifecycle methods during
        // that pass, not construction. A singleton first resolved there permanently captures that
        // pass's unattached IJSRuntime (no live circuit yet), and every later real circuit reuses
        // that same dead instance, so its JS interop calls fail forever with "cannot be issued at
        // this time... statically rendered" — regardless of which lifecycle method later calls it.
        // Scoped services get a fresh instance per circuit's own DI scope, which is exactly what's
        // needed here. See doc/en/wasm/dev/old/Global-FileView-And-MessagingSystem/integrationPlan.md Phase 4 live
        // browser check notes.
        services.AddScoped<IndexedDbLocalUserRepository>();
        services.AddScoped<ILocalUserRepository>(provider =>
            provider.GetRequiredService<IndexedDbLocalUserRepository>());
        services.AddScoped<IndexedDbLocalGroupRepository>();
        services.AddScoped<ILocalGroupRepository>(provider =>
            provider.GetRequiredService<IndexedDbLocalGroupRepository>());

        // Settings are files (ADR 0044). The retired IndexedDB settings store is only read once by
        // the migration below; it is never written again.
        services.AddScoped<ILegacySettingsStore>(provider =>
            new IndexedDbLegacySettingsStore(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.AddScoped<IndexedDbCapabilityGrantRepository>();
        services.AddScoped<IPersistentCapabilityGrantRepository>(provider =>
            provider.GetRequiredService<IndexedDbCapabilityGrantRepository>());
        services.AddSingleton<ICapabilityGrantRepository>(_ => new CapabilityGrantRepository());
        services.AddSingleton<ITopicMessageBus>(provider =>
            new InMemoryTopicMessageBus(provider.GetRequiredService<ICapabilityGrantRepository>()));

        // Measurements of ADR 0057, in memory, from zero at each start, never persisted. CPU time is
        // measured when the ADR 0067 accountant is active, simulated otherwise (fed by
        // ResourceSamplingService); disk and network bytes are really counted by
        // the persistence, the filesystem service and each app's network gateway.
        // Optional measured CPU and memory (ADR 0066): AddHackerOsResourceAccounting, called by the
        // host before this method, may already have registered them. Otherwise both measurements are
        // off and every per-process figure stays simulated, exactly as before phase 5.
        // The simulated machine (ADR 0057): HackerOs:Hardware, registered by the host's
        // AddHackerOsHardware; a host that does not gets the default profile.
        services.TryAddSingleton(VirtualHardwareProfile.Default);
        services.TryAddSingleton(ResourceAccountingPlan.Disabled);
        services.TryAddSingleton<ICpuTimeAccountant>(NullCpuTimeAccountant.Instance);
        services.TryAddSingleton<IMemoryAccountant>(NullMemoryAccountant.Instance);
        services.TryAddSingleton<IKernelMemoryLedger>(NullKernelMemoryLedger.Instance);
        services.AddScoped<IProcessIoAccounting, ProcessIoAccounting>();
        services.AddScoped<IBlockDeviceStatistics>(_ => new BlockDeviceStatistics());
        services.AddScoped<INetworkInterfaceStatistics, NetworkInterfaceStatistics>();
        services.AddScoped<ICpuTimeAccounting>(provider =>
        {
            AppCatalog catalog = provider.GetRequiredService<AppCatalog>();
            // System time: system services, and processes the catalog does not know (OS-owned).
            return new CpuTimeAccounting(
                provider.GetRequiredService<VirtualHardwareProfile>(),
                process => !catalog.Manifests.ContainsKey(process.AppId)
                    || (process.Kind == AppKind.Service && catalog.IsSystemApp(process.AppId)),
                CpuAccountant(provider),
                // ADR 0070, section 2: the pool's workers are the cores of /proc/stat.
                () => provider.GetService<IWorkerPoolStatus>());
        });

        services.AddScoped<IndexedDbFileSystemProvider>(provider => new IndexedDbFileSystemProvider(
            provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(),
            provider.GetRequiredService<IBlockDeviceStatistics>()));
        services.AddScoped<IStorageUsageProvider>(provider => new IndexedDbStorageUsageProvider(
            provider.GetRequiredService<IndexedDbFileSystemProvider>(),
            provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.AddScoped<IndexedDbFileSystemBootstrapper>();
        services.AddScoped<IFileSystemProvider>(provider =>
            provider.GetRequiredService<IndexedDbFileSystemProvider>());
        // /proc and /dev are virtual, read-only mounts computed on every read and never written to
        // IndexedDB (ADR 0046). The process manager is resolved lazily: it depends on the session
        // service, which depends on FileSystemSeeder, which reaches /proc through this router.
        services.AddScoped<FileSystemMountRouter>(provider =>
            new FileSystemMountRouter(provider.GetRequiredService<IFileSystemProvider>(),
            [
                new FileSystemMount(ProcFileSystemProvider.DefaultMountPath, new ProcFileSystemProvider(
                    () => provider.GetRequiredService<IProcessManager>(),
                    provider.GetRequiredService<IResourceSimulator>(),
                    provider.GetRequiredService<VirtualHardwareProfile>(),
                    provider.GetRequiredService<ISimulationClock>(),
                    process => provider.GetRequiredService<ISessionService>().CurrentPrincipal is { } principal
                        && principal.UserId == process.UserId
                            ? principal.LoginName.Value
                            : null,
                    environments: () => provider.GetRequiredService<IProcessEnvironmentTable>(),
                    metrics: new ProcMetricsSources(
                        provider.GetRequiredService<ICpuTimeAccounting>(),
                        provider.GetRequiredService<IProcessIoAccounting>(),
                        provider.GetRequiredService<IBlockDeviceStatistics>(),
                        provider.GetRequiredService<INetworkInterfaceStatistics>(),
                        () => provider.GetRequiredService<IFileSystemMountRouter>(),
                        CpuAccountant(provider),
                        MemoryAccountant(provider),
                        // ADR 0071, section 5: /proc/hackeros/workers, on the hosts that have workers.
                        () => provider.GetService<IWorkerPoolStatus>(),
                        // ADR 0074, section 2: /proc/net/tcp lists the listening ports.
                        () => provider.GetService<ListeningPortTable>())
                    {
                        // ADR 0125: /proc/<pid>/limits and /proc/<pid>/fd.
                        ResourceLimits = () => provider.GetRequiredService<IProcessResourceLimitTable>(),
                        FileDescriptors = () => provider.GetRequiredService<IProcessFileDescriptorTable>(),
                        // ADR 0128: Job and Host of a background task's /proc/<pid>/status.
                        BackgroundJobs = () => provider.GetService<BackgroundJobService>(),
                        // ADR 0126: /proc/<pid>/oom_score(_adj) and oom_kill of /proc/vmstat.
                        OomScores = () => provider.GetService<InMemoryProcessOomScoreTable>(),
                        OomKills = () => provider.GetService<OomKiller>()?.KillCount ?? 0,
                        // ADR 0131, section 5: the first lines of /proc/hackeros/workers.
                        CrossOriginIsolation = () => provider.GetService<CrossOriginIsolationState>()?.Status
                    })),
                // ADR 0126: /dev/kmsg is the kernel ring buffer that dmesg reads.
                new FileSystemMount(DevFileSystemProvider.DefaultMountPath, new DevFileSystemProvider(
                    provider.GetRequiredService<ISimulationClock>(),
                    () => provider.GetService<KernelLog>()))
            ]));
        // ADR 0181: network drives (folders of the host computer) are mounted while the OS runs.
        services.AddScoped<IFileSystemMountRouter>(provider => provider.GetRequiredService<FileSystemMountRouter>());
        services.AddScoped<IDynamicFileSystemMountRouter>(provider => provider.GetRequiredService<FileSystemMountRouter>());
        services.AddScoped<IHostFolderAccess>(provider =>
            new BrowserHostFolderAccess(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.AddScoped<NetworkDriveService>(provider => new NetworkDriveService(
            provider.GetRequiredService<IDynamicFileSystemMountRouter>(),
            provider.GetRequiredService<IHostFolderAccess>(),
            () => provider.GetRequiredService<ISessionService>().CurrentPrincipal,
            provider.GetService<IEventBus>(),
            logger: provider.GetService<ILogger<NetworkDriveService>>()));
        // Depends on IFileSystemMountRouter, which is Scoped (see above) — see this file's
        // IJSRuntime scoping note.
        services.AddScoped<IFileSystemPathResolver, FileSystemPathResolver>();
        services.AddSingleton<IFileSystemAuthorizer, FileSystemAuthorizer>();
        // ADR 0185 §15: a network drive's watched folders are listed again while the page is visible.
        services.AddScoped<IHostActivitySignal>(provider =>
            new BrowserHostActivitySignal(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.AddScoped<IFileSystemService>(provider =>
        {
            FileSystemService service = ActivatorUtilities.CreateInstance<FileSystemService>(provider);
            service.HostActivity = provider.GetService<IHostActivitySignal>();
            return service;
        });
        services.AddTransient<FileSystemSeeder>();
        // Depends on IFileSystemService, which is Scoped (see above) — see this file's IJSRuntime
        // scoping note.
        services.AddScoped<SettingsStoreToFilesMigration>(provider => new SettingsStoreToFilesMigration(
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetRequiredService<ILegacySettingsStore>(),
            provider.GetService<ILogger<SettingsStoreToFilesMigration>>()));
        services.AddScoped<IOsSettingsService>(provider => new OsSettingsService(
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<ISessionService>(),
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetService<ITopicMessageBus>(),
            provider.GetService<IEventBus>(),
            provider.GetService<ILogger<OsSettingsService>>()));

        services.AddScoped<WebCryptoPasswordHasher>(provider =>
            new WebCryptoPasswordHasher(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.AddSingleton<ILoginProgressTracker, LoginProgressTracker>();

        // Optional server connection (ADR 0028) — opt-in, per-device; a device that never
        // connects sees no behavior change anywhere else in this composition.
        services.AddScoped<IServerConnectionRepository>(provider =>
            new IndexedDbServerConnectionRepository(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.AddSingleton<HttpClient>();
        // TryAdd, not Add: a host or a test may register its own clients first. Every host reaches
        // the server over HTTP and WebSocket since ADR 0130 retired the direct injection of ADR 0036.
        services.TryAddSingleton<IAccountClient, HttpAccountClient>();
        services.TryAddSingleton<IProxyClient, HttpProxyClient>();
        services.AddScoped<IServerConnectionService>(provider =>
        {
            WebCryptoPasswordHasher hasher = provider.GetRequiredService<WebCryptoPasswordHasher>();
            return new ServerConnectionService(
                provider.GetRequiredService<IServerConnectionRepository>(),
                provider.GetRequiredService<IAccountClient>(),
                asyncPasswordHasher: (password, salt, iterations, length, ct) =>
                    hasher.DeriveKeyAsync(password, salt, iterations, length, ct));
        });

        // ADR 0134: automatic connection to the server that served the OS, and the state the
        // notification-area icon shows. A browser host registers the client with its own origin
        // first (AddHackerOsBrowserHost); without one, only a server linked in Settings is used.
        services.TryAddScoped<IHostInfoClient>(provider => new HttpHostInfoClient(provider.GetRequiredService<HttpClient>(), ownOrigin: null));
        services.AddScoped<ServerPresenceService>(provider => new ServerPresenceService(
            provider.GetRequiredService<IServerConnectionService>(),
            provider.GetRequiredService<IHostInfoClient>(),
            cancellationToken => provider.GetRequiredService<IOsSettingsService>().ReadFileAsync(ServerPresenceService.ConfigFile, cancellationToken),
            provider.GetService<Microsoft.Extensions.Logging.ILogger<ServerPresenceService>>()));

        // Sync plumbing shared by every domain below; each is a no-op when disconnected. Settings
        // have no domain of their own anymore: user settings are home files carried by FileSystem
        // sync (ADR 0044 section 10).
        services.AddScoped<ISyncCursorRepository>(provider =>
            new IndexedDbSyncCursorRepository(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.AddScoped<ISyncRecordStateRepository>(provider =>
            new IndexedDbSyncRecordStateRepository(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.TryAddSingleton<ISyncClient, HttpSyncClient>();

        // FileSystem domain sync (ADR 0030) — reuses the same connection and sync scaffolding as
        // above; scoped to the active session's own /home/{userId}, so a no-op when
        // disconnected or no session is active.
        services.AddSingleton<IContentTransferClient, HttpContentTransferClient>();
        services.AddScoped<IFileSystemSyncService>(provider => new FileSystemSyncService(
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<ISessionService>(),
            provider.GetRequiredService<IServerConnectionService>(),
            provider.GetRequiredService<ISyncClient>(),
            provider.GetRequiredService<IContentTransferClient>(),
            provider.GetRequiredService<ISyncCursorRepository>(),
            provider.GetRequiredService<ISyncRecordStateRepository>(),
            logger: provider.GetService<Microsoft.Extensions.Logging.ILogger<FileSystemSyncService>>()));

        // Grants domain sync (ADR 0031) — pull-only, applies server-issued grants into the durable
        // grant store. Not wired into live capability enforcement in this pass (see ADR 0031).
        services.AddScoped<IGrantsSyncService>(provider => new GrantsSyncService(
            provider.GetRequiredService<IPersistentCapabilityGrantRepository>(),
            provider.GetRequiredService<IServerConnectionService>(),
            provider.GetRequiredService<ISyncClient>(),
            provider.GetRequiredService<ISyncCursorRepository>()));

        // FileAssociations domain sync (ADR 0033): /etc/hackeros/file-associations.json under its own
        // domain string. The AppCatalog domain is retired by ADR 0048: uninstalls are apps.config
        // files, and the user layer travels with /home through the FileSystem domain.
        services.AddScoped<IFileAssociationsSyncService>(provider => new FileAssociationsSyncService(
            provider.GetRequiredService<IOsSettingsService>(),
            provider.GetRequiredService<IServerConnectionService>(),
            provider.GetRequiredService<ISyncClient>(),
            provider.GetRequiredService<ISyncCursorRepository>(),
            provider.GetRequiredService<ISyncRecordStateRepository>()));

        // ADR 0101: periodic synchronization (started at login, idle without a server) and the
        // state of the OS conflict dialog the shell renders.
        services.AddScoped<SyncScheduler>(provider => new SyncScheduler(
            provider.GetRequiredService<IFileSystemSyncService>(),
            provider.GetRequiredService<IGrantsSyncService>(),
            provider.GetRequiredService<IFileAssociationsSyncService>(),
            provider.GetRequiredService<IServerConnectionService>(),
            provider.GetRequiredService<ISessionService>(),
            provider.GetRequiredService<IOsSettingsService>(),
            provider.GetRequiredService<ITopicMessageBus>(),
            logger: provider.GetService<Microsoft.Extensions.Logging.ILogger<SyncScheduler>>(),
            events: provider.GetRequiredService<IEventBus>()));
        services.AddScoped<SyncConflictCenter>(provider => new SyncConflictCenter(
            provider.GetRequiredService<IFileSystemSyncService>()));

        // Admin re-authentication elevation (ADR 0042) — powers the terminal's `sudo` built-in and
        // the admin-approval prompt on a denied permission. Never touches ISessionService's own
        // state machine; verifies credentials and mints short-lived, single-use tickets only.
        services.AddScoped<IElevationTicketStore, InMemoryElevationTicketStore>();
        services.AddScoped<IAdminElevationVerifier>(provider =>
        {
            WebCryptoPasswordHasher elevationHasher = provider.GetRequiredService<WebCryptoPasswordHasher>();
            return new AdminElevationVerifier(
                provider.GetRequiredService<ILocalUserRepository>(),
                provider.GetRequiredService<IAuditLog>(),
                asyncHasher: (password, salt, iterations, length, ct) =>
                    elevationHasher.DeriveKeyAsync(password, salt, iterations, length, ct));
        });
        services.AddScoped<IElevationService>(provider => new ElevationService(
            provider.GetRequiredService<IAdminElevationVerifier>(),
            provider.GetRequiredService<IElevationTicketStore>()));

        // Dangerous capabilities (ADR 0049): remembered decisions, the launch gate and its
        // secure-desktop overlay (the broker is the host's default approval prompt), and the
        // administration behind Settings > Permissions. Scoped: they read the session user's files.
        services.AddScoped<IAppPermissionStore>(provider => new AppPermissionStore(
            provider.GetRequiredService<IOsSettingsService>(),
            provider.GetService<ILogger<AppPermissionStore>>()));
        services.AddScoped<DangerousCapabilityApprovalBroker>();
        services.AddScoped<IDangerousCapabilityApprovalPrompt>(provider =>
            provider.GetRequiredService<DangerousCapabilityApprovalBroker>());
        services.AddScoped<IDangerousCapabilityGate>(provider => new DangerousCapabilityGate(
            provider.GetRequiredService<AppCatalog>(),
            provider.GetRequiredService<ICapabilityGrantRepository>(),
            provider.GetRequiredService<IAppPermissionStore>(),
            provider.GetRequiredService<IOsSettingsService>(),
            provider.GetRequiredService<IAdminElevationVerifier>(),
            provider.GetRequiredService<IAuditLog>(),
            provider.GetRequiredService<IDangerousCapabilityApprovalPrompt>()));
        // OS authentication agent (ADR 0055 section 5, polkit-like): gateways that need an
        // administrator ask it; its secure-desktop overlay is rendered by the shell.
        services.AddScoped<AdministratorAuthenticationBroker>();
        services.AddScoped<IAdministratorAuthenticationPrompt>(provider =>
            provider.GetRequiredService<AdministratorAuthenticationBroker>());
        services.AddScoped<IAdministratorAuthenticationAgent>(provider => new AdministratorAuthenticationAgent(
            provider.GetRequiredService<IAdminElevationVerifier>(),
            provider.GetRequiredService<IAuditLog>(),
            provider.GetRequiredService<IAdministratorAuthenticationPrompt>())
        {
            // ADR 0168 section 4: an administrator may confirm with a passkey instead of a password.
            PasskeyVerifier = provider.GetRequiredService<IAdminElevationVerifier>() is AdminElevationVerifier verifier
                ? (login, ct) => verifier.VerifyWithPasskeyAsync(login, provider.GetRequiredService<PasskeyService>(), ct)
                : null
        });
        services.AddScoped<AppPermissionAdministration>(provider => new AppPermissionAdministration(
            provider.GetRequiredService<AppCatalog>(),
            provider.GetRequiredService<IAppPermissionStore>(),
            provider.GetRequiredService<IOsSettingsService>(),
            provider.GetRequiredService<IAdminElevationVerifier>(),
            provider.GetRequiredService<IAuditLog>()));
        // Reboot and factory reset (ADR 0056 section 11): the browser reloads the page and deletes
        // the OS database; HackSystem reaches them only after the authentication agent agreed.
        services.AddScoped<ISystemPowerDevice>(provider =>
            new BrowserSystemPowerDevice(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.AddScoped<SystemPowerService>(provider => new SystemPowerService(
            provider.GetRequiredService<ISystemPowerDevice>(),
            provider.GetRequiredService<ISessionService>(),
            provider.GetRequiredService<LogFileWriter>(),
            provider.GetRequiredService<IAuditLog>()));
        // Browser databases of web sites, hackweb.<app id> (ADR 0088): IndexedDB of the host browser.
        services.AddScoped<HackerOs.Infrastructure.Browser.WebData.WebSiteDatabaseInterop>();
        services.AddScoped<HackerOs.Platform.Core.WebData.IWebSiteDatabaseStore>(provider =>
            new HackerOs.Ecosystem.WebData.IndexedDbWebSiteDatabaseStore(
                provider.GetRequiredService<HackerOs.Infrastructure.Browser.WebData.WebSiteDatabaseInterop>()));
        // Raw storage snapshot behind HackStorageDiagnostics (ADR 0056 section 11).
        services.AddScoped<IStorageInspector>(provider =>
            new IndexedDbStorageInspector(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        // Whole-machine backup and restore behind HackSystemBackup (ADR 0099 section 1): IndexedDB
        // export/import, download to the host and the host's file picker. Every host runs the OS in
        // the browser since ADR 0130; a host without a browser leaves the gateway unsupported and
        // Settings hides the page.
        services.AddScoped<ISystemBackupDevice>(provider =>
            new BrowserSystemBackupDevice(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.AddScoped<SystemBackupService>(provider => new SystemBackupService(
            provider.GetRequiredService<ISystemBackupDevice>(),
            provider.GetRequiredService<SystemPowerService>(),
            provider.GetRequiredService<IAuditLog>()));
        // Free space of the browser storage (ADR 0099 section 2): the low-space warning, the root
        // reserve the filesystem enforces, and the real figures behind df.
        services.AddScoped<IStorageCapacityProbe>(provider =>
            new BrowserStorageManager(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
        services.AddScoped<StorageSpaceMonitor>(provider => new StorageSpaceMonitor(
            provider.GetRequiredService<IStorageCapacityProbe>(),
            provider.GetRequiredService<INotificationQueue>(),
            () => provider.GetRequiredService<ISessionService>().CurrentPrincipal,
            language: () => provider.GetRequiredService<HackerOs.Platform.Core.Globalization.SessionLocaleService>().Culture));
        services.AddScoped<IStorageSpaceGuard>(provider => provider.GetRequiredService<StorageSpaceMonitor>());

        // User keyring (ADR 0100 section 2): ~/.local/share/keyrings/login.keyring, sealed with
        // AES-GCM through Web Crypto, unlocked by the session service at login.
        services.AddScoped<IKeyringCipher>(provider =>
            new HackerOs.Ecosystem.Keyring.WebCryptoKeyringCipher(provider.GetRequiredService<WebCryptoPasswordHasher>()));
        services.AddScoped<UserKeyringService>(provider => new UserKeyringService(
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetRequiredService<IKeyringCipher>(),
            provider.GetRequiredService<IAuditLog>(),
            provider.GetService<ILogger<UserKeyringService>>()));

        // passwd and the Settings "Users" tab (ADR 0120): new credentials hashed through Web Crypto
        // like at login, the login keyring re-sealed or set aside, every attempt in auth.log.
        services.AddScoped<LocalPasswordService>(provider =>
        {
            WebCryptoPasswordHasher hasher = provider.GetRequiredService<WebCryptoPasswordHasher>();
            return new LocalPasswordService(
                provider.GetRequiredService<ILocalUserRepository>(),
                provider.GetRequiredService<IAuditLog>(),
                provider.GetRequiredService<UserKeyringService>(),
                asyncHasher: (password, salt, iterations, length, ct) =>
                    hasher.DeriveKeyAsync(password, salt, iterations, length, ct));
        });

        // Passkeys (ADR 0168): WebAuthn through webAuthn.js, verified by the OS, a keyring slot per
        // PRF passkey. Adding and deleting accounts (ADR 0169) hashes like at login.
        services.AddScoped<IPasskeyAuthenticator>(provider =>
            new HackerOs.Ecosystem.Passkeys.BrowserPasskeyAuthenticator(provider.GetRequiredService<IJSRuntime>()));
        services.AddScoped<PasskeyService>(provider => new PasskeyService(
            provider.GetRequiredService<ILocalUserRepository>(),
            provider.GetRequiredService<IPasskeyAuthenticator>(),
            provider.GetRequiredService<IAuditLog>(),
            provider.GetRequiredService<UserKeyringService>()));
        services.AddScoped<LocalAccountService>(provider =>
        {
            WebCryptoPasswordHasher hasher = provider.GetRequiredService<WebCryptoPasswordHasher>();
            return new LocalAccountService(
                provider.GetRequiredService<ILocalUserRepository>(),
                provider.GetRequiredService<ILocalGroupRepository>(),
                provider.GetRequiredService<IAuditLog>(),
                provider.GetRequiredService<IFileSystemService>(),
                asyncHasher: (password, salt, iterations, length, ct) =>
                    hasher.DeriveKeyAsync(password, salt, iterations, length, ct));
        });

        services.AddScoped<ISessionService>(provider =>
        {
            WebCryptoPasswordHasher hasher = provider.GetRequiredService<WebCryptoPasswordHasher>();
            return new LocalSessionService(
                provider.GetRequiredService<ILocalUserRepository>(),
                provider.GetRequiredService<FileSystemSeeder>(),
                provider.GetRequiredService<IEventBus>(),
                provider.GetRequiredService<IAuditLog>(),
                InstallationId.FromGuid(Guid.NewGuid()),
                DeviceId.FromGuid(Guid.NewGuid()),
                progressTracker: provider.GetRequiredService<ILoginProgressTracker>(),
                asyncHasher: (password, salt, iterations, length, ct) =>
                    hasher.DeriveKeyAsync(password, salt, iterations, length, ct),
                grantRepository: provider.GetService<ICapabilityGrantRepository>(),
                catalog: provider.GetService<AppCatalog>(),
                profileInitializer: provider.GetRequiredService<SettingsStoreToFilesMigration>(),
                keyring: provider.GetRequiredService<UserKeyringService>());
        });
        // Per-process arguments, working directory and environment (ADR 0050): filled and
        // emptied by the process manager, read by /proc and each app's Environment gateway.
        services.AddScoped<InMemoryProcessEnvironmentTable>();
        services.AddScoped<IProcessEnvironmentTable>(provider =>
            provider.GetRequiredService<InMemoryProcessEnvironmentTable>());
        // Per-process resource limits and open descriptors (ADR 0125), filled and emptied by the
        // process manager like the environment table; HackerOs:ResourceLimits:Enabled=false turns
        // every limit off.
        services.TryAddSingleton(ResourceLimitsSettings.Default);
        services.AddScoped<InMemoryProcessResourceLimitTable>(provider =>
            new InMemoryProcessResourceLimitTable(provider.GetRequiredService<ResourceLimitsSettings>().Enabled));
        services.AddScoped<IProcessResourceLimitTable>(provider =>
            provider.GetRequiredService<InMemoryProcessResourceLimitTable>());
        services.AddScoped<InMemoryProcessFileDescriptorTable>(provider =>
        {
            InMemoryProcessResourceLimitTable limits = provider.GetRequiredService<InMemoryProcessResourceLimitTable>();
            return new InMemoryProcessFileDescriptorTable(pid => limits.GetOrNone(pid).NoFile.Soft);
        });
        services.AddScoped<IProcessFileDescriptorTable>(provider =>
            provider.GetRequiredService<InMemoryProcessFileDescriptorTable>());
        // RLIMIT_CPU and RLIMIT_RSS against the measured CPU time and memory (ADR 0125, section 2);
        // inactive while both measurements are off (ADR 0066).
        services.AddScoped<ResourceLimitEnforcer>(provider => new ResourceLimitEnforcer(
            provider.GetRequiredService<ISimulationClock>(),
            () => provider.GetRequiredService<IProcessManager>(),
            provider.GetRequiredService<IProcessResourceLimitTable>(),
            CpuAccountant(provider),
            MemoryAccountant(provider)));
        // The OOM killer (ADR 0126): oom_score_adj per process, the kernel ring buffer behind
        // /dev/kmsg and kern.log, and the killer itself, configured from /etc/hackeros/memory.config
        // and started by EcosystemBootCoordinator once settings exist. Off by default; on, it acts
        // only where memory is measured, plus the guard against the browser's real memory limit.
        services.AddScoped<InMemoryProcessOomScoreTable>();
        services.AddScoped<KernelLog>(provider => new KernelLog(
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetService<LogFileWriter>()));
        services.AddScoped<OomKiller>(provider => new OomKiller(
            provider.GetRequiredService<ISimulationClock>(),
            () => provider.GetRequiredService<IProcessManager>(),
            new SystemMemoryProbe(
                () => provider.GetRequiredService<IProcessManager>(),
                provider.GetRequiredService<IResourceSimulator>(),
                provider.GetRequiredService<VirtualHardwareProfile>(),
                MemoryAccountant(provider)),
            () => provider.GetService<InMemoryProcessOomScoreTable>(),
            provider.GetRequiredService<KernelLog>(),
            new BrowserRuntimeMemorySource(() => provider.GetService<IWorkerPoolStatus>()),
            new NotificationQueueOomNotifier(
                provider.GetRequiredService<INotificationQueue>(),
                provider.GetRequiredService<ISimulationClock>(),
                () => provider.GetService<AppCatalog>(),
                () => provider.GetRequiredService<ISessionService>().CurrentPrincipal,
                () => provider.GetRequiredService<HackerOs.Platform.Core.Globalization.SessionLocaleService>().Culture)));
        services.AddScoped<IProcessManager>(provider =>
        {
            InMemoryProcessManager manager = new(
                provider.GetRequiredService<ISimulationClock>(),
                provider.GetRequiredService<ISessionService>(),
                provider.GetRequiredService<IEventBus>(),
                200,
                provider.GetRequiredService<InMemoryProcessEnvironmentTable>(),
                provider.GetRequiredService<InMemoryProcessResourceLimitTable>(),
                provider.GetRequiredService<InMemoryProcessFileDescriptorTable>(),
                provider.GetRequiredService<InMemoryProcessOomScoreTable>());
            // The limit checks start with the process table, like the kernel's timer tick.
            provider.GetRequiredService<ResourceLimitEnforcer>().Start();
            return manager;
        });
        // pam_limits (ADR 0125, section 3): /etc/security/limits.conf at the session's first launch.
        services.AddScoped<LoginResourceLimitsProvider>(provider => new LoginResourceLimitsProvider(
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetRequiredService<IProcessResourceLimitTable>(),
            async (principal, cancellationToken) =>
            {
                ILocalGroupRepository groups = provider.GetRequiredService<ILocalGroupRepository>();
                List<string> names = [];
                foreach (LocalGroupId groupId in principal.GroupIds)
                {
                    if (await groups.FindByIdAsync(groupId, cancellationToken).ConfigureAwait(false) is { } group)
                    {
                        names.Add(group.Name.Value);
                    }
                }

                return names;
            },
            provider.GetRequiredService<IAuditLog>()));
        // Login environment (/etc/environment) and machine name (/etc/hostname), ADR 0050.
        services.AddScoped<LoginEnvironmentProvider>(provider => new LoginEnvironmentProvider(
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetService<ILogger<LoginEnvironmentProvider>>()));
        // ADR 0104: the platform's language (sign-in screens: /etc/locale.conf; desktop: the
        // user's login environment) and the IStringLocalizer<T> every platform component injects.
        services.AddHackerOsPlatformLocalization();
        services.AddSingleton<IResourceSimulator>(provider => new DeterministicResourceSimulator(
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetRequiredService<ISimulationRandom>(),
            provider.GetRequiredService<VirtualHardwareProfile>()));
        // The scheduler tick: samples every process once per simulated second (ADR 0057), started
        // by the boot sequence next to the clock driver.
        services.AddScoped<ResourceSamplingService>(provider => new ResourceSamplingService(
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetRequiredService<IResourceSimulator>(),
            () => provider.GetRequiredService<IProcessManager>(),
            provider.GetRequiredService<ICpuTimeAccounting>(),
            provider.GetRequiredService<IProcessIoAccounting>()));

        services.AddScoped<IndexedDbAppCatalogRepository>();
        services.AddScoped<IPersistentAppCatalogRepository>(provider =>
            provider.GetRequiredService<IndexedDbAppCatalogRepository>());
        // ADR 0071, section 2: "the system is started", raised by EcosystemBootCoordinator once the
        // desktop is shown; the worker pool (WebAssembly hosts only) warms up after it.
        services.TryAddSingleton<SystemStartupSignal>();
        // ADR 0131: what the page found at boot; the browser host also registers the probe.
        services.TryAddSingleton<CrossOriginIsolationState>();
        services.AddScoped<EcosystemBootCoordinator>();

        services.AddSingleton(_ => catalog ?? CreateEmptyCatalog());
        // ADR 0098 §5: assets checked against their manifest hash at first read. The WebAssembly host
        // registers its _content/ source first; a host without one serves no asset.
        services.TryAddSingleton<IAppAssetSource>(NoAppAssetSource.Instance);
        services.AddSingleton<AppAssetStore>(provider => new AppAssetStore(
            provider.GetRequiredService<AppCatalog>(),
            provider.GetRequiredService<IAppAssetSource>(),
            provider.GetService<ILogger<AppAssetStore>>()));
        // ADR 0182: apps installed from their GitHub repository; the OS downloads with fetch itself.
        services.TryAddSingleton<IAppPackageDownloader>(static _ => new HttpAppPackageDownloader(new HttpClient()));
        services.TryAddSingleton(static provider => new AppPackageInstaller(
            provider.GetRequiredService<IAppPackageDownloader>(),
            logger: provider.GetService<ILogger<AppPackageInstaller>>()));
        services.AddSingleton<AppEnablementRegistry>(provider =>
            new AppEnablementRegistry(provider.GetRequiredService<AppCatalog>()));
        services.AddSingleton<IAppEnablementRegistry>(provider =>
            provider.GetRequiredService<AppEnablementRegistry>());
        // ADR 0074: the local half of the network stack, one per session. The table drops the
        // sockets of dead processes; the simulated localhost's open ports are its system daemons.
        services.AddScoped<ListeningPortTable>(provider => new ListeningPortTable(
            pid => provider.GetRequiredService<IProcessManager>().TryGetActive(pid, out _),
            () => provider.GetRequiredService<ISimulatedNetworkService>().GetHost("127.0.0.1") is { } localhost
                ? localhost.Ports.Where(static port => port.State == SimulatedPortState.Open).Select(static port => port.PortNumber)
                : []));
        // ADR 0080, section 4: hackdns publishes the names bound by the hosted static sites (.site files).
        services.AddScoped<IHostedSiteNameSource>(provider => new HostedSiteNameSource(
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<ISimulationClock>()));
        services.AddScoped<HackDnsResolver>(provider =>
        {
            AppCatalog appCatalog = provider.GetRequiredService<AppCatalog>();
            IAppEnablementRegistry enablement = provider.GetRequiredService<IAppEnablementRegistry>();
            return new HackDnsResolver(
                provider.GetRequiredService<ISimulatedNetworkService>(),
                provider.GetRequiredService<IFileSystemService>(),
                provider.GetRequiredService<ISimulationClock>(),
                // Source 3: the URLs of installed web apps, minus the uninstalled ones.
                () => appCatalog.Manifests.Values.Where(manifest => manifest.Web is not null && enablement.IsEnabled(manifest.Id)),
                provider.GetService<IHostedSiteNameSource>());
        });
        services.AddScoped<LocalNetworkServices>(provider => new LocalNetworkServices(
            provider.GetRequiredService<ListeningPortTable>(),
            provider.GetRequiredService<HackDnsResolver>(),
            // Lazy: the orchestrator depends on the context factory, which carries these services.
            new WebAppActivator(
                () => provider.GetRequiredService<AppLifecycleOrchestrator>(),
                provider.GetRequiredService<ListeningPortTable>())));
        services.AddScoped<AppExecutionContextFactory>(provider => new AppExecutionContextFactory(
            provider.GetRequiredService<ICapabilityGrantRepository>(),
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<IEventBus>(),
            provider.GetRequiredService<ITopicMessageBus>(),
            provider.GetRequiredService<INotificationQueue>(),
            provider.GetRequiredService<IDiagnosticSink>(),
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetRequiredService<IProcessManager>(),
            // Resolved lazily: AppIntentDispatcher depends (transitively, via AppLifecycleOrchestrator)
            // on this very factory, so eagerly injecting it here would be circular.
            intentDispatcherProvider: () => provider.GetRequiredService<AppIntentDispatcher>(),
            logWriter: provider.GetRequiredService<LogFileWriter>(),
            environments: provider.GetRequiredService<IProcessEnvironmentTable>(),
            machineName: () => provider.GetRequiredService<LoginEnvironmentProvider>().HostName,
            // ADR 0056 section 6: each app's Network gateway, with the OS-owned real-network fallback.
            network: provider.GetRequiredService<ISimulatedNetworkService>(),
            serverConnection: provider.GetRequiredService<IServerConnectionService>(),
            proxy: provider.GetRequiredService<IProxyClient>(),
            // HackApps / HackAppManagement (ADR 0056 sections 2 and 3). Lazy for the same reason:
            // the installation service depends on the orchestrator, which depends on this factory.
            appCatalog: new AppCatalogGatewayServices(
                provider.GetRequiredService<AppCatalog>(),
                provider.GetRequiredService<IAppEnablementRegistry>(),
                () => provider.GetRequiredService<FileAssociationResolver>(),
                () => provider.GetRequiredService<IAppInstallationService>(),
                () => provider.GetRequiredService<IAdministratorAuthenticationAgent>(),
                () => provider.GetRequiredService<AppAssetStore>())
            {
                Packages = () => provider.GetService<AppPackageInstaller>()
            },
            // Lazy for the same reason: ShellInterpreter depends on the orchestrator (ADR 0056 section 7).
            shellHostProvider: () => provider.GetRequiredService<ShellHostService>(),
            // ADR 0057: traffic counters for /proc/net/dev and /proc/<pid>/io, and HackDriveInfo.
            metrics: new SystemMetricsServices(
                provider.GetRequiredService<INetworkInterfaceStatistics>(),
                provider.GetRequiredService<IProcessIoAccounting>(),
                new AppDriveGateway(
                    () => provider.GetRequiredService<IFileSystemMountRouter>(),
                    provider.GetRequiredService<VirtualHardwareProfile>(),
                    provider.GetRequiredService<IStorageUsageProvider>())
                {
                    // ADR 0181: HackDriveInfo.MountHostFolderAsync, ReconnectAsync and UnmountAsync.
                    // Resolved now, with the execution factory, so it sees the first login event.
                    NetworkDrives = NetworkDrivesOf(provider)
                },
                // ADR 0068: write-stream buffers and sockets in flight, charged to each app's process.
                provider.GetRequiredService<IKernelMemoryLedger>())
            {
                // ADR 0146: HackCrossOriginIsolation reads the status the boot probe recorded.
                CrossOriginIsolation = new AppCrossOriginIsolationGateway(() => provider.GetService<CrossOriginIsolationState>()?.Status)
            },
            // HackClipboard (ADR 0056 section 12): one OS clipboard shared by every app of the session.
            clipboard: provider.GetRequiredService<HackerOs.Platform.Core.Clipboard.SystemClipboard>(),
            // HackSettings.OpenSystemAsync, HackPermissionAdministration, HackServerConnection,
            // HackSystem and HackStorageDiagnostics (ADR 0056 sections 8 to 11), all lazy.
            localNetwork: provider.GetRequiredService<LocalNetworkServices>(),
            // HackWebDb (ADR 0088): hackweb.<app id> for web apps, and their listing in Diagnostic.
            webDatabases: provider.GetRequiredService<HackerOs.Platform.Core.WebData.IWebSiteDatabaseStore>(),
            // HackKeyring (ADR 0100 section 2): the session user's keyring, per app.
            keyring: provider.GetRequiredService<UserKeyringService>(),
            // HackAccessibility (ADR 0105 section 3): the session's effective accessibility preferences.
            accessibility: provider.GetRequiredService<HackerOs.Platform.Core.Appearance.AccessibilityPreferenceService>(),
            administration: new SystemAdministrationGatewayServices(
                () => provider.GetRequiredService<IOsSettingsService>(),
                () => provider.GetRequiredService<AppPermissionAdministration>(),
                new ServerConnectionGatewayServices(
                    () => provider.GetRequiredService<IServerConnectionService>(),
                    () => provider.GetRequiredService<IFileSystemSyncService>(),
                    () => provider.GetRequiredService<IGrantsSyncService>(),
                    () => provider.GetRequiredService<IFileAssociationsSyncService>(),
                    () => provider.GetRequiredService<IProxyClient>(),
                    () => provider.GetRequiredService<SyncScheduler>(),
                    () => provider.GetRequiredService<SyncConflictCenter>(),
                    () => provider.GetRequiredService<IOsSettingsService>()),
                () => provider.GetRequiredService<SystemPowerService>(),
                () => provider.GetRequiredService<IStorageInspector>(),
                () => provider.GetRequiredService<IAdministratorAuthenticationAgent>(),
                OperatingSystem.IsBrowser() ? () => provider.GetRequiredService<SystemBackupService>() : null)
            {
                Passwords = () => provider.GetRequiredService<LocalPasswordService>(),
                // HackUserAccounts (ADR 0168, 0169): passkeys of the session user, adding and deleting accounts.
                Accounts = () => provider.GetRequiredService<LocalAccountService>(),
                Passkeys = () => provider.GetRequiredService<PasskeyService>(),
                // HackComponentProviders (ADR 0156): a third-party provider goes through the approval window.
                ComponentProviders = () => provider.GetRequiredService<HackerOs.Platform.Core.ComponentProviders.ComponentProviderService>(),
                ApprovalPrompt = () => provider.GetRequiredService<IDangerousCapabilityApprovalPrompt>(),
                // HackFileMenuEntries (ADR 0159 §8): Settings lists and hides the registered entries.
                FileMenuEntries = () => provider.GetRequiredService<HackerOs.Platform.Core.ContextMenus.FileContextMenuAdministration>(),
                // HackScreens (ADR 0184): Settings > Screens drives the secondary screens of the browser host.
                Screens = OperatingSystem.IsBrowser() ? () => provider.GetRequiredService<ScreenControl>() : null,
            })
        {
            // HackBackground (ADR 0128): each task a child process, in a worker when the host has some.
            BackgroundJobs = () => provider.GetRequiredService<BackgroundJobService>(),
            // ADR 0193: IAppExecutionContext.TimeZone follows Settings > Date and time.
            TimeZones = provider.GetRequiredService<HackerOs.Platform.Core.Globalization.SessionTimeZoneService>(),
        });
        services.AddScoped<BackgroundJobService>(provider => new BackgroundJobService(
            provider.GetRequiredService<IProcessManager>(),
            // Lazy: the orchestrator depends on the context factory, which carries this service.
            appId => provider.GetRequiredService<AppLifecycleOrchestrator>().TryGetDescriptor(appId, out AppDescriptor? descriptor) ? descriptor : null,
            () => provider.GetService<IRemoteProcessHost>()));
        services.AddScoped<HackerOs.Platform.Core.Clipboard.SystemClipboard>(provider =>
            new HackerOs.Platform.Core.Clipboard.SystemClipboard(
                provider.GetRequiredService<IEventBus>(),
                provider.GetRequiredService<IKernelMemoryLedger>()));
        // Shared clipboard with the real machine (ADR 0065), started by App.razor once settings exist.
        // Depends on IJSRuntime through its channel — see this file's IJSRuntime scoping note.
        services.AddScoped<HackerOs.Simulation.Abstractions.Clipboard.IHostClipboardChannel, HackerOs.Infrastructure.Browser.Interop.BrowserHostClipboardChannel>();
        services.AddScoped<HackerOs.Platform.Core.Clipboard.HostClipboardBridge>();
        services.AddScoped<FileAssociationResolver>(provider => new FileAssociationResolver(
            provider.GetRequiredService<AppCatalog>(),
            provider.GetRequiredService<IAppEnablementRegistry>(),
            provider.GetRequiredService<IOsSettingsService>(),
            provider.GetRequiredService<IFileSystemService>()));

        // Simulated network (ADR 0085, replacing ADR 0034's smoke-test-only seed): the default
        // hosts and sites (hackersearch.net is the Browser's home page) plus the .hackeros
        // smoke-test fixtures, which the E2E tests and the docs still use.
        services.AddSingleton<ISimulatedWebsiteRegistry>(_ =>
        {
            InMemorySimulatedWebsiteRegistry registry = new();
            foreach (ISimulatedWebsiteController controller in DefaultSimulatedHostCatalog.BuildWebsites().Concat(SmokeTestNetworkSeed.Websites))
            {
                registry.Register(controller);
            }

            return registry;
        });
        services.AddSingleton<ISimulatedNetworkService>(provider => new InMemorySimulatedNetworkService(
            [.. DefaultSimulatedHostCatalog.Build(), .. SmokeTestNetworkSeed.Hosts],
            provider.GetRequiredService<ISimulatedWebsiteRegistry>()));

        services.AddScoped<AppLifecycleOrchestrator>(provider => new AppLifecycleOrchestrator(
            provider.GetRequiredService<AppCatalog>(),
            descriptorProvider?.Invoke(provider) ?? new Dictionary<string, AppDescriptor>(StringComparer.Ordinal),
            provider.GetRequiredService<AppEnablementRegistry>(),
            provider.GetRequiredService<IProcessManager>(),
            provider.GetRequiredService<ICapabilityGrantRepository>(),
            provider.GetRequiredService<AppExecutionContextFactory>(),
            provider.GetRequiredService<IEventBus>(),
            descriptorLoaderProvider?.Invoke(provider),
            provider,
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<IElevationTicketStore>(),
            provider.GetRequiredService<IDangerousCapabilityGate>(),
            provider.GetRequiredService<LoginEnvironmentProvider>(),
            // ADR 0070: commands and services in Web Workers, on the hosts that have them (the
            // WebAssembly hosts register it in Program.cs); none on the server (section 6).
            provider.GetService<IRemoteProcessHost>(),
            provider.GetRequiredService<ISimulationClock>(),
            // ADR 0097: a service that fails is written to /var/log/syslog.
            provider.GetService<ILogger<AppLifecycleOrchestrator>>())
        {
            // ADR 0125, section 3: a session's root process gets the limits of limits.conf.
            LoginResourceLimits = provider.GetRequiredService<LoginResourceLimitsProvider>()
        });
        // Uninstall/reinstall through /etc/hackeros/apps.config + ~/.config/hackeros/apps.config
        // (ADR 0048). Started by EcosystemBootCoordinator once storage is ready; feeds the
        // AppEnablementRegistry every launch path consults.
        services.AddScoped<AppEnablementToFilesMigration>(provider => new AppEnablementToFilesMigration(
            provider.GetRequiredService<IFileSystemService>(),
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetRequiredService<AppCatalog>(),
            provider.GetService<ILogger<AppEnablementToFilesMigration>>()));
        services.AddScoped<AppInstallationService>(provider =>
        {
            AppInstallationService installation = new(
                provider.GetRequiredService<AppCatalog>(),
                provider.GetRequiredService<AppEnablementRegistry>(),
                provider.GetRequiredService<AppLifecycleOrchestrator>(),
                provider.GetRequiredService<IFileSystemService>(),
                provider.GetRequiredService<ISessionService>(),
                provider.GetRequiredService<ISimulationClock>(),
                provider.GetRequiredService<ITopicMessageBus>(),
                provider.GetRequiredService<IEventBus>(),
                provider.GetService<ILogger<AppInstallationService>>(),
                // ADR 0088 section 4: uninstalling a web app deletes its browser database.
                provider.GetRequiredService<HackerOs.Platform.Core.WebData.IWebSiteDatabaseStore>());
            // ADR 0156 §4: an app uninstalled for the machine loses its line in components.config,
            // whether Settings, the terminal or an edit of apps.config uninstalled it.
            installation.Changed += (_, _) => _ = RemoveUninstalledComponentProvidersAsync(provider, installation);
            // ADR 0151 §3: the first reload (at boot, StartAsync) writes and reads context-menus.d; an app
            // uninstalled for the machine then loses its file, a reinstalled one gets it back.
            installation.Changed += (_, _) => _ = SyncFileContextMenusAsync(provider);
            return installation;
        });
        services.AddScoped<IAppInstallationService>(provider => provider.GetRequiredService<AppInstallationService>());
        // ADR 0097 section 4: system services at the end of the boot, the user's at each login;
        // stopped at the logout and the shutdown. Resolved with EcosystemBootCoordinator, so it
        // subscribes to the session events before the first login.
        services.AddScoped<ServiceStartupCoordinator>(provider => new ServiceStartupCoordinator(
            provider.GetRequiredService<AppLifecycleOrchestrator>(),
            provider.GetRequiredService<IEventBus>(),
            provider.GetRequiredService<ISessionService>(),
            provider.GetRequiredService<IProcessManager>(),
            provider.GetRequiredService<AppInstallationService>(),
            () => provider.GetRequiredService<ISimulationClock>().UtcNow,
            provider.GetService<ILogger<ServiceStartupCoordinator>>()));
        // The headless shell hsh (ADR 0050): the terminal runs every line through it.
        services.AddScoped<ShellInterpreter>(provider => new ShellInterpreter(
            provider.GetRequiredService<AppLifecycleOrchestrator>(),
            provider.GetRequiredService<AppCatalog>(),
            provider.GetRequiredService<IAppEnablementRegistry>(),
            provider.GetRequiredService<IKernelMemoryLedger>()));
        // Shells hosted by terminal emulators through HackShell (ADR 0056 section 7, shell.host).
        services.AddScoped<ShellHostService>(provider => new ShellHostService(
            provider.GetRequiredService<ShellInterpreter>(),
            provider.GetRequiredService<IProcessManager>(),
            provider.GetRequiredService<IElevationService>()));
        services.AddScoped<AppIntentDispatcher>(provider => new AppIntentDispatcher(
            provider.GetRequiredService<AppLifecycleOrchestrator>(),
            provider.GetRequiredService<AppCatalog>(),
            provider.GetRequiredService<IAppEnablementRegistry>(),
            provider.GetRequiredService<FileAssociationResolver>(),
            provider.GetRequiredService<ICapabilityGrantRepository>(),
            provider.GetRequiredService<IFileSystemService>()));

        services.AddSingleton<IFileSystemSelectedResourceHandleRegistry>(provider =>
            new FileSystemSelectedResourceHandleRegistry(
                provider.GetRequiredService<ISimulationClock>(),
                provider.GetRequiredService<ICapabilityGrantRepository>(),
                provider.GetRequiredService<IEventBus>()));
        services.AddSingleton<FileDialogServiceFactory>(provider => new FileDialogServiceFactory(
            provider.GetRequiredService<IFileSystemSelectedResourceHandleRegistry>()));
        services.AddScoped<FileDialogCoordinator>(provider =>
        {
            ISessionService sessionService = provider.GetRequiredService<ISessionService>();
            FileDialogServiceFactory factory = provider.GetRequiredService<FileDialogServiceFactory>();
            SessionId sessionId = sessionService.CurrentPrincipal?.SessionId ?? SessionId.FromGuid(Guid.Empty);
            return (FileDialogCoordinator)factory.Create(sessionId);
        });
        services.AddScoped<IFileDialogService>(provider => provider.GetRequiredService<FileDialogCoordinator>());
        services.AddScoped<DialogCoordinator>(provider => new DialogCoordinator(
            provider.GetRequiredService<IFileDialogService>()));
        services.AddScoped<IDialogService>(provider => provider.GetRequiredService<DialogCoordinator>());
        // Detached display surfaces (ADR 0078): windows of the host browser driven by this instance.
        services.AddScoped(provider => new DisplaySurfaceService(
            provider.GetRequiredService<IJSRuntime>(),
            provider.GetRequiredService<INotificationQueue>(),
            provider.GetRequiredService<ISessionService>(),
            provider.GetRequiredService<ISimulationClock>(),
            provider.GetRequiredService<HackerOs.Platform.Core.Globalization.SessionLocaleService>()));
        services.AddScoped<IDisplaySurfaceService>(provider => provider.GetRequiredService<DisplaySurfaceService>());
        // ADR 0178 §4: the secondary screens, each a display surface registered in WindowRuntime.
        services.AddScoped(provider => new ScreenService(
            provider.GetRequiredService<DisplaySurfaceService>(),
            provider.GetRequiredService<IJSRuntime>(),
            provider.GetRequiredService<WindowRuntime>(),
            provider.GetRequiredService<IOsSettingsService>(),
            provider.GetService<ILogger<ScreenService>>(),
            provider.GetRequiredService<HackerOs.Platform.Core.Globalization.SessionLocaleService>()));
        // ADR 0184: HackScreens over the screen service and the window manager.
        services.AddScoped(provider => new ScreenControl(
            provider.GetRequiredService<ScreenService>(),
            provider.GetRequiredService<WindowRuntime>()));
        // ADR 0150: the OS context menu. The platform provides the contract hackeros.context-menu
        // itself; the service holds each window's menus and the one menu open in the OS.
        services.AddScoped(provider => new HackerOs.Platform.Blazor.ContextMenus.ContextMenuService(
            provider.GetService<ILogger<HackerOs.Platform.Blazor.ContextMenus.ContextMenuService>>(),
            provider.GetRequiredService<HackerOs.Platform.Blazor.ContextMenus.IFileMenuEntrySource>()));
        // ADR 0151, ADR 0159: the file menu entries apps register. The registry copies the manifests'
        // entries to /etc/hackeros/context-menus.d at boot and after each install change, and reads the
        // folder; the source adds them to every file menu, whichever app provides the file control.
        services.AddScoped(provider => new HackerOs.Platform.Core.ContextMenus.FileContextMenuRegistry(
            provider.GetRequiredService<AppCatalog>(),
            ((OsSettingsService)provider.GetRequiredService<IOsSettingsService>()).CreateSystemGateway(),
            appId => provider.GetRequiredService<AppInstallationService>().GetState(appId) == AppInstallationState.UninstalledForMachine,
            provider.GetService<ILogger<HackerOs.Platform.Core.ContextMenus.FileContextMenuRegistry>>()));
        services.AddScoped(provider => new HackerOs.Platform.Core.ContextMenus.FileContextMenuPreferences(
            provider.GetRequiredService<IOsSettingsService>()));
        services.AddScoped(provider => new HackerOs.Platform.Core.ContextMenus.FileContextMenuAdministration(
            provider.GetRequiredService<HackerOs.Platform.Core.ContextMenus.FileContextMenuRegistry>(),
            provider.GetRequiredService<HackerOs.Platform.Core.ContextMenus.FileContextMenuPreferences>(),
            provider.GetRequiredService<AppCatalog>()));
        services.AddScoped(provider => new HackerOs.Platform.Core.ContextMenus.FileContextMenuLauncher(
            provider.GetRequiredService<AppCatalog>(),
            provider.GetRequiredService<IAppEnablementRegistry>(),
            provider.GetRequiredService<AppLifecycleOrchestrator>()));
        services.AddScoped<HackerOs.Platform.Blazor.ContextMenus.IFileMenuEntrySource>(provider =>
        {
            HackerOs.Platform.Core.ContextMenus.FileContextMenuLauncher launcher =
                provider.GetRequiredService<HackerOs.Platform.Core.ContextMenus.FileContextMenuLauncher>();
            HackerOs.Platform.Core.ContextMenus.FileContextMenuPreferences preferences =
                provider.GetRequiredService<HackerOs.Platform.Core.ContextMenus.FileContextMenuPreferences>();
            ILogger? logger = provider.GetService<ILogger<HackerOs.Platform.Blazor.ContextMenus.FileMenuEntries>>();
            return new HackerOs.Platform.Blazor.ContextMenus.FileMenuEntries(
                provider.GetRequiredService<AppCatalog>(),
                provider.GetRequiredService<HackerOs.Platform.Core.ContextMenus.FileContextMenuRegistry>(),
                launcher,
                preferences.GetHiddenAsync,
                () => provider.GetRequiredService<HackerOs.Platform.Core.Globalization.SessionLocaleService>().Culture,
                async (appId, entry, scope, paths) =>
                {
                    if (provider.GetRequiredService<ISessionService>().CurrentPrincipal is not { } principal)
                    {
                        return;
                    }

                    foreach (AppLaunchResult result in await launcher.LaunchAsync(appId, entry, scope, paths, principal))
                    {
                        if (!result.IsSuccess)
                        {
                            logger?.LogWarning(
                                "context-menu: the entry '{Name}' of {AppId} could not launch its app: {Status} {Code}",
                                entry.Name, appId, result.Status, result.ErrorCode);
                        }
                    }
                });
        });
        services.AddSingleton(new HackPlatformComponent(
            HackerOs.AppSdk.Components.HackComponentContracts.ContextMenu,
            typeof(HackerOs.Platform.Blazor.ContextMenus.PlatformContextMenu)));
        // ADR 0156: /etc/hackeros/components.config, the administrator's choice of the app that
        // provides the file view and the file dialogs, behind Settings > OS components.
        services.AddScoped(provider =>
        {
            LogFileWriter? log = provider.GetService<LogFileWriter>();
            return new HackerOs.Platform.Core.ComponentProviders.ComponentProviderService(
                provider.GetRequiredService<AppCatalog>(),
                provider.GetRequiredService<IAppEnablementRegistry>(),
                provider.GetRequiredService<IOsSettingsService>(),
                provider.GetRequiredService<IAdminElevationVerifier>(),
                provider.GetRequiredService<IAuditLog>(),
                log is null ? null : message => log.Write(LogFileTarget.Syslog, DiagnosticSeverity.Information, "components", message));
        });
        // Embeddable components registered in manifests (ADR 0063): a window embeds another app's
        // component by contract; the provider's assembly is loaded as a launch would load it. The
        // administrator's choice is tried first for the file view and the file dialogs (ADR 0156).
        services.AddScoped<IHackComponentHost>(provider =>
        {
            AppLifecycleOrchestrator orchestrator = provider.GetRequiredService<AppLifecycleOrchestrator>();
            LogFileWriter? log = provider.GetService<LogFileWriter>();
            return new HackComponentHost(
                provider.GetRequiredService<AppCatalog>(),
                provider.GetRequiredService<IAppEnablementRegistry>(),
                orchestrator.EnsureDescriptorAsync,
                platformComponents: provider.GetServices<HackPlatformComponent>(),
                reportError: log is null
                    ? null
                    : message => log.Write(LogFileTarget.Syslog, DiagnosticSeverity.Error, "components", message))
            {
                Selection = provider.GetRequiredService<HackerOs.Platform.Core.ComponentProviders.ComponentProviderService>(),
                ReportWarning = log is null
                    ? null
                    : message => log.Write(LogFileTarget.Syslog, DiagnosticSeverity.Warning, "components", message)
            };
        });
        // Blazor sites (ADR 0084): the browser asks the OS to start a site's renderer process.
        services.AddScoped<IHackWebSiteHost>(provider =>
        {
            AppLifecycleOrchestrator orchestrator = provider.GetRequiredService<AppLifecycleOrchestrator>();
            return new HackWebSiteHost(
                provider.GetRequiredService<AppCatalog>(),
                provider.GetRequiredService<IAppEnablementRegistry>(),
                provider.GetRequiredService<ISessionService>(),
                orchestrator.EnsureDescriptorAsync,
                orchestrator.StartAuxiliaryAsync,
                orchestrator.StopAsync);
        });
        services.AddSingleton<IWindowAppFrameworkLifecycle>(NullWindowAppFrameworkLifecycle.Instance);
        // Scoped, not Singleton: on the server host these back live desktop/window state for one
        // circuit. A Singleton here outlives circuit reconnects (e.g. a hard browser reload, which
        // tears down and recreates every Scoped service including AppLifecycleOrchestrator and its
        // running app instances) — the previous circuit's WindowRuntime.Windows entries would
        // survive with an OwnerInstanceId pointing at an app instance the new circuit's orchestrator
        // never launched, so DesktopShell's Orchestrator.TryGetRunningInstance lookup permanently
        // fails and the window is stuck rendering its dead "window-content-placeholder" fallback.
        // No-op behavior change for the WASM host, which has exactly one implicit scope anyway.
        services.AddScoped(_ => new WindowRuntime(new WindowBounds(0, 0, 1280, 720)));
        services.AddScoped<WindowLaunchCoordinator>();
        services.AddScoped<WindowCloseGuardRegistry>();
        services.AddScoped<AppBackHandlerRegistry>();
        // ADR 0095: window events to apps' OnWindow* hooks, and the window manager's keyboard
        // commands. Scoped like WindowRuntime; the probe depends on IJSRuntime (see the scoping note).
        services.AddScoped<IWindowClientAreaProbe, BrowserWindowClientAreaProbe>();
        services.AddScoped(provider => new WindowAppEventDispatcher(
            provider.GetRequiredService<WindowRuntime>(),
            provider.GetService<IWindowClientAreaProbe>()));
        // ADR 0117: each rendered window app gets a gateway bound to its own window (title).
        services.AddScoped<AppWindowGatewayFactory>();
        services.AddScoped(provider => new WindowKeyboardCommands(
            provider.GetRequiredService<WindowRuntime>(),
            provider.GetService<IOsSettingsService>(),
            provider.GetService<ILogger<WindowKeyboardCommands>>(),
            provider.GetRequiredService<HackerOs.Platform.Core.Globalization.SessionLocaleService>()));
        // Depends on AppLifecycleOrchestrator, which is Scoped (see above) — see this file's
        // IJSRuntime scoping note.
        services.AddScoped<WindowCloseCoordinator>();
        // ADR 0127 §2-3: which app windows stop answering, and their "Attendre | Terminer" dialog.
        // Scoped like WindowRuntime; reads the same accountant and threshold as /proc (ADR 0066/0067).
        services.AddScoped(provider => new WindowResponsivenessMonitor(
            provider.GetRequiredService<WindowRuntime>(),
            provider.GetRequiredService<IProcessManager>(),
            CpuAccountant(provider),
            provider.GetRequiredService<ResourceAccountingPlan>().Settings.UnresponsiveThreshold,
            TimeProvider.System,
            provider.GetService<IOsSettingsService>(),
            provider.GetService<ILogger<WindowResponsivenessMonitor>>()));
        services.AddScoped<FileDialogWindowAdapter>(provider => new FileDialogWindowAdapter(
            provider.GetRequiredService<FileDialogCoordinator>(),
            provider.GetRequiredService<WindowRuntime>(),
            provider.GetRequiredService<DialogCoordinator>(),
            provider.GetRequiredService<HackerOs.Platform.Core.Globalization.SessionLocaleService>()));
        // Depends on IFileSystemService, which is Scoped (see above) — see this file's IJSRuntime
        // scoping note.
        services.AddScoped<TestDemoFixtureSeeder>();

        services.AddSingleton<IIconCatalog, IconCatalog>();
        // The file control draws its icons with its own DefaultShellIconProvider (ADR 0161 §1): the
        // host no longer references its code, which loads with the File Explorer.

        // Depends on IJSRuntime through the browser probe — see this file's IJSRuntime scoping note.
        services.AddScoped<IPlatformEnvironmentProbe, BrowserPlatformEnvironmentProbe>();
        // Depends on IPlatformEnvironmentProbe, which is Scoped (see above) — see this file's
        // IJSRuntime scoping note.
        services.AddScoped<UiPlatformPreferenceService>();
        services.AddScoped<HackerOs.Platform.Core.Appearance.ThemePreferenceService>();
        // ADR 0105: accessibility.config resolved against the host's media queries (browser probe,
        // so Scoped like the environment probe above).
        services.AddScoped<HackerOs.Platform.Core.Appearance.IHostAccessibilityProbe, BrowserHostAccessibilityProbe>();
        services.AddScoped<HackerOs.Platform.Core.Appearance.AccessibilityPreferenceService>();
        // ADR 0193: the session's time zone (datetime.config), live like the accessibility preferences.
        services.AddScoped<HackerOs.Platform.Core.Globalization.SessionTimeZoneService>();
        // ADR 0103: user themes of /etc/hackeros/themes and ~/.config/hackeros/themes.
        services.AddScoped<HackerOs.Platform.Core.Appearance.UserThemeCatalogService>(provider =>
            new HackerOs.Platform.Core.Appearance.UserThemeCatalogService(
                provider.GetRequiredService<IFileSystemService>(),
                provider.GetRequiredService<ISessionService>(),
                provider.GetRequiredService<ISimulationClock>(),
                provider.GetRequiredService<IOsSettingsService>(),
                provider.GetService<ITopicMessageBus>(),
                provider.GetService<ILogger<HackerOs.Platform.Core.Appearance.UserThemeCatalogService>>()));
        services.AddScoped<WindowGeometryPersistence>();
        // ADR 0116: remembers app window geometry from WindowRuntime's facts; read back by
        // WindowLaunchCoordinator. Scoped like WindowRuntime.
        services.AddScoped<WindowGeometryRecorder>(provider => new WindowGeometryRecorder(
            provider.GetRequiredService<WindowRuntime>(),
            provider.GetRequiredService<WindowGeometryPersistence>()));
        services.AddScoped<StartMenuPreferencesService>();
        // Depends on AppLifecycleOrchestrator and UiPlatformPreferenceService, both Scoped (see
        // above) — see this file's IJSRuntime scoping note.
        services.AddScoped<PlatformShellSwitchCoordinator>();

        return services;
    }

    /// <summary>
    /// ADR 0156 §4: removes from <c>/etc/hackeros/components.config</c> the choice of every app now
    /// uninstalled for the machine. Runs after each change of the installed state; a failure is
    /// written to the system log and never blocks the uninstall.
    /// </summary>
    private static async Task RemoveUninstalledComponentProvidersAsync(IServiceProvider provider, AppInstallationService installation)
    {
        try
        {
            await provider.GetRequiredService<HackerOs.Platform.Core.ComponentProviders.ComponentProviderService>()
                .RemoveUninstalledAsync(appId => installation.GetState(appId) == AppInstallationState.UninstalledForMachine)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            provider.GetService<LogFileWriter>()?.Write(
                LogFileTarget.Syslog,
                DiagnosticSeverity.Warning,
                "components",
                $"component.provider.removed: components.config could not be updated after an uninstall: {exception.Message}");
        }
    }

    /// <summary>
    /// ADR 0151 §3.5: brings <c>/etc/hackeros/context-menus.d</c> in line with the installed apps after
    /// each change of the installed state; a failure is written to the system log and never blocks it.
    /// </summary>
    private static async Task SyncFileContextMenusAsync(IServiceProvider provider)
    {
        try
        {
            await provider.GetRequiredService<HackerOs.Platform.Core.ContextMenus.FileContextMenuRegistry>().SyncAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            provider.GetService<LogFileWriter>()?.Write(
                LogFileTarget.Syslog,
                DiagnosticSeverity.Warning,
                "context-menu",
                $"context-menus.d could not be updated after an install change: {exception.Message}");
        }
    }

    private static AppCatalog CreateEmptyCatalog()
    {
        AppCatalogBuildResult result = AppCatalog.Build([]);
        return result.Catalog ?? throw new InvalidOperationException("The empty app catalog must be valid.");
    }

    /// <summary>The network drive service, resolved once, returned by the drive gateway (ADR 0181).</summary>
    private static Func<NetworkDriveService?> NetworkDrivesOf(IServiceProvider provider)
    {
        NetworkDriveService drives = provider.GetRequiredService<NetworkDriveService>();
        return () => drives;
    }

    /// <summary>
    /// The CPU measurement as <c>/proc</c> reads it: with Web Workers, their processes' times come from
    /// their heartbeats (ADR 0070, section 5); otherwise the main thread's accountant.
    /// </summary>
    private static ICpuTimeAccountant CpuAccountant(IServiceProvider provider) =>
        (ICpuTimeAccountant?)provider.GetService<WorkerAwareCpuTimeAccountant>() ?? provider.GetRequiredService<ICpuTimeAccountant>();

    /// <summary>The memory measurement as <c>/proc</c> reads it: with Web Workers, each one's runtime heap is counted.</summary>
    private static IMemoryAccountant MemoryAccountant(IServiceProvider provider) =>
        (IMemoryAccountant?)provider.GetService<WorkerAwareMemoryAccountant>() ?? provider.GetRequiredService<IMemoryAccountant>();
}
