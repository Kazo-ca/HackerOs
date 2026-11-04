using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Blazor.Shell;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

/// <summary>
/// Tests for the <c>MOB-008</c> controlled platform-switch sequence: confirm dirty windows, stop
/// their instances with <see cref="ProcessExitReason.PlatformChanged"/>, then persist.
/// </summary>
public sealed class PlatformShellSwitchCoordinatorTests
{
    [Fact]
    public async Task RequestExplicitAsync_confirms_stops_and_persists_when_every_window_agrees()
    {
        Fixture fixture = new();
        AppLaunchResult launch = await fixture.LaunchWindowAppAsync();
        WindowId windowId = fixture.CreateWindowFor(launch);
        AlwaysConfirmGuard guard = new();
        using IDisposable registration = fixture.CloseGuards.Register(windowId, guard);
        await fixture.PlatformPreference.InitializeAsync();
        PlatformShellSwitchCoordinator coordinator = fixture.CreateCoordinator();

        bool switched = await coordinator.RequestExplicitAsync(WellKnownAppPlatforms.Mobile);

        Assert.True(switched);
        Assert.Empty(fixture.WindowRuntime.Windows);
        Assert.False(fixture.Manager.TryGetActive(launch.Process!.Pid, out _));
        ProcessRecord history = Assert.Single(fixture.Manager.GetHistory(), item => item.Pid == launch.Process.Pid);
        Assert.Equal(ProcessExitReason.PlatformChanged, history.ExitReason);
        Assert.Equal(WellKnownAppPlatforms.Mobile, fixture.PlatformPreference.Current.ActivePlatform);
    }

    [Fact]
    public async Task RequestExplicitAsync_leaves_everything_untouched_when_a_window_rejects()
    {
        Fixture fixture = new();
        AppLaunchResult launch = await fixture.LaunchWindowAppAsync();
        WindowId windowId = fixture.CreateWindowFor(launch);
        RejectingGuard guard = new();
        using IDisposable registration = fixture.CloseGuards.Register(windowId, guard);
        await fixture.PlatformPreference.InitializeAsync();
        PlatformShellSwitchCoordinator coordinator = fixture.CreateCoordinator();

        bool switched = await coordinator.RequestExplicitAsync(WellKnownAppPlatforms.Mobile);

        Assert.False(switched);
        Assert.Single(fixture.WindowRuntime.Windows);
        Assert.True(fixture.Manager.TryGetActive(launch.Process!.Pid, out _));
        Assert.Equal(WellKnownAppPlatforms.Desktop, fixture.PlatformPreference.Current.ActivePlatform);
    }

    [Fact]
    public async Task RequestExplicitAsync_does_not_disturb_windows_when_already_the_active_platform()
    {
        Fixture fixture = new();
        AppLaunchResult launch = await fixture.LaunchWindowAppAsync();
        WindowId windowId = fixture.CreateWindowFor(launch);
        RejectingGuard guard = new();
        using IDisposable registration = fixture.CloseGuards.Register(windowId, guard);
        await fixture.PlatformPreference.InitializeAsync();
        PlatformShellSwitchCoordinator coordinator = fixture.CreateCoordinator();

        // Desktop is already active (default), so re-selecting it explicitly must not consult the
        // (rejecting) guard or touch the window at all.
        bool switched = await coordinator.RequestExplicitAsync(WellKnownAppPlatforms.Desktop);

        Assert.True(switched);
        Assert.Single(fixture.WindowRuntime.Windows);
        Assert.True(fixture.Manager.TryGetActive(launch.Process!.Pid, out _));
        Assert.Equal(UiPlatformSelectionSource.Explicit, fixture.PlatformPreference.Current.SelectionSource);
    }

    [Fact]
    public async Task RequestExplicitAsync_leaves_a_shared_entry_point_window_running()
    {
        Fixture fixture = new();
        AppLaunchResult launch = await fixture.LaunchSharedAppAsync();
        WindowId windowId = fixture.CreateWindowFor(launch, "org.hackeros.test-shared");
        // A rejecting guard proves the window was never even asked to confirm close.
        RejectingGuard guard = new();
        using IDisposable registration = fixture.CloseGuards.Register(windowId, guard);
        await fixture.PlatformPreference.InitializeAsync();
        PlatformShellSwitchCoordinator coordinator = fixture.CreateCoordinator();

        bool switched = await coordinator.RequestExplicitAsync(WellKnownAppPlatforms.Mobile);

        Assert.True(switched);
        Assert.Single(fixture.WindowRuntime.Windows);
        Assert.Equal(windowId, fixture.WindowRuntime.Windows[0].Id);
        Assert.True(fixture.Manager.TryGetActive(launch.Process!.Pid, out _));
        Assert.Equal(WellKnownAppPlatforms.Mobile, fixture.PlatformPreference.Current.ActivePlatform);
    }

