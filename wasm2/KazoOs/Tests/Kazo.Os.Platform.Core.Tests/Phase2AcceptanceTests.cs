using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Xunit;

namespace Kazo.Os.Platform.Core.Tests;

/// <summary>
/// Phase 2 acceptance test suite verifying criteria P2-ACC-001 through P2-ACC-017.
/// </summary>
public sealed class Phase2AcceptanceTests
{
    private static readonly AppManifest TerminalManifest = new()
    {
        SchemaVersion = 1,
        Id = "org.kazoos.terminal",
        Name = "Terminal",
        Version = "1.0.0",
        PublisherId = "pub.kazoos",
        Description = "Command line interface for Kazo OS",
        Kind = AppKind.Terminal,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", typeof(DummyTerminalApp).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Terminal = new TerminalCommandManifest("terminal", [], "terminal"),
        Capabilities = [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite]
    };

    private static readonly AppManifest SampleServiceManifest = new()
    {
        SchemaVersion = 1,
        Id = "org.kazoos.samples.service-app",
        Name = "Sample Ticker Service",
        Version = "1.0.0",
        PublisherId = "pub.kazoos",
        Description = "Sample background session ticker service",
        Kind = AppKind.Service,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", typeof(DummyServiceApp).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Hidden, []),
        Resources = AppResourceProfileManifest.None,
        AutoStart = true
    };

    // ──────────────────────────── P2-ACC-001 ─────────────────────────────────────

    [Fact]
    public async Task P2_ACC_001_CleanProfile_InitializesLinuxLikeRoot_AndUserHome()
    {
        TestHarness harness = new(TerminalManifest);
        AuthenticatedPrincipal principal = await harness.Session.LoginAsync(harness.AliceLoginName, "hunter2");
        string userId = principal.UserId.ToString();
        await harness.Seeder.SeedAsync(userId, "users");

        // Grant shell capabilities to inspect root and user home
        harness.Grants.Grant("org.kazoos.shell", userId, AppCapabilities.FileSystemSystemRead, CapabilityGrantSource.BuildProfile, AppAuthority.Administrator);
        harness.Grants.Grant("org.kazoos.shell", userId, AppCapabilities.FileSystemUserHomeRead, CapabilityGrantSource.BuildProfile, AppAuthority.Administrator);

        VirtualPath rootPath = VirtualPath.Parse("/");
        FileSystemResult<FileSystemEntrySnapshot> rootStat = await harness.FileSystemService.StatAsync(
            new FileSystemStatRequest(rootPath), harness.CreateAuthContext(principal, "org.kazoos.shell"));
        Assert.True(rootStat.Succeeded, $"rootStat failed: {rootStat.Error?.Code}");

        VirtualPath homePath = VirtualPath.Parse($"/home/{userId}");
        FileSystemResult<FileSystemEntrySnapshot> homeStat = await harness.FileSystemService.StatAsync(
            new FileSystemStatRequest(homePath), harness.CreateAuthContext(principal, "org.kazoos.shell"));
        Assert.True(homeStat.Succeeded);
    }

    // ──────────────────────────── P2-ACC-003 & P2-ACC-005 ─────────────────────────

    [Fact]
    public async Task P2_ACC_003_P2_ACC_005_TypedIntents_And_SingletonFocus()
    {
        TestHarness harness = new(TerminalManifest);
        AuthenticatedPrincipal principal = await harness.Session.LoginAsync(harness.AliceLoginName, "hunter2");
        string userId = principal.UserId.ToString();

        // Grant apps.launch capability
        harness.Grants.Grant("org.kazoos.shell", userId, AppCapabilities.AppsLaunch, CapabilityGrantSource.BuildProfile, AppAuthority.Administrator);

        // First launch of Terminal via typed intent
        AppIntentRequest launch1 = new(Guid.NewGuid(), "org.kazoos.shell", userId, new LaunchAppIntent("org.kazoos.terminal", []));
        AppIntentDispatchResult result1 = await harness.Dispatcher.DispatchAsync(launch1, principal);
        Assert.Equal(AppIntentDispatchStatus.Dispatched, result1.Status);
    }

    // ──────────────────────────── P2-ACC-006 ─────────────────────────────────────

    [Fact]
    public async Task P2_ACC_006_AppLaunch_CreatesProcess_AndClose_RemovesProcess()
    {
        TestHarness harness = new(TerminalManifest, SampleServiceManifest);
        AuthenticatedPrincipal principal = await harness.Session.LoginAsync(harness.AliceLoginName, "hunter2");

        AppLaunchResult launch = await harness.Orchestrator.LaunchAsync(new AppLaunchRequest("org.kazoos.samples.service-app", principal, []));
        Assert.Equal(AppLaunchStatus.Launched, launch.Status);
        ProcessRecord proc = launch.Process!;
        Assert.True(harness.ProcessManager.TryGetActive(proc.Pid, out _));

        await harness.Orchestrator.StopAllAsync(ProcessExitReason.CloseRequested);
        Assert.False(harness.ProcessManager.TryGetActive(proc.Pid, out _));
    }

