using System.Text;
using AngleSharp.Html.Parser;
using Kazo.Os.Apps.Browser.Bridge;
using Kazo.Os.Apps.Browser.DevTools;
using Kazo.Os.Apps.Browser.Net;
using Kazo.Os.Apps.Browser.Rendering;
using Xunit;

namespace Kazo.Os.Apps.Browser.Tests;

public sealed class HtmlPageComposerTests
{
    private static readonly Uri Page = new("https://intranet.hack/docs/index.html");

    private static PageBootstrap Bootstrap(string token = "t0k3n") =>
        new(token, Page.AbsoluteUri, string.Empty, "sid=1", new Dictionary<string, string>(), new Dictionary<string, string>(), 1024, false);

    private static async Task<(ComposedPage Page, DevToolsLog Log)> ComposeAsync(FakeBrowserTransport transport, string html, Uri? url = null)
    {
        DevToolsLog log = new();
        BrowserResourceLoader loader = new(transport, log);
        PageSession session = new(url ?? Page, "t0k3n", loader, CancellationToken.None);
        ComposedPage page = await new HtmlPageComposer(loader).ComposeAsync(html, session, Bootstrap());
        return (page, log);
    }

    private static AngleSharp.Html.Dom.IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    private static string DecodeDataUrl(string dataUrl)
    {
        string base64 = dataUrl[(dataUrl.IndexOf(";base64,", StringComparison.Ordinal) + 8)..];
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    [Fact]
    public async Task Head_starts_with_charset_base_policy_and_bridge_before_any_page_content()
    {
        FakeBrowserTransport transport = new();
        (ComposedPage page, _) = await ComposeAsync(transport, "<html><head><title>Docs</title><script>var a = 1;</script></head><body><p>Hi</p></body></html>");

        var document = Parse(page.Html);
        var head = document.Head!.Children.ToList();
        Assert.Equal("meta", head[0].LocalName);
        Assert.Equal("base", head[1].LocalName);
        Assert.Equal(Page.AbsoluteUri, head[1].GetAttribute("href"));
        Assert.Equal("Content-Security-Policy", head[2].GetAttribute("http-equiv"));
        Assert.Contains("connect-src 'none'", head[2].GetAttribute("content"));
        Assert.True(head[3].HasAttribute("data-kazoos-bridge"));
        Assert.Contains("\"token\":\"t0k3n\"", head[3].TextContent);
        Assert.Equal("Docs", page.Title);
        Assert.Equal("Hi", document.QuerySelector("p")!.TextContent);
    }

    [Fact]
    public async Task Stylesheets_scripts_and_images_are_loaded_through_the_network_and_inlined()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        FakeBrowserTransport transport = new FakeBrowserTransport()
            .Serve("https://intranet.hack/docs/site.css", "text/css", "@import 'theme.css'; body { background: url(img/bg.png); }")
            .Serve("https://intranet.hack/docs/theme.css", "text/css", "h1 { color: red; }")
            .Serve("https://intranet.hack/docs/img/bg.png", "image/png", png)
            .Serve("https://intranet.hack/js/app.js", "text/javascript", "console.log('app');")
            .Serve("https://intranet.hack/logo.png", "image/png", png);

        (ComposedPage page, DevToolsLog log) = await ComposeAsync(transport,
            "<html><head><link rel=stylesheet href=site.css media=screen><script src=/js/app.js defer></script></head>"
            + "<body><img src=/logo.png alt=logo><div style=\"background:url('/logo.png')\"></div></body></html>");

        var document = Parse(page.Html);
        Assert.Empty(document.QuerySelectorAll("link[rel=stylesheet]"));
        var style = document.QuerySelector("style[data-href]")!;
        Assert.Equal("screen", style.GetAttribute("media"));
        Assert.Contains("url(\"data:image/png;base64,", style.TextContent);
        Assert.Contains("@import url(\"data:text/css", style.TextContent);

        var script = document.QuerySelector("script[data-src]")!;
        Assert.Equal("https://intranet.hack/js/app.js", script.GetAttribute("data-src"));
        Assert.Equal("console.log('app');", DecodeDataUrl(script.GetAttribute("src")!));
        Assert.True(script.HasAttribute("defer"));

        Assert.StartsWith("data:image/png;base64,", document.QuerySelector("img")!.GetAttribute("src"));
        Assert.Contains("data:image/png;base64,", document.QuerySelector("div")!.GetAttribute("style"));

        // The logo is named twice but requested once, like a browser's memory cache.
        Assert.Equal(1, transport.CountRequests("https://intranet.hack/logo.png"));
        Assert.Equal(5, page.SubresourceCount);
        Assert.Contains(log.Network, entry => entry.Kind == NetworkRequestKind.Stylesheet && entry.Url.EndsWith("theme.css", StringComparison.Ordinal));
        Assert.Contains(log.Network, entry => entry.Kind == NetworkRequestKind.Script && entry.StatusCode == 200);
    }

