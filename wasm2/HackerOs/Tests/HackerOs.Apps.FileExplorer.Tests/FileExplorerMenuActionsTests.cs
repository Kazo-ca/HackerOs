using Xunit;
using HackerOs.AppSdk.Blazor.Files;

namespace HackerOs.Apps.FileExplorer.Tests;

/// <summary>The names the window's file menu entries use (ADR 0185 §10-13).</summary>
public sealed class FileExplorerMenuActionsTests
{
    private static HackFileViewItem Item(string name, bool folder = false) =>
        new("/home/alice/" + name, name, folder, 0, folder ? "inode/directory" : "text/plain", DateTimeOffset.UnixEpoch);

    [Fact]
    public void DefaultArchiveName_uses_the_one_item_or_Archive()
    {
        Assert.Equal("Photos.zip", FileExplorerWindow.DefaultArchiveName([Item("Photos", folder: true)]));
        Assert.Equal("Archive.zip", FileExplorerWindow.DefaultArchiveName([Item("a.txt"), Item("b.txt")]));
    }

    [Fact]
    public void UniqueArchiveName_numbers_a_taken_name()
    {
        HashSet<string> taken = ["Photos.zip", "Photos (2).zip"];

        Assert.Equal("Photos (3).zip", FileExplorerWindow.UniqueArchiveName("Photos.zip", taken.Contains));
        Assert.Equal("Notes.zip", FileExplorerWindow.UniqueArchiveName("Notes.zip", taken.Contains));
    }

    [Theory]
    [InlineData("/mnt/photos", "photos")]
    [InlineData("/mnt/photos/", "photos")]
    [InlineData("/", "/")]
    public void DriveLabel_is_the_last_segment_of_the_mount_point(string mountPoint, string expected)
    {
        Assert.Equal(expected, FileExplorerWindow.DriveLabel(mountPoint));
    }
}
