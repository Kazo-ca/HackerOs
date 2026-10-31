using System.Globalization;
using HackerOs.Apps.Browser.Bridge;
using HackerOs.Apps.Browser.DevTools;
using HackerOs.Apps.Browser.Net;
using HackerOs.Apps.Browser.Rendering;
using HackerOs.AppSdk.Net;
using Xunit;

namespace HackerOs.Apps.Browser.Tests;

/// <summary>ADR 0210 in the browser: the address bar's source icon and warnings, and the relay's error pages.</summary>
public sealed class BrowserRealSitesTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    [Fact]
    public void A_real_site_shows_the_proxy_icon_and_no_warning_over_https()
    {
        AddressBarIndicator indicator = AddressBarIndicators.For(new Uri("https://example.com/"), BrowserResponseSource.RealNetwork);

        Assert.Equal("⇄", indicator.SourceIcon);
        Assert.Equal("source-real", indicator.SourceClass);
        Assert.Equal("Real site, through the HackerOS server's proxy", indicator.SourceTitle);
        Assert.Null(indicator.NotSecureLabel);
    }

    [Fact]
    public void An_http_page_says_not_secure_next_to_its_icon()
    {
        AddressBarIndicator indicator = AddressBarIndicators.For(new Uri("http://example.com/"), BrowserResponseSource.RealNetwork, French);

        Assert.Equal("Vrai site, par le proxy du serveur HackerOS", indicator.SourceTitle);
        Assert.Equal("Non sécurisé", indicator.NotSecureLabel);
    }

    [Theory]
    [InlineData(BrowserResponseSource.Simulated, "source-simulated")]
    [InlineData(BrowserResponseSource.LocalSite, "source-local")]
    public void Every_network_source_has_its_own_icon(BrowserResponseSource source, string cssClass)
    {
        AddressBarIndicator indicator = AddressBarIndicators.For(new Uri("https://intranet.hack/"), source);

        Assert.NotEmpty(indicator.SourceIcon);
        Assert.Equal(cssClass, indicator.SourceClass);
        Assert.NotEmpty(indicator.SourceTitle);
    }

    [Fact]
    public void A_file_or_an_error_page_shows_no_source_icon()
    {
        Assert.Empty(AddressBarIndicators.For(new Uri("file:///home/user/a.html"), BrowserResponseSource.File).SourceIcon);
        Assert.Empty(AddressBarIndicators.For(new Uri("https://example.com/"), null).SourceIcon);
    }

    [Fact]
    public void A_relay_refusal_shows_the_blocked_by_administrator_page()
    {
        BrowserErrorView view = BrowserErrorPages.ForNetworkError("ERR_BLOCKED_BY_ADMINISTRATOR", new Uri("https://example.com:8443/"), French);

        Assert.Equal("ERR_BLOCKED_BY_ADMINISTRATOR", view.Code);
        Assert.Equal("Votre administrateur a bloqué example.com.", view.Message);
        Assert.Contains(view.Hints, hint => hint.Contains("HackerOs:Proxy", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_host_suggests_connecting_a_server_for_real_sites()
    {
        BrowserErrorView view = BrowserErrorPages.ForNetworkError("ERR_NAME_NOT_RESOLVED", new Uri("https://example.com/"));

        Assert.Contains(view.Hints, hint => hint.Contains("HackerOS server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_script_fetch_of_a_real_page_goes_through_the_browser_transport_not_the_frame()
    {
        // The frame's CSP is connect-src 'none': a page's fetch can only reach the network through
        // the bridge, so a real site's scripts follow the same relay path as the page itself.
        Assert.Contains("connect-src 'none'", HtmlPageComposer.ContentSecurityPolicy, StringComparison.Ordinal);
        FakeBrowserTransport transport = new FakeBrowserTransport().Serve("https://example.com/api", "application/json", "{}");
        DevToolsLog log = new();
        BrowserResourceLoader loader = new(transport, log);
        PageBridgeHandler handler = new(loader, new HackCookieContainer());
        PageSession session = new(new Uri("https://example.com/"), "t", loader, CancellationToken.None);

        BridgeFetchReply reply = await handler.FetchAsync(session, new BridgeFetchRequest("GET", "https://example.com/api", [], null, "fetch", "same-origin", "cors"));

        Assert.Equal(200, reply.Status);
        Assert.Equal(1, transport.CountRequests("https://example.com/api"));
    }

    [Fact]
    public void Domain_cookies_of_a_real_site_are_readable_by_its_subdomains_only()
    {
        HackCookieContainer cookies = new();
        cookies.Add(new HackCookie(".example.com", "sid", "1"));
        cookies.Add(new HackCookie("www.example.com", "own", "2"));

        Assert.Equal(2, cookies.GetCookies(new Uri("https://www.example.com/")).Count);
        Assert.Single(cookies.GetCookies(new Uri("https://example.com/")));
        Assert.Empty(cookies.GetCookies(new Uri("https://notexample.com/")));
    }
}