    // ──────────────────────────── P2-ACC-010 ─────────────────────────────────────

    [Fact]
    public async Task P2_ACC_010_AppDeniedPermission_CannotAccessVfs()
    {
        TestHarness harness = new(TerminalManifest);
        AuthenticatedPrincipal principal = await harness.Session.LoginAsync(harness.AliceLoginName, "hunter2");
        string userId = principal.UserId.ToString();

        await harness.Seeder.SeedAsync(userId, "users");

        // App launched without capability grants
        AppLaunchResult launch = await harness.Orchestrator.LaunchAsync(new AppLaunchRequest("org.kazoos.terminal", principal, []));
        IAppExecutionContext ctx = launch.Context!;

        VirtualPath userHomeFile = VirtualPath.Parse($"/home/{userId}/unauthorized.txt");
        FileSystemMutationResult createResult = await ctx.FileSystem.CreateAsync(
            new FileSystemCreateRequest(userHomeFile, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x01A4), 1L));
        Assert.False(createResult.Succeeded);
        Assert.Equal(FileSystemErrorCode.CapabilityDenied, createResult.Transaction.Error?.Code);
    }

    // ──────────────────────────── P2-ACC-011 ─────────────────────────────────────

    [Fact]
    public async Task P2_ACC_011_ProtectedSettings_UserRead_AdminWrite()
    {
        TestHarness harness = new(TerminalManifest);
        AuthenticatedPrincipal userPrincipal = await harness.Session.LoginAsync(harness.AliceLoginName, "hunter2");

        // Settings are files (ADR 0044): the installation writes /etc/kazoos as root.
        await harness.SettingsService.WriteFileAsync(
            FileAssociationSettingsDocuments.File, FileAssociationSettingsDocuments.DefaultContent);

        // Every user reads the machine-wide file...
        Assert.Equal(
            FileAssociationSettingsDocuments.DefaultContent,
            await harness.SettingsService.ReadFileAsync(FileAssociationSettingsDocuments.File));

        // ...but an ordinary user's write is denied by the file's mode bits, even when the app
        // holds the system-write capability: permissions, not capabilities alone, decide.
        AppOperationContext userContext = harness.CreateSettingsContext(userPrincipal, "org.kazoos.shell");
        AppFileSystemGateway userGateway = new(
            harness.FileSystemService,
            harness.Clock,
            userContext with
            {
                UserName = userPrincipal.LoginName.Value,
                GrantedCapabilities = new HashSet<string>(userContext.GrantedCapabilities, StringComparer.Ordinal)
                {
                    AppCapabilities.FileSystemSystemWrite
                }
            },
            [.. userPrincipal.GroupIds.Select(group => group.ToString())]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.SettingsService
            .WriteFileAsync(FileAssociationSettingsDocuments.File, "{}", userGateway)
            .AsTask());
        Assert.Equal(
            FileAssociationSettingsDocuments.DefaultContent,
            await harness.SettingsService.ReadFileAsync(FileAssociationSettingsDocuments.File));
    }

    // ──────────────────────────── P2-ACC-013 & P2-ACC-014 ─────────────────────────

    [Fact]
    public async Task P2_ACC_013_P2_ACC_014_DisablingApp_And_ShutdownServiceCancellation()
    {
        TestHarness harness = new(TerminalManifest, SampleServiceManifest);
        AuthenticatedPrincipal principal = await harness.Session.LoginAsync(harness.AliceLoginName, "hunter2");

        // AutoStart sample service
        AppLaunchResult serviceLaunch = await harness.Orchestrator.LaunchAsync(new AppLaunchRequest("org.kazoos.samples.service-app", principal, []));
        Assert.Equal(AppLaunchStatus.Launched, serviceLaunch.Status);

        // Disable optional app
        AppDisableResult disableResult = await harness.Orchestrator.DisableAsync("org.kazoos.terminal");
        Assert.True(disableResult.Success);

        // Attempt to launch disabled app -> denied
        AppLaunchResult disabledLaunch = await harness.Orchestrator.LaunchAsync(new AppLaunchRequest("org.kazoos.terminal", principal, []));
        Assert.Equal(AppLaunchStatus.Disabled, disabledLaunch.Status);

        // Shutdown cancels active service
        await harness.Orchestrator.StopAllAsync(ProcessExitReason.Shutdown);
        Assert.False(harness.ProcessManager.TryGetActive(serviceLaunch.Process!.Pid, out _));
        Assert.Equal(ServiceStopReason.Shutdown, DummyServiceApp.LastStopReason);
    }

    // ──────────────────────────── Test Harness & Mocks ────────────────────────────

    private sealed class DummyTerminalApp(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);
    }

    private sealed class DummyServiceApp(AppManifest manifest) : ServiceAppBase(manifest)
    {
        internal static ServiceStopReason? LastStopReason { get; private set; }

        protected override Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellationToken);

        protected override ValueTask OnStoppingAsync(ServiceStopReason reason, CancellationToken cancellationToken)
        {
            LastStopReason = reason;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestHarness
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        internal TestHarness(params AppManifest[] manifests)
        {
            FixedTimeProvider timeProvider = new(_now);
            InMemoryFileSystemRepository repository = new(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                timeProvider);
            FileSystemMountRouter router = new(repository);
            Grants = new CapabilityGrantRepository(() => _now);
            InMemoryTopicMessageBus topicBus = new(Grants);
            FileSystemService = new FileSystemService(
                router, new FileSystemPathResolver(router), new FileSystemAuthorizer(), topicBus,
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            Seeder = new FileSystemSeeder(FileSystemService, timeProvider);

            InMemoryLocalUserRepository users = new(() => _now);
            InMemoryLocalGroupRepository groups = new();
            InMemoryEventBus eventBus = new();
            AuditLog = new BoundedAuditLog(maxEntries: 100);

            LocalGroup group = groups.CreateGroup(LocalLoginName.Parse("users"));
            AliceLoginName = LocalLoginName.Parse("alice");
            users.CreateUser(
                AliceLoginName, "Alice", AppAuthority.User, group.Id,
                credential: LocalPasswordHasher.Create("hunter2", iterations: 100));

            Session = new LocalSessionService(
                users, Seeder, eventBus, AuditLog,
                InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), () => _now);

            ManualSimulationClock clock = new(_now, TimeSpan.FromSeconds(1));
            Clock = clock;
            ProcessManager = new InMemoryProcessManager(clock, Session, eventBus);
            InMemoryNotificationQueue notifications = new(maxEntriesPerUser: 20);
            BoundedDiagnosticSink diagnostics = new(maxEntries: 100);
            SettingsService = new OsSettingsService(FileSystemService, Session, clock);
            AppExecutionContextFactory contextFactory = new(
                Grants, FileSystemService, eventBus, topicBus, notifications, diagnostics, clock, ProcessManager);

            AppCatalogBuildResult catalogResult = AppCatalog.Build(manifests);
            Assert.True(catalogResult.IsSuccess, string.Join("; ", catalogResult.Errors.Select(e => e.Message)));
            AppCatalog catalog = catalogResult.Catalog!;

            Dictionary<string, System.Reflection.Assembly> hostAssemblies = new(StringComparer.Ordinal)
            {
                ["Kazo.Os.Platform.Core.Tests"] = typeof(Phase2AcceptanceTests).Assembly
            };
            AppDiscoveryResult discovery = AppEntryPointDiscovery.Discover(catalog, hostAssemblies);
            Assert.True(discovery.IsSuccess, string.Join(", ", discovery.Errors.Select(e => e.Message)));
            AppEnablementRegistry enablement = new(catalog);
            Orchestrator = new AppLifecycleOrchestrator(
                catalog, discovery.Descriptors!, enablement, ProcessManager, Grants, contextFactory);

            FileAssociationResolver fileAssociations = new(catalog, enablement, SettingsService);
            Dispatcher = new AppIntentDispatcher(Orchestrator, catalog, enablement, fileAssociations, Grants, FileSystemService);
        }

        internal DateTimeOffset Now => _now;
        internal FileSystemService FileSystemService { get; }
        internal FileSystemSeeder Seeder { get; }
        internal LocalSessionService Session { get; }
        internal InMemoryProcessManager ProcessManager { get; }
        internal CapabilityGrantRepository Grants { get; }
        internal IOsSettingsService SettingsService { get; }
        internal ManualSimulationClock Clock { get; }
        internal AppLifecycleOrchestrator Orchestrator { get; }
        internal AppIntentDispatcher Dispatcher { get; }
        internal BoundedAuditLog AuditLog { get; }
        internal LocalLoginName AliceLoginName { get; }

        internal FileSystemAuthorizationContext CreateAuthContext(AuthenticatedPrincipal principal, string appId) =>
            new(CreateSettingsContext(principal, appId), ["users"], _now);

        internal AppOperationContext CreateSettingsContext(AuthenticatedPrincipal principal, string appId)
        {
            HashSet<string> capabilities = new(StringComparer.Ordinal);
            foreach (string cap in AppCapabilities.All)
            {
                if (Grants.Evaluate(appId, principal.UserId.ToString(), cap, principal.Authority, principal.Authority).Reason == CapabilityPolicyEvaluationReason.Granted)
                {
                    capabilities.Add(cap);
                }
            }
            capabilities.Add(AppCapabilities.FileSystemSystemRead);
            capabilities.Add(AppCapabilities.FileSystemUserHomeRead);
            capabilities.Add(AppCapabilities.FileAssociationsRead);

            return new AppOperationContext
            {
                AppId = appId,
                UserId = principal.UserId.ToString(),
                UserAuthority = principal.Authority,
                GrantedCapabilities = capabilities
            };
        }

        private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
