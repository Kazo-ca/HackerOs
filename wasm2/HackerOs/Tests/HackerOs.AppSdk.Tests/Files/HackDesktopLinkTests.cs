using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Tests.IO;
using HackerOs.Tests.Support;

namespace HackerOs.AppSdk.Tests.Files;

/// <summary>The shortcut to a file or a folder of ADR 0185 §13 (<c>Type=Link</c>).</summary>
public sealed class HackDesktopLinkTests
{
    [Fact]
    public void Format_writes_a_link_with_an_encoded_file_url_that_parses_back()
    {
        HackDesktopLink link = new("My report", "/home/alice/Mes documents/été #1.txt");

        string text = link.Format();

        Assert.Contains("Type=Link\n", text);
        Assert.Contains("URL=file:///home/alice/Mes%20documents/%C3%A9t%C3%A9%20%231.txt\n", text);
        Assert.True(HackDesktopLink.TryParse(text, out HackDesktopLink? read));
        Assert.Equal(link, read);
    }

    [Theory]
    [InlineData("[Desktop Entry]\nType=Application\nName=X\nExec=org.hackeros.calculator\n")]
    [InlineData("[Desktop Entry]\nType=Link\nName=X\nURL=https://example.com/\n")]
    [InlineData("[Desktop Entry]\nType=Link\nName=X\nURL=file://server/share\n")]
    [InlineData("[Desktop Entry]\nType=Link\nURL=file:///tmp\n")]
    [InlineData("[Other]\nType=Link\nName=X\nURL=file:///tmp\n")]
    [InlineData("")]
    public void TryParse_refuses_what_is_not_a_local_link(string content)
    {
        Assert.False(HackDesktopLink.TryParse(content, out _));
    }

    [Fact]
    public void An_app_shortcut_is_not_a_link_and_a_link_is_not_an_app_shortcut()
    {
        string link = new HackDesktopLink("Docs", "/home/alice/Documents").Format();

        Assert.False(HackDesktopEntry.TryParse(link, out _));
        Assert.True(HackDesktopLink.TryParse("[Desktop Entry]\nType=Link\nName=Docs\nURL=file://localhost/home/alice/Documents\nIcon=folder\n", out HackDesktopLink? read));
        Assert.Equal(new HackDesktopLink("Docs", "/home/alice/Documents", "folder"), read);
    }

    [Fact]
    public async Task CreateLinkAsync_names_the_file_after_the_target_and_numbers_taken_names()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory("/home/alice/Desktop");

        string first = await HackDesktopLink.CreateLinkAsync("/home/alice/Desktop", "/home/alice/Documents/");
        string second = await HackDesktopLink.CreateLinkAsync("/home/alice/Desktop", "/home/alice/Documents");

        Assert.Equal("/home/alice/Desktop/Documents.desktop", first);
        Assert.Equal("/home/alice/Desktop/Documents (2).desktop", second);
        Assert.True(HackDesktopLink.TryParse(await HackFile.ReadAllTextAsync(first), out HackDesktopLink? link));
        Assert.Equal(new HackDesktopLink("Documents", "/home/alice/Documents"), link);
        await Assert.ThrowsAsync<ArgumentException>(() => HackDesktopLink.CreateLinkAsync("/home/alice/Desktop", "relative/path"));
    }

    [Fact]
    public void ToUrl_refuses_a_relative_path()
    {
        Assert.Throws<ArgumentException>(() => HackDesktopLink.ToUrl("a/b"));
        Assert.Equal("file:///", HackDesktopLink.ToUrl("/"));
        Assert.Equal("/", HackDesktopLink.PathFromUrl("file:///"));
    }
}
