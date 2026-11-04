using System.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

/// <summary>ADR 0052: the freedesktop trash behind <see cref="KazoTrash"/>.</summary>
public sealed class KazoTrashTests
{
    private const string Trash = "/home/alice/.local/share/Trash";

    private static string LocalStamp =>
        TimeZoneInfo.ConvertTime(KazoTestHost.Now, TimeZoneInfo.Local).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    [Fact]
    public async Task MoveToTrash_moves_the_file_and_writes_its_trashinfo()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/Documents/final report.txt", "draft");

        KazoTrashItem item = await KazoTrash.MoveToTrashAsync("Documents/final report.txt");

        Assert.Equal("final report.txt", item.Name);
        Assert.Equal("/home/alice/Documents/final report.txt", item.OriginalPath);
        Assert.False(item.IsDirectory);
        Assert.Equal(5, item.Length);
        Assert.False(fs.Exists("/home/alice/Documents/final report.txt"));
        Assert.Equal("draft", fs.ContentOf(Trash + "/files/final report.txt"));
        Assert.Equal(
            "[Trash Info]\nPath=/home/alice/Documents/final%20report.txt\nDeletionDate=" + LocalStamp + "\n",
            fs.ContentOf(Trash + "/info/final report.txt.trashinfo"));
        Assert.Equal(Trash, KazoTrash.TrashDirectory);
    }

    [Fact]
    public async Task MoveToTrash_creates_private_trash_directories()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt");

        await KazoTrash.MoveToTrashAsync("a.txt");

        Assert.Equal((UnixFileMode)0b111_000_000, await KazoFile.GetUnixFileModeAsync(Trash));
        Assert.Equal((UnixFileMode)0b111_000_000, await KazoFile.GetUnixFileModeAsync(Trash + "/files"));
        Assert.Equal((UnixFileMode)0b111_000_000, await KazoFile.GetUnixFileModeAsync(Trash + "/info"));
        Assert.Equal((UnixFileMode)0b110_000_000, await KazoFile.GetUnixFileModeAsync(Trash + "/info/a.txt.trashinfo"));
    }

    [Fact]
    public async Task MoveToTrash_numbers_colliding_names_before_the_extension()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);

        List<string> names = [];
        for (int i = 0; i < 3; i++)
        {
            fs.WithFile("/home/alice/report.txt", "v" + i);
            names.Add((await KazoTrash.MoveToTrashAsync("report.txt")).Name);
        }

        fs.WithFile("/home/alice/.bashrc");
        await KazoTrash.MoveToTrashAsync(".bashrc");
        fs.WithFile("/home/alice/sub/.bashrc");
        names.Add((await KazoTrash.MoveToTrashAsync("sub/.bashrc")).Name);

        Assert.Equal(["report.txt", "report.2.txt", "report.3.txt", ".bashrc.2"], names);
        Assert.Equal("v2", fs.ContentOf(Trash + "/files/report.3.txt"));
    }

    [Fact]
    public async Task MoveToTrash_skips_a_name_held_by_an_element_without_metadata()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile(Trash + "/files/a.txt", "orphan").WithDirectory(Trash + "/info").WithFile("/home/alice/a.txt", "new");

        KazoTrashItem item = await KazoTrash.MoveToTrashAsync("a.txt");

        Assert.Equal("a.2.txt", item.Name);
        Assert.Equal("orphan", fs.ContentOf(Trash + "/files/a.txt"));
    }

    [Fact]
    public async Task MoveToTrash_moves_a_whole_directory_and_sums_its_size()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/project/a.txt", "123").WithFile("/home/alice/project/src/b.txt", "4567");

        KazoTrashItem item = await KazoTrash.MoveToTrashAsync("project");

        Assert.True(item.IsDirectory);
        Assert.Equal(7, item.Length);
        Assert.False(fs.Exists("/home/alice/project"));
        Assert.Equal("4567", fs.ContentOf(Trash + "/files/project/src/b.txt"));
    }

    [Fact]
    public async Task MoveToTrash_of_a_missing_path_throws_not_found()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);

        await Assert.ThrowsAsync<KazoFileNotFoundException>(() => KazoTrash.MoveToTrashAsync("missing.txt"));
        Assert.False(fs.Exists(Trash));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/home/alice")]
    [InlineData("/home/alice/.local")]
    [InlineData("/home/alice/.local/share/Trash")]
    public async Task MoveToTrash_refuses_the_root_the_home_and_the_trash_itself(string path)
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory(Trash + "/files");

        KazoIOException error = await Assert.ThrowsAsync<KazoIOException>(() => KazoTrash.MoveToTrashAsync(path));

        Assert.Equal(FileSystemErrorCode.ProtectedEntry, error.ErrorCode);
        Assert.True(fs.Exists(path));
    }

    [Fact]
    public async Task MoveToTrash_of_an_element_already_in_the_trash_deletes_it_permanently()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt");
        await KazoTrash.MoveToTrashAsync("a.txt");

        await KazoTrash.MoveToTrashAsync(Trash + "/files/a.txt");

        Assert.False(fs.Exists(Trash + "/files/a.txt"));
        Assert.False(fs.Exists(Trash + "/files/a.2.txt"));
    }

    [Fact]
    public async Task MoveToTrash_on_a_virtual_filesystem_fails_and_leaves_no_trashinfo()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory(KazoTestHost.Home).WithFile("/proc/uptime", "42");
        ScriptedFileSystemGateway scripted = new(fs)
        {
            OnMove = request => request.SourcePath.Value.StartsWith("/proc/", StringComparison.Ordinal)
                ? ScriptedFileSystemGateway.Error(FileSystemOperation.Move, FileSystemErrorCode.CrossMountOperation, request.SourcePath.Value)
                : null
        };
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(scripted), KazoTestHost.Home);

        KazoIOException error = await Assert.ThrowsAsync<KazoIOException>(() => KazoTrash.MoveToTrashAsync("/proc/uptime"));

        Assert.Equal("Cannot move to trash: virtual file system", error.Message);
        Assert.True(fs.Exists("/proc/uptime"));
        Assert.False(fs.Exists(Trash + "/info/uptime.trashinfo"));
    }

    [Fact]
    public async Task MoveToTrash_under_sudo_uses_roots_trash()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory(KazoTestHost.Home).WithFile("/etc/motd", "hi");
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(fs, isRootElevated: true), "/etc");

        KazoTrashItem item = await KazoTrash.MoveToTrashAsync("motd");

        Assert.Equal("/root/.local/share/Trash", KazoTrash.TrashDirectory);
        Assert.Equal("hi", fs.ContentOf("/root/.local/share/Trash/files/motd"));
        Assert.Equal("/etc/motd", item.OriginalPath);
        Assert.Equal((UnixFileMode)0b111_000_000, await KazoFile.GetUnixFileModeAsync("/root"));
    }

    [Fact]
    public async Task GetItems_lists_oldest_first_cleans_orphan_metadata_and_keeps_unknown_elements()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile(Trash + "/info/new.txt.trashinfo", "[Trash Info]\nPath=/home/alice/new.txt\nDeletionDate=2026-10-10T12:00:00\n")
            .WithFile(Trash + "/files/new.txt", "12")
            .WithFile(Trash + "/info/old.txt.trashinfo", "[Trash Info]\nPath=/home/alice/a%20b/old.txt\nDeletionDate=2026-01-02T03:04:05\n")
            .WithFile(Trash + "/files/old.txt", "1")
            .WithFile(Trash + "/info/gone.txt.trashinfo", "[Trash Info]\nPath=/home/alice/gone.txt\nDeletionDate=2026-10-10T12:00:00\n")
            .WithFile(Trash + "/files/stray.bin", "abc");

        IReadOnlyList<KazoTrashItem> items = await KazoTrash.GetItemsAsync();

        Assert.Equal(["stray.bin", "old.txt", "new.txt"], items.Select(i => i.Name));
        Assert.Null(items[0].OriginalPath);
        Assert.Null(items[0].DeletedAt);
        Assert.Equal("/home/alice/a b/old.txt", items[1].OriginalPath);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), items[1].DeletedAt!.Value.DateTime);
        Assert.Equal(2, items[2].Length);
        Assert.False(fs.Exists(Trash + "/info/gone.txt.trashinfo"));
    }

    [Fact]
    public async Task GetItems_of_a_missing_trash_is_empty()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);

        Assert.Empty(await KazoTrash.GetItemsAsync());
    }

    [Fact]
    public async Task Restore_puts_the_element_back_recreating_missing_parents()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/deep/dir/a.txt", "x");
        KazoTrashItem item = await KazoTrash.MoveToTrashAsync("deep/dir/a.txt");
        await KazoDirectory.DeleteAsync("deep", recursive: true);

        string restored = await KazoTrash.RestoreAsync(item);

        Assert.Equal("/home/alice/deep/dir/a.txt", restored);
        Assert.Equal("x", fs.ContentOf(restored));
        Assert.False(fs.Exists(Trash + "/files/a.txt"));
        Assert.False(fs.Exists(Trash + "/info/a.txt.trashinfo"));
    }

    [Fact]
    public async Task Restore_to_an_explicit_destination_and_never_overwrites()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt", "old");
        KazoTrashItem item = await KazoTrash.MoveToTrashAsync("a.txt");
        fs.WithFile("/home/alice/a.txt", "new");

        KazoIOException error = await Assert.ThrowsAsync<KazoIOException>(() => KazoTrash.RestoreAsync(item));
        Assert.Contains("already exists", error.Message);
        Assert.Equal("new", fs.ContentOf("/home/alice/a.txt"));

        string restored = await KazoTrash.RestoreAsync(item, "b.txt");
        Assert.Equal("/home/alice/b.txt", restored);
        Assert.Equal("old", fs.ContentOf("/home/alice/b.txt"));
    }

    [Fact]
    public async Task Restore_of_an_element_with_unknown_origin_needs_a_destination()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile(Trash + "/files/stray.bin");
        KazoTrashItem item = (await KazoTrash.GetItemsAsync()).Single();

        await Assert.ThrowsAsync<KazoIOException>(() => KazoTrash.RestoreAsync(item));
        Assert.Equal("/home/alice/stray.bin", await KazoTrash.RestoreAsync(item, "~/stray.bin"));
    }

    [Fact]
    public async Task Restore_of_a_vanished_element_throws_not_found()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);

        await Assert.ThrowsAsync<KazoFileNotFoundException>(
            () => KazoTrash.RestoreAsync(new KazoTrashItem("nope.txt", "/home/alice/nope.txt", null, false, 0)));
        await Assert.ThrowsAsync<ArgumentException>(
            () => KazoTrash.RestoreAsync(new KazoTrashItem("../../etc", null, null, false, 0)));
    }

    [Fact]
    public async Task Delete_and_Empty_remove_elements_permanently_and_log()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory(KazoTestHost.Home)
            .WithFile("/home/alice/a.txt").WithFile("/home/alice/b.txt").WithFile("/home/alice/c/d.txt");
        FakeAppLoggingGateway log = new();
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(fs, logging: log), KazoTestHost.Home);

        KazoTrashItem a = await KazoTrash.MoveToTrashAsync("a.txt");
        await KazoTrash.MoveToTrashAsync("b.txt");
        await KazoTrash.MoveToTrashAsync("c");

        await KazoTrash.DeleteAsync(a);
        Assert.False(fs.Exists(Trash + "/files/a.txt"));
        Assert.False(fs.Exists(Trash + "/info/a.txt.trashinfo"));
        await Assert.ThrowsAsync<KazoFileNotFoundException>(() => KazoTrash.DeleteAsync(a));

        await KazoTrash.EmptyAsync();
        Assert.Empty(await KazoTrash.GetItemsAsync());
        Assert.True(fs.Exists(Trash + "/files"));

        Assert.All(log.Entries, entry => Assert.Equal(DiagnosticSeverity.Information, entry.Severity));
        Assert.Equal(5, log.Entries.Count);
        Assert.Contains("Moved '/home/alice/a.txt' to the trash as 'a.txt'.", log.Entries.Select(e => e.Message));
    }

    [Fact]
    public void A_trashinfo_written_by_the_sync_says_the_file_was_deleted_on_another_device()
    {
        string content = KazoTrashInfo.Format("/home/alice/note.txt", new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero))
            + "X-KazoOS-Origin=deleted-on-another-device\nX-KazoOS-Device=0f3c\n";

        KazoTrashInfo info = KazoTrashInfo.Parse(content)!;

        Assert.Equal("/home/alice/note.txt", info.OriginalPath);
        Assert.True(info.DeletedOnAnotherDevice);
        Assert.False(KazoTrashInfo.Parse(KazoTrashInfo.Format("/home/alice/a.txt", DateTimeOffset.Now))!.DeletedOnAnotherDevice);
    }

    [Fact]
    public void A_trashinfo_written_before_the_rename_still_says_the_file_was_deleted_on_another_device()
    {
        // ADR 0228 §2: entries written before Kazo OS carry X-HackerOS-Origin.
        string content = KazoTrashInfo.Format("/home/alice/old.txt", new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero))
            + "X-HackerOS-Origin=deleted-on-another-device\nX-HackerOS-Device=0f3c\n";

        Assert.True(KazoTrashInfo.Parse(content)!.DeletedOnAnotherDevice);
    }
}
