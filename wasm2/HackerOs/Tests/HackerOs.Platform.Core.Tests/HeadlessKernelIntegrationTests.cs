using HackerOs.AppSdk.Settings;
using HackerOs.Platform.Core.Settings;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.Platform.Core.Diagnostics;
using HackerOs.Platform.Core.Discovery;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Intents;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Platform.Core.Notifications;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Processes;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Platform.Core.Time;
using HackerOs.Simulation.Abstractions;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests;

/// <summary>
/// Phase 1 exit-gate test for `P1-GATE-003`: proves the headless kernel boots, authenticates a
/// session, discovers a real app assembly, launches and executes an app that performs real
/// capability-checked filesystem and settings operations through its scoped gateways, stops a
/// running service on shutdown, disables an app, and logs the session out — with no Blazor,
/// browser, or renderer dependency anywhere in the path.
/// </summary>
public sealed class HeadlessKernelIntegrationTests
{
    [Fact]
    public async Task Login_installs_the_icon_packs_in_the_background_once_per_boot()
    {
        Fixture fixture = new(KernelSmokeApp.CreateManifest());

        await fixture.Session.LoginAsync(fixture.AliceLoginName, "hunter2");
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!InstallAudits(fixture).Any() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await fixture.Session.LogoutAsync();
        await fixture.Session.LoginAsync(fixture.AliceLoginName, "hunter2");
        await Task.Delay(100);

        AuditEntry install = Assert.Single(InstallAudits(fixture));
        Assert.Equal(AuditOutcome.Success, install.Outcome);

        static IEnumerable<AuditEntry> InstallAudits(Fixture fixture) =>
            fixture.AuditLog.Entries.Where(entry => entry.Action == "filesystem.install-icon-packs");
    }

    [Fact]
    public async Task Headless_kernel_boots_discovers_launches_executes_shuts_down_disables_and_logs_out()
    {
        Fixture fixture = new(KernelSmokeApp.CreateManifest(), WaiterManifest());

        // Session: authenticate and provision the exact home directory the gateway will resolve
        // (`/home/{userId}` keyed by the GUID user identity, not the login name).
        AuthenticatedPrincipal principal = await fixture.Session.LoginAsync(fixture.AliceLoginName, "hunter2");
        string userId = principal.UserId.ToString();
        await fixture.Seeder.SeedAsync(userId, "users");

        // Policy: grant exactly the capabilities the smoke app declares, as a build profile would.
        AppManifest smokeManifest = KernelSmokeApp.CreateManifest();
        foreach (string capability in smokeManifest.Capabilities)
        {
            fixture.Grants.Grant(
                smokeManifest.Id, userId, capability, CapabilityGrantSource.BuildProfile, AppAuthority.Administrator);
        }

        // Discovery: both manifests resolved their entry points from the real test assembly.
        Assert.True(fixture.Discovery.IsSuccess, string.Join(", ", fixture.Discovery.Errors.Select(e => e.Message)));

        // Boot a background service so shutdown has something real to stop.
        AppLaunchResult serviceLaunch = await fixture.Orchestrator.LaunchAsync(
            new AppLaunchRequest(WaiterManifest().Id, principal, []));
        Assert.Equal(AppLaunchStatus.Launched, serviceLaunch.Status);
        Assert.True(fixture.Manager.TryGetActive(serviceLaunch.Process!.Pid, out _));

        // App launch + command execution: the terminal app runs synchronously to completion,
        // performing real filesystem and settings operations through its scoped gateways.
        AppLaunchResult smokeLaunch = await fixture.Orchestrator.LaunchAsync(
            new AppLaunchRequest(smokeManifest.Id, principal, []));

        Assert.Equal(AppLaunchStatus.Launched, smokeLaunch.Status);
        Assert.Equal(0, smokeLaunch.ExitCode);
        Assert.Contains("FS-OK", smokeLaunch.StandardOutput);
        Assert.Contains("SETTINGS-OK", smokeLaunch.StandardOutput);

        // Shutdown: stop every remaining running process (the service) before logout, mirroring
        // a real OS closing applications first, and verify it observed the shutdown reason.
        await fixture.Orchestrator.StopAllAsync(ProcessExitReason.Shutdown);
        Assert.False(fixture.Manager.TryGetActive(serviceLaunch.Process!.Pid, out _));
        Assert.Equal(ServiceStopReason.Shutdown, WaiterServiceApp.LastStopReasonObserved);

        // Disable: the smoke app can no longer be launched once disabled.
        AppDisableResult disable = await fixture.Orchestrator.DisableAsync(smokeManifest.Id);
        Assert.True(disable.Success);
        AppLaunchResult disabledLaunch = await fixture.Orchestrator.LaunchAsync(
            new AppLaunchRequest(smokeManifest.Id, principal, []));
        Assert.Equal(AppLaunchStatus.Disabled, disabledLaunch.Status);

        // Logout: the session tears down and records a full audit trail from boot to logout.
        await fixture.Session.LogoutAsync();
        Assert.Contains(fixture.AuditLog.Entries, e => e.Action == "session.login" && e.Outcome == AuditOutcome.Success);
        Assert.Contains(fixture.AuditLog.Entries, e => e.Action == "session.logout" && e.Outcome == AuditOutcome.Success);
    }

