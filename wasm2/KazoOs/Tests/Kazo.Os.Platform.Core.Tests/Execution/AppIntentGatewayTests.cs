using Kazo.Os.Platform.Core.Settings;
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
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>
/// End-to-end tests for <see cref="IAppExecutionContext.Intents"/> exercising the real
/// <c>AppIntentGateway</c> through <see cref="AppExecutionContextFactory"/> and the real
/// <see cref="AppIntentDispatcher"/> -- the exact path a window app (e.g. File Explorer) uses
/// when it asks the system to open or launch something, rather than a mocked gateway.
/// </summary>
public sealed class AppIntentGatewayTests
{
    [Fact]
    public async Task OpenFileAsync_ResolvesSoleCandidate_AndLaunchesIt()
    {
        Fixture fixture = new(FileManagerManifest(), NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        AppIntentOpenFileResult result = await caller.Intents.OpenFileAsync(
            VirtualPath.Parse("/home/alice/notes.txt"));

        Assert.Equal(AppIntentOpenFileOutcome.Opened, result.Outcome);
    }

    [Fact]
    public async Task OpenFileAsync_MultipleCandidates_ReturnsChooserRequiredWithCandidates()
    {
        Fixture fixture = new(FileManagerManifest(), NotepadManifest(), TextEditorManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        AppIntentOpenFileResult result = await caller.Intents.OpenFileAsync(
            VirtualPath.Parse("/home/alice/notes.txt"));

        Assert.Equal(AppIntentOpenFileOutcome.ChooserRequired, result.Outcome);
        Assert.Equal(2, result.CandidateAppIds!.Count);
    }

    [Fact]
    public async Task OpenFileAsync_NoHandler_ReturnsNoHandler()
    {
        Fixture fixture = new(FileManagerManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        AppIntentOpenFileResult result = await caller.Intents.OpenFileAsync(
            VirtualPath.Parse("/home/alice/photo.png"));

        Assert.Equal(AppIntentOpenFileOutcome.NoHandler, result.Outcome);
    }

    [Fact]
    public async Task LaunchAsync_WithFileArgument_OpensThatFileInTheExplicitlyChosenApp()
    {
        Fixture fixture = new(FileManagerManifest(), NotepadManifest(), TextEditorManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        fixture.Grant("org.kazoos.filemanager", principal, AppCapabilities.AppsLaunch);
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal);

        AppIntentLaunchResult result = await caller.Intents.LaunchAsync(
            "org.kazoos.texteditor", ["/home/alice/notes.txt"]);

        Assert.Equal(AppIntentLaunchOutcome.Launched, result.Outcome);
    }

    [Fact]
    public async Task OpenFileAsync_WithoutAppsLaunchCapability_ThrowsAccessDenied()
    {
        AppManifest restrictedFileManager = FileManagerManifest() with { Capabilities = [] };
        Fixture fixture = new(restrictedFileManager, NotepadManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        IAppExecutionContext caller = await fixture.LaunchCallerAsync(principal, restrictedFileManager);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(
            () => caller.Intents.OpenFileAsync(VirtualPath.Parse("/home/alice/notes.txt")).AsTask());
    }

    private static AppManifest FileManagerManifest() => new()
    {
        Id = "org.kazoos.filemanager",
        Name = "File Manager",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "Exercises IAppExecutionContext.Intents as a calling app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.kazoos.filemanager.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Capabilities = [AppCapabilities.AppsLaunch],
        Resources = AppResourceProfileManifest.None
    };

    private static AppManifest NotepadManifest() => new()
    {
        Id = "org.kazoos.notepad",
        Name = "Notepad",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "A simple text editor.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.kazoos.notepad.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = [new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])]
    };

    private static AppManifest TextEditorManifest() => new()
    {
        Id = "org.kazoos.texteditor",
        Name = "Text Editor",
        Version = "1.0.0",
        PublisherId = "org.kazoos",
        Description = "An advanced text editor.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "org.kazoos.texteditor.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        FileHandlers = [new FileHandlerManifest("text/plain", [".txt"], ["open", "edit"])]
    };

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        private readonly LocalLoginName _aliceLoginName;
        private AppIntentDispatcher? _dispatcher;

        internal Fixture(params AppManifest[] manifests)
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
            InMemoryEventBus eventBus = new();
            BoundedAuditLog auditLog = new(maxEntries: 100);

            LocalGroup group = groups.CreateGroup(LocalLoginName.Parse("users"));
            _aliceLoginName = LocalLoginName.Parse("alice");
            users.CreateUser(
                _aliceLoginName, "Alice", AppAuthority.User, group.Id,
                credential: LocalPasswordHasher.Create("hunter2", iterations: 100));

            ManualSimulationClock clock = new(_now, TimeSpan.FromSeconds(1));
            Manager = new InMemoryProcessManager(clock, Session = new LocalSessionService(
                users, seeder, eventBus, auditLog,
                InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), () => _now), eventBus);
            InMemoryNotificationQueue notifications = new(maxEntriesPerUser: 20);
            BoundedDiagnosticSink diagnostics = new(maxEntries: 100);
            Settings = new OsSettingsService(fileSystem, Session, clock);

            // Resolved lazily, mirroring the production wiring in EcosystemServiceCollectionExtensions:
            // AppIntentDispatcher depends (via AppLifecycleOrchestrator) on this very factory.
            AppExecutionContextFactory contextFactory = new(
                Grants, fileSystem, eventBus, topicBus, notifications, diagnostics, clock, Manager,
                intentDispatcherProvider: () => _dispatcher!);

            AppCatalogBuildResult catalogResult = AppCatalog.Build(manifests);
            Assert.True(catalogResult.IsSuccess, string.Join(", ", catalogResult.Errors.Select(e => e.Message)));
            AppCatalog catalog = catalogResult.Catalog!;

            Dictionary<string, AppDescriptor> descriptors = new(StringComparer.Ordinal);
            foreach (AppManifest manifest in manifests)
            {
                descriptors[manifest.Id] = new AppDescriptor(manifest, typeof(object), typeof(object).Assembly);
            }

            AppEnablementRegistry enablement = new(catalog);
            Orchestrator = new AppLifecycleOrchestrator(
                catalog, descriptors, enablement, Manager, Grants, contextFactory);
            FileAssociationResolver resolver = new(catalog, enablement, Settings);
            _dispatcher = new AppIntentDispatcher(Orchestrator, catalog, enablement, resolver, Grants, fileSystem);
        }

        internal LocalSessionService Session { get; }
        internal InMemoryProcessManager Manager { get; }
        internal CapabilityGrantRepository Grants { get; }
        internal IOsSettingsService Settings { get; }
        internal AppLifecycleOrchestrator Orchestrator { get; }

        internal Task<AuthenticatedPrincipal> LoginAsync() => Session.LoginAsync(_aliceLoginName, "hunter2");

        internal void Grant(string appId, AuthenticatedPrincipal principal, string capability) =>
            Grants.Grant(appId, principal.UserId.ToString(), capability, CapabilityGrantSource.UserApproval, AppAuthority.Administrator);

        /// <summary>Launches the file-manager (caller) window app and returns its live execution context.</summary>
        internal async Task<IAppExecutionContext> LaunchCallerAsync(
            AuthenticatedPrincipal principal, AppManifest? callerManifest = null)
        {
            string appId = (callerManifest ?? FileManagerManifest()).Id;
            AppLaunchResult result = await Orchestrator.LaunchAsync(new AppLaunchRequest(appId, principal, []));
            Assert.Equal(AppLaunchStatus.Launched, result.Status);
            return result.Context!;
        }

        private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
