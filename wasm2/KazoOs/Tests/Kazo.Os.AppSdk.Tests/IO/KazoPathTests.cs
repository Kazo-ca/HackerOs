using Kazo.Os.AppSdk.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

public sealed class KazoPathTests
{
    [Theory]
    [InlineData(new[] { "/a", "b", "c.txt" }, "/a/b/c.txt")]
    [InlineData(new[] { "/a/", "b" }, "/a/b")]
    [InlineData(new[] { "a", "/b", "c" }, "/b/c")]
    [InlineData(new[] { "a", "", "c" }, "a/c")]
    public void Combine_resets_on_a_rooted_segment(string[] segments, string expected) =>
        Assert.Equal(expected, KazoPath.Combine(segments));

    [Fact]
    public void Join_never_resets_and_skips_empty_segments()
    {
        Assert.Equal("a/b/c", KazoPath.Join("a", null, "b", "", "c"));
        Assert.Equal("/a//b", KazoPath.Join("/a/", "/b")); // like Path.Join: no trimming
        Assert.Equal("/a/b", KazoPath.Join("/a", "/b"));
    }

    [Theory]
    [InlineData("/home/alice/notes.txt", "notes.txt", "notes", ".txt", "/home/alice")]
    [InlineData("/home/alice/.bashrc", ".bashrc", "", ".bashrc", "/home/alice")]
    [InlineData("/home/alice/archive.tar.gz", "archive.tar.gz", "archive.tar", ".gz", "/home/alice")]
    [InlineData("/file.", "file.", "file", "", "/")]
    [InlineData("/etc", "etc", "etc", "", "/")]
    [InlineData("name", "name", "name", "", "")]
    [InlineData("/a/b/", "", "", "", "/a/b")]
    public void Segment_helpers_match_System_IO_Path(
        string path, string fileName, string withoutExtension, string extension, string directoryName)
    {
        Assert.Equal(fileName, KazoPath.GetFileName(path));
        Assert.Equal(withoutExtension, KazoPath.GetFileNameWithoutExtension(path));
        Assert.Equal(extension, KazoPath.GetExtension(path));
        Assert.Equal(directoryName, KazoPath.GetDirectoryName(path));
    }

    [Fact]
    public void Edge_cases_follow_System_IO_Path()
    {
        Assert.Null(KazoPath.GetDirectoryName("/"));
        Assert.Null(KazoPath.GetDirectoryName(""));
        Assert.Null(KazoPath.GetFileName(null));
        Assert.True(KazoPath.IsPathRooted("/x"));
        Assert.False(KazoPath.IsPathRooted("x/y"));
        Assert.False(KazoPath.IsPathRooted(null));
        Assert.Equal('/', KazoPath.DirectorySeparatorChar);
        Assert.True(KazoPath.HasExtension("a.txt"));
        Assert.False(KazoPath.HasExtension("a"));
    }

    [Theory]
    [InlineData("/d/report.txt", ".md", "/d/report.md")]
    [InlineData("/d/report.txt", "md", "/d/report.md")]
    [InlineData("/d/report.txt", null, "/d/report")]
    [InlineData("/d.x/report", ".md", "/d.x/report.md")]
    public void ChangeExtension_replaces_only_the_file_extension(string path, string? extension, string expected) =>
        Assert.Equal(expected, KazoPath.ChangeExtension(path, extension));

    [Theory]
    [InlineData("~", "/home/alice")]
    [InlineData("~/docs/../notes.txt", "/home/alice/notes.txt")]
    [InlineData("notes.txt", "/var/log/notes.txt")]
    [InlineData("../tmp/./x", "/var/tmp/x")]
    [InlineData("/etc//hackeros/", "/etc/hackeros")]
    [InlineData("~other", "/var/log/~other")]
    public void GetFullPath_expands_home_and_resolves_against_the_working_directory(string path, string expected)
    {
        using (KazoExecutionContext.Enter(KazoTestHost.CreateContext(new FakeAppFileSystemGateway()), "/var/log"))
        {
            Assert.Equal(expected, KazoPath.GetFullPath(path));
        }
    }

    [Fact]
    public void GetFullPath_with_a_base_path_is_pure()
    {
        Assert.Equal("/srv/a/c", KazoPath.GetFullPath("b/../c", "/srv/a"));
        Assert.Equal("/x", KazoPath.GetFullPath("/x", "/srv"));
        Assert.Throws<ArgumentException>(() => KazoPath.GetFullPath("b", "relative"));
    }

    [Fact]
    public void GetFullPath_rejects_empty_and_escaping_paths()
    {
        Assert.Throws<ArgumentException>(() => KazoPath.GetFullPath(" ", "/"));
        Assert.Throws<ArgumentException>(() => KazoPath.GetFullPath("/../..", "/"));
        Assert.Throws<ArgumentNullException>(() => KazoPath.GetFullPath(null!));
    }
}
