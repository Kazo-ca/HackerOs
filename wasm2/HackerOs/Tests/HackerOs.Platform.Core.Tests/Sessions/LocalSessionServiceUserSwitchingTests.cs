using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Platform.Core.Diagnostics;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Sessions;

/// <summary>"Switch user" (ADR 0201): several sessions open, one in the foreground.</summary>
public sealed class LocalSessionServiceUserSwitchingTests
{
    [Fact]
    public async Task Switching_away_keeps_the_session_open_in_the_background_with_its_token_live()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal alice = await fixture.Service.LoginAsync(fixture.Alice, "hunter2");
        using CancellationTokenSource process = fixture.Service.CreateLinkedCancellationSource();
        List<SessionSwitchedOutEvent> switched = [];
        using IDisposable subscription = fixture.EventBus.Subscribe<SessionSwitchedOutEvent>(switched.Add);

        await fixture.Service.SwitchAwayAsync();

        Assert.Equal(SessionState.LoggedOut, fixture.Service.State);
        Assert.Null(fixture.Service.CurrentPrincipal);
        Assert.False(process.IsCancellationRequested);
        Assert.Equal([alice], fixture.Service.BackgroundSessions);
        Assert.Equal([alice], fixture.Service.OpenSessions);
        Assert.Equal(alice.SessionId, Assert.Single(switched).SessionId);
    }

    [Fact]
    public async Task Another_account_opens_a_second_session_while_the_first_keeps_running()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal alice = await fixture.Service.LoginAsync(fixture.Alice, "hunter2");
        using CancellationTokenSource aliceProcess = fixture.Service.CreateLinkedCancellationSource();
        await fixture.Service.SwitchAwayAsync();

        AuthenticatedPrincipal bob = await fixture.Service.LoginAsync(fixture.Bob, "swordfish");

        Assert.Equal(SessionState.Active, fixture.Service.State);
        Assert.Same(bob, fixture.Service.CurrentPrincipal);
        Assert.NotEqual(alice.SessionId, bob.SessionId);
        Assert.Equal([alice, bob], fixture.Service.OpenSessions);
        Assert.Equal([alice], fixture.Service.BackgroundSessions);
        Assert.False(aliceProcess.IsCancellationRequested);
    }

    [Fact]
    public async Task Signing_in_again_resumes_the_same_session_without_a_new_activation()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal alice = await fixture.Service.LoginAsync(fixture.Alice, "hunter2");
        await fixture.Service.SwitchAwayAsync();
        int activations = 0;
        List<SessionResumedEvent> resumed = [];
        using IDisposable activated = fixture.EventBus.Subscribe<SessionActivatedEvent>(_ => activations++);
        using IDisposable resumedSubscription = fixture.EventBus.Subscribe<SessionResumedEvent>(resumed.Add);

        AuthenticatedPrincipal again = await fixture.Service.LoginAsync(fixture.Alice, "hunter2");

        Assert.Same(alice, again);
        Assert.Same(alice, fixture.Service.CurrentPrincipal);
        Assert.Equal(0, activations);
        Assert.Equal(alice.SessionId, Assert.Single(resumed).SessionId);
        Assert.Empty(fixture.Service.BackgroundSessions);
        Assert.Contains(fixture.AuditLog.Entries, entry => entry.Action == "session.resume");
    }

    [Fact]
    public async Task Resuming_with_the_wrong_password_is_refused_and_the_session_stays_in_the_background()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal alice = await fixture.Service.LoginAsync(fixture.Alice, "hunter2");
        await fixture.Service.SwitchAwayAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.LoginAsync(fixture.Alice, "wrong"));

        Assert.Equal(SessionState.LoggedOut, fixture.Service.State);
        Assert.Equal([alice], fixture.Service.BackgroundSessions);
    }

    [Fact]
    public async Task The_new_tokens_link_to_the_foreground_session_only()
    {
        Fixture fixture = new();
        await fixture.Service.LoginAsync(fixture.Alice, "hunter2");
        await fixture.Service.SwitchAwayAsync();
        await fixture.Service.LoginAsync(fixture.Bob, "swordfish");
        using CancellationTokenSource bobProcess = fixture.Service.CreateLinkedCancellationSource();

        await fixture.Service.LogoutAsync();

        Assert.True(bobProcess.IsCancellationRequested);
        Assert.Single(fixture.Service.BackgroundSessions);
    }

    [Fact]
    public async Task Ending_a_background_session_cancels_its_token_and_publishes_its_logout()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal alice = await fixture.Service.LoginAsync(fixture.Alice, "hunter2");
        using CancellationTokenSource aliceProcess = fixture.Service.CreateLinkedCancellationSource();
        await fixture.Service.SwitchAwayAsync();
        AuthenticatedPrincipal bob = await fixture.Service.LoginAsync(fixture.Bob, "swordfish");
        List<SessionLoggedOutEvent> loggedOut = [];
        using IDisposable subscription = fixture.EventBus.Subscribe<SessionLoggedOutEvent>(loggedOut.Add);

        await fixture.Service.EndBackgroundSessionAsync(alice.SessionId);

        Assert.True(aliceProcess.IsCancellationRequested);
        Assert.Same(bob, fixture.Service.CurrentPrincipal);
        Assert.Equal([bob], fixture.Service.OpenSessions);
        Assert.Equal(alice.SessionId, Assert.Single(loggedOut).SessionId);
        Assert.False(fixture.Service.HasOpenSession(alice.UserId));
    }

    [Fact]
    public async Task The_foreground_session_cannot_be_ended_as_a_background_one()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal alice = await fixture.Service.LoginAsync(fixture.Alice, "hunter2");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EndBackgroundSessionAsync(alice.SessionId));
    }

    [Fact]
    public async Task Switching_away_with_no_foreground_session_throws()
    {
        Fixture fixture = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAwayAsync());
    }

    [Fact]
    public async Task A_session_past_the_limit_is_refused_but_an_open_one_still_resumes()
    {
        Fixture fixture = new(maxOpenSessions: 1);
        AuthenticatedPrincipal alice = await fixture.Service.LoginAsync(fixture.Alice, "hunter2");
        await fixture.Service.SwitchAwayAsync();

        SessionLimitReachedException refused = await Assert.ThrowsAsync<SessionLimitReachedException>(
            () => fixture.Service.LoginAsync(fixture.Bob, "swordfish"));
        Assert.Equal(1, refused.MaxOpenSessions);
        Assert.Equal(SessionState.LoggedOut, fixture.Service.State);

        Assert.Same(alice, await fixture.Service.LoginAsync(fixture.Alice, "hunter2"));
    }

    [Fact]
    public void The_default_limit_is_three_sessions()
    {
        Fixture fixture = new();

        Assert.Equal(3, fixture.Service.MaxOpenSessions);
        Assert.Equal(LocalSessionService.DefaultMaxOpenSessions, fixture.Service.MaxOpenSessions);
    }

    [Fact]
    public async Task Shutting_down_ends_every_open_session()
    {
        Fixture fixture = new();
        await fixture.Service.LoginAsync(fixture.Alice, "hunter2");
        using CancellationTokenSource aliceProcess = fixture.Service.CreateLinkedCancellationSource();
        await fixture.Service.SwitchAwayAsync();
        await fixture.Service.LoginAsync(fixture.Bob, "swordfish");
        using CancellationTokenSource bobProcess = fixture.Service.CreateLinkedCancellationSource();

        await fixture.Service.ShutdownAsync();

        Assert.Equal(SessionState.Stopped, fixture.Service.State);
        Assert.True(aliceProcess.IsCancellationRequested);
        Assert.True(bobProcess.IsCancellationRequested);
        Assert.Empty(fixture.Service.OpenSessions);
    }

    [Fact]
    public async Task An_account_with_a_background_session_counts_as_signed_in()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal alice = await fixture.Service.LoginAsync(fixture.Alice, "hunter2");
        await fixture.Service.SwitchAwayAsync();

        Assert.True(fixture.Service.HasOpenSession(alice.UserId));
    }

    [Fact]
    public async Task Userdel_refuses_an_account_with_a_session_in_the_background()
    {
        Fixture fixture = new();
        AuthenticatedPrincipal bob = await fixture.Service.LoginAsync(fixture.Bob, "swordfish");
        await fixture.Service.SwitchAwayAsync();
        AuthenticatedPrincipal alice = await fixture.Service.LoginAsync(fixture.Alice, "hunter2");
        LocalAccountService accounts = new(fixture.Users, fixture.Groups, fixture.AuditLog, iterations: 100)
        {
            HasOpenSession = fixture.Service.HasOpenSession,
        };

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await accounts.DeleteAsync(alice, "alice", bob.LoginName.Value, removeHome: false));

        Assert.Contains("currently signed in", refused.Message, StringComparison.Ordinal);
    }

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        internal Fixture(int maxOpenSessions = LocalSessionService.DefaultMaxOpenSessions)
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

            Users = new InMemoryLocalUserRepository(() => _now);
            Groups = new InMemoryLocalGroupRepository();
            EventBus = new InMemoryEventBus();
            AuditLog = new BoundedAuditLog(maxEntries: 100);

            LocalGroup group = Groups.CreateGroup(LocalLoginName.Parse("users"));
            Alice = LocalLoginName.Parse("alice");
            Bob = LocalLoginName.Parse("bob");
            Users.CreateUser(
                Alice, "Alice", AppAuthority.Administrator, group.Id,
                credential: LocalPasswordHasher.Create("hunter2", iterations: 100));
            Users.CreateUser(
                Bob, "Bob", AppAuthority.User, group.Id,
                credential: LocalPasswordHasher.Create("swordfish", iterations: 100));

            Service = new LocalSessionService(
                Users,
                seeder,
                EventBus,
                AuditLog,
                InstallationId.FromGuid(Guid.NewGuid()),
                DeviceId.FromGuid(Guid.NewGuid()),
                () => _now)
            {
                MaxOpenSessions = maxOpenSessions,
            };
        }

        internal InMemoryLocalUserRepository Users { get; }
        internal InMemoryLocalGroupRepository Groups { get; }
        internal InMemoryEventBus EventBus { get; }
        internal BoundedAuditLog AuditLog { get; }
        internal LocalSessionService Service { get; }
        internal LocalLoginName Alice { get; }
        internal LocalLoginName Bob { get; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
