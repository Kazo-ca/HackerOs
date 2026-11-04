using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>
/// Contract and security tests for `P1-EXEC-008`, covering capability denial, structured
/// constraints, gateway isolation, selected-handle expiry/revocation, and cancellation
/// propagation for `IAppExecutionContext`.
/// </summary>
public sealed class AppExecutionContextTests
{
    [Fact]
    public async Task Notification_post_is_denied_without_the_notifications_capability()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);

        AppGatewayAccessDeniedException exception = Assert.Throws<AppGatewayAccessDeniedException>(
            () => context.Notifications.Post(NotificationSeverity.Information, "Title", "Message"));

        Assert.Equal(AppCapabilities.NotificationsPost, exception.Capability);
        Assert.False(exception.Evaluation.Granted);
    }

    [Fact]
    public async Task Notification_post_succeeds_once_the_capability_is_granted()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([AppCapabilities.NotificationsPost]);

        NotificationId id = context.Notifications.Post(NotificationSeverity.Information, "Title", "Message");

        Assert.NotEqual(default, id.Value);
    }

    [Fact]
    public async Task Capability_checker_denies_a_resource_outside_the_grants_structured_constraint()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);
        VirtualPath allowed = VirtualPath.Parse("/home/alice/Documents");
        VirtualPath outside = VirtualPath.Parse("/etc/passwd");
        fixture.Grants.Grant(
            fixture.AppId,
            fixture.AliceUserId,
            AppCapabilities.FileSystemUserSelectedRead,
            CapabilityGrantSource.UserApproval,
            AppAuthority.Administrator,
            [new VirtualPathCapabilityConstraint(allowed, includeDescendants: true)]);

        CapabilityPolicyEvaluation withinGrant = context.Capabilities.Evaluate(
            AppCapabilities.FileSystemUserSelectedRead,
            resourceCandidate: new VirtualPathResourceCandidate(allowed));
        CapabilityPolicyEvaluation outsideGrant = context.Capabilities.Evaluate(
            AppCapabilities.FileSystemUserSelectedRead,
            resourceCandidate: new VirtualPathResourceCandidate(outside));

        Assert.True(withinGrant.Granted);
        Assert.False(outsideGrant.Granted);
        Assert.Equal(CapabilityPolicyEvaluationReason.Constrained, outsideGrant.Reason);
    }

    [Fact]
    public async Task Filesystem_gateway_denies_home_reads_without_the_home_capability()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);

        FileSystemResult<FileSystemDirectorySnapshot> result = await context.FileSystem.EnumerateAsync(
            new FileSystemEnumerateRequest(VirtualPath.Parse($"/home/{fixture.AliceUserId}")));

        Assert.Equal(FileSystemErrorCode.CapabilityDenied, result.Error?.Code);
    }

    [Fact]
    public async Task Filesystem_gateway_allows_home_reads_once_granted()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([AppCapabilities.FileSystemUserHomeRead]);

        FileSystemResult<FileSystemDirectorySnapshot> result = await context.FileSystem.EnumerateAsync(
            new FileSystemEnumerateRequest(VirtualPath.Parse($"/home/{fixture.AliceUserId}")));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task An_app_may_always_kill_its_own_process_without_any_capability()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);

        ProcessRecord killed = context.Processes.Kill(context.ProcessId);

        Assert.Equal(ProcessState.Stopped, killed.State);
    }

    [Fact]
    public async Task Killing_another_process_is_denied_without_the_manage_capability()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);
        ProcessRecord other = fixture.Manager.Start(fixture.RequestFor("com.hackeros.other"));

        AppGatewayAccessDeniedException exception = Assert.Throws<AppGatewayAccessDeniedException>(
            () => context.Processes.Kill(other.Pid));

        Assert.Equal(AppCapabilities.ProcessManage, exception.Capability);
        Assert.True(fixture.Manager.TryGetActive(other.Pid, out _));
    }

    [Fact]
    public async Task Killing_another_process_succeeds_once_process_manage_is_granted()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([AppCapabilities.ProcessManage]);
        ProcessRecord other = fixture.Manager.Start(fixture.RequestFor("com.hackeros.other"));

        ProcessRecord killed = context.Processes.Kill(other.Pid);

        Assert.Equal(ProcessState.Stopped, killed.State);
    }

    [Fact]
    public async Task List_processes_is_scoped_to_the_own_process_without_the_list_capability()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);
        fixture.Manager.Start(fixture.RequestFor("com.hackeros.other"));

        IReadOnlyList<ProcessRecord> visible = context.Processes.ListProcesses();

        Assert.Single(visible);
        Assert.Equal(context.ProcessId, visible[0].Pid);
    }

    [Fact]
    public async Task List_processes_returns_every_active_process_with_the_list_capability()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([AppCapabilities.ProcessList]);
        fixture.Manager.Start(fixture.RequestFor("com.hackeros.other"));

        IReadOnlyList<ProcessRecord> visible = context.Processes.ListProcesses();

        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public async Task A_selected_handle_no_longer_allows_access_once_it_expires()
    {
        Fixture fixture = new();
        await fixture.CreateContextAsync([]);
        FileSystemSelectedResourceHandle handle = fixture.Registry.Issue(
            fixture.AppId, fixture.AliceUserId, VirtualPath.Parse("/home/alice/Documents"),
            FileSystemHandleAccess.Read, TimeSpan.FromSeconds(1));

        Assert.True(handle.Allows(
            fixture.AppId, fixture.AliceUserId, VirtualPath.Parse("/home/alice/Documents"),
            FileSystemHandleAccess.Read, fixture.Clock.UtcNow));

        fixture.Clock.Advance(2);
        fixture.Registry.TryGet(handle.Id, out FileSystemSelectedResourceHandle current);

        Assert.False(current.Allows(
            fixture.AppId, fixture.AliceUserId, VirtualPath.Parse("/home/alice/Documents"),
            FileSystemHandleAccess.Read, fixture.Clock.UtcNow));
    }

    [Fact]
    public void Explicit_revocation_denies_a_selected_handle_immediately()
    {
        Fixture fixture = new();
        FileSystemSelectedResourceHandle handle = fixture.Registry.Issue(
            fixture.AppId, "user", VirtualPath.Parse("/home/user"),
            FileSystemHandleAccess.Read, TimeSpan.FromMinutes(5));

        bool revoked = fixture.Registry.Revoke(handle.Id);
        fixture.Registry.TryGet(handle.Id, out FileSystemSelectedResourceHandle current);

        Assert.True(revoked);
        Assert.True(current.Revoked);
        Assert.False(current.Allows(fixture.AppId, "user", VirtualPath.Parse("/home/user"), FileSystemHandleAccess.Read, fixture.Clock.UtcNow));
    }

    [Fact]
    public async Task A_selected_handle_is_automatically_revoked_when_its_owning_process_exits()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);
        FileSystemSelectedResourceHandle handle = fixture.Registry.Issue(
            fixture.AppId, fixture.AliceUserId, VirtualPath.Parse("/home/alice"),
            FileSystemHandleAccess.Read, TimeSpan.FromMinutes(5), context.ProcessId);

        fixture.Manager.Kill(context.ProcessId);
        fixture.Registry.TryGet(handle.Id, out FileSystemSelectedResourceHandle current);

        Assert.True(current.Revoked);
    }

    [Fact]
    public async Task A_selected_handle_is_automatically_revoked_when_the_owning_user_logs_out()
    {
        Fixture fixture = new();
        await fixture.CreateContextAsync([]);
        FileSystemSelectedResourceHandle handle = fixture.Registry.Issue(
            fixture.AppId, fixture.AliceUserId, VirtualPath.Parse("/home/alice"),
            FileSystemHandleAccess.Read, TimeSpan.FromMinutes(5));

        await fixture.Session.LogoutAsync();
        fixture.Registry.TryGet(handle.Id, out FileSystemSelectedResourceHandle current);

        Assert.True(current.Revoked);
    }

    [Fact]
    public void A_selected_handle_is_automatically_revoked_when_its_app_is_disabled()
    {
        Fixture fixture = new();
        FileSystemSelectedResourceHandle handle = fixture.Registry.Issue(
            fixture.AppId, "user", VirtualPath.Parse("/home/alice"),
            FileSystemHandleAccess.Read, TimeSpan.FromMinutes(5));

        fixture.EventBus.Publish(new AppDisabledEvent(fixture.AppId));
        fixture.Registry.TryGet(handle.Id, out FileSystemSelectedResourceHandle current);

        Assert.True(current.Revoked);
    }

    [Fact]
    public async Task The_context_cancellation_token_fires_when_its_process_is_killed()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);

        fixture.Manager.Kill(context.ProcessId);

        Assert.True(context.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task The_context_exposes_no_root_service_provider_or_platform_internal_types()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);

        _ = context;
        foreach (System.Reflection.PropertyInfo property in typeof(IAppExecutionContext).GetProperties())
        {
            Assert.NotEqual("IServiceProvider", property.PropertyType.Name);
            Assert.False(
                property.PropertyType.Namespace?.StartsWith("Kazo.Os.Platform.Core", StringComparison.Ordinal) == true,
                $"{property.Name} exposes a concrete Platform.Core type ({property.PropertyType}).");
        }
    }

    [Fact]
    public async Task Context_home_directory_is_the_principals_login_home_not_its_user_id()
    {
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);

        Assert.Equal("/home/alice", context.HomeDirectory);
    }

    [Fact]
    public async Task Clock_callbacks_and_event_handlers_run_inside_the_subscribing_apps_ambient_context()
    {
        // ADR 0043: a gateway callback runs on the publisher's flow, so the gateway re-enters its own app.
        Fixture fixture = new();
        IAppExecutionContext context = await fixture.CreateContextAsync([]);
        IAppExecutionContext? seenByClock = null;
        IAppExecutionContext? seenByEvent = null;
        context.Clock.Schedule(TimeSpan.FromSeconds(1), () => seenByClock = KazoExecutionContext.CurrentOrNull);
        using IDisposable subscription = context.Events.Subscribe<AmbientProbeEvent>(_ => seenByEvent = KazoExecutionContext.CurrentOrNull);

        fixture.Clock.Advance();
        fixture.EventBus.Publish(new AmbientProbeEvent());

        Assert.Same(context, seenByClock);
        Assert.Same(context, seenByEvent);
        Assert.Null(KazoExecutionContext.CurrentOrNull);
    }

    private sealed record AmbientProbeEvent;

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        internal Fixture()
        {
            FixedTimeProvider timeProvider = new(_now);
            InMemoryFileSystemRepository repository = new(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                timeProvider);
            FileSystemMountRouter router = new(repository);
            Grants = new CapabilityGrantRepository(() => _now);
            TopicBus = new InMemoryTopicMessageBus(Grants);
            FileSystem = new FileSystemService(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                TopicBus,
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            Seeder = new FileSystemSeeder(FileSystem, timeProvider);

            InMemoryLocalUserRepository users = new(() => _now);
            InMemoryLocalGroupRepository groups = new();
            EventBus = new InMemoryEventBus();
            AuditLog = new BoundedAuditLog(maxEntries: 100);

            LocalGroup group = groups.CreateGroup(LocalLoginName.Parse("users"));
            AliceLoginName = LocalLoginName.Parse("alice");
            users.CreateUser(
                AliceLoginName, "Alice", AppAuthority.User, group.Id,
                credential: LocalPasswordHasher.Create("hunter2", iterations: 100));

            Session = new LocalSessionService(
                users, Seeder, EventBus, AuditLog,
                InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), () => _now);

            Clock = new ManualSimulationClock(_now, TimeSpan.FromSeconds(1));
            Manager = new InMemoryProcessManager(Clock, Session, EventBus);
            Notifications = new InMemoryNotificationQueue(maxEntriesPerUser: 20);
            Diagnostics = new BoundedDiagnosticSink(maxEntries: 100);
            Registry = new FileSystemSelectedResourceHandleRegistry(Clock, Grants, EventBus);
            Factory = new AppExecutionContextFactory(
                Grants, FileSystem, EventBus, TopicBus, Notifications, Diagnostics, Clock, Manager);

            Manifest = new AppManifest
            {
                Id = AppId,
                Name = "Test App",
                Version = "1.0.0",
                PublisherId = "org.hackeros",
                Description = "Execution context test application.",
                Kind = AppKind.Terminal,
                EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", "TestApp"),
                SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
                Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
                Resources = AppResourceProfileManifest.None
            };
        }

        internal string AppId => "org.hackeros.test-app";
        internal string AliceUserId { get; private set; } = string.Empty;

        internal FileSystemService FileSystem { get; }
        internal FileSystemSeeder Seeder { get; }
        internal InMemoryEventBus EventBus { get; }
        internal InMemoryTopicMessageBus TopicBus { get; }
        internal BoundedAuditLog AuditLog { get; }
        internal LocalSessionService Session { get; }
        internal ManualSimulationClock Clock { get; }
        internal InMemoryProcessManager Manager { get; }
        internal CapabilityGrantRepository Grants { get; }
        internal InMemoryNotificationQueue Notifications { get; }
        internal BoundedDiagnosticSink Diagnostics { get; }
        internal FileSystemSelectedResourceHandleRegistry Registry { get; }
        internal AppExecutionContextFactory Factory { get; }
        internal LocalLoginName AliceLoginName { get; }
        internal AppManifest Manifest { get; }

        internal async Task<IAppExecutionContext> CreateContextAsync(IReadOnlyList<string> grantedCapabilities)
        {
            AuthenticatedPrincipal principal = await Session.LoginAsync(AliceLoginName, "hunter2");
            AliceUserId = principal.UserId.ToString();
            await Seeder.SeedAsync(AliceUserId, "users");
            foreach (string capability in grantedCapabilities)
            {
                Grants.Grant(AppId, AliceUserId, capability, CapabilityGrantSource.UserApproval, AppAuthority.Administrator);
            }

            ProcessRecord process = Manager.Start(RequestForPrincipal(principal, AppId));
            return Factory.Create(
                Manifest, principal, process, new HashSet<string>(grantedCapabilities, StringComparer.Ordinal));
        }

        internal ProcessStartRequest RequestFor(string appId) => RequestForPrincipal(CurrentPrincipal, appId);

        private AuthenticatedPrincipal CurrentPrincipal =>
            Session.CurrentPrincipal ?? throw new InvalidOperationException("No active session.");

        private static ProcessStartRequest RequestForPrincipal(AuthenticatedPrincipal principal, string appId) => new(
            null, appId, AppInstanceId.FromGuid(Guid.NewGuid()), AppKind.Terminal,
            principal.UserId, principal.SessionId, ResourceProfile.None);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
