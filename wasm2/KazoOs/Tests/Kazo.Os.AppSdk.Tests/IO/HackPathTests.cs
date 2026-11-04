using Kazo.Os.AppSdk.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

public sealed class HackPathTests
{
    [Theory]
    [InlineData(new[] { "/a", "b", "c.txt" }, "/a/b/c.txt")]
    [InlineData(new[] { "/a/", "b" }, "/a/b")]
    [InlineData(new[] { "a", "/b", "c" }, "/b/c")]
    [InlineData(new[] { "a", "", "c" }, "a/c")]
    public void Combine_resets_on_a_rooted_segment(string[] segments, string expected) =>
        Assert.Equal(expected, HackPath.Combine(segments));

    [Fact]
    public void Join_never_resets_and_skips_empty_segments()
    {
        Assert.Equal("a/b/c", HackPath.Join("a", null, "b", "", "c"));
        Assert.Equal("/a//b", HackPath.Join("/a/", "/b")); // like Path.Join: no trimming
        Assert.Equal("/a/b", HackPath.Join("/a", "/b"));
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
        Assert.Equal(fileName, HackPath.GetFileName(path));
        Assert.Equal(withoutExtension, HackPath.GetFileNameWithoutExtension(path));
        Assert.Equal(extension, HackPath.GetExtension(path));
        Assert.Equal(directoryName, HackPath.GetDirectoryName(path));
    }

    [Fact]
    public void Edge_cases_follow_System_IO_Path()
    {
        Assert.Null(HackPath.GetDirectoryName("/"));
        Assert.Null(HackPath.GetDirectoryName(""));
        Assert.Null(HackPath.GetFileName(null));
        Assert.True(HackPath.IsPathRooted("/x"));
        Assert.False(HackPath.IsPathRooted("x/y"));
        Assert.False(HackPath.IsPathRooted(null));
        Assert.Equal('/', HackPath.DirectorySeparatorChar);
        Assert.True(HackPath.HasExtension("a.txt"));
        Assert.False(HackPath.HasExtension("a"));
    }

    [Theory]
    [InlineData("/d/report.txt", ".md", "/d/report.md")]
    [InlineData("/d/report.txt", "md", "/d/report.md")]
    [InlineData("/d/report.txt", null, "/d/report")]
    [InlineData("/d.x/report", ".md", "/d.x/report.md")]
    public void ChangeExtension_replaces_only_the_file_extension(string path, string? extension, string expected) =>
        Assert.Equal(expected, HackPath.ChangeExtension(path, extension));

    [Theory]
    [InlineData("~", "/home/alice")]
    [InlineData("~/docs/../notes.txt", "/home/alice/notes.txt")]
    [InlineData("notes.txt", "/var/log/notes.txt")]
    [InlineData("../tmp/./x", "/var/tmp/x")]
    [InlineData("/etc//hackeros/", "/etc/hackeros")]
    [InlineData("~other", "/var/log/~other")]
    public void GetFullPath_expands_home_and_resolves_against_the_working_directory(string path, string expected)
    {
        using (HackExecutionContext.Enter(HackTestHost.CreateContext(new FakeAppFileSystemGateway()), "/var/log"))
        {
            Assert.Equal(expected, HackPath.GetFullPath(path));
        }
    }

    [Fact]
    public void GetFullPath_with_a_base_path_is_pure()
    {
        Assert.Equal("/srv/a/c", HackPath.GetFullPath("b/../c", "/srv/a"));
        Assert.Equal("/x", HackPath.GetFullPath("/x", "/srv"));
        Assert.Throws<ArgumentException>(() => HackPath.GetFullPath("b", "relative"));
    }

    [Fact]
    public void GetFullPath_rejects_empty_and_escaping_paths()
    {
        Assert.Throws<ArgumentException>(() => HackPath.GetFullPath(" ", "/"));
        Assert.Throws<ArgumentException>(() => HackPath.GetFullPath("/../..", "/"));
        Assert.Throws<ArgumentNullException>(() => HackPath.GetFullPath(null!));
    }
}
