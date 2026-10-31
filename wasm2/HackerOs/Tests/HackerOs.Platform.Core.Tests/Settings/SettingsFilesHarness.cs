using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Diagnostics;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Sessions;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Time;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Tests.Settings;

/// <summary>
/// Real in-memory filesystem, session service and <see cref="OsSettingsService"/> for settings
/// file tests (ADR 0044). Users: <c>alice</c> and <c>bob</c> (ordinary users, separate groups).
/// </summary>
internal sealed class SettingsFilesHarness
{
    internal const string Password = "hunter2";
    private int _entryId = 1;
    private int _transactionId = 100;
    private readonly DateTimeOffset _now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    internal SettingsFilesHarness(ILegacySettingsStore? legacyStore = null, bool withKeyring = false, bool withAccounts = true)
    {
        TimeProvider timeProvider = new FixedTimeProvider(_now);
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
        Clock = new ManualSimulationClock(_now, TimeSpan.FromSeconds(1));
        EventBus = new InMemoryEventBus();
        Migration = new SettingsStoreToFilesMigration(FileSystem, Clock, legacyStore ?? new EmptyLegacySettingsStore());

        Users = new InMemoryLocalUserRepository(() => _now);
        if (withAccounts)
        {
            LocalGroup aliceGroup = Groups.CreateGroup(LocalLoginName.Parse("alice"));
            LocalGroup bobGroup = Groups.CreateGroup(LocalLoginName.Parse("bob"));
            Alice = Users.CreateUser(
                LocalLoginName.Parse("alice"), "Alice", AppAuthority.User, aliceGroup.Id,
                credential: LocalPasswordHasher.Create(Password, iterations: 100));
            Bob = Users.CreateUser(
                LocalLoginName.Parse("bob"), "Bob", AppAuthority.User, bobGroup.Id,
                credential: LocalPasswordHasher.Create(Password, iterations: 100));
        }
        else
        {
            // A fresh installation (ADR 0187): no account at all.
            Alice = null!;
            Bob = null!;
        }

        // ADR 0100: a keyring with .NET's AES-GCM and a tiny work factor, unlocked by the session.
        Keyring = withKeyring
            ? new HackerOs.Platform.Core.Keyring.UserKeyringService(
                FileSystem, Clock, new HackerOs.Platform.Core.Keyring.ManagedKeyringCipher(), Audit, iterations: 10)
            : null;
        Seeder = new FileSystemSeeder(FileSystem, timeProvider);
        Session = new LocalSessionService(
            Users,
            Seeder,
            EventBus,
            Audit,
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            () => _now,
            profileInitializer: Migration,
            keyring: Keyring);
        Settings = new OsSettingsService(FileSystem, Session, Clock, TopicBus, EventBus);
    }

    internal FileSystemService FileSystem { get; }
    internal BoundedAuditLog Audit { get; } = new(maxEntries: 100);
    internal HackerOs.Platform.Core.Keyring.UserKeyringService? Keyring { get; }
    internal CapabilityGrantRepository Grants { get; }
    internal InMemoryTopicMessageBus TopicBus { get; }
    internal InMemoryEventBus EventBus { get; }
    internal ManualSimulationClock Clock { get; }
    internal InMemoryLocalUserRepository Users { get; }
    internal InMemoryLocalGroupRepository Groups { get; } = new();
    internal LocalUser Alice { get; }
    internal LocalUser Bob { get; }
    internal LocalSessionService Session { get; }
    internal FileSystemSeeder Seeder { get; }
    internal OsSettingsService Settings { get; }
    internal SettingsStoreToFilesMigration Migration { get; }

    /// <summary>Logs in a user, which seeds the home directory and runs the per-user migration.</summary>
    internal Task<AuthenticatedPrincipal> LoginAsync(LocalUser user) =>
        Session.LoginAsync(user.LoginName, Password);

    /// <summary>
    /// Installs the shipped icon packs now, as the login does in the background (idempotent, so it
    /// may run next to that background install).
    /// </summary>
    internal ValueTask InstallIconPacksAsync() => Seeder.InstallIconPacksAsync();

    /// <summary>Boot step: system migration and installation defaults.</summary>
    internal ValueTask BootAsync() => Migration.MigrateSystemAsync();

    /// <summary>Gateway of an app run by <paramref name="principal"/>, with the given capabilities.</summary>
    internal IAppFileSystemGateway CreateAppGateway(AuthenticatedPrincipal principal, string appId, params string[] capabilities) =>
        new AppFileSystemGateway(
            FileSystem,
            Clock,
            new AppOperationContext
            {
                AppId = appId,
                UserId = principal.UserId.ToString(),
                UserName = principal.LoginName.Value,
                UserAuthority = principal.Authority,
                GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal)
            },
            [.. principal.GroupIds.Select(group => group.ToString())]);

    /// <summary>Unrestricted system gateway, standing in for <c>nano</c> run as root.</summary>
    internal IAppFileSystemGateway CreateRootGateway() =>
        new AppFileSystemGateway(
            FileSystem,
            Clock,
            new AppOperationContext
            {
                AppId = OsSettingsService.ShellAppId,
                UserId = "system",
                UserAuthority = AppAuthority.System,
                GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                IsSystemOperation = true
            },
            ["system"]);

    /// <summary>Reads a text file as root, or <see langword="null"/> when absent.</summary>
    internal async Task<string?> ReadAsync(string path) =>
        (await SettingsTextFile.ReadAsync(CreateRootGateway(), VirtualPath.Parse(path))).Content;

    /// <summary>Writes a text file as root (creating parents), like an administrator using <c>nano</c>.</summary>
    internal async Task WriteAsRootAsync(string path, string content)
    {
        FileSystemError? error = await SettingsTextFile.EditAsync(
            CreateRootGateway(), VirtualPath.Parse(path), _ => content, createIfMissing: true);
        Assert.Null(error);
    }

    /// <summary>Writes a text file as the session user (like <c>nano</c> run by that user).</summary>
    internal async Task WriteAsUserAsync(string path, string content)
    {
        AuthenticatedPrincipal principal = Session.CurrentPrincipal ?? throw new InvalidOperationException("No session.");
        IAppFileSystemGateway nano = CreateAppGateway(
            principal, "org.hackeros.nano", AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite);
        FileSystemError? error = await SettingsTextFile.EditAsync(nano, VirtualPath.Parse(path), _ => content, createIfMissing: true);
        Assert.Null(error);
    }

    /// <summary>Gets a file's metadata, or <see langword="null"/> when absent.</summary>
    internal async Task<FileSystemEntryMetadata?> StatAsync(string path)
    {
        FileSystemResult<FileSystemEntrySnapshot> stat = await CreateRootGateway().StatAsync(new FileSystemStatRequest(VirtualPath.Parse(path)));
        return stat.Succeeded ? stat.Value!.Metadata : null;
    }

    /// <summary>UTF-8 helper for assertions.</summary>
    internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
