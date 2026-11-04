using Kazo.Os.Apps.Browser.Net;
using Kazo.Os.Simulation.Abstractions.Network;
using Xunit;

namespace Kazo.Os.Apps.Browser.Tests;

public sealed class BrowserUrlTests
{
    private const string Search = "https://hackersearch.net/search?q=";

    [Theory]
    [InlineData("https://intranet.hack/a?b=1", "https://intranet.hack/a?b=1")]
    [InlineData("intranet.hack", "https://intranet.hack/")]
    [InlineData("intranet.hack/docs", "https://intranet.hack/docs")]
    [InlineData("localhost:8080/api", "http://localhost:8080/api")]
    [InlineData("127.0.0.1", "http://127.0.0.1/")]
    [InlineData("/home/neo/site/index.html", "file:///home/neo/site/index.html")]
    [InlineData("~/my site/a.html", "file:///home/neo/my%20site/a.html")]
    [InlineData("file:///etc/hosts", "file:///etc/hosts")]
    [InlineData("how to hack", "https://hackersearch.net/search?q=how%20to%20hack")]
    [InlineData("netstat", "https://hackersearch.net/search?q=netstat")]
    [InlineData("about:blank", "about:blank")]
    public void Address_bar_text_becomes_a_url(string input, string expected)
    {
        AddressTarget? target = BrowserUrl.FromAddressBar(input, "/home/neo", Search);

        Assert.NotNull(target);
        Assert.Equal(expected, target.Url.AbsoluteUri);
        Assert.False(target.ViewSource);
    }

    [Fact]
    public void View_source_prefix_is_recognized_and_empty_input_ignored()
    {
        AddressTarget? target = BrowserUrl.FromAddressBar("view-source:intranet.hack", "/home/neo", Search);

        Assert.True(target!.ViewSource);
        Assert.Equal("https://intranet.hack/", target.Url.AbsoluteUri);
        Assert.Null(BrowserUrl.FromAddressBar("   ", "/home/neo", Search));
        Assert.Null(BrowserUrl.FromAddressBar("view-source:", "/home/neo", Search));
    }

    [Theory]
    [InlineData("img/a.png", "https://site.hack/dir/img/a.png")]
    [InlineData("/root.css", "https://site.hack/root.css")]
    [InlineData("//cdn.hack/x.js", "https://cdn.hack/x.js")]
    [InlineData("../up.png", "https://site.hack/up.png")]
    [InlineData("#top", null)]
    [InlineData("data:image/png;base64,AA==", null)]
    [InlineData("javascript:void(0)", null)]
    [InlineData("mailto:neo@hack", null)]
    [InlineData("  ", null)]
    public void Page_references_resolve_against_the_base(string reference, string? expected)
    {
        Uri? resolved = BrowserUrl.Resolve(new Uri("https://site.hack/dir/page.html"), reference);

        Assert.Equal(expected, resolved?.AbsoluteUri);
    }

    [Fact]
    public void Origins_storage_keys_and_display_follow_browser_rules()
    {
        Uri page = new("https://Site.hack/a");
        Uri port = new("http://localhost:8080/x");
        Uri file = new("file:///home/neo/my%20site/index.html");

        Assert.Equal("https://site.hack", BrowserUrl.GetOrigin(page));
        Assert.Equal("http://localhost:8080", BrowserUrl.GetOrigin(port));
        Assert.Equal("null", BrowserUrl.GetOrigin(file));
        Assert.True(BrowserUrl.IsSameOrigin(page, new Uri("https://site.hack/other?q=1")));
        Assert.False(BrowserUrl.IsSameOrigin(page, new Uri("http://site.hack/a")));
        Assert.False(BrowserUrl.IsSameOrigin(file, file));

        Assert.Equal("https_site.hack", BrowserUrl.GetStorageKey(page));
        Assert.Equal("http_localhost_8080", BrowserUrl.GetStorageKey(port));
        Assert.Equal("file", BrowserUrl.GetStorageKey(file));

        Assert.Equal("file:///home/neo/my site/index.html", BrowserUrl.ToDisplayString(file));
        Assert.Equal("view-source:https://site.hack/a", BrowserUrl.ToDisplayString(page, viewSource: true));
        Assert.Equal("/home/neo/my site/index.html", BrowserUrl.ToLocalPath(file));
        Assert.Equal("/", BrowserUrl.ToLocalPath(new Uri("file:///")));
    }

    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("/srv/www/STYLE.CSS", "text/css; charset=utf-8")]
    [InlineData("app.mjs", "text/javascript; charset=utf-8")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("README", "application/octet-stream")]
    [InlineData("/home/neo/.bashrc", "application/octet-stream")]
    [InlineData("archive.unknown", "application/octet-stream")]
    public void File_types_come_from_the_extension(string fileName, string expected) =>
        Assert.Equal(expected, SimulatedFileTypes.GetContentType(fileName));
}
