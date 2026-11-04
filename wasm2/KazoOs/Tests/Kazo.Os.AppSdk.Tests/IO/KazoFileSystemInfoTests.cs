using Kazo.Os.AppSdk.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

public sealed class KazoFileSystemInfoTests
{
    [Fact]
    public async Task FileInfo_snapshot_exposes_metadata()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/doc.md", "12345");

        KazoFileInfo info = await KazoFileInfo.GetAsync("doc.md");

        Assert.True(info.Exists);
        Assert.Equal("/home/alice/doc.md", info.FullName);
        Assert.Equal("doc.md", info.Name);
        Assert.Equal(".md", info.Extension);
        Assert.Equal(5, info.Length);
        Assert.Equal("/home/alice", info.DirectoryName);
        Assert.Equal("/home/alice", info.Directory!.FullName);
        Assert.Equal("user", info.Owner);
        Assert.Equal("users", info.Group);
        Assert.Equal(fs.RevisionOf("/home/alice/doc.md"), info.Revision);
        Assert.Equal((UnixFileMode)0b110_100_100, info.UnixFileMode);
        Assert.False(info.IsSymbolicLink);
        Assert.Null(info.LinkTarget);
        Assert.Equal(DateTimeKind.Utc, info.LastWriteTimeUtc.Kind);
    }

    [Fact]
    public async Task Missing_entries_report_System_IO_defaults()
    {
        using IDisposable scope = KazoTestHost.Enter(out _);

        KazoFileInfo info = await KazoFileInfo.GetAsync("ghost.txt");

        Assert.False(info.Exists);
        Assert.Null(info.Revision);
        Assert.Equal(DateTime.FromFileTimeUtc(0), info.CreationTimeUtc);
        Assert.Equal((UnixFileMode)(-1), info.UnixFileMode);
        Assert.Throws<KazoFileNotFoundException>(() => info.Length);
    }

    [Fact]
    public async Task Constructor_does_not_read_until_refresh_and_operations_keep_the_snapshot_current()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt", "A");

        KazoFileInfo info = new("a.txt");
        Assert.False(info.Exists);
        await info.RefreshAsync();
        Assert.True(info.Exists);

        KazoFileInfo copy = await info.CopyToAsync("b.txt");
        await info.MoveToAsync("c.txt");
        Assert.Equal("/home/alice/c.txt", info.FullName);
        Assert.True(info.Exists);
        Assert.True(copy.Exists);

        await info.DeleteAsync();
        Assert.False(info.Exists);
        Assert.False(fs.Exists("/home/alice/c.txt"));
    }

    [Fact]
    public async Task DirectoryInfo_lists_typed_children_and_navigates()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/proj/readme.md", "#").WithFile("/home/alice/proj/src/main.cs", "x");

        KazoDirectoryInfo proj = await KazoDirectoryInfo.GetAsync("proj");

        Assert.True(proj.Exists);
        Assert.Equal("/home/alice", proj.Parent!.FullName);
        Assert.Null(new KazoDirectoryInfo("/").Parent);
        Assert.Equal("/", new KazoDirectoryInfo("/").Name);
        Assert.Equal(["readme.md"], (await proj.GetFilesAsync()).Select(f => f.Name));
        Assert.Equal(1, (await proj.GetFilesAsync()).Single().Length);
        Assert.Equal(["src"], (await proj.GetDirectoriesAsync()).Select(d => d.Name));
        Assert.Equal(2, (await proj.GetFileSystemInfosAsync()).Length);
        Assert.Equal(2, (await proj.GetFilesAsync("*", SearchOption.AllDirectories)).Length);

        KazoDirectoryInfo sub = await proj.CreateSubdirectoryAsync("docs/api");
        Assert.Equal("/home/alice/proj/docs/api", sub.FullName);
        Assert.True(sub.Exists);

        await proj.MoveToAsync("project");
        Assert.True(proj.Exists);
        await proj.DeleteAsync(recursive: true);
        Assert.False(proj.Exists);
    }
}
