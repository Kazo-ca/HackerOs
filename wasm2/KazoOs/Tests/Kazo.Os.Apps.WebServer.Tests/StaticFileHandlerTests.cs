using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.Apps.WebServer.Daemon;

namespace Kazo.Os.Apps.WebServer.Tests;

/// <summary>URL to file mapping of hackwebd: never outside the root, whatever the URL says.</summary>
public sealed class StaticFileHandlerTests
{
    private static ServedSite Site(string binding = "http://docs.hack:8080/", string root = "/home/alice/www")
    {
        Assert.True(WebAppUrls.TryParse(binding, allowWildcardHost: true, out WebBinding parsed, out _));
        KazoWebSite site = new() { Name = "docs", Root = root, Bindings = [binding] };
        return new ServedSite(new KazoWebSiteDeclaration("/home/alice/.config/hackweb/sites/docs.site", KazoWebSiteScope.User, "alice", site, []), parsed);
    }

    [Theory]
    [InlineData("http://docs.hack:8080/", "/home/alice/www")]
    [InlineData("http://docs.hack:8080/css/site.css", "/home/alice/www/css/site.css")]
    [InlineData("http://docs.hack:8080/a%20b.html", "/home/alice/www/a b.html")]
    [InlineData("http://docs.hack:8080/./css//site.css", "/home/alice/www/css/site.css")]
    public void Urls_map_under_the_root(string url, string expected)
    {
        Assert.True(StaticFileHandler.TryMapPath(Site(), new Uri(url), out string? path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("http://docs.hack:8080/%2e%2e/%2e%2e/etc/passwd", "/home/alice/www/etc/passwd")]
    [InlineData("http://docs.hack:8080/css/%2E%2E/%2E%2E/secret", "/home/alice/www/secret")]
    public void Encoded_dot_segments_are_resolved_inside_the_root(string url, string expected)
    {
        // The URL parser removes dot segments (RFC 3986 section 5.2.4) before the server sees the path,
        // as every HTTP stack does: "/../.." can climb to the site root, never above it.
        Assert.True(StaticFileHandler.TryMapPath(Site(), new Uri(url), out string? path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("http://docs.hack:8080/a%5c..%5csecret")]
    [InlineData("http://docs.hack:8080/a%00.html")]
    public void Traversal_and_odd_characters_are_refused(string url)
    {
        Assert.False(StaticFileHandler.TryMapPath(Site(), new Uri(url), out _));
    }

    [Fact]
    public void A_binding_path_is_the_base_of_the_site()
    {
        ServedSite site = Site("http://localhost:8080/docs/", "/srv/docs");

        Assert.True(StaticFileHandler.TryMapPath(site, new Uri("http://localhost:8080/docs/guide/index.html"), out string? path));
        Assert.Equal("/srv/docs/guide/index.html", path);
    }

    [Fact]
    public void Status_pages_look_like_nginx()
    {
        string page = StaticFileHandler.StatusPage(404, "Not Found");

        Assert.Contains("<title>404 Not Found</title>", page, StringComparison.Ordinal);
        Assert.Contains("<hr><center>hackwebd/1.0</center>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_system_site_is_served_as_www_data_and_a_user_site_as_its_owner()
    {
        ServedSite user = Site();
        ServedSite system = user with { Declaration = user.Declaration with { Scope = KazoWebSiteScope.System, Owner = null } };

        Assert.False(user.ServesAsWwwData);
        Assert.True(system.ServesAsWwwData);
    }
}
