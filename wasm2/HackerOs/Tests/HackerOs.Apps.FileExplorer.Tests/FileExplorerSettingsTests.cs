using Xunit;

namespace HackerOs.Apps.FileExplorer.Tests;

/// <summary>The Places panel's width, saved in <c>~/.config/hackeros/file-explorer.json</c> (ADR 0185 §1).</summary>
public sealed class FileExplorerSettingsTests
{
    [Theory]
    [InlineData(null, FileExplorerSettings.DefaultSidebarWidth)]
    [InlineData("", FileExplorerSettings.DefaultSidebarWidth)]
    [InlineData("not json", FileExplorerSettings.DefaultSidebarWidth)]
    [InlineData("[1,2]", FileExplorerSettings.DefaultSidebarWidth)]
    [InlineData("{\"sidebarWidth\":\"wide\"}", FileExplorerSettings.DefaultSidebarWidth)]
    [InlineData("{\"sidebarWidth\":220}", 220)]
    [InlineData("{\"sidebarWidth\":20}", FileExplorerSettings.MinSidebarWidth)]
    [InlineData("{\"sidebarWidth\":9000}", FileExplorerSettings.MaxSidebarWidth)]
    public void ParseSidebarWidth_reads_a_clamped_width_or_the_default(string? content, int expected) =>
        Assert.Equal(expected, FileExplorerSettings.ParseSidebarWidth(content));

    [Fact]
    public void WithSidebarWidth_keeps_the_other_keys_and_round_trips()
    {
        string written = FileExplorerSettings.WithSidebarWidth("{\"other\":true,\"sidebarWidth\":150}", 260);

        Assert.Contains("\"other\": true", written, StringComparison.Ordinal);
        Assert.Equal(260, FileExplorerSettings.ParseSidebarWidth(written));
        Assert.Equal(FileExplorerSettings.MaxSidebarWidth, FileExplorerSettings.ParseSidebarWidth(FileExplorerSettings.WithSidebarWidth("broken", 1000)));
    }

    [Fact]
    public void GetPath_is_in_the_users_hackeros_config_folder() =>
        Assert.Equal("/home/ada/.config/hackeros/file-explorer.json", FileExplorerSettings.GetPath("/home/ada/"));

    [Theory]
    [InlineData(200, "ArrowLeft", 184)]
    [InlineData(200, "ArrowRight", 216)]
    [InlineData(145, "ArrowLeft", FileExplorerSettings.MinSidebarWidth)]
    [InlineData(200, "Home", FileExplorerSettings.MinSidebarWidth)]
    [InlineData(200, "End", FileExplorerSettings.MaxSidebarWidth)]
    public void Arrow_keys_on_the_handle_move_the_edge(int width, string key, int expected) =>
        Assert.Equal(expected, ExplorerSidebarResizer.WidthAfterKey(width, key));

    [Fact]
    public void Other_keys_leave_the_width_alone() => Assert.Null(ExplorerSidebarResizer.WidthAfterKey(200, "a"));
}
