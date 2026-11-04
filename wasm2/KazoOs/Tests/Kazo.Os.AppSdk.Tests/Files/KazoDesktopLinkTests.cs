using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Tests.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.Files;

/// <summary>The shortcut to a file or a folder of ADR 0185 §13 (<c>Type=Link</c>).</summary>
public sealed class KazoDesktopLinkTests
{
    [Fact]
    public void Format_writes_a_link_with_an_encoded_file_url_that_parses_back()
    {
        KazoDesktopLink link = new("My report", "/home/alice/Mes documents/été #1.txt");

        string text = link.Format();

        Assert.Contains("Type=Link\n", text);
        Assert.Contains("URL=file:///home/alice/Mes%20documents/%C3%A9t%C3%A9%20%231.txt\n", text);
        Assert.True(KazoDesktopLink.TryParse(text, out KazoDesktopLink? read));
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
        Assert.False(KazoDesktopLink.TryParse(content, out _));
    }

    [Fact]
    public void An_app_shortcut_is_not_a_link_and_a_link_is_not_an_app_shortcut()
    {
        string link = new KazoDesktopLink("Docs", "/home/alice/Documents").Format();

        Assert.False(KazoDesktopEntry.TryParse(link, out _));
        Assert.True(KazoDesktopLink.TryParse("[Desktop Entry]\nType=Link\nName=Docs\nURL=file://localhost/home/alice/Documents\nIcon=folder\n", out KazoDesktopLink? read));
        Assert.Equal(new KazoDesktopLink("Docs", "/home/alice/Documents", "folder"), read);
    }

    [Fact]
    public async Task CreateLinkAsync_names_the_file_after_the_target_and_numbers_taken_names()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory("/home/alice/Desktop");

        string first = await KazoDesktopLink.CreateLinkAsync("/home/alice/Desktop", "/home/alice/Documents/");
        string second = await KazoDesktopLink.CreateLinkAsync("/home/alice/Desktop", "/home/alice/Documents");

        Assert.Equal("/home/alice/Desktop/Documents.desktop", first);
        Assert.Equal("/home/alice/Desktop/Documents (2).desktop", second);
        Assert.True(KazoDesktopLink.TryParse(await KazoFile.ReadAllTextAsync(first), out KazoDesktopLink? link));
        Assert.Equal(new KazoDesktopLink("Documents", "/home/alice/Documents"), link);
        await Assert.ThrowsAsync<ArgumentException>(() => KazoDesktopLink.CreateLinkAsync("/home/alice/Desktop", "relative/path"));
    }

    [Fact]
    public void ToUrl_refuses_a_relative_path()
    {
        Assert.Throws<ArgumentException>(() => KazoDesktopLink.ToUrl("a/b"));
        Assert.Equal("file:///", KazoDesktopLink.ToUrl("/"));
        Assert.Equal("/", KazoDesktopLink.PathFromUrl("file:///"));
    }
}
