using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>Contract tests of <c>KazoApps</c> and <c>KazoAppManagement</c>'s gateways (ADR 0056, sections 2 and 3).</summary>
public sealed class AppCatalogGatewayTests
{
    private const string Calc = "org.test.calc";
    private const string Lib = "org.test.lib";
    private const string Viewer = "org.test.viewer";
    private const string Term = "org.test.term";

    [Fact]
    public async Task Installed_apps_are_sorted_by_name_flag_system_apps_and_hide_uninstalled_ones()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync();
        AppCatalogGateway catalog = harness.CreateCatalogGateway();
        int changes = 0;
        catalog.Changed += (_, _) => changes++;

        IReadOnlyList<KazoAppInfo> before = await catalog.GetInstalledAsync();
        Assert.Equal(["Calc", "Lib", "Term", "Viewer"], before.Select(app => app.Name));
        Assert.True(before.Single(app => app.Id == Term).IsSystemApp);

        await harness.CreateManagementGateway(alice, AppCapabilities.AppsManage).UninstallAsync(Lib, KazoInstallScope.User);

        IReadOnlyList<KazoAppInfo> after = await catalog.GetInstalledAsync();
        Assert.Equal(["Calc", "Term"], after.Select(app => app.Name));
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task Handlers_list_only_installed_window_apps_that_declare_the_extension()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync();

        IReadOnlyList<KazoAppInfo> handlers = await harness.CreateCatalogGateway()
            .GetHandlersAsync(VirtualPath.Parse("/home/alice/notes.txt"));

