using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Platform.Core.Tests.FileSystem.HostFolders;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0231: an app with only <c>filesystem.user-home.read</c> reads the user's own network drive
/// through <c>KazoFile</c> the same way on the main thread and in a Web Worker, and another user's
/// drive stays refused.
/// </summary>
public sealed class NetworkDriveWorkerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KazoFile_reads_the_users_own_drive_with_user_home_read(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers);
        MountDrive(os, "Music", "alice", ("#EXTM3U\n", "list.m3u8"));

        ShellRun run = await os.RunAsync("readfile /mnt/Music/list.m3u8").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("#EXTM3U\n", run.Stdout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Another_users_drive_stays_refused_with_user_home_read(bool workers)
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers);
        MountDrive(os, "Shared", "bob", ("x", "a.txt"));

        ShellRun run = await os.RunAsync("readfile /mnt/Shared/a.txt").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stdout);
    }

    private static void MountDrive(OsFixture os, string name, string owner, params (string Text, string File)[] files)
    {
        FakeHostFolder folder = new(name);
        foreach ((string text, string file) in files)
        {
            folder.Put(text, file);
        }

        VirtualPath mountPath = VirtualPath.Parse($"/mnt/{name}");
        os.Router.Mount(new FileSystemMount(
            mountPath,
            new HostFolderFileSystemProvider(mountPath, folder, owner, "users", connected: true),
            isProtectedRoot: false));
    }
}
