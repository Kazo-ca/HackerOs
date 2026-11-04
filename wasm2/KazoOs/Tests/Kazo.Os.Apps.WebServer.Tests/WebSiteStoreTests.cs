using System.Net;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.Apps.WebServer.Daemon;
using Kazo.Os.Apps.WebServer.Manager;

namespace Kazo.Os.Apps.WebServer.Tests;

/// <summary>The pure parts of Web Server Manager: site states, suggestions and log lines.</summary>
public sealed class WebSiteStoreTests
{
    private static KazoWebSiteDeclaration Declaration(string name, bool enabled = true, params string[] bindings) =>
        KazoWebSiteFile.Parse(
            $"/home/alice/.config/hackweb/sites/{name}.site",
            KazoWebSiteFile.Serialize(new KazoWebSite { Name = name, Root = "/home/alice/" + name, Bindings = bindings, Enabled = enabled }),
            KazoWebSiteScope.User,
            "alice");

    [Fact]
    public void State_follows_the_sockets_hackwebd_owns()
    {
        KazoWebSiteDeclaration site = Declaration("docs", true, "http://docs.hack:8080/", "http://localhost:8081/");
        HashSet<string> none = [];
        HashSet<string> one = ["http://docs.hack:8080/"];
        HashSet<string> both = ["http://docs.hack:8080/", "http://localhost:8081/"];

        Assert.Equal(WebSiteState.Down, WebSiteStore.StateOf(site, none));
        Assert.Equal(WebSiteState.Partial, WebSiteStore.StateOf(site, one));
        Assert.Equal(WebSiteState.Running, WebSiteStore.StateOf(site, both));
        Assert.Equal(WebSiteState.Stopped, WebSiteStore.StateOf(Declaration("off", false, "http://localhost:8082/"), both));
        Assert.Equal(WebSiteState.Invalid, WebSiteStore.StateOf(Declaration("low", true, "http://localhost:80/"), both));
    }

    [Fact]
    public void Suggest_picks_the_folder_name_and_the_first_free_port()
    {
        WebSiteRow taken = new("my-site", Declaration("my-site", true, "http://localhost:8080/"), null, ["http://localhost:8080/"], WebSiteState.Running);
        KazoTcpListenerInfo other = new(IPAddress.Any, 8081, 12, "python3", "alice", []);
        WebServerSnapshot snapshot = new(null, [taken], [other]);

        KazoWebSite site = WebSiteStore.Suggest("/home/alice/My Site/", snapshot);

        Assert.Equal("my-site-2", site.Name);
        Assert.Equal("/home/alice/My Site", site.Root);
        Assert.Equal(["http://localhost:8082/"], site.Bindings);
        Assert.Empty(KazoWebSiteFile.Validate(site, KazoWebSiteScope.User));
    }

    [Fact]
    public void Error_lines_use_the_nginx_layout()
    {
        DateTimeOffset at = new(2026, 10, 16, 9, 5, 7, TimeSpan.Zero);

        Assert.Equal("2026/10/16 09:05:07 [error] 42#0: docs: open() failed", KazoWebdLog.FormatError("error", "docs", "open() failed", at, 42));
        Assert.Equal("2026/10/16 09:05:07 [notice] 42#0: exiting", KazoWebdLog.FormatError("notice", null, "exiting", at, 42));
        Assert.Equal("/var/log/hackweb/docs.access.log", KazoWebdLog.AccessLogPath("docs"));
    }
}
