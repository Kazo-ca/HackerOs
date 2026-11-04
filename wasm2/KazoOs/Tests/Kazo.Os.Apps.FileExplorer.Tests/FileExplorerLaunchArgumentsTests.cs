using Kazo.Os.App.Abstractions;
using Kazo.Os.Apps.FileExplorer;
using Xunit;

namespace Kazo.Os.Apps.FileExplorer.Tests;

/// <summary>
/// The explorer's command line (ADR 0039 and ADR 0096, section 1): the folder to open and the item
/// to select, as <c>RevealFileIntent</c> sends them (<c>&lt;parent&gt; --select=&lt;path&gt;</c>).
/// </summary>
public sealed class FileExplorerLaunchArgumentsTests
{
    [Fact]
    public void No_argument_opens_the_home_folder()
    {
        (VirtualPath? folder, VirtualPath? select) = FileExplorerWindow.ParseLaunchArguments([]);

        Assert.Null(folder);
        Assert.Null(select);
    }

    [Fact]
    public void A_folder_argument_is_the_start_folder()
    {
        (VirtualPath? folder, VirtualPath? select) = FileExplorerWindow.ParseLaunchArguments(["/home/alice/Documents"]);

        Assert.Equal("/home/alice/Documents", folder?.Value);
        Assert.Null(select);
    }

    [Fact]
    public void Reveal_arguments_open_the_parent_and_select_the_item()
    {
        (VirtualPath? folder, VirtualPath? select) = FileExplorerWindow.ParseLaunchArguments(
            ["/home/alice", "--select=/home/alice/notes.txt"]);

        Assert.Equal("/home/alice", folder?.Value);
        Assert.Equal("/home/alice/notes.txt", select?.Value);
    }

    [Fact]
    public void Select_alone_opens_the_parent_of_the_item()
    {
        (VirtualPath? folder, VirtualPath? select) = FileExplorerWindow.ParseLaunchArguments(["--select=/etc/hackeros/os.conf"]);

        Assert.Equal("/etc/hackeros", folder?.Value);
        Assert.Equal("/etc/hackeros/os.conf", select?.Value);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("--select=relative.txt")]
    [InlineData("--unknown")]
    public void Malformed_arguments_are_ignored(string argument)
    {
        (VirtualPath? folder, VirtualPath? select) = FileExplorerWindow.ParseLaunchArguments([argument]);

        Assert.Null(folder);
        Assert.Null(select);
    }
}