        Assert.Equal([Calc, Viewer], handlers.Select(app => app.Id));
    }

    [Fact]
    public async Task Default_handlers_need_file_associations_read_and_a_change_also_needs_home_write()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync();

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() =>
            harness.CreateCatalogGateway(alice).GetDefaultHandlersAsync().AsTask());
        AppCatalogGateway readOnly = harness.CreateCatalogGateway(alice, AppCapabilities.FileAssociationsRead);
        await readOnly.GetDefaultHandlersAsync();
        AppGatewayAccessDeniedException denied = await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() =>
            readOnly.SetUserDefaultHandlerAsync(".txt", "open", Viewer).AsTask());
        Assert.Equal(AppCapabilities.FileSystemUserHomeWrite, denied.Capability);
    }

    [Fact]
    public async Task A_users_default_comes_first_in_the_defaults_and_in_open_with()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync();
        AppCatalogGateway gateway = harness.CreateCatalogGateway(
            alice, AppCapabilities.FileAssociationsRead, AppCapabilities.FileSystemUserHomeWrite);

        Assert.Equal(KazoDefaultHandlerStatus.Changed, await gateway.SetUserDefaultHandlerAsync(".TXT", "open", Viewer));
        Assert.Equal(KazoDefaultHandlerStatus.AppCannotHandle, await gateway.SetUserDefaultHandlerAsync(".txt", "open", Lib));

        KazoFileAssociation first = (await gateway.GetDefaultHandlersAsync())[0];
        Assert.Equal(new KazoFileAssociation(".txt", "open", Viewer, KazoFileAssociationLayer.User), first);
        IReadOnlyList<KazoAppInfo> handlers = await gateway.GetHandlersAsync(VirtualPath.Parse("/home/alice/notes.txt"));
        Assert.Equal([Viewer, Calc], handlers.Select(app => app.Id));
    }

    [Fact]
    public async Task Management_requires_apps_manage()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync();
        AppManagementGateway gateway = harness.CreateManagementGateway(alice);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.GetAllAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.UninstallAsync(Calc, KazoInstallScope.User).AsTask());
        Assert.True(harness.Enablement.IsEnabled(Calc));
    }

    [Fact]
    public async Task GetAll_reports_states_and_the_uninstall_closure()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync();
        AppManagementGateway gateway = harness.CreateManagementGateway(alice, AppCapabilities.AppsManage);

        await gateway.UninstallAsync(Calc, KazoInstallScope.User);
        IReadOnlyList<KazoAppInstallInfo> all = await gateway.GetAllAsync();

        Assert.Equal(KazoAppInstallState.UninstalledForUser, all.Single(entry => entry.App.Id == Calc).State);
        Assert.Equal([Viewer, Lib], all.Single(entry => entry.App.Id == Lib).UninstallClosure);
    }

    [Fact]
    public async Task A_system_app_is_refused_before_any_prompt()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync();
        CountingAgent agent = new(succeed: true);
        AppManagementGateway gateway = harness.CreateManagementGateway(alice, agent, AppCapabilities.AppsManage);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.UninstallAsync(Term, KazoInstallScope.Machine).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.UninstallAsync(Term, KazoInstallScope.User).AsTask());
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task Machine_scope_asks_the_agent_and_a_refusal_writes_nothing()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync();
        CountingAgent agent = new(succeed: false);
        AppManagementGateway gateway = harness.CreateManagementGateway(alice, agent, AppCapabilities.AppsManage);

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => gateway.UninstallAsync(Calc, KazoInstallScope.Machine).AsTask());

        Assert.Equal(1, agent.Calls);
        Assert.Equal("apps.uninstall.machine", agent.LastRequest!.Action);
        Assert.Equal("Uninstall Calc for all users", agent.LastRequest.Operation);
        Assert.True(harness.Enablement.IsEnabled(Calc));
        Assert.DoesNotContain(Calc, await harness.Settings.ReadAsync("/etc/hackeros/apps.config") ?? string.Empty);
    }

    [Fact]
    public async Task Machine_scope_writes_etc_once_an_administrator_authenticated_even_for_a_plain_user()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync();
        AppManagementGateway gateway = harness.CreateManagementGateway(alice, new CountingAgent(succeed: true), AppCapabilities.AppsManage);

        IReadOnlyList<string> written = await gateway.UninstallAsync(Calc, KazoInstallScope.Machine);

        Assert.Equal([Calc], written);
        Assert.Contains($"{Calc}=true", await harness.Settings.ReadAsync("/etc/hackeros/apps.config"));
        Assert.Equal(KazoAppInstallState.UninstalledForMachine, (await gateway.GetAllAsync()).Single(entry => entry.App.Id == Calc).State);

        Assert.Equal([Calc], await gateway.ReinstallAsync(Calc, KazoInstallScope.Machine));
        Assert.True(harness.Enablement.IsEnabled(Calc));
    }

    [Fact]
    public async Task A_user_cannot_reinstall_for_themselves_what_the_administrator_uninstalled()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Settings.WriteAsRootAsync("/etc/hackeros/apps.config", $"[uninstalled]\n{Calc}=true\n");
        AuthenticatedPrincipal alice = await harness.LoginAsync();
        AppManagementGateway gateway = harness.CreateManagementGateway(alice, AppCapabilities.AppsManage);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.ReinstallAsync(Calc, KazoInstallScope.User).AsTask());
        Assert.Contains("administrator", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Under_sudo_the_real_agent_passes_without_any_prompt()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync();
        AdministratorAuthenticationAgent agent = new(new RefusingVerifier(), new BoundedAuditLog(maxEntries: 10));
        AppManagementGateway gateway = harness.CreateManagementGateway(alice, agent, AppCapabilities.AppsManage, isRootElevated: true);

        await gateway.UninstallAsync(Calc, KazoInstallScope.Machine);

        Assert.False(harness.Enablement.IsEnabled(Calc));
    }

    private static AppManifest Window(string id, string name, string[] extensions, params AppDependencyManifest[] dependencies) => new()
    {
        Id = id,
        Name = name,
        Version = "1.0.0",
        PublisherId = "org.test",
        Description = name,
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", $"{id}.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Dependencies = dependencies,
        FileHandlers = extensions.Length == 0 ? [] : [new FileHandlerManifest(null, extensions, ["open"])]
    };

    private static AppDependencyManifest Needs(string appId) => new(appId, "1.0.0", null, Optional: false);

    /// <summary>Records requests and answers with a fixed outcome.</summary>
    private sealed class CountingAgent(bool succeed) : IAdministratorAuthenticationAgent
    {
        public int Calls { get; private set; }

        public AdministratorAuthenticationRequest? LastRequest { get; private set; }

        public Task<AdministratorAuthenticationResult> AuthenticateAsync(
            AdministratorAuthenticationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(succeed
                ? new AdministratorAuthenticationResult(true, "admin", null)
                : AdministratorAuthenticationResult.Failed("cancelled"));
        }
    }

    private sealed class RefusingVerifier : IAdminElevationVerifier
    {
        public Task<AdminElevationVerificationResult> VerifyAsync(string loginName, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(AdminElevationVerificationResult.Failed("invalid-credentials"));
    }

    /// <summary>Grants exactly the listed capabilities (the gateways only call <see cref="Require"/>).</summary>
    private sealed class FixedCapabilities(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!granted.Contains(capability, StringComparer.Ordinal))
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }

    /// <summary>
    /// Settings harness (alice) plus a catalog: Calc opens .txt; Lib; Viewer needs Lib and opens
    /// .txt; Term is a system app.
    /// </summary>
    private sealed class Harness
    {
        private Harness(SettingsFilesHarness settings)
        {
            Settings = settings;
            AppCatalogBuildResult built = AppCatalog.Build(
                [
                    Window(Calc, "Calc", [".txt"]),
                    Window(Lib, "Lib", []),
                    Window(Viewer, "Viewer", [".txt"], Needs(Lib)),
                    Window(Term, "Term", [])
                ],
                [Term]);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(error => error.Message)));
            Catalog = built.Catalog!;
            Enablement = new AppEnablementRegistry(Catalog);
            InMemoryProcessManager processes = new(settings.Clock, settings.Session, settings.EventBus);
            AppExecutionContextFactory contextFactory = new(
                settings.Grants, settings.FileSystem, settings.EventBus, settings.TopicBus,
                new InMemoryNotificationQueue(maxEntriesPerUser: 20), new BoundedDiagnosticSink(maxEntries: 100),
                settings.Clock, processes);
            Dictionary<string, AppDescriptor> descriptors = Catalog.Manifests.Values.ToDictionary(
                manifest => manifest.Id,
                manifest => new AppDescriptor(manifest, typeof(object), typeof(object).Assembly),
                StringComparer.Ordinal);
            AppLifecycleOrchestrator orchestrator = new(
                Catalog, descriptors, Enablement, processes, settings.Grants, contextFactory, settings.EventBus);
            Installation = new AppInstallationService(
                Catalog, Enablement, orchestrator, settings.FileSystem, settings.Session, settings.Clock,
                settings.TopicBus, settings.EventBus);
            Resolver = new Kazo.Os.Platform.Core.Intents.FileAssociationResolver(Catalog, Enablement, settings.Settings, settings.FileSystem);
        }

        internal SettingsFilesHarness Settings { get; }
        internal AppCatalog Catalog { get; }
        internal AppEnablementRegistry Enablement { get; }
        internal AppInstallationService Installation { get; }
        internal Kazo.Os.Platform.Core.Intents.FileAssociationResolver Resolver { get; }

        internal static async Task<Harness> CreateAsync()
        {
            Harness harness = new(new SettingsFilesHarness());
            await harness.Settings.BootAsync();
            await new AppEnablementToFilesMigration(harness.Settings.FileSystem, harness.Settings.Clock, harness.Catalog).MigrateAsync([]);
            await harness.Installation.StartAsync();
            return harness;
        }

        internal async Task<AuthenticatedPrincipal> LoginAsync()
        {
            AuthenticatedPrincipal principal = await Settings.LoginAsync(Settings.Alice);
            await Installation.ReloadAsync();
            return principal;
        }

        internal AppCatalogGateway CreateCatalogGateway() =>
            new(Catalog, Enablement, () => Resolver, () => Installation);

        internal AppCatalogGateway CreateCatalogGateway(AuthenticatedPrincipal principal, params string[] capabilities) =>
            new(Catalog, Enablement, () => Resolver, () => Installation, principal, new FixedCapabilities(capabilities));

        internal AppManagementGateway CreateManagementGateway(AuthenticatedPrincipal principal, params string[] capabilities) =>
            CreateManagementGateway(principal, new CountingAgent(succeed: false), capabilities);

        internal AppManagementGateway CreateManagementGateway(
            AuthenticatedPrincipal principal, IAdministratorAuthenticationAgent agent, string capability, bool isRootElevated) =>
            new(Catalog, () => Installation, () => agent, new FixedCapabilities(capability), Catalog.Manifests[Term],
                principal, isRootElevated, Settings.FileSystem, Settings.Clock);

        internal AppManagementGateway CreateManagementGateway(
            AuthenticatedPrincipal principal, IAdministratorAuthenticationAgent agent, params string[] capabilities) =>
            new(Catalog, () => Installation, () => agent, new FixedCapabilities(capabilities), Catalog.Manifests[Term],
                principal, false, Settings.FileSystem, Settings.Clock);
    }
}