    [Fact]
    public async Task A_missing_image_becomes_an_empty_data_url_and_a_console_error()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport().Status("https://intranet.hack/docs/missing.png", 404, "Not Found");

        (ComposedPage page, DevToolsLog log) = await ComposeAsync(transport, "<img src=missing.png><img src=https://nowhere.hack/x.png>");

        var images = Parse(page.Html).QuerySelectorAll("img");
        Assert.All(images, image => Assert.Equal(DataUrl.Empty, image.GetAttribute("src")));
        Assert.Contains(log.Console, entry => entry.Message.Contains("status of 404", StringComparison.Ordinal));
        Assert.Contains(log.Console, entry => entry.Message == "Failed to load resource: net::ERR_NAME_NOT_RESOLVED");
        Assert.Contains(log.Network, entry => entry.Error == "ERR_NAME_NOT_RESOLVED");
    }

    [Fact]
    public async Task The_page_base_element_decides_relative_urls_and_is_replaced()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport().Serve("https://cdn.hack/assets/a.js", "text/javascript", "1");

        (ComposedPage page, _) = await ComposeAsync(transport, "<head><base href=https://cdn.hack/assets/><script src=a.js></script></head>");

        var bases = Parse(page.Html).QuerySelectorAll("base");
        Assert.Single(bases);
        Assert.Equal("https://cdn.hack/assets/", bases[0].GetAttribute("href"));
        Assert.Equal(new Uri("https://cdn.hack/assets/"), page.BaseUrl);
        Assert.Equal(1, transport.CountRequests("https://cdn.hack/assets/a.js"));
    }

    [Fact]
    public async Task A_web_page_may_not_load_a_local_file()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport().Serve("file:///etc/secret.png", "image/png", [1]);

        (ComposedPage page, DevToolsLog log) = await ComposeAsync(transport, "<img src=file:///etc/secret.png>");

        Assert.Equal(DataUrl.Empty, Parse(page.Html).QuerySelector("img")!.GetAttribute("src"));
        Assert.Equal(0, transport.CountRequests("file:///etc/secret.png"));
        Assert.Contains(log.Console, entry => entry.Message.StartsWith("Not allowed to load local resource", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_page_loads_its_relative_files()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport().Serve("file:///home/neo/site/style.css", "text/css", "p{}");

        (ComposedPage page, _) = await ComposeAsync(transport, "<link rel=stylesheet href=style.css>", new Uri("file:///home/neo/site/index.html"));

        Assert.Equal("p{}", Parse(page.Html).QuerySelector("style")!.TextContent);
    }

    [Fact]
    public async Task Meta_refresh_is_handed_to_the_bridge()
    {
        (ComposedPage page, _) = await ComposeAsync(new FakeBrowserTransport(), "<meta http-equiv=refresh content=\"3; url=/next\">");

        var document = Parse(page.Html);
        Assert.Null(document.QuerySelector("meta[http-equiv=refresh]"));
        Assert.Contains("\"refresh\":{\"seconds\":3,\"url\":\"https://intranet.hack/next\"}", document.QuerySelector("script[data-kazoos-bridge]")!.TextContent);
    }

    [Fact]
    public async Task Nested_frames_and_preloads_are_neutralized()
    {
        (ComposedPage page, DevToolsLog log) = await ComposeAsync(new FakeBrowserTransport(),
            "<link rel=preload href=a.js as=script><iframe src=https://other.hack/></iframe>");

        var document = Parse(page.Html);
        Assert.Null(document.QuerySelector("link[rel=preload]"));
        Assert.Equal("about:blank", document.QuerySelector("iframe")!.GetAttribute("src"));
        Assert.Contains(log.Console, entry => entry.Level == ConsoleLevel.Warning && entry.Message.Contains("<iframe>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Srcset_candidates_are_inlined_with_their_descriptors()
    {
        FakeBrowserTransport transport = new FakeBrowserTransport()
            .Serve("https://intranet.hack/docs/a.png", "image/png", [1])
            .Serve("https://intranet.hack/docs/b.png", "image/png", [2]);

        (ComposedPage page, _) = await ComposeAsync(transport, "<img srcset=\"a.png 1x, b.png 2x\">");

        string srcset = Parse(page.Html).QuerySelector("img")!.GetAttribute("srcset")!;
        Assert.Matches(@"^data:image/png;base64,\S+ 1x, data:image/png;base64,\S+ 2x$", srcset);
    }

    [Fact]
    public void The_bridge_script_is_embedded_as_a_function_expression()
    {
        string source = PageBridgeScript.Source;

        Assert.Contains("function (boot)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("</script", source, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("}", source.TrimEnd(), StringComparison.Ordinal);
    }
}
