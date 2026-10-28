using System.Text;
using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.FileSystem.HostFolders;
using HackerOs.Platform.Core.Tests.FileSystem.Virtual;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Sessions;
using static HackerOs.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture;

namespace HackerOs.Platform.Core.Tests.FileSystem.HostFolders;

/// <summary>Network drives (ADR 0181): a host folder mounted under <c>/mnt</c> through the real file system service.</summary>
public sealed class NetworkDriveServiceTests
{
    private const string Read = AppCapabilities.FileSystemSystemRead;
    private const string Write = AppCapabilities.FileSystemSystemWrite;

    [Fact]
    public async Task Mounted_folder_is_read_and_written_through_the_file_system()
    {
        Harness harness = await Harness.CreateAsync();
        FakeHostFolder folder = new("Projects");
        folder.Put("hello from the host", "notes.txt");
        harness.Access.NextPick = folder;

        NetworkDrive? drive = await harness.Drives.MountAsync(null);

        Assert.NotNull(drive);
        Assert.Equal("/mnt/Projects", drive.MountPoint.Value);
        Assert.Equal("alice", drive.OwnerId);
        Assert.True(drive.IsConnected);
        FileSystemAuthorizationContext context = harness.Fixture.AppContext(null, Read, Write);
        Assert.Equal("hello from the host", await harness.Fixture.ReadTextAsync("/mnt/Projects/notes.txt", context));

        FileSystemMutationResult created = await harness.Fixture.Service.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse("/mnt/Projects/todo.txt"), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x1A4)),
            context);
        Assert.True(created.Succeeded, $"create: {created.Transaction.Error?.Code}");
        FileSystemMutationResult written = await harness.Fixture.Service.WriteAsync(
            new FileSystemWriteRequest(VirtualPath.Parse("/mnt/Projects/todo.txt")),
            new BytesSource(Encoding.UTF8.GetBytes("buy milk")),
            context);
        Assert.True(written.Succeeded, $"write: {written.Transaction.Error?.Code}");
        Assert.Equal("buy milk", folder.Text("todo.txt"));

        FileSystemResult<FileSystemDirectorySnapshot> listing = await harness.Fixture.Service.EnumerateAsync(
            new FileSystemEnumerateRequest(VirtualPath.Parse("/mnt/Projects")), context);
        Assert.Equal(["notes.txt", "todo.txt"], listing.Value!.Entries.Select(static entry => entry.Name.Value));
        Assert.All(listing.Value.Entries, static entry => Assert.Equal("alice", entry.Metadata.OwnerId));
    }

    [Fact]
    public async Task Other_users_cannot_read_a_drive_they_do_not_own()
    {
        Harness harness = await Harness.CreateAsync();
        FakeHostFolder folder = new("Private");
        folder.Put("secret", "a.txt");
        harness.Access.NextPick = folder;
        await harness.Drives.MountAsync(null);

        FileSystemAuthorizationContext bob = new(
            new AppOperationContext
            {
                AppId = "org.tests.virtual-fs",
                UserId = "bob",
                UserName = "bob",
                UserAuthority = AppAuthority.User,
                GrantedCapabilities = new HashSet<string>([Read, Write], StringComparer.Ordinal)
            },
            ["users"],
            BootUtc);
        FileSystemResult<FileSystemContentReadHandle> read = await harness.Fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/mnt/Private/a.txt")), bob);

        Assert.False(read.Succeeded);
        Assert.Equal(FileSystemErrorCode.PermissionDenied, read.Error!.Code);
    }

    [Fact]
    public async Task Taken_names_get_a_number_and_the_list_is_saved_in_the_home()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Access.NextPick = new FakeHostFolder("Photos");
        await harness.Drives.MountAsync(null);
        harness.Access.NextPick = new FakeHostFolder("Photos");

        NetworkDrive? second = await harness.Drives.MountAsync(null);

        Assert.Equal("/mnt/Photos-2", second!.MountPoint.Value);
        string table = await harness.Fixture.ReadTextAsync(
            "/home/alice/" + NetworkDriveService.TableRelativePath, harness.Fixture.SystemContext());
        using JsonDocument document = JsonDocument.Parse(table);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            ["/mnt/Photos", "/mnt/Photos-2"],
            document.RootElement.GetProperty("drives").EnumerateArray().Select(static drive => drive.GetProperty("mountPoint").GetString()));
    }

    [Fact]
    public async Task Restore_after_reload_mounts_connected_or_disconnected_and_drops_lost_handles()
    {
        Harness first = await Harness.CreateAsync();
        FakeHostFolder kept = new("Kept");
        FakeHostFolder asks = new("Asks");
        FakeHostFolder lost = new("Lost");
        foreach (FakeHostFolder folder in new[] { kept, asks, lost })
        {
            first.Access.NextPick = folder;
            await first.Drives.MountAsync(null);
        }

        await first.Drives.UnmountAllAsync();
        Assert.Empty(first.Drives.GetDrives());
        string lostKey = first.Access.StoredKeys.Single(key => first.Access.Stored(key) == lost);
        await first.Access.ForgetAsync(lostKey);
        asks.Permission = HostFolderPermission.Prompt;

        await first.Drives.RestoreAsync();

        IReadOnlyList<NetworkDrive> drives = first.Drives.GetDrives();
        Assert.Equal(["/mnt/Asks", "/mnt/Kept"], drives.Select(static drive => drive.MountPoint.Value));
        Assert.False(drives[0].IsConnected);
        Assert.True(drives[1].IsConnected);
        string table = await first.Fixture.ReadTextAsync(
            "/home/alice/" + NetworkDriveService.TableRelativePath, first.Fixture.SystemContext());
        Assert.DoesNotContain("/mnt/Lost", table, StringComparison.Ordinal);

        FileSystemResult<FileSystemDirectorySnapshot> refused = await first.Fixture.Service.EnumerateAsync(
            new FileSystemEnumerateRequest(VirtualPath.Parse("/mnt/Asks")), first.Fixture.AppContext(null, Read));
        Assert.Equal(FileSystemErrorCode.ProviderUnavailable, refused.Error!.Code);

        Assert.True(await first.Drives.ReconnectAsync("/mnt/Asks"));
        Assert.True(first.Drives.Find(VirtualPath.Parse("/mnt/Asks"))!.IsConnected);
    }

    [Fact]
    public async Task Unmount_removes_the_mount_point_and_forgets_the_handle()
    {
        Harness harness = await Harness.CreateAsync();
        FakeHostFolder folder = new("Work");
        folder.Put("stays on the computer", "keep.txt");
        harness.Access.NextPick = folder;
        await harness.Drives.MountAsync(null);

        await harness.Drives.UnmountAsync("/mnt/Work");

        Assert.Empty(harness.Drives.GetDrives());
        Assert.Empty(harness.Access.StoredKeys);
        Assert.True(folder.Disposed);
        Assert.Equal("stays on the computer", folder.Text("keep.txt"));
        FileSystemResult<FileSystemEntrySnapshot> stat = await harness.Fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/mnt/Work")), harness.Fixture.SystemContext());
        Assert.Equal(FileSystemErrorCode.NotFound, stat.Error!.Code);
    }

    [Fact]
    public async Task Only_the_owner_or_an_administrator_unmounts()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Access.NextPick = new FakeHostFolder("Shared");
        await harness.Drives.MountAsync(null);

        harness.Principal = Principal("bob", AppAuthority.User);
        NetworkDriveException refused = await Assert.ThrowsAsync<NetworkDriveException>(
            () => harness.Drives.UnmountAsync("/mnt/Shared").AsTask());
        Assert.Equal(NetworkDriveError.NotOwner, refused.Error);

        harness.Principal = Principal("root", AppAuthority.Administrator);
        await harness.Drives.UnmountAsync("/mnt/Shared");
        Assert.Empty(harness.Drives.GetDrives());
    }

    [Theory]
    [InlineData("/home/alice/drive")]
    [InlineData("/mnt")]
    [InlineData("/mnt/a/b")]
    [InlineData("relative")]
    public async Task Mount_points_outside_mnt_are_refused(string mountPoint)
    {
        Harness harness = await Harness.CreateAsync();
        harness.Access.NextPick = new FakeHostFolder("X");

        NetworkDriveException refused = await Assert.ThrowsAsync<NetworkDriveException>(
            () => harness.Drives.MountAsync(mountPoint).AsTask());

        Assert.Equal(NetworkDriveError.InvalidMountPoint, refused.Error);
        Assert.Empty(harness.Access.StoredKeys);
    }

    [Fact]
    public async Task A_chosen_mount_point_that_has_files_is_refused_and_the_handle_forgotten()
    {
        Harness harness = await Harness.CreateAsync();
        FileSystemAuthorizationContext system = harness.Fixture.SystemContext();
        await harness.Fixture.Service.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse("/mnt/busy"), FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0x1ED)), system);
        await harness.Fixture.Service.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse("/mnt/busy/file"), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x1A4)), system);
        harness.Access.NextPick = new FakeHostFolder("X");

        NetworkDriveException refused = await Assert.ThrowsAsync<NetworkDriveException>(
            () => harness.Drives.MountAsync("/mnt/busy").AsTask());

        Assert.Equal(NetworkDriveError.InvalidMountPoint, refused.Error);
        Assert.Empty(harness.Access.StoredKeys);
    }

    [Fact]
    public async Task Closing_the_dialog_mounts_nothing_and_missing_support_or_gesture_is_reported()
    {
        Harness harness = await Harness.CreateAsync();

        Assert.Null(await harness.Drives.MountAsync(null));

        harness.Access.RequireGesture = true;
        harness.Access.NextPick = new FakeHostFolder("X");
        Assert.Equal(NetworkDriveError.UserActivationRequired,
            (await Assert.ThrowsAsync<NetworkDriveException>(() => harness.Drives.MountAsync(null).AsTask())).Error);

        harness.Access.Supported = false;
        Assert.Equal(NetworkDriveError.NotSupported,
            (await Assert.ThrowsAsync<NetworkDriveException>(() => harness.Drives.MountAsync(null).AsTask())).Error);

        harness.Principal = null;
        Assert.Equal(NetworkDriveError.NotSignedIn,
            (await Assert.ThrowsAsync<NetworkDriveException>(() => harness.Drives.MountAsync(null).AsTask())).Error);
    }

    [Fact]
    public async Task Proc_mounts_lists_the_drive_as_hostfs()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Access.NextPick = new FakeHostFolder("My Files");
        await harness.Drives.MountAsync(null);

        string mounts = await harness.Fixture.ReadTextAsync("/proc/mounts", harness.Fixture.AppContext(null, Read));

        Assert.Contains("host:My\\040Files /mnt/My\\040Files hostfs rw,nosuid,nodev 0 0", mounts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_drops_a_mount_point_outside_mnt_written_into_the_list()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Access.NextPick = new FakeHostFolder("Safe");
        await harness.Drives.MountAsync(null);
        await harness.Drives.UnmountAllAsync();
        string path = "/home/alice/" + NetworkDriveService.TableRelativePath;
        string table = await harness.Fixture.ReadTextAsync(path, harness.Fixture.SystemContext());
        string key = harness.Access.StoredKeys.Single();
        string forged = table.Replace("/mnt/Safe", "/etc", StringComparison.Ordinal);
        FileSystemMutationResult written = await harness.Fixture.Service.WriteAsync(
            new FileSystemWriteRequest(VirtualPath.Parse(path)), new BytesSource(Encoding.UTF8.GetBytes(forged)), harness.Fixture.SystemContext());
        Assert.True(written.Succeeded);

        await harness.Drives.RestoreAsync();

        Assert.Empty(harness.Drives.GetDrives());
        Assert.Equal("/", harness.Fixture.Router.Resolve(VirtualPath.Parse("/etc/hostname")).Mount.Path.Value);
        Assert.DoesNotContain("/etc", await harness.Fixture.ReadTextAsync(path, harness.Fixture.SystemContext()), StringComparison.Ordinal);
        Assert.Contains(key, harness.Access.StoredKeys);
    }

    [Fact]
    public async Task Another_user_neither_lists_the_drive_nor_keeps_it_mounted_at_their_login()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Access.NextPick = new FakeHostFolder("Mine");
        await harness.Drives.MountAsync(null);

        harness.Principal = Principal("bob", AppAuthority.User);
        Assert.Empty(harness.Drives.GetDrives());

        await harness.Drives.RestoreAsync();

        Assert.Null(harness.Drives.Find(VirtualPath.Parse("/mnt/Mine")));
        Assert.Equal("/", harness.Fixture.Router.Resolve(VirtualPath.Parse("/mnt/Mine")).Mount.Path.Value);
    }

    internal static AuthenticatedPrincipal Principal(string login, AppAuthority authority)
    {
        LocalGroupId group = LocalGroupId.FromGuid(Guid.NewGuid());
        return new AuthenticatedPrincipal(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse(login),
            login,
            authority,
            group,
            [group],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            BootUtc);
    }

    private sealed class Harness
    {
        private Harness(VirtualFileSystemFixture fixture)
        {
            Fixture = fixture;
            Principal = NetworkDriveServiceTests.Principal("alice", AppAuthority.User);
            Drives = new NetworkDriveService(fixture.Router, Access, () => Principal);
        }

        public VirtualFileSystemFixture Fixture { get; }

        public FakeHostFolderAccess Access { get; } = new();

        public AuthenticatedPrincipal? Principal { get; set; }

        public NetworkDriveService Drives { get; }

        public static async Task<Harness> CreateAsync() => new(await CreateSeededAsync());
    }
}
