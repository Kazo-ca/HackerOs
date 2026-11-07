using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Sessions;

public sealed class LocalPasswordHasherTests
{
    [Fact]
    public void A_correct_password_verifies()
    {
        LocalPasswordCredential credential = LocalPasswordHasher.Create("hunter2", iterations: 100);
        Assert.True(LocalPasswordHasher.Verify("hunter2", credential));
    }

    [Fact]
    public void An_incorrect_password_fails_verification()
    {
        LocalPasswordCredential credential = LocalPasswordHasher.Create("hunter2", iterations: 100);
        Assert.False(LocalPasswordHasher.Verify("wrong-password", credential));
    }

    [Fact]
    public void An_unrecognized_kdf_identifier_fails_closed()
    {
        LocalPasswordCredential credential = new("future-kdf-v2", [1, 2, 3, 4], 100, [5, 6, 7, 8]);
        Assert.False(LocalPasswordHasher.Verify("hunter2", credential));
    }

    [Fact]
    public async Task CreateAsync_and_VerifyAsync_work_with_custom_delegate()
    {
        bool delegateCalled = false;
        ValueTask<byte[]> CustomHasher(string pwd, byte[] salt, int iter, int len, CancellationToken ct)
        {
            delegateCalled = true;
            return ValueTask.FromResult(new byte[len]);
        }

        LocalPasswordCredential credential = await LocalPasswordHasher.CreateAsync("hunter2", iterations: 100, asyncHasher: CustomHasher);
        Assert.True(delegateCalled);

        delegateCalled = false;
        bool verified = await LocalPasswordHasher.VerifyAsync("hunter2", credential, asyncHasher: CustomHasher);
        Assert.True(delegateCalled);
        Assert.True(verified);
    }

    [Fact]
    public async Task CreateAsync_and_VerifyAsync_fallback_gracefully_when_delegate_throws()
    {
        ValueTask<byte[]> FailingHasher(string pwd, byte[] salt, int iter, int len, CancellationToken ct)
        {
            throw new InvalidOperationException("JS Disconnected");
        }

        LocalPasswordCredential credential = await LocalPasswordHasher.CreateAsync("hunter2", iterations: 100, asyncHasher: FailingHasher);
        Assert.NotNull(credential);
        Assert.Equal(100, credential.Iterations);

        bool verified = await LocalPasswordHasher.VerifyAsync("hunter2", credential, asyncHasher: FailingHasher);
        Assert.True(verified);
    }
}

public sealed class InMemoryLocalUserRepositoryTests
{
    [Fact]
    public async Task Async_contract_honors_cancellation_before_mutation()
    {
        ILocalUserRepository repository = new InMemoryLocalUserRepository();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await repository.CreateUserAsync(
                LocalLoginName.Parse("alice"),
                "Alice",
                AppAuthority.User,
                LocalGroupId.FromGuid(Guid.NewGuid()),
                cancellationToken: cancellation.Token));

        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task Async_contract_returns_null_when_user_is_absent()
    {
        ILocalUserRepository repository = new InMemoryLocalUserRepository();

        LocalUser? user = await repository.FindByLoginNameAsync(LocalLoginName.Parse("missing"));

        Assert.Null(user);
    }

    [Fact]
    public void The_last_enabled_administrator_cannot_be_disabled()
    {
        InMemoryLocalUserRepository repository = new(() => DateTimeOffset.UnixEpoch);
        LocalUser admin = repository.CreateUser(
            LocalLoginName.Parse("admin"), "Admin", AppAuthority.Administrator, LocalGroupId.FromGuid(Guid.NewGuid()));

        Assert.Throws<InvalidOperationException>(() => repository.SetEnabled(admin.Id, enabled: false));
    }

