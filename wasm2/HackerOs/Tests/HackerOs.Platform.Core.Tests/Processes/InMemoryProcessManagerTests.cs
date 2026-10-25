using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Diagnostics;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Processes;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Platform.Core.Time;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Time;

namespace HackerOs.Platform.Core.Tests.Processes;

public sealed partial class InMemoryProcessManagerTests
{
    [Fact]
    public void Starting_a_process_allocates_a_positive_pid_and_links_the_session_token()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));

        Assert.True(process.Pid.Value > 0);
        Assert.Equal(ProcessState.Starting, process.State);
        Assert.False(fixture.Manager.GetCancellationToken(process.Pid).IsCancellationRequested);
    }

    [Fact]
    public void Pids_are_never_reused_even_after_history_eviction()
    {
        Fixture fixture = new(maxHistory: 1);
        ProcessRecord first = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.Kill(first.Pid);
        ProcessRecord second = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));

        Assert.NotEqual(first.Pid, second.Pid);
        Assert.True(second.Pid.Value > first.Pid.Value);
    }

    [Fact]
    public void Starting_a_child_with_an_inactive_parent_throws()
    {
        Fixture fixture = new();
        ProcessRecord parent = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.Kill(parent.Pid);

        Assert.Throws<InvalidOperationException>(
            () => fixture.Manager.Start(fixture.RequestFor("com.hackeros.child", parent.Pid)));
    }

    [Fact]
    public void MarkRunning_sets_the_start_timestamp_and_transitions_to_running()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));

        ProcessRecord running = fixture.Manager.MarkRunning(process.Pid);

        Assert.Equal(ProcessState.Running, running.State);
        Assert.NotNull(running.StartedAtUtc);
    }

    [Fact]
    public void MarkRunning_on_a_non_starting_process_throws()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.MarkRunning(process.Pid);

        Assert.Throws<InvalidOperationException>(() => fixture.Manager.MarkRunning(process.Pid));
    }

    [Fact]
    public void Complete_moves_an_active_process_to_history_with_the_exit_code()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.MarkRunning(process.Pid);

        ProcessRecord completed = fixture.Manager.Complete(process.Pid, exitCode: 0);

        Assert.Equal(ProcessState.Stopped, completed.State);
        Assert.Equal(0, completed.ExitCode);
        Assert.Equal(ProcessExitReason.Completed, completed.ExitReason);
        Assert.False(fixture.Manager.TryGetActive(process.Pid, out _));
        Assert.Contains(fixture.Manager.GetHistory(), r => r.Pid == process.Pid);
    }

    [Fact]
    public void Fault_moves_an_active_process_to_the_faulted_terminal_state()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));

        ProcessRecord faulted = fixture.Manager.Fault(process.Pid);

        Assert.Equal(ProcessState.Faulted, faulted.State);
        Assert.Equal(ProcessExitReason.Fault, faulted.ExitReason);
        Assert.False(fixture.Manager.TryGetActive(process.Pid, out _));
    }

    [Fact]
    public void Kill_cancels_the_process_token_and_force_stops_it()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        CancellationToken token = fixture.Manager.GetCancellationToken(process.Pid);

        ProcessRecord killed = fixture.Manager.Kill(process.Pid);

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(ProcessState.Stopped, killed.State);
        Assert.Equal(ProcessExitReason.Killed, killed.ExitReason);
    }

    [Fact]
    public async Task A_child_with_a_parent_exit_grace_period_gets_a_stop_request_then_a_kill()
    {
        Fixture fixture = new();
        ProcessRecord parent = fixture.Manager.Start(fixture.RequestFor("com.hackeros.app"));
        ProcessRecord child = fixture.Manager.Start(fixture.RequestFor("com.hackeros.app", parent.Pid) with
        {
            ParentExitGracePeriod = TimeSpan.FromSeconds(5),
        });
        ProcessRecord grandchild = fixture.Manager.Start(fixture.RequestFor("com.hackeros.helper", child.Pid));
        fixture.Manager.MarkRunning(child.Pid);
        CancellationToken token = fixture.Manager.GetCancellationToken(child.Pid);

        fixture.Manager.Kill(parent.Pid, ProcessExitReason.CloseRequested);

        // ADR 0147: SIGTERM at once (stopping, token cancelled), its own child left to it.
        Assert.True(fixture.Manager.TryGetActive(child.Pid, out ProcessRecord? stopping));
        Assert.Equal(ProcessState.Stopping, stopping!.State);
        Assert.True(token.IsCancellationRequested);
        Assert.True(fixture.Manager.TryGetActive(grandchild.Pid, out _));

        // SIGKILL at the end of the grace period, with what it left running.
        fixture.Clock.Advance(5);
        // The leftovers are killed just after the child ends, by the same background task: wait for both.
        await WaitUntilAsync(() => !fixture.Manager.TryGetActive(child.Pid, out _) && !fixture.Manager.TryGetActive(grandchild.Pid, out _));
        Assert.Equal(ProcessExitReason.Timeout, fixture.Manager.GetHistory().Single(r => r.Pid == child.Pid).ExitReason);
        Assert.Equal(ProcessExitReason.DependencyStop, fixture.Manager.GetHistory().Single(r => r.Pid == grandchild.Pid).ExitReason);
    }

    [Fact]
    public void A_sigkill_of_the_parent_ends_a_child_with_a_grace_period_at_once()
    {
        Fixture fixture = new();
        ProcessRecord parent = fixture.Manager.Start(fixture.RequestFor("com.hackeros.app"));
        ProcessRecord child = fixture.Manager.Start(fixture.RequestFor("com.hackeros.app", parent.Pid) with
        {
            ParentExitGracePeriod = TimeSpan.FromSeconds(5),
        });

        fixture.Manager.Kill(parent.Pid);

        Assert.False(fixture.Manager.TryGetActive(child.Pid, out _));
        Assert.Equal(ProcessExitReason.DependencyStop, fixture.Manager.GetHistory().Single(r => r.Pid == child.Pid).ExitReason);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public void Killing_a_parent_cascades_to_active_descendants_as_dependency_stop()
    {
        Fixture fixture = new();
        ProcessRecord parent = fixture.Manager.Start(fixture.RequestFor("com.hackeros.shell"));
        ProcessRecord child = fixture.Manager.Start(fixture.RequestFor("com.hackeros.child", parent.Pid));
        ProcessRecord grandchild = fixture.Manager.Start(fixture.RequestFor("com.hackeros.grandchild", child.Pid));

        fixture.Manager.Kill(parent.Pid);

        Assert.False(fixture.Manager.TryGetActive(child.Pid, out _));
        Assert.False(fixture.Manager.TryGetActive(grandchild.Pid, out _));
        ProcessRecord childHistory = fixture.Manager.GetHistory().Single(r => r.Pid == child.Pid);
        ProcessRecord grandchildHistory = fixture.Manager.GetHistory().Single(r => r.Pid == grandchild.Pid);
        Assert.Equal(ProcessExitReason.DependencyStop, childHistory.ExitReason);
        Assert.Equal(ProcessExitReason.DependencyStop, grandchildHistory.ExitReason);
    }

    [Fact]
    public async Task StopAsync_completes_immediately_when_the_process_reports_completion_in_time()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.MarkRunning(process.Pid);

        Task<ProcessRecord> stopTask = fixture.Manager.StopAsync(process.Pid, TimeSpan.FromSeconds(5));
        fixture.Manager.Complete(process.Pid, exitCode: 0);
        ProcessRecord stopped = await stopTask;

        Assert.Equal(ProcessState.Stopped, stopped.State);
        Assert.Equal(ProcessExitReason.Completed, stopped.ExitReason);
    }

    [Fact]
    public async Task StopAsync_force_stops_with_timeout_when_the_deadline_elapses()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.MarkRunning(process.Pid);

        Task<ProcessRecord> stopTask = fixture.Manager.StopAsync(process.Pid, TimeSpan.FromSeconds(1));
        fixture.Clock.Advance(2);
        ProcessRecord stopped = await stopTask;

        Assert.Equal(ProcessState.Stopped, stopped.State);
        Assert.Equal(ProcessExitReason.Timeout, stopped.ExitReason);
    }

    [Fact]
    public void TryGetSingleton_finds_the_active_process_for_an_app_id()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.settings"));

        Assert.True(fixture.Manager.TryGetSingleton("com.hackeros.settings", out ProcessRecord found));
        Assert.Equal(process.Pid, found.Pid);
        Assert.False(fixture.Manager.TryGetSingleton("com.hackeros.unknown", out _));
    }

    [Fact]
    public void History_retention_is_bounded_and_evicts_the_oldest_entry()
    {
        Fixture fixture = new(maxHistory: 2);
        ProcessRecord first = fixture.Manager.Start(fixture.RequestFor("com.hackeros.a"));
        fixture.Manager.Kill(first.Pid);
        ProcessRecord second = fixture.Manager.Start(fixture.RequestFor("com.hackeros.b"));
        fixture.Manager.Kill(second.Pid);
        ProcessRecord third = fixture.Manager.Start(fixture.RequestFor("com.hackeros.c"));
        fixture.Manager.Kill(third.Pid);

        IReadOnlyList<ProcessRecord> history = fixture.Manager.GetHistory();
        Assert.Equal(2, history.Count);
        Assert.DoesNotContain(history, r => r.Pid == first.Pid);
        Assert.Contains(history, r => r.Pid == second.Pid);
        Assert.Contains(history, r => r.Pid == third.Pid);
    }

    [Fact]
    public async Task Session_logout_cancels_every_active_process_token()
    {
        Fixture fixture = new();
        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        CancellationToken token = fixture.Manager.GetCancellationToken(process.Pid);

        await fixture.Session.LogoutAsync();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void State_transitions_publish_process_state_changed_events()
    {
        Fixture fixture = new();
        List<ProcessStateChangedEvent> events = [];
        using IDisposable subscription = fixture.EventBus.Subscribe<ProcessStateChangedEvent>(events.Add);

        ProcessRecord process = fixture.Manager.Start(fixture.RequestFor("com.hackeros.terminal"));
        fixture.Manager.MarkRunning(process.Pid);
        fixture.Manager.Complete(process.Pid, exitCode: 0);

        Assert.Equal(3, events.Count);
        Assert.Equal(ProcessState.Starting, events[0].NewState);
        Assert.Equal(ProcessState.Running, events[1].NewState);
        Assert.Equal(ProcessState.Stopped, events[2].NewState);
    }

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        private readonly LocalUserId _userId;
        private readonly SessionId _sessionId;

        internal Fixture(int maxHistory = 200)
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
            FileSystem = fileSystem;

            InMemoryLocalUserRepository users = new(() => _now);
            InMemoryLocalGroupRepository groups = new();
            EventBus = new InMemoryEventBus();
            BoundedAuditLog auditLog = new(maxEntries: 100);

            LocalGroup group = groups.CreateGroup(LocalLoginName.Parse("users"));
            LocalUser alice = users.CreateUser(
                LocalLoginName.Parse("alice"), "Alice", AppAuthority.User, group.Id,
                credential: LocalPasswordHasher.Create("hunter2", iterations: 100));

            Session = new LocalSessionService(
                users, seeder, EventBus, auditLog,
                InstallationId.FromGuid(Guid.NewGuid()), DeviceId.FromGuid(Guid.NewGuid()), () => _now);
            AuthenticatedPrincipal principal = Session.LoginAsync(alice.LoginName, "hunter2").GetAwaiter().GetResult();
            Principal = principal;
            _userId = principal.UserId;
            _sessionId = principal.SessionId;

            Clock = new ManualSimulationClock(_now, TimeSpan.FromSeconds(1));
            Manager = new InMemoryProcessManager(Clock, Session, EventBus, maxHistory);
        }

        internal InMemoryEventBus EventBus { get; }
        internal FileSystemService FileSystem { get; }
        internal LocalSessionService Session { get; }
        internal ManualSimulationClock Clock { get; }
        internal AuthenticatedPrincipal Principal { get; }
        internal InMemoryProcessManager Manager { get; }

        internal ProcessStartRequest RequestFor(string appId, ProcessId? parentPid = null) => new(
            parentPid, appId, AppInstanceId.FromGuid(Guid.NewGuid()), AppKind.Terminal,
            _userId, _sessionId, ResourceProfile.None);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
