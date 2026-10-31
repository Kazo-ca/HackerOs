using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Policy;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.Tests.FileSystem;

public sealed class FileSystemSeederTests
{
    [Fact]
    public async Task Clean_profile_contains_required_system_and_user_directories()
    {
        Fixture fixture = new();

        await fixture.Seeder.SeedAsync("alice", "users");

        string[] paths =
        [
            "/", "/bin", "/etc", "/home", "/mnt", "/tmp", "/var", "/var/log", "/var/log/apps",
            "/home/alice", "/home/alice/Desktop", "/home/alice/Documents",
            "/home/alice/Downloads", "/home/alice/.config",
            "/home/alice/.config/hackeros", "/etc/hackeros", "/var/lib/hackeros",
            "/home/alice/.local/state/hackeros"
        ];
        foreach (string path in paths)
        {
            FileSystemResult<FileSystemEntrySnapshot> result = await fixture.Repository.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse(path)),
                fixture.SystemContext);
            Assert.Equal(FileSystemEntryKind.Directory, result.Value?.Metadata.Kind);
        }

        FileSystemEntrySnapshot home = (await fixture.Repository.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/alice")),
            fixture.SystemContext)).Value!;
        Assert.Equal("alice", home.Metadata.OwnerId);
        Assert.Equal("users", home.Metadata.GroupId);
    }

    [Fact]
    public async Task Login_seed_leaves_the_shared_data_to_the_background_installs()
    {
        Fixture fixture = new();

        await fixture.Seeder.SeedAsync("alice", "users");

        Assert.Equal(FileSystemEntryKind.Directory, (await StatAsync(fixture, "/usr/share")).Value?.Metadata.Kind);
        Assert.Equal(FileSystemErrorCode.NotFound, (await StatAsync(fixture, "/usr/share/icons")).Error?.Code);
        Assert.Equal(FileSystemErrorCode.NotFound, (await StatAsync(fixture, "/usr/share/sounds")).Error?.Code);
    }

    [Fact]
    public async Task Icon_pack_install_writes_the_shipped_packs_once()
    {
        Fixture fixture = new();
        await fixture.Seeder.SeedAsync("alice", "users");

        await fixture.Seeder.InstallIconPacksAsync();
        FileSystemEntrySnapshot pack = (await StatAsync(fixture, "/usr/share/icons/neon/icon-pack.json")).Value!;
        await fixture.Seeder.InstallIconPacksAsync();

        Assert.Equal("system", pack.Metadata.OwnerId);
        Assert.Equal(pack.Metadata.Revision, (await StatAsync(fixture, "/usr/share/icons/neon/icon-pack.json")).Value!.Metadata.Revision);
    }

    [Fact]
    public async Task Recorded_install_is_skipped_at_the_next_boot()
    {
        Fixture fixture = new();
        await fixture.Seeder.SeedAsync("alice", "users");
        await fixture.Seeder.InstallIconPacksAsync();
        await fixture.Seeder.InstallSoundThemesAsync();

        FileSystemEntrySnapshot list = (await StatAsync(fixture, "/var/lib/hackeros/installed/icon-packs.list")).Value!;
        Assert.True(((FileMetadata)list.Metadata).Length > 0);
        Assert.True((await StatAsync(fixture, "/var/lib/hackeros/installed/sound-themes.list")).Succeeded);

        // Like dpkg, a recorded package is not checked file by file again: a deleted file stays deleted.
        await DeleteAsync(fixture, "/usr/share/icons/neon/icon-pack.json");
        await fixture.Seeder.InstallIconPacksAsync();

        Assert.Equal(FileSystemErrorCode.NotFound, (await StatAsync(fixture, "/usr/share/icons/neon/icon-pack.json")).Error?.Code);
    }

    [Fact]
    public async Task Install_interrupted_by_a_reload_completes_its_empty_files()
    {
        Fixture fixture = new();
        await fixture.Seeder.SeedAsync("alice", "users");
        await fixture.Seeder.InstallIconPacksAsync();
        // The reload came between creating the file and writing it, before the list was written.
        await DeleteAsync(fixture, "/var/lib/hackeros/installed/icon-packs.list");
        await DeleteAsync(fixture, "/usr/share/icons/neon/icon-pack.json");
        await fixture.Repository.CreateAsync(
            new FileSystemCreateRequest(
                VirtualPath.Parse("/usr/share/icons/neon/icon-pack.json"), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x01A4)),
            fixture.SystemContext);

        await fixture.Seeder.InstallIconPacksAsync();

        FileSystemEntrySnapshot pack = (await StatAsync(fixture, "/usr/share/icons/neon/icon-pack.json")).Value!;
        Assert.True(((FileMetadata)pack.Metadata).Length > 0);
        Assert.True((await StatAsync(fixture, "/var/lib/hackeros/installed/icon-packs.list")).Succeeded);
    }

    private static async Task DeleteAsync(Fixture fixture, string path)
    {
        VirtualPath target = VirtualPath.Parse(path);
        long revision = (await StatAsync(fixture, path)).Value!.Metadata.Revision;
        long parent = (await StatAsync(fixture, path[..path.LastIndexOf('/')])).Value!.Metadata.Revision;
        FileSystemMutationResult deleted = await fixture.Repository.DeleteAsync(
            new FileSystemDeleteRequest(target, revision, parent), fixture.SystemContext);
        Assert.True(deleted.Succeeded, deleted.Transaction.Error?.Code.ToString());
    }

    private static ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(Fixture fixture, string path) =>
        fixture.Repository.StatAsync(new FileSystemStatRequest(VirtualPath.Parse(path)), fixture.SystemContext);

    [Fact]
    public async Task Repeated_seed_preserves_entry_ids_and_revisions()
    {
        Fixture fixture = new();
        await fixture.Seeder.SeedAsync("alice", "users");
        FileSystemEntrySnapshot before = (await fixture.Repository.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/alice/Documents")),
            fixture.SystemContext)).Value!;

        await fixture.Seeder.SeedAsync("alice", "users");
        FileSystemEntrySnapshot after = (await fixture.Repository.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/alice/Documents")),
            fixture.SystemContext)).Value!;

        Assert.Equal(before.Metadata.Id, after.Metadata.Id);
        Assert.Equal(before.Metadata.Revision, after.Metadata.Revision);
    }

    [Fact]
    public async Task Adding_second_user_preserves_first_user_home()
    {
        Fixture fixture = new();
        await fixture.Seeder.SeedAsync("alice", "users");
        FileSystemEntrySnapshot aliceBefore = (await fixture.Repository.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/alice")),
            fixture.SystemContext)).Value!;

        await fixture.Seeder.SeedAsync("bob", "users");
        FileSystemEntrySnapshot aliceAfter = (await fixture.Repository.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/alice")),
            fixture.SystemContext)).Value!;
        FileSystemEntrySnapshot bob = (await fixture.Repository.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/bob")),
            fixture.SystemContext)).Value!;

        Assert.Equal(aliceBefore.Metadata.Id, aliceAfter.Metadata.Id);
        Assert.Equal("bob", bob.Metadata.OwnerId);
    }

    [Fact]
    public async Task Seed_survives_a_boot_writer_creating_var_log_apps_concurrently()
    {
        // Boot log writers create files in /var/log while the seeder runs: its create of
        // /var/log/apps then sees /var/log at a newer revision (RevisionConflict) and must re-read
        // the parent and retry, not fail the boot.
        Fixture fixture = new(inner => new RacingCreateService(
            inner, VirtualPath.Parse("/var/log/apps"), VirtualPath.Parse("/var/log/syslog")));

        await fixture.Seeder.SeedAsync("alice", "users");

        FileSystemResult<FileSystemEntrySnapshot> apps = await fixture.Repository.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/var/log/apps")),
            fixture.SystemContext);
        Assert.Equal(FileSystemEntryKind.Directory, apps.Value?.Metadata.Kind);
    }

    /// <summary>Creates <c>siblingPath</c> just before the seeder's first create of <c>racedPath</c>.</summary>
    private sealed class RacingCreateService(IFileSystemService inner, VirtualPath racedPath, VirtualPath siblingPath) : IFileSystemService
    {
        private bool _raced;

        public string ProviderId => inner.ProviderId;

        public async ValueTask<FileSystemMutationResult> CreateAsync(
            FileSystemCreateRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default)
        {
            if (!_raced && request.Path == racedPath)
            {
                _raced = true;
                await inner.CreateAsync(
                    new FileSystemCreateRequest(siblingPath, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x01A4)),
                    context,
                    cancellationToken);
            }

            return await inner.CreateAsync(request, context, cancellationToken);
        }

        public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(FileSystemReadRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => inner.ReadAsync(request, context, cancellationToken);
        public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(FileSystemEnumerateRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => inner.EnumerateAsync(request, context, cancellationToken);
        public ValueTask<FileSystemMutationResult> WriteAsync(FileSystemWriteRequest request, IFileSystemContentSource content, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => inner.WriteAsync(request, content, context, cancellationToken);
        public ValueTask<FileSystemMutationResult> MoveAsync(FileSystemMoveRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => inner.MoveAsync(request, context, cancellationToken);
        public ValueTask<FileSystemMutationResult> CopyAsync(FileSystemCopyRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => inner.CopyAsync(request, context, cancellationToken);
        public ValueTask<FileSystemMutationResult> DeleteAsync(FileSystemDeleteRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => inner.DeleteAsync(request, context, cancellationToken);
        public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(FileSystemStatRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => inner.StatAsync(request, context, cancellationToken);
        public ValueTask<FileSystemMutationResult> SetPermissionsAsync(FileSystemSetPermissionsRequest request, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default) => inner.SetPermissionsAsync(request, context, cancellationToken);
    }

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 100;
        private readonly DateTimeOffset _now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        internal Fixture(Func<IFileSystemService, IFileSystemService>? wrap = null)
        {
            FixedTimeProvider timeProvider = new(_now);
            Repository = new InMemoryFileSystemRepository(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]),
                timeProvider);
            FileSystemMountRouter router = new(Repository);
            FileSystemService service = new(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                new InMemoryTopicMessageBus(new CapabilityGrantRepository(() => _now)),
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            Service = service;
            Seeder = new FileSystemSeeder(wrap is null ? service : wrap(service), timeProvider);
            SystemContext = CreateSystemContext(_now);
        }

        internal InMemoryFileSystemRepository Repository { get; }
        internal FileSystemService Service { get; }
        internal FileSystemSeeder Seeder { get; }
        internal FileSystemAuthorizationContext SystemContext { get; }

        private static FileSystemAuthorizationContext CreateSystemContext(DateTimeOffset now)
        {
            AppOperationContext operation = new()
            {
                AppId = "org.hackeros.kernel",
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