    [Fact]
    public async Task RequestExplicitAsync_stops_only_the_window_whose_app_is_not_shared()
    {
        Fixture fixture = new();
        AppLaunchResult desktopOnlyLaunch = await fixture.LaunchWindowAppAsync();
        WindowId desktopOnlyWindowId = fixture.CreateWindowFor(desktopOnlyLaunch, "org.hackeros.test-window");
        AppLaunchResult sharedLaunch = await fixture.LaunchSharedAppAsync();
        WindowId sharedWindowId = fixture.CreateWindowFor(sharedLaunch, "org.hackeros.test-shared");
        AlwaysConfirmGuard confirmGuard = new();
        using IDisposable desktopOnlyRegistration = fixture.CloseGuards.Register(desktopOnlyWindowId, confirmGuard);
        await fixture.PlatformPreference.InitializeAsync();
        PlatformShellSwitchCoordinator coordinator = fixture.CreateCoordinator();

        bool switched = await coordinator.RequestExplicitAsync(WellKnownAppPlatforms.Mobile);

        Assert.True(switched);
        WindowRuntimeState remaining = Assert.Single(fixture.WindowRuntime.Windows);
        Assert.Equal(sharedWindowId, remaining.Id);
        Assert.False(fixture.Manager.TryGetActive(desktopOnlyLaunch.Process!.Pid, out _));
        Assert.True(fixture.Manager.TryGetActive(sharedLaunch.Process!.Pid, out _));
    }

    [Fact]
    public async Task RequestAutoAsync_confirms_and_stops_windows_before_reverting()
    {
        Fixture fixture = new();
        AppLaunchResult launch = await fixture.LaunchWindowAppAsync();
        WindowId windowId = fixture.CreateWindowFor(launch);
        AlwaysConfirmGuard guard = new();
        using IDisposable registration = fixture.CloseGuards.Register(windowId, guard);
        await fixture.PlatformPreference.InitializeAsync();
        await fixture.PlatformPreference.SetExplicitAsync(WellKnownAppPlatforms.Mobile);
        PlatformShellSwitchCoordinator coordinator = fixture.CreateCoordinator();

        bool switched = await coordinator.RequestAutoAsync();

        Assert.True(switched);
        Assert.Equal(UiPlatformSelectionSource.Auto, fixture.PlatformPreference.Current.SelectionSource);
        Assert.Empty(fixture.WindowRuntime.Windows);
    }

