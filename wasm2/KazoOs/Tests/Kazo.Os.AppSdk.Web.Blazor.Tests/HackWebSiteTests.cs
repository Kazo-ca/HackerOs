using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Web.Blazor.Sites;

namespace Kazo.Os.AppSdk.Web.Blazor.Tests;

/// <summary>The site services of a tab (ADR 0084 §4 and §6): title and HTTP client.</summary>
public sealed class HackWebSiteTests
{
    [Fact]
    public void The_title_starts_as_the_site_name_and_changes_once_per_value()
    {
        HackWebSite site = CreateSite();
        List<string> titles = [];
        site.TitleChanged += titles.Add;

        site.SetTitle("Notes");
        site.SetTitle(" Notes ");
        site.SetTitle(null);

        Assert.Equal(["Notes", "Intranet"], titles);
        Assert.Equal("Intranet", site.Title);
    }

    [Fact]
    public void The_http_client_is_on_the_site_address_with_the_tab_cookies()
    {
        HackCookieContainer cookies = new();
        HackWebSite site = CreateSite(cookies);

        Assert.Same(cookies, site.Http.Cookies);
        Assert.Equal(new Uri("http://intranet.hack/"), site.Http.BaseAddress);
        Assert.Equal("org.test.intranet", site.AppId);
    }

    private static HackWebSite CreateSite(HackCookieContainer? cookies = null)
    {
        HackSiteNavigationManager navigation = new("http://intranet.hack/", "http://intranet.hack/");
        HackHttpClient http = new(cookies ?? new HackCookieContainer()) { BaseAddress = new Uri("http://intranet.hack/") };
        return new HackWebSite(navigation, http, "org.test.intranet", "Intranet");
    }
}