    [Fact]
    public void The_last_enabled_administrator_cannot_be_demoted()
    {
        InMemoryLocalUserRepository repository = new(() => DateTimeOffset.UnixEpoch);
        LocalUser admin = repository.CreateUser(
            LocalLoginName.Parse("admin"), "Admin", AppAuthority.Administrator, LocalGroupId.FromGuid(Guid.NewGuid()));

        Assert.Throws<InvalidOperationException>(() => repository.SetAuthority(admin.Id, AppAuthority.User));
    }

    [Fact]
    public void A_second_administrator_may_be_disabled_when_another_remains()
    {
        InMemoryLocalUserRepository repository = new(() => DateTimeOffset.UnixEpoch);
        LocalGroupId groupId = LocalGroupId.FromGuid(Guid.NewGuid());
        LocalUser first = repository.CreateUser(LocalLoginName.Parse("admin1"), "Admin One", AppAuthority.Administrator, groupId);
        LocalUser second = repository.CreateUser(LocalLoginName.Parse("admin2"), "Admin Two", AppAuthority.Administrator, groupId);

        LocalUser updated = repository.SetEnabled(second.Id, enabled: false);

        Assert.False(updated.Enabled);
        Assert.Equal(first.Revision, repository.GetAll().Single(u => u.Id == first.Id).Revision);
    }

    [Fact]
    public void Creating_a_duplicate_login_name_throws()
    {
        InMemoryLocalUserRepository repository = new();
        LocalGroupId groupId = LocalGroupId.FromGuid(Guid.NewGuid());
        repository.CreateUser(LocalLoginName.Parse("alice"), "Alice", AppAuthority.User, groupId);

        Assert.Throws<InvalidOperationException>(
            () => repository.CreateUser(LocalLoginName.Parse("ALICE"), "Alice Two", AppAuthority.User, groupId));
    }
}