    private static AppManifest WaiterManifest() => new()
    {
        Id = "org.hackeros.kernel-smoke.waiter",
        Name = "Waiter",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "Waits for cancellation or shutdown.",
        Kind = AppKind.Service,
        EntryPoint = new AppEntryPointManifest("HackerOs.Platform.Core.Tests", typeof(WaiterServiceApp).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    private sealed class WaiterServiceApp(AppManifest manifest) : ServiceAppBase(manifest)
    {
        internal static ServiceStopReason? LastStopReasonObserved { get; private set; }

        protected override Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellationToken);

        protected override ValueTask OnStoppingAsync(ServiceStopReason reason, CancellationToken cancellationToken)
        {
            LastStopReasonObserved = reason;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A terminal app that proves real end-to-end use of the scoped filesystem and settings
    /// gateways: it creates, writes, and reads back a private file under its own user's home
    /// directory, then reads the protected clean-profile file-association settings document.
    /// </summary>
    private sealed class KernelSmokeApp(AppManifest manifest) : TerminalAppBase(manifest)
    {
        private const string Payload = "hello-from-kernel-smoke-app";

        internal static AppManifest CreateManifest() => new()
        {
            Id = "org.hackeros.kernel-smoke",
            Name = "Kernel Smoke",
            Version = "1.0.0",
            PublisherId = "org.hackeros",
            Description = "Exercises the filesystem gateway and settings files end to end.",
            Kind = AppKind.Terminal,
            EntryPoint = new AppEntryPointManifest("HackerOs.Platform.Core.Tests", typeof(KernelSmokeApp).FullName!),
            SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
            Terminal = new TerminalCommandManifest("kernel-smoke", [], "kernel-smoke"),
            Capabilities =
            [
                AppCapabilities.FileSystemUserHomeRead,
                AppCapabilities.FileSystemUserHomeWrite,
                AppCapabilities.FileSystemPrivateRead,
                AppCapabilities.FileSystemPrivateWrite,
                AppCapabilities.FileAssociationsRead
            ],
            Settings = new AppSettingsSchemaManifest(
                1,
                [new AppSettingFieldManifest("greeting", AppSettingValueType.String, "unset", AppSettingScope.AppUser, AppSettingSensitivity.Public)])
        };

        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            IAppExecutionContext app = context.App;
            VirtualPath homePath = VirtualPath.Parse($"/home/{app.UserId}");

            FileSystemResult<FileSystemEntrySnapshot> homeStat = await app.FileSystem.StatAsync(
                new FileSystemStatRequest(homePath), cancellationToken);
            if (!homeStat.Succeeded)
            {
                await context.StandardError.WriteLineAsync($"home-stat-failed:{homeStat.Error?.Code}");
                return 1;
            }

            VirtualPath filePath = VirtualPath.Parse($"/home/{app.UserId}/kernel-smoke.txt");
            FileSystemMutationResult create = await app.FileSystem.CreateAsync(
                new FileSystemCreateRequest(
                    filePath, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x01A4), homeStat.Value!.Metadata.Revision),
                cancellationToken);
            if (!create.Succeeded)
            {
                await context.StandardError.WriteLineAsync($"create-failed:{create.Transaction.Error?.Code}");
                return 1;
            }

            FileSystemMutationResult write = await app.FileSystem.WriteAsync(
                new FileSystemWriteRequest(filePath, create.Entry!.Metadata.Revision),
                new TextSource(Payload),
                cancellationToken);
            if (!write.Succeeded)
            {
                await context.StandardError.WriteLineAsync($"write-failed:{write.Transaction.Error?.Code}");
                return 1;
            }

            FileSystemResult<FileSystemContentReadHandle> read = await app.FileSystem.ReadAsync(
                new FileSystemReadRequest(filePath), cancellationToken);
            if (!read.Succeeded)
            {
                await context.StandardError.WriteLineAsync($"read-failed:{read.Error?.Code}");
                return 1;
            }

            string readBack;
            await using (FileSystemContentReadHandle handle = read.Value!)
            using (StreamReader reader = new(handle.Content, Encoding.UTF8))
            {
                readBack = await reader.ReadToEndAsync(cancellationToken);
            }

            if (readBack != Payload)
            {
                await context.StandardError.WriteLineAsync($"content-mismatch:{readBack}");
                return 1;
            }

            await context.StandardOutput.WriteLineAsync("FS-OK".AsMemory(), cancellationToken);

            // Settings are real files (ADR 0044): the default applies, then a write lands in
            // ~/.config/<appId>/settings.config and is read back through the same files.
            if (await HackSettings.GetAsync<string>(app, "greeting", cancellationToken) != "unset")
            {
                await context.StandardError.WriteLineAsync("settings-default-failed");
                return 1;
            }

            await HackSettings.SetAsync(app, "greeting", "hello", cancellationToken);
            if (await HackSettings.GetAsync<string>(app, "greeting", cancellationToken) != "hello")
            {
                await context.StandardError.WriteLineAsync("settings-roundtrip-failed");
                return 1;
            }

            await context.StandardOutput.WriteLineAsync("SETTINGS-OK".AsMemory(), cancellationToken);
            return 0;
        }

        private sealed class TextSource(string content) : IFileSystemContentSource
        {
            private readonly byte[] _content = Encoding.UTF8.GetBytes(content);
            public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text("text/plain");
            public long? Length => _content.LongLength;
            public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
                ValueTask.FromResult<Stream>(new MemoryStream(_content, writable: false));
        }
    }

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

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
            Seeder = new FileSystemSeeder(fileSystem, timeProvider);

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
            Manager = new InMemoryProcessManager(clock, Session, eventBus);
            InMemoryNotificationQueue notifications = new(maxEntriesPerUser: 20);
            BoundedDiagnosticSink diagnostics = new(maxEntries: 100);
            Settings = new OsSettingsService(fileSystem, Session, clock);
            AppExecutionContextFactory contextFactory = new(
                Grants, fileSystem, eventBus, topicBus, notifications, diagnostics, clock, Manager);

            AppCatalogBuildResult catalogResult = AppCatalog.Build(manifests);
            Assert.True(catalogResult.IsSuccess, string.Join(", ", catalogResult.Errors.Select(e => e.Message)));
            AppCatalog catalog = catalogResult.Catalog!;

            Dictionary<string, System.Reflection.Assembly> hostAssemblies = new(StringComparer.Ordinal)
            {
                ["HackerOs.Platform.Core.Tests"] = typeof(Fixture).Assembly
            };
            Discovery = AppEntryPointDiscovery.Discover(catalog, hostAssemblies);

            AppEnablementRegistry enablement = new(catalog);
            Orchestrator = new AppLifecycleOrchestrator(
                catalog, Discovery.Descriptors!, enablement, Manager, Grants, contextFactory);
        }

        internal LocalSessionService Session { get; }
        internal FileSystemSeeder Seeder { get; }
        internal InMemoryProcessManager Manager { get; }
        internal CapabilityGrantRepository Grants { get; }
        internal IOsSettingsService Settings { get; }
        internal AppDiscoveryResult Discovery { get; }
        internal AppLifecycleOrchestrator Orchestrator { get; }
        internal BoundedAuditLog AuditLog { get; }
        internal LocalLoginName AliceLoginName { get; }

        private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
