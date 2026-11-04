using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

public sealed class KazoDirectoryTests
{
    [Fact]
    public async Task CreateDirectory_creates_missing_parents_with_the_umask_and_is_idempotent()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);

        KazoDirectoryInfo created = await KazoDirectory.CreateDirectoryAsync("projects/game/src");
        await KazoDirectory.CreateDirectoryAsync("projects/game/src");

        Assert.True(created.Exists);
        Assert.Equal("/home/alice/projects/game/src", created.FullName);
        Assert.True(fs.Exists("/home/alice/projects/game"));
        Assert.Equal((UnixFileMode)0b111_101_101, created.UnixFileMode);
        Assert.Equal((UnixFileMode)0b111_101_101, await KazoFile.GetUnixFileModeAsync("projects"));
    }

    [Fact]
    public async Task CreateDirectory_fails_when_a_file_is_in_the_way()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/blocker");

        await Assert.ThrowsAsync<KazoIOException>(() => KazoDirectory.CreateDirectoryAsync("blocker/child"));
    }

    [Fact]
    public async Task Exists_is_true_only_for_directories()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt");

        Assert.True(await KazoDirectory.ExistsAsync("~"));
        Assert.True(await KazoDirectory.ExistsAsync("/"));
        Assert.False(await KazoDirectory.ExistsAsync("a.txt"));
        Assert.False(await KazoDirectory.ExistsAsync("missing"));
        Assert.False(await KazoDirectory.ExistsAsync(null));
    }

    [Fact]
    public async Task Listing_filters_by_kind_and_pattern_in_ordinal_order()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/b.txt").WithFile("/home/alice/a.txt").WithFile("/home/alice/c.md")
            .WithFile("/home/alice/sub/deep.txt").WithDirectory("/home/alice/empty");

        Assert.Equal(["/home/alice/a.txt", "/home/alice/b.txt", "/home/alice/c.md"], await KazoDirectory.GetFilesAsync("~"));
        Assert.Equal(["/home/alice/a.txt", "/home/alice/b.txt"], await KazoDirectory.GetFilesAsync("~", "*.txt"));
        Assert.Equal(["/home/alice/a.txt", "/home/alice/b.txt"], await KazoDirectory.GetFilesAsync("~", "?.t?t"));
        Assert.Equal(["/home/alice/empty", "/home/alice/sub"], await KazoDirectory.GetDirectoriesAsync("~"));
        Assert.Equal(
            ["/home/alice/a.txt", "/home/alice/b.txt", "/home/alice/sub/deep.txt"],
            await KazoDirectory.GetFilesAsync("~", "*.txt", SearchOption.AllDirectories));
        Assert.Equal(5, (await KazoDirectory.GetFileSystemEntriesAsync("~")).Length);

        List<string> streamed = [];
        await foreach (string entry in KazoDirectory.EnumerateFileSystemEntriesAsync("~", "*", SearchOption.AllDirectories))
        {
            streamed.Add(entry);
        }

        Assert.Equal(6, streamed.Count);
    }

    [Fact]
    public async Task Listing_validates_its_arguments()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt");

        await Assert.ThrowsAsync<KazoDirectoryNotFoundException>(() => KazoDirectory.GetFilesAsync("missing"));
        await Assert.ThrowsAsync<ArgumentException>(() => KazoDirectory.GetFilesAsync("~", "sub/*.txt"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => KazoDirectory.GetFilesAsync("~", null!));
    }

    [Fact]
    public async Task Move_renames_a_subtree_and_refuses_an_existing_destination_or_itself()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/src/a.txt", "A").WithDirectory("/home/alice/taken");

        await Assert.ThrowsAsync<KazoIOException>(() => KazoDirectory.MoveAsync("src", "taken"));
        await Assert.ThrowsAsync<KazoIOException>(() => KazoDirectory.MoveAsync("src", "src/inner"));
        await Assert.ThrowsAsync<KazoDirectoryNotFoundException>(() => KazoDirectory.MoveAsync("missing", "x"));
        await KazoDirectory.MoveAsync("src", "dst");

        Assert.Equal("A", fs.ContentOf("/home/alice/dst/a.txt"));
        Assert.False(fs.Exists("/home/alice/src"));
    }

    [Fact]
    public async Task Move_across_mounts_copies_the_tree_then_deletes_the_source()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway()
            .WithFile("/home/alice/src/a.txt", "A").WithFile("/home/alice/src/sub/b.txt", "B").WithDirectory("/mnt/usb");
        ScriptedFileSystemGateway gateway = new(fs)
        {
            OnMove = r => ScriptedFileSystemGateway.Error(FileSystemOperation.Move, FileSystemErrorCode.CrossMountOperation, r.SourcePath.Value)
        };
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(gateway));

        await KazoDirectory.MoveAsync("src", "/mnt/usb/src");

        Assert.Equal("A", fs.ContentOf("/mnt/usb/src/a.txt"));
        Assert.Equal("B", fs.ContentOf("/mnt/usb/src/sub/b.txt"));
        Assert.False(fs.Exists("/home/alice/src"));
    }

    [Fact]
    public async Task Copy_duplicates_a_subtree_and_refuses_an_existing_destination_or_itself()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/src/a.txt", "A").WithFile("/home/alice/src/sub/b.txt", "B").WithDirectory("/home/alice/taken");

        await Assert.ThrowsAsync<KazoIOException>(() => KazoDirectory.CopyAsync("src", "taken"));
        await Assert.ThrowsAsync<KazoIOException>(() => KazoDirectory.CopyAsync("src", "src/inner"));
        await Assert.ThrowsAsync<KazoDirectoryNotFoundException>(() => KazoDirectory.CopyAsync("missing", "x"));
        await KazoDirectory.CopyAsync("src", "dst");

        Assert.Equal("A", fs.ContentOf("/home/alice/dst/a.txt"));
        Assert.Equal("B", fs.ContentOf("/home/alice/dst/sub/b.txt"));
        Assert.Equal("A", fs.ContentOf("/home/alice/src/a.txt"));
        Assert.False(fs.Exists("/home/alice/src/inner"));
    }

    [Fact]
    public async Task Copy_across_mounts_recreates_the_tree_entry_by_entry()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway()
            .WithFile("/home/alice/src/a.txt", "A").WithFile("/home/alice/src/sub/b.txt", "B").WithDirectory("/mnt/usb");
        ScriptedFileSystemGateway gateway = new(fs)
        {
            OnCopy = r => r.SourcePath.Value == "/home/alice/src"
                ? ScriptedFileSystemGateway.Error(FileSystemOperation.Copy, FileSystemErrorCode.CrossMountOperation, r.SourcePath.Value)
                : null
        };
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(gateway));

        await KazoDirectory.CopyAsync("src", "/mnt/usb/src");

        Assert.Equal("A", fs.ContentOf("/mnt/usb/src/a.txt"));
        Assert.Equal("B", fs.ContentOf("/mnt/usb/src/sub/b.txt"));
        Assert.True(fs.Exists("/home/alice/src/a.txt"));
    }

    [Fact]
    public async Task Delete_requires_recursive_for_a_non_empty_directory()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/full/a.txt").WithDirectory("/home/alice/empty").WithFile("/home/alice/file");

        await KazoDirectory.DeleteAsync("empty");
        KazoIOException notEmpty = await Assert.ThrowsAsync<KazoIOException>(() => KazoDirectory.DeleteAsync("full"));
        await KazoDirectory.DeleteAsync("full", recursive: true);

        Assert.Equal(FileSystemErrorCode.DirectoryNotEmpty, notEmpty.ErrorCode);
        Assert.False(fs.Exists("/home/alice/empty"));
        Assert.False(fs.Exists("/home/alice/full/a.txt"));
        await Assert.ThrowsAsync<KazoDirectoryNotFoundException>(() => KazoDirectory.DeleteAsync("empty"));
        await Assert.ThrowsAsync<KazoIOException>(() => KazoDirectory.DeleteAsync("file"));
    }
}