public sealed class LocalSessionServiceTests
{
    [Fact]
    public async Task Logging_in_with_the_correct_password_activates_the_session_and_seeds_the_home()
    {
        Fixture fixture = new();

        AuthenticatedPrincipal principal = await fixture.Service.LoginAsync(fixture.AliceLoginName, "hunter2");

        Assert.Equal(SessionState.Active, fixture.Service.State);
        Assert.Same(principal, fixture.Service.CurrentPrincipal);
        Assert.Equal("/home/alice", principal.HomePath);

        FileSystemResult<FileSystemEntrySnapshot> home = await fixture.FileSystem.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/alice")),
            fixture.SystemContext);
        Assert.True(home.Succeeded);
    }

    [Fact]
    public async Task Logging_in_with_the_wrong_password_throws_and_returns_to_logged_out()
    {
        Fixture fixture = new();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.LoginAsync(fixture.AliceLoginName, "wrong-password"));

        Assert.Equal(SessionState.LoggedOut, fixture.Service.State);
        Assert.Null(fixture.Service.CurrentPrincipal);
    }

    [Fact]
    public async Task Logging_in_as_a_disabled_user_throws()
    {
        Fixture fixture = new();
        fixture.Users.SetEnabled(fixture.Alice.Id, enabled: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.LoginAsync(fixture.AliceLoginName, "hunter2"));
    }

    [Fact]
    public async Task Logging_in_while_already_active_throws()
    {
        Fixture fixture = new();
        await fixture.Service.LoginAsync(fixture.AliceLoginName, "hunter2");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.LoginAsync(fixture.AliceLoginName, "hunter2"));
    }

    [Fact]
    public async Task Logout_cancels_linked_process_tokens_and_returns_to_logged_out()
    {
        Fixture fixture = new();
        await fixture.Service.LoginAsync(fixture.AliceLoginName, "hunter2");
        using CancellationTokenSource linked = fixture.Service.CreateLinkedCancellationSource();

        await fixture.Service.LogoutAsync();

        Assert.Equal(SessionState.LoggedOut, fixture.Service.State);
        Assert.Null(fixture.Service.CurrentPrincipal);
        Assert.True(linked.IsCancellationRequested);
    }

    [Fact]
    public async Task Shutdown_reaches_the_terminal_stopped_state_and_blocks_further_login()
    {
        Fixture fixture = new();
        await fixture.Service.LoginAsync(fixture.AliceLoginName, "hunter2");
        using CancellationTokenSource linked = fixture.Service.CreateLinkedCancellationSource();

        await fixture.Service.ShutdownAsync();

        Assert.Equal(SessionState.Stopped, fixture.Service.State);
        Assert.True(linked.IsCancellationRequested);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.LoginAsync(fixture.AliceLoginName, "hunter2"));
    }

    [Fact]
    public void Creating_a_linked_token_before_activation_throws()
    {
        Fixture fixture = new();
        Assert.Throws<InvalidOperationException>(() => fixture.Service.CreateLinkedCancellationSource());
    }

    [Fact]
    public async Task Login_logout_and_shutdown_publish_lifecycle_events()
    {
        Fixture fixture = new();
        List<object> events = [];
        using IDisposable s1 = fixture.EventBus.Subscribe<SessionActivatedEvent>(e => events.Add(e));
        using IDisposable s2 = fixture.EventBus.Subscribe<SessionLoggedOutEvent>(e => events.Add(e));
        using IDisposable s3 = fixture.EventBus.Subscribe<SessionShutDownEvent>(e => events.Add(e));

        await fixture.Service.LoginAsync(fixture.AliceLoginName, "hunter2");
        await fixture.Service.LogoutAsync();
        await fixture.Service.LoginAsync(fixture.AliceLoginName, "hunter2");
        await fixture.Service.ShutdownAsync();

        Assert.Equal(2, events.OfType<SessionActivatedEvent>().Count());
        Assert.Single(events.OfType<SessionLoggedOutEvent>());
        Assert.Single(events.OfType<SessionShutDownEvent>());
    }

    [Fact]
    public async Task Failed_and_successful_logins_are_audited()
    {
        Fixture fixture = new();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.LoginAsync(fixture.AliceLoginName, "wrong-password"));
        await fixture.Service.LoginAsync(fixture.AliceLoginName, "hunter2");

        Assert.Contains(fixture.AuditLog.Entries, e => e.Action == "session.login" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains(fixture.AuditLog.Entries, e => e.Action == "session.login" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Login_seeds_declared_app_capabilities_for_authenticated_user()
    {
        Fixture fixture = new();
        CapabilityGrantRepository grants = new();
        AppManifest manifest = new()
        {
            Id = "org.kazoos.text-editor",
            Name = "Text Editor",
            Version = "1.0.0",
            PublisherId = "org.kazoos",
            Description = "Test Editor",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Kazo.Os.Apps.TextEditor", "Kazo.Os.Apps.TextEditor.TextEditorWindow"),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
            Capabilities = [AppCapabilities.DialogFileSave, AppCapabilities.FileSystemUserHomeRead]
        };
        AppCatalogBuildResult catalogBuild = AppCatalog.Build([manifest]);
        Assert.NotNull(catalogBuild.Catalog);

        LocalSessionService service = new(
            fixture.Users,
            new FileSystemSeeder(fixture.FileSystem, new FixedTimeProvider(DateTimeOffset.UtcNow)),
            fixture.EventBus,
            fixture.AuditLog,
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            grantRepository: grants,
            catalog: catalogBuild.Catalog);

        AuthenticatedPrincipal principal = await service.LoginAsync(fixture.AliceLoginName, "hunter2");

        CapabilityPolicyEvaluation fileSave = grants.Evaluate(
            "org.kazoos.text-editor", principal.UserId.ToString(), AppCapabilities.DialogFileSave, AppAuthority.User, AppAuthority.User);
        CapabilityPolicyEvaluation homeRead = grants.Evaluate(
            "org.kazoos.text-editor", principal.UserId.ToString(), AppCapabilities.FileSystemUserHomeRead, AppAuthority.User, AppAuthority.User);

        Assert.True(fileSave.Granted);
        Assert.True(homeRead.Granted);
    }

    [Fact]
    public async Task An_app_installed_during_the_session_gets_its_declared_capabilities_at_once()
    {
        // Issue #386: an app added to the live catalog after login had no capability until the next login.
        Fixture fixture = new();
        CapabilityGrantRepository grants = new();
        AppManifest TestApp(string id, params string[] capabilities) => new()
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            PublisherId = "org.kazoos",
            Description = "Test app",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Tests", "Tests.Window"),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
            Capabilities = capabilities,
        };
        AppManifest existing = TestApp("org.kazoos.existing", AppCapabilities.DialogFileOpen);
        AppManifest installed = TestApp(
            "org.kazoos.installed", AppCapabilities.FileSystemUserHomeRead, AppCapabilities.ProcessManage);
        Assert.True(AppCapabilities.IsDangerous(AppCapabilities.ProcessManage));
        AppCatalog live = AppCatalog.Build([existing]).Catalog!.CreateLiveCopy();

        using LocalSessionService service = new(
            fixture.Users,
            new FileSystemSeeder(fixture.FileSystem, new FixedTimeProvider(DateTimeOffset.UtcNow)),
            fixture.EventBus,
            fixture.AuditLog,
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            grantRepository: grants,
            catalog: live);
        AuthenticatedPrincipal principal = await service.LoginAsync(fixture.AliceLoginName, "hunter2");
        string userId = principal.UserId.ToString();
        Assert.False(grants.Evaluate(installed.Id, userId, AppCapabilities.FileSystemUserHomeRead, AppAuthority.User, AppAuthority.User).Granted);

        live.ReplaceContents(AppCatalog.Build([existing, installed]).Catalog!);

        Assert.True(grants.Evaluate(installed.Id, userId, AppCapabilities.FileSystemUserHomeRead, AppAuthority.User, AppAuthority.User).Granted);
        // A dangerous capability of a non-system app still waits for an administrator (ADR 0049).
        Assert.False(grants.Evaluate(installed.Id, userId, AppCapabilities.ProcessManage, AppAuthority.User, AppAuthority.User).Granted);
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
            FileSystem = new FileSystemService(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                new InMemoryTopicMessageBus(new CapabilityGrantRepository(() => _now)),
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            FileSystemSeeder seeder = new(FileSystem, timeProvider);
            SystemContext = CreateSystemContext(_now);

            Users = new InMemoryLocalUserRepository(() => _now);
            Groups = new InMemoryLocalGroupRepository();
            EventBus = new InMemoryEventBus();
            AuditLog = new BoundedAuditLog(maxEntries: 100);

            LocalGroup group = Groups.CreateGroup(LocalLoginName.Parse("users"));
            AliceLoginName = LocalLoginName.Parse("alice");
            Alice = Users.CreateUser(
                AliceLoginName, "Alice", AppAuthority.User, group.Id,
                credential: LocalPasswordHasher.Create("hunter2", iterations: 100));

            Service = new LocalSessionService(
                Users,
                seeder,
                EventBus,
                AuditLog,
                InstallationId.FromGuid(Guid.NewGuid()),
                DeviceId.FromGuid(Guid.NewGuid()),
                () => _now);
        }

        internal FileSystemService FileSystem { get; }
        internal InMemoryLocalUserRepository Users { get; }
        internal InMemoryLocalGroupRepository Groups { get; }
        internal InMemoryEventBus EventBus { get; }
        internal BoundedAuditLog AuditLog { get; }
        internal LocalSessionService Service { get; }
        internal LocalUser Alice { get; }
        internal LocalLoginName AliceLoginName { get; }
        internal FileSystemAuthorizationContext SystemContext { get; }

        private static FileSystemAuthorizationContext CreateSystemContext(DateTimeOffset now)
        {
            AppOperationContext operation = new()
            {
                AppId = "org.kazoos.kernel",
                UserId = "system",
                UserAuthority = AppAuthority.System,
                GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                IsSystemOperation = true
            };
            return new FileSystemAuthorizationContext(operation, ["system"], now);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
