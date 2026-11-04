using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Web.Blazor.Sites;

namespace Kazo.Os.AppSdk.Web.Blazor.Tests;

/// <summary>The site services of a tab (ADR 0084 §4 and §6): title and HTTP client.</summary>
public sealed class KazoWebSiteTests
{
    [Fact]
    public void The_title_starts_as_the_site_name_and_changes_once_per_value()
    {
        KazoWebSite site = CreateSite();
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
        KazoCookieContainer cookies = new();
        KazoWebSite site = CreateSite(cookies);

        Assert.Same(cookies, site.Http.Cookies);
        Assert.Equal(new Uri("http://intranet.hack/"), site.Http.BaseAddress);
        Assert.Equal("org.test.intranet", site.AppId);
    }

    private static KazoWebSite CreateSite(KazoCookieContainer? cookies = null)
    {
        KazoSiteNavigationManager navigation = new("http://intranet.hack/", "http://intranet.hack/");
        KazoHttpClient http = new(cookies ?? new KazoCookieContainer()) { BaseAddress = new Uri("http://intranet.hack/") };
        return new KazoWebSite(navigation, http, "org.test.intranet", "Intranet");
    }
}
