using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.FileSystem.Virtual;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using static Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture;

namespace Kazo.Os.Platform.Core.Tests.FileSystem.HostFolders;

/// <summary>
/// ADR 0231: the network drives the acting user mounted count as their personal files, so an app
/// with <c>filesystem.user-home.*</c> reads and writes them; another user's drive and <c>/mnt</c>
/// itself stay under <c>filesystem.system.*</c>.
/// </summary>
public sealed class NetworkDriveAppAccessTests
{
    private const string HomeRead = AppCapabilities.FileSystemUserHomeRead;
    private const string HomeWrite = AppCapabilities.FileSystemUserHomeWrite;
    private const string SystemRead = AppCapabilities.FileSystemSystemRead;

    [Fact]
    public async Task An_app_with_user_home_read_reads_the_users_own_drive()
    {
        (VirtualFileSystemFixture fixture, _) = await MountAsync("Music", ("track list", "list.txt"));

        string text = await fixture.ReadTextAsync("/mnt/Music/list.txt", fixture.AppContext(null, HomeRead));

        Assert.Equal("track list", text);
    }

    [Fact]
    public async Task An_app_with_user_home_write_writes_the_users_own_drive()
    {
        (VirtualFileSystemFixture fixture, FakeHostFolder folder) = await MountAsync("Music");
        FileSystemAuthorizationContext context = fixture.AppContext(null, HomeRead, HomeWrite);

        FileSystemMutationResult created = await fixture.Service.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse("/mnt/Music/new.m3u8"), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x180)),
            context);
        FileSystemMutationResult written = await fixture.Service.WriteAsync(
            new FileSystemWriteRequest(VirtualPath.Parse("/mnt/Music/new.m3u8")),
            new BytesSource(Encoding.UTF8.GetBytes("#EXTM3U")),
            context);

        Assert.True(created.Succeeded, $"create: {created.Transaction.Error?.Code}");
        Assert.True(written.Succeeded, $"write: {written.Transaction.Error?.Code}");
        Assert.Equal("#EXTM3U", folder.Text("new.m3u8"));
    }

    [Fact]
    public async Task Writing_the_own_drive_needs_user_home_write_not_only_read()
    {
        (VirtualFileSystemFixture fixture, _) = await MountAsync("Music");

        FileSystemMutationResult created = await fixture.Service.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse("/mnt/Music/new.txt"), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x180)),
            fixture.AppContext(null, HomeRead));

        Assert.False(created.Succeeded);
        Assert.Equal(FileSystemErrorCode.CapabilityDenied, created.Transaction.Error!.Code);
        Assert.Equal(HomeWrite, created.Transaction.Error.RequiredCapability);
    }

    [Fact]
    public async Task An_app_without_any_file_capability_is_refused_the_user_home_one()
    {
        (VirtualFileSystemFixture fixture, _) = await MountAsync("Music", ("x", "a.txt"));

        FileSystemResult<FileSystemContentReadHandle> read = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/mnt/Music/a.txt")), fixture.AppContext(null));

        Assert.False(read.Succeeded);
        Assert.Equal(FileSystemErrorCode.CapabilityDenied, read.Error!.Code);
        Assert.Equal(HomeRead, read.Error.RequiredCapability);
    }

    [Fact]
    public async Task An_app_with_only_the_system_capability_keeps_reading_the_own_drive()
    {
        (VirtualFileSystemFixture fixture, _) = await MountAsync("Music", ("kept", "a.txt"));

        string text = await fixture.ReadTextAsync("/mnt/Music/a.txt", fixture.AppContext(null, SystemRead));

        Assert.Equal("kept", text);
    }

    [Fact]
    public async Task Another_users_drive_stays_a_system_path_for_an_app_with_user_home_read()
    {
        (VirtualFileSystemFixture fixture, _) = await MountAsync("Music", ("secret", "a.txt"));

        // The drive's root: the capability is checked before its 0700 mode bits.
        FileSystemResult<FileSystemDirectorySnapshot> listing = await fixture.Service.EnumerateAsync(
            new FileSystemEnumerateRequest(VirtualPath.Parse("/mnt/Music")), Bob(HomeRead, HomeWrite));
        // Inside it, crossing the 0700 root already stops another account.
        FileSystemResult<FileSystemContentReadHandle> read = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/mnt/Music/a.txt")), Bob(HomeRead, HomeWrite));

        Assert.False(listing.Succeeded);
        Assert.Equal(FileSystemErrorCode.CapabilityDenied, listing.Error!.Code);
        Assert.Equal(SystemRead, listing.Error.RequiredCapability);
        Assert.False(read.Succeeded);
        Assert.Equal(FileSystemErrorCode.PermissionDenied, read.Error!.Code);
    }

    [Fact]
    public async Task Another_user_with_the_system_capability_is_still_stopped_by_the_mode_bits()
    {
        (VirtualFileSystemFixture fixture, _) = await MountAsync("Music", ("secret", "a.txt"));

        FileSystemResult<FileSystemContentReadHandle> read = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/mnt/Music/a.txt")), Bob(SystemRead));

        Assert.False(read.Succeeded);
        Assert.Equal(FileSystemErrorCode.PermissionDenied, read.Error!.Code);
    }

    [Fact]
    public async Task Mnt_itself_stays_a_system_path()
    {
        (VirtualFileSystemFixture fixture, _) = await MountAsync("Music");

        FileSystemResult<FileSystemDirectorySnapshot> listing = await fixture.Service.EnumerateAsync(
            new FileSystemEnumerateRequest(VirtualPath.Parse("/mnt")), fixture.AppContext(null, HomeRead));

        Assert.False(listing.Succeeded);
        Assert.Equal(FileSystemErrorCode.CapabilityDenied, listing.Error!.Code);
        Assert.Equal(SystemRead, listing.Error.RequiredCapability);
    }

    [Fact]
    public async Task A_disconnected_own_drive_answers_as_before()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        FakeHostFolder folder = new("Asleep");
        folder.Put("x", "a.txt");
        VirtualPath mountPath = VirtualPath.Parse("/mnt/Asleep");
        fixture.Router.Mount(new FileSystemMount(
            mountPath,
            new HostFolderFileSystemProvider(mountPath, folder, "alice", "alice", connected: false),
            isProtectedRoot: false));

        FileSystemResult<FileSystemContentReadHandle> read = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/mnt/Asleep/a.txt")), fixture.AppContext(null, HomeRead));

        Assert.False(read.Succeeded);
        Assert.Equal(FileSystemErrorCode.ProviderUnavailable, read.Error!.Code);
    }

    /// <summary>Mounts a fake host folder as Alice's drive through the real network drive service.</summary>
    private static async Task<(VirtualFileSystemFixture Fixture, FakeHostFolder Folder)> MountAsync(
        string name,
        params (string Text, string File)[] files)
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        FakeHostFolder folder = new(name);
        foreach ((string text, string file) in files)
        {
            folder.Put(text, file);
        }

        FakeHostFolderAccess access = new() { NextPick = folder };
        NetworkDriveService drives = new(
            fixture.Router, access, () => NetworkDriveServiceTests.Principal("alice", AppAuthority.User));
        NetworkDrive? drive = await drives.MountAsync(null);
        Assert.NotNull(drive);
        Assert.Equal("alice", drive.OwnerId);
        return (fixture, folder);
    }

    private static FileSystemAuthorizationContext Bob(params string[] capabilities) =>
        new(
            new AppOperationContext
            {
                AppId = "org.tests.virtual-fs",
                UserId = "bob",
                UserName = "bob",
                UserAuthority = AppAuthority.User,
                GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal)
            },
            ["users"],
            BootUtc);
}