    private sealed class AlwaysConfirmGuard : IWindowCloseGuard
    {
        public ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class RejectingGuard : IWindowCloseGuard
    {
        public ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class Fixture
    {
        private readonly DateTimeOffset _now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        private readonly LocalLoginName _aliceLoginName;
        private int _entryId = 1;
        private int _transactionId = 100;

        internal Fixture()
        {
            FixedTimeProvider timeProvider = new(_now);
            InMemoryFileSystemRepository repository = new(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                timeProvider);
            FileSystemMountRouter router = new(repository);
            Grants = new CapabilityGrantRepository(() => _now);
            InMemoryTopicMessageBus topicBus = new(Grants);
            FileSystemService fileSystem = new(
                router, new FileSystemPathResolver(router), new FileSystemAuthorizer(), topicBus,
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            FileSystemSeeder seeder = new(fileSystem, timeProvider);

            InMemoryLocalUserRepository users = new(() => _now);
            InMemoryLocalGroupRepository groups = new();
            EventBus = new InMemoryEventBus();
            BoundedAuditLog auditLog = new(maxEntries: 100);

            LocalGroup group = groups.CreateGroup(LocalLoginName.Parse("users"));
            _aliceLoginName = LocalLoginName.Parse("alice");
            users.CreateUser(
                _aliceLoginName, "Alice", AppAuthority.User, group.Id,
                credential: LocalPasswordHasher.Create("hunter2", iterations: 100));

            Session = new LocalSessionService(
                users, seeder, EventBus, auditLog,
                InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), () => _now);

            ManualSimulationClock clock = new(_now, TimeSpan.FromSeconds(1));
            Manager = new InMemoryProcessManager(clock, Session, EventBus);
            InMemoryNotificationQueue notifications = new(maxEntriesPerUser: 20);
            BoundedDiagnosticSink diagnostics = new(maxEntries: 100);
            Settings = new OsSettingsService(fileSystem, Session, clock);
            AppExecutionContextFactory contextFactory = new(
                Grants, fileSystem, EventBus, topicBus, notifications, diagnostics, clock, Manager);

            AppManifest manifest = CreateWindowManifest();
            AppManifest sharedManifest = CreateSharedEntryPointManifest();
            AppCatalogBuildResult catalogResult = AppCatalog.Build([manifest, sharedManifest]);
            AppCatalog catalog = catalogResult.Catalog!;
            Catalog = catalog;
            Dictionary<string, AppDescriptor> descriptors = new(StringComparer.Ordinal)
            {
                // Placeholder entry point type, never instantiated by the orchestrator for a window app.
                [manifest.Id] = new AppDescriptor(manifest, typeof(object), typeof(object).Assembly),
                [sharedManifest.Id] = new AppDescriptor(sharedManifest, typeof(object), typeof(object).Assembly)
            };
            AppEnablementRegistry enablement = new(catalog);
            Orchestrator = new AppLifecycleOrchestrator(
                catalog, descriptors, enablement, Manager, Grants, contextFactory, EventBus);

            WindowRuntime = new WindowRuntime(new WindowBounds(0, 0, 1280, 720));
            CloseGuards = new WindowCloseGuardRegistry();
            PlatformPreference = new UiPlatformPreferenceService(Settings, new FixedProbe());
        }

        internal LocalSessionService Session { get; }
        internal InMemoryEventBus EventBus { get; }
        internal InMemoryProcessManager Manager { get; }
        internal CapabilityGrantRepository Grants { get; }
        internal IOsSettingsService Settings { get; }
        internal AppLifecycleOrchestrator Orchestrator { get; }
        internal WindowRuntime WindowRuntime { get; }
        internal WindowCloseGuardRegistry CloseGuards { get; }
        internal UiPlatformPreferenceService PlatformPreference { get; }
        internal AppCatalog Catalog { get; private set; } = null!;

        internal PlatformShellSwitchCoordinator CreateCoordinator() =>
            new(WindowRuntime, Orchestrator, CloseGuards, PlatformPreference, Catalog);

        internal async Task<AppLaunchResult> LaunchWindowAppAsync()
        {
            AuthenticatedPrincipal principal = await Session.LoginAsync(_aliceLoginName, "hunter2");
            return await Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.test-window", principal, []));
        }

        internal async Task<AppLaunchResult> LaunchSharedAppAsync()
        {
            AuthenticatedPrincipal principal = Session.CurrentPrincipal as AuthenticatedPrincipal
                ?? await Session.LoginAsync(_aliceLoginName, "hunter2");
            return await Orchestrator.LaunchAsync(new AppLaunchRequest("org.hackeros.test-shared", principal, []));
        }

        internal WindowId CreateWindowFor(AppLaunchResult launch, string appId = "org.hackeros.test-window")
        {
            WindowId windowId = WindowId.FromGuid(Guid.NewGuid());
            WindowRuntimeState state = new(
                windowId,
                appId,
                WindowOwnerId.FromGuid(launch.Process!.AppInstanceId.Value),
                "Test Window",
                null,
                new WindowBounds(20, 20, 640, 480),
                null,
                0,
                WindowVisualState.Normal,
                new WindowConstraints(true, 320, 240));
            WindowRuntime.Apply(new CreateWindowCommand(state));
            return windowId;
        }

        private static AppManifest CreateWindowManifest() => new()
        {
            Id = "org.hackeros.test-window",
            Name = "Test Window",
            Version = "1.0.0",
            PublisherId = "pub.hackeros",
            Description = "Platform switch coordinator test app.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Test.dll", "Test.Window"),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None
        };

        private static AppManifest CreateSharedEntryPointManifest() => new()
        {
            Id = "org.hackeros.test-shared",
            Name = "Test Shared Window",
            Version = "1.0.0",
            PublisherId = "pub.hackeros",
            Description = "Platform switch coordinator test app declaring one shared entry point.",
            Kind = AppKind.Window,
            Platform = new AppManifestPlatform(
                [WellKnownAppPlatforms.Desktop.Value, WellKnownAppPlatforms.Mobile.Value],
                [new AppPlatformEntryPointManifest(
                    [WellKnownAppPlatforms.Desktop.Value, WellKnownAppPlatforms.Mobile.Value],
                    "Test.dll",
                    "Test.SharedWindow")]),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None
        };

        private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private sealed class FixedProbe : IPlatformEnvironmentProbe
        {
            public PlatformEnvironmentSnapshot Current { get; } = new(
                new PlatformEnvironmentSignals(1280, 800, false, true, 0, false),
                WellKnownAppPlatforms.Desktop,
                "test");

            public event Action? Changed { add { } remove { } }

            public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
