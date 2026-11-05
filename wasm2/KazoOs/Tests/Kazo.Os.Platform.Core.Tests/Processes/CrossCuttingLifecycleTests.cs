using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>
/// Cross-cutting tests for `P1-SYS-011`, proving session, process, event, diagnostic, and
/// resource-simulation subsystems compose correctly end to end with no sleeps or wall-clock
/// dependence.
/// </summary>
public sealed class CrossCuttingLifecycleTests
{
    [Fact]
    public async Task Logout_cancels_every_descendant_process_token_and_leaves_a_full_audit_trail()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal principal = await fixture.Session.LoginAsync(fixture.AliceLoginName, "hunter2");

        ProcessRecord shell = fixture.Manager.Start(fixture.RequestFor(principal, "com.kazoos.shell"));
        ProcessRecord child = fixture.Manager.Start(fixture.RequestFor(principal, "com.kazoos.editor", shell.Pid));
        CancellationToken shellToken = fixture.Manager.GetCancellationToken(shell.Pid);
        CancellationToken childToken = fixture.Manager.GetCancellationToken(child.Pid);

        await fixture.Session.LogoutAsync();

        Assert.True(shellToken.IsCancellationRequested);
        Assert.True(childToken.IsCancellationRequested);
        Assert.Contains(fixture.AuditLog.Entries, e => e.Action == "session.login" && e.Outcome == AuditOutcome.Success);
        Assert.Contains(fixture.AuditLog.Entries, e => e.Action == "session.logout" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Killing_a_parent_process_does_not_affect_the_session_or_unrelated_processes()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal principal = await fixture.Session.LoginAsync(fixture.AliceLoginName, "hunter2");

        ProcessRecord shell = fixture.Manager.Start(fixture.RequestFor(principal, "com.kazoos.shell"));
        ProcessRecord child = fixture.Manager.Start(fixture.RequestFor(principal, "com.kazoos.editor", shell.Pid));
        ProcessRecord unrelated = fixture.Manager.Start(fixture.RequestFor(principal, "com.kazoos.settings"));
        CancellationToken unrelatedToken = fixture.Manager.GetCancellationToken(unrelated.Pid);

        fixture.Manager.Kill(shell.Pid);

        Assert.False(fixture.Manager.TryGetActive(shell.Pid, out _));
        Assert.False(fixture.Manager.TryGetActive(child.Pid, out _));
        Assert.True(fixture.Manager.TryGetActive(unrelated.Pid, out _));
        Assert.False(unrelatedToken.IsCancellationRequested);
        Assert.Equal(SessionState.Active, fixture.Session.State);
    }

    [Fact]
    public async Task Resource_ticks_stop_counting_a_process_once_it_is_stopped()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal principal = await fixture.Session.LoginAsync(fixture.AliceLoginName, "hunter2");

        ResourceProfile profile = new(0.5, 1.0, 0.5, 1.0, 0.5, 1.0, 0.5, 1.0);
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor(principal, "com.kazoos.worker", resourceProfile: profile));
        fixture.Manager.MarkRunning(process.Pid);

        IReadOnlyList<ProcessResourceSample> whileRunning = fixture.Simulator.Tick(fixture.Manager.GetActiveProcesses());
        Assert.Single(whileRunning);
        Assert.True(whileRunning[0].CpuUsage > 0);

        ProcessRecord stopped = fixture.Manager.Complete(process.Pid, exitCode: 0);
        IReadOnlyList<ProcessResourceSample> afterStop = fixture.Simulator.Tick([stopped, .. fixture.Manager.GetActiveProcesses()]);
        Assert.Empty(afterStop);
    }

    [Fact]
    public async Task Every_process_state_transition_and_session_transition_publishes_an_event_in_order()
    {
        Fixture fixture = new();
        List<object> events = [];
        using IDisposable s1 = fixture.EventBus.Subscribe<SessionActivatedEvent>(events.Add);
        using IDisposable s2 = fixture.EventBus.Subscribe<ProcessStateChangedEvent>(events.Add);
        using IDisposable s3 = fixture.EventBus.Subscribe<SessionLoggedOutEvent>(events.Add);

        AuthenticatedPrincipal principal = await fixture.Session.LoginAsync(fixture.AliceLoginName, "hunter2");
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor(principal, "com.kazoos.shell"));
        fixture.Manager.MarkRunning(process.Pid);
        fixture.Manager.Complete(process.Pid, exitCode: 0);
        await fixture.Session.LogoutAsync();

        Assert.IsType<SessionActivatedEvent>(events[0]);
        Assert.IsType<ProcessStateChangedEvent>(events[1]);
        Assert.IsType<ProcessStateChangedEvent>(events[2]);
        Assert.IsType<ProcessStateChangedEvent>(events[3]);
        Assert.IsType<SessionLoggedOutEvent>(events[4]);
    }

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
            FileSystemService fileSystem = new(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                new InMemoryTopicMessageBus(new CapabilityGrantRepository(() => _now)),
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            FileSystemSeeder seeder = new(fileSystem, timeProvider);

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
                users, seeder, EventBus, AuditLog,
                InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), () => _now);

            Clock = new ManualSimulationClock(_now, TimeSpan.FromSeconds(1));
            Manager = new InMemoryProcessManager(Clock, Session, EventBus);
            Simulator = new DeterministicResourceSimulator(Clock, new SeededSimulationRandom(1), VirtualHardwareProfile.Default);
        }

        internal InMemoryEventBus EventBus { get; }
        internal BoundedAuditLog AuditLog { get; }
        internal LocalSessionService Session { get; }
        internal ManualSimulationClock Clock { get; }
        internal InMemoryProcessManager Manager { get; }
        internal DeterministicResourceSimulator Simulator { get; }
        internal LocalLoginName AliceLoginName { get; }

        internal ProcessStartRequest RequestFor(
            AuthenticatedPrincipal principal, string appId, ProcessId? parentPid = null, ResourceProfile? resourceProfile = null) => new(
            parentPid, appId, AppInstanceId.FromGuid(Guid.NewGuid()), AppKind.Terminal,
            principal.UserId, principal.SessionId, resourceProfile ?? ResourceProfile.None);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
