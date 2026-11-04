using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Platform.Core.WebData;
using Kazo.Os.AppSdk.WebData;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Platform.Core.Tests.Apps;

/// <summary>Contract tests of ADR 0048 section 7: uninstall and reinstall through <c>apps.config</c>.</summary>
public sealed class AppInstallationServiceTests
{
    private const string Calc = "org.test.calc";
    private const string Lib = "org.test.lib";
    private const string Viewer = "org.test.viewer";
    private const string Term = "org.test.term";
    private const string Kernel = "org.test.kernel";
    private const string Unknown = "org.other-build.app";
    private const string Site = "org.test.site";

    [Fact]
    public async Task User_uninstall_writes_the_home_file_blocks_launch_stops_instances_and_keeps_data()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Settings.Alice);
        await harness.Settings.WriteAsUserAsync($"/home/alice/.config/{Calc}/prefs.config", "mode=scientific\n");
        AppLaunchResult running = await harness.Orchestrator.LaunchAsync(new AppLaunchRequest(Calc, alice, []));
        Assert.Equal(AppLaunchStatus.Launched, running.Status);

        AppInstallationResult result = await harness.Installation.UninstallAsync(Calc, AppInstallationScope.User);

        Assert.True(result.Success);
        Assert.Contains($"{Calc}=true", await harness.Settings.ReadAsync("/home/alice/.config/kazoos/apps.config"));
        Assert.Equal(AppInstallationState.UninstalledForUser, harness.Installation.GetState(Calc));
        Assert.False(harness.Enablement.IsEnabled(Calc));
        Assert.False(harness.Processes.TryGetActive(running.Process!.Pid, out _));
        AppLaunchResult relaunch = await harness.Orchestrator.LaunchAsync(new AppLaunchRequest(Calc, alice, []));
        Assert.Equal(AppLaunchStatus.Disabled, relaunch.Status);
        Assert.Equal("mode=scientific\n", await harness.Settings.ReadAsync($"/home/alice/.config/{Calc}/prefs.config"));
        Assert.Contains(harness.Log.Lines, line => line == $"apps: {Calc} uninstalled (scope=user, by=alice)");
    }

    [Fact]
    public async Task The_uninstall_plan_lists_the_running_processes_and_the_result_the_stopped_ones()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Settings.Alice);
        AppLaunchResult running = await harness.Orchestrator.LaunchAsync(new AppLaunchRequest(Calc, alice, []));

        AppUninstallPlan plan = await harness.Installation.PlanUninstallAsync(Calc);
        AppInstallationResult result = await harness.Installation.UninstallAsync(Calc, AppInstallationScope.User);

        // ADR 0097 section 5: what will stop is shown before, what stopped is reported after.
        AppProcessInfo planned = Assert.Single(plan.RunningProcesses);
        Assert.Equal(running.Process!.Pid.Value, planned.Pid);
        Assert.Equal("alice", planned.UserName);
        AppProcessInfo stopped = Assert.Single(result.StoppedProcesses!);
        Assert.Equal(planned.Pid, stopped.Pid);
        Assert.False(stopped.Forced);
        Assert.Empty((await harness.Installation.PlanUninstallAsync(Calc)).RunningProcesses);
    }

    [Fact]
    public async Task Uninstalling_a_website_deletes_its_browser_database_and_nothing_else()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync(harness.Settings.Alice);
        KazoWebDbSchemaDefinition schema = new(1, [new KazoWebDbMigrationStep(1, [new KazoWebDbStoreDefinition("notes", null, true, [])], [], [], [])]);
        await harness.WebDatabases.OpenAsync(KazoWebDbNames.ForApp(Site), schema);
        await harness.WebDatabases.OpenAsync(KazoWebDbNames.ForApp("org.test.other-site"), schema);

        AppInstallationResult calc = await harness.Installation.UninstallAsync(Calc, AppInstallationScope.User);
        Assert.Equal(2, (await harness.WebDatabases.ListAsync()).Count);
        AppInstallationResult site = await harness.Installation.UninstallAsync(Site, AppInstallationScope.User);

        Assert.True(calc.Success);
        Assert.True(site.Success);
        Kazo.Os.AppSdk.Administration.KazoWebSiteDatabaseInfo left = Assert.Single(await harness.WebDatabases.ListAsync());
        Assert.Equal("kazoweb.org.test.other-site", left.Name);
        Assert.Contains(harness.Log.Lines, line => line == $"apps: {Site} browser database kazoweb.{Site} deleted");
    }

    [Fact]
    public async Task Reinstall_removes_the_line_and_launches_again()
    {
        Harness harness = await Harness.CreateAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Settings.Alice);
        await harness.Installation.UninstallAsync(Calc, AppInstallationScope.User);

        AppInstallationResult result = await harness.Installation.ReinstallAsync(Calc, AppInstallationScope.User);

        Assert.True(result.Success);
        Assert.Equal(AppInstallationState.Installed, harness.Installation.GetState(Calc));
        Assert.DoesNotContain(Calc, await harness.Settings.ReadAsync("/home/alice/.config/kazoos/apps.config"));
        Assert.Equal(AppLaunchStatus.Launched, (await harness.Orchestrator.LaunchAsync(new AppLaunchRequest(Calc, alice, []))).Status);
    }

    [Fact]
    public async Task A_system_app_cannot_be_uninstalled_by_the_service()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync(harness.Settings.Alice);

        AppInstallationResult result = await harness.Installation.UninstallAsync(Term, AppInstallationScope.User);

        Assert.False(result.Success);
        Assert.Equal($"apps.app.system:{Term}", Assert.Single(result.Errors));
        Assert.Equal(AppInstallationState.System, harness.Installation.GetState(Term));
        Assert.True(harness.Enablement.IsEnabled(Term));
    }

    [Fact]
    public async Task An_app_a_system_app_needs_cannot_be_uninstalled()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync(harness.Settings.Alice);

        AppInstallationResult result = await harness.Installation.UninstallAsync(Kernel, AppInstallationScope.User);

        Assert.False(result.Success);
        Assert.Equal($"apps.closure.system-app:{Term}", Assert.Single(result.Errors));
        Assert.True(harness.Enablement.IsEnabled(Kernel));
    }

    [Fact]
    public async Task Lines_for_system_apps_and_invalid_values_are_ignored_with_a_warning_and_unknown_ids_silently()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Settings.WriteAsRootAsync(
            "/etc/kazoos/apps.config",
            $"[uninstalled]\n{Term}=true\n{Kernel}=true\n{Viewer}=maybe\n{Unknown}=true\n{Calc}=true\n");

        await harness.Installation.ReloadAsync();

        Assert.True(harness.Enablement.IsEnabled(Term));
        Assert.True(harness.Enablement.IsEnabled(Kernel));
        Assert.True(harness.Enablement.IsEnabled(Viewer));
        Assert.False(harness.Enablement.IsEnabled(Calc));
        Assert.Contains(harness.Log.Lines, line => line.Contains($"{Term} is a system app", StringComparison.Ordinal));
        Assert.Contains(harness.Log.Lines, line => line.Contains($"{Kernel} is needed by system app {Term}", StringComparison.Ordinal));
        Assert.Contains(harness.Log.Lines, line => line.Contains("'maybe' is not true or false", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Log.Lines, line => line.Contains(Unknown, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Machine_scope_needs_rights_on_the_file_and_an_approved_write_applies_to_every_user()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync(harness.Settings.Alice);

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(
            () => harness.Installation.UninstallAsync(Calc, AppInstallationScope.Machine).AsTask());
        Assert.True(harness.Enablement.IsEnabled(Calc));

        // An approved write (sudo, or the ADR 0042 approval ticket) reaches the file.
        AppInstallationResult approved = await harness.Installation.UninstallAsync(
            Calc, AppInstallationScope.Machine, harness.Settings.CreateRootGateway());
        Assert.True(approved.Success);
        Assert.Contains($"{Calc}=true", await harness.Settings.ReadAsync("/etc/kazoos/apps.config"));
        Assert.Equal(AppInstallationState.UninstalledForMachine, harness.Installation.GetState(Calc));

        await harness.Settings.Session.LogoutAsync();
        await harness.LoginAsync(harness.Settings.Bob);
        Assert.Equal(AppInstallationState.UninstalledForMachine, harness.Installation.GetState(Calc));
        Assert.False(harness.Enablement.IsEnabled(Calc));
    }

    [Fact]
    public async Task A_user_cannot_reinstall_for_themselves_an_app_uninstalled_for_the_machine()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Settings.WriteAsRootAsync("/etc/kazoos/apps.config", $"[uninstalled]\n{Calc}=true\n");
        await harness.LoginAsync(harness.Settings.Alice);

        AppInstallationResult result = await harness.Installation.ReinstallAsync(Calc, AppInstallationScope.User);

        Assert.False(result.Success);
        Assert.Equal($"apps.uninstalled-by-administrator:{Calc}", Assert.Single(result.Errors));
        Assert.False(harness.Enablement.IsEnabled(Calc));
    }

    [Fact]
    public async Task The_effective_state_is_machine_or_user_and_logout_drops_the_user_layer()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.Settings.WriteAsRootAsync("/etc/kazoos/apps.config", $"[uninstalled]\n{Calc}=true\n");
        await harness.LoginAsync(harness.Settings.Alice);
        await harness.Installation.UninstallAsync(Viewer, AppInstallationScope.User);

        Assert.False(harness.Enablement.IsEnabled(Calc));
        Assert.False(harness.Enablement.IsEnabled(Viewer));

        await harness.Settings.Session.LogoutAsync();
        await harness.Installation.ReloadAsync();

        Assert.False(harness.Enablement.IsEnabled(Calc));
        Assert.True(harness.Enablement.IsEnabled(Viewer));
    }

    [Fact]
    public async Task Uninstalling_a_dependency_uninstalls_its_dependents_and_reinstalling_restores_dependencies()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync(harness.Settings.Alice);

        Assert.Equal([Viewer, Lib], harness.Installation.GetUninstallClosure(Lib));
        AppInstallationResult uninstall = await harness.Installation.UninstallAsync(Lib, AppInstallationScope.User);
        Assert.Equal([Viewer, Lib], uninstall.AppIds);
        Assert.False(harness.Enablement.IsEnabled(Viewer));

        AppInstallationResult reinstall = await harness.Installation.ReinstallAsync(Viewer, AppInstallationScope.User);

        Assert.Equal([Lib, Viewer], reinstall.AppIds);
        Assert.True(harness.Enablement.IsEnabled(Lib));
        Assert.True(harness.Enablement.IsEnabled(Viewer));
    }

    [Fact]
    public async Task An_edit_of_apps_config_through_the_file_api_applies_without_restart()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync(harness.Settings.Alice);

        // Like `nano ~/.config/kazoos/apps.config`: no call to the service.
        await harness.Settings.WriteAsUserAsync("/home/alice/.config/kazoos/apps.config", $"[uninstalled]\n{Calc}=true\n");
        await WaitUntilAsync(() => !harness.Enablement.IsEnabled(Calc));

        await harness.Settings.WriteAsUserAsync("/home/alice/.config/kazoos/apps.config", "[uninstalled]\n");
        await WaitUntilAsync(() => harness.Enablement.IsEnabled(Calc));
    }

    [Fact]
    public async Task An_uninstall_keeps_unknown_ids_and_comments_of_the_file()
    {
        Harness harness = await Harness.CreateAsync();
        await harness.LoginAsync(harness.Settings.Alice);
        await harness.Settings.WriteAsUserAsync(
            "/home/alice/.config/kazoos/apps.config", $"# mine\n[uninstalled]\n{Unknown}=true\n");

        await harness.Installation.UninstallAsync(Calc, AppInstallationScope.User);

        Assert.Equal(
            $"# mine\n[uninstalled]\n{Unknown}=true\n{Calc}=true\n",
            await harness.Settings.ReadAsync("/home/alice/.config/kazoos/apps.config"));
    }

    [Fact]
    public async Task The_migration_moves_disabled_flags_into_the_machine_file_once()
    {
        Harness harness = await Harness.CreateAsync(boot: false);
        AppEnablementToFilesMigration migration = new(harness.Settings.FileSystem, harness.Settings.Clock, harness.Catalog);

        await migration.MigrateAsync(
        [
            new PersistedAppCatalogEntry(harness.Catalog.Manifests[Calc], IsEnabled: false),
            new PersistedAppCatalogEntry(harness.Catalog.Manifests[Term], IsEnabled: false),
            new PersistedAppCatalogEntry(harness.Catalog.Manifests[Viewer], IsEnabled: true)
        ]);
        await migration.MigrateAsync([new PersistedAppCatalogEntry(harness.Catalog.Manifests[Viewer], IsEnabled: false)]);

        string content = (await harness.Settings.ReadAsync("/etc/kazoos/apps.config"))!;
        Assert.Contains($"{Calc}=true", content);
        Assert.DoesNotContain(Term, content);
        Assert.DoesNotContain(Viewer, content);
        Assert.NotNull(await harness.Settings.ReadAsync(AppEnablementToFilesMigration.MarkerPath));
        Assert.Equal("system", (await harness.Settings.StatAsync("/etc/kazoos/apps.config"))!.OwnerId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static AppManifest Window(string id, string name, params AppDependencyManifest[] dependencies) => new()
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
        Dependencies = dependencies
    };

    private static AppDependencyManifest Needs(string appId) => new(appId, "1.0.0", null, Optional: false);

    /// <summary>
    /// Settings harness (alice, bob) plus a catalog: Calc, Lib and Viewer (needs Lib) are ordinary;
    /// Term is a system app that needs Kernel.
    /// </summary>
    private sealed class Harness
    {
        private Harness(SettingsFilesHarness settings)
        {
            Settings = settings;
            AppCatalogBuildResult built = AppCatalog.Build(
                [
                    Window(Calc, "Calc"),
                    Window(Lib, "Lib"),
                    Window(Viewer, "Viewer", Needs(Lib)),
                    Window(Kernel, "Kernel"),
                    Window(Term, "Term", Needs(Kernel)),
                    Window(Site, "Site") with { Kind = AppKind.Web, Web = new WebAppManifest(["http://site.hack/"]) }
                ],
                [Term]);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(error => error.Message)));
            Catalog = built.Catalog!;
            Enablement = new AppEnablementRegistry(Catalog);
            Processes = new InMemoryProcessManager(settings.Clock, settings.Session, settings.EventBus);
            AppExecutionContextFactory contextFactory = new(
                settings.Grants, settings.FileSystem, settings.EventBus, settings.TopicBus,
                new InMemoryNotificationQueue(maxEntriesPerUser: 20), new BoundedDiagnosticSink(maxEntries: 100),
                settings.Clock, Processes);
            Dictionary<string, AppDescriptor> descriptors = Catalog.Manifests.Values.ToDictionary(
                manifest => manifest.Id,
                manifest => new AppDescriptor(manifest, typeof(object), typeof(object).Assembly),
                StringComparer.Ordinal);
            Orchestrator = new AppLifecycleOrchestrator(
                Catalog, descriptors, Enablement, Processes, settings.Grants, contextFactory, settings.EventBus);
            Installation = new AppInstallationService(
                Catalog, Enablement, Orchestrator, settings.FileSystem, settings.Session, settings.Clock,
                settings.TopicBus, settings.EventBus, Log, WebDatabases);
        }

        internal SettingsFilesHarness Settings { get; }
        internal AppCatalog Catalog { get; }
        internal AppEnablementRegistry Enablement { get; }
        internal InMemoryProcessManager Processes { get; }
        internal AppLifecycleOrchestrator Orchestrator { get; }
        internal AppInstallationService Installation { get; }
        internal ListLogger<AppInstallationService> Log { get; } = new();
        internal InMemoryWebSiteDatabaseStore WebDatabases { get; } = new();

        /// <summary>Boots like the Ecosystem host: settings defaults, the apps.config migration, then the service.</summary>
        internal static async Task<Harness> CreateAsync(bool boot = true)
        {
            Harness harness = new(new SettingsFilesHarness());
            if (boot)
            {
                await harness.Settings.BootAsync();
                await new AppEnablementToFilesMigration(harness.Settings.FileSystem, harness.Settings.Clock, harness.Catalog)
                    .MigrateAsync([]);
                await harness.Installation.StartAsync();
            }

            return harness;
        }

        /// <summary>Logs in and waits for the login-triggered reload, so the user layer applies.</summary>
        internal async Task<AuthenticatedPrincipal> LoginAsync(LocalUser user)
        {
            AuthenticatedPrincipal principal = await Settings.LoginAsync(user);
            await Installation.ReloadAsync();
            return principal;
        }
    }

    /// <summary>Captures formatted log lines (what reaches <c>/var/log/syslog</c> in the host).</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly object _sync = new();
        private readonly List<string> _lines = [];

        internal IReadOnlyList<string> Lines
        {
            get
            {
                lock (_sync)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }
}
