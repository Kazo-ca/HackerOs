using Kazo.Os.AppSdk.Net.Hosting;

namespace Kazo.Os.AppSdk.Tests.Net;

/// <summary>The <c>.site</c> format shared by kazowebd, Web Server Manager and the resolver (ADR 0077, section 1).</summary>
public sealed class KazoWebSiteFileTests
{
    private const string UserPath = "/home/alice/.config/kazoweb/sites/docs.site";

    [Fact]
    public void A_minimal_file_gets_the_defaults()
    {
        KazoWebSiteDeclaration declaration = KazoWebSiteFile.Parse(UserPath, """
            {
              // comments and trailing commas are tolerated, like a hand-edited config
              "name": "docs",
              "root": "/home/alice/www",
              "bindings": ["http://docs.hack:8080/"],
            }
            """, KazoWebSiteScope.User, "alice");

        Assert.True(declaration.IsServable, $"site={declaration.Site} errors={string.Join("; ", declaration.Errors)}");
        Assert.Equal("docs", declaration.Name);
        Assert.Equal(["index.html", "index.htm", "default.htm"], declaration.Site!.DefaultDocuments);
        Assert.False(declaration.Site.DirectoryBrowsing);
        Assert.True(declaration.Site.Enabled);
        Assert.Equal("alice", declaration.Owner);
    }

    [Fact]
    public void Serialize_round_trips_in_camel_case()
    {
        KazoWebSite site = new()
        {
            Name = "intranet",
            Root = "/var/www/html",
            Bindings = ["http://intranet.hack/"],
            DirectoryBrowsing = true,
            Enabled = false
        };

        string json = KazoWebSiteFile.Serialize(site);
        KazoWebSiteDeclaration back = KazoWebSiteFile.Parse("/etc/kazoweb/sites/intranet.site", json, KazoWebSiteScope.System);

        Assert.Contains("\"directoryBrowsing\": true", json, StringComparison.Ordinal);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
        Assert.True(back.IsValid);
        Assert.False(back.IsServable);
        Assert.Equal(site.Bindings, back.Site!.Bindings);
    }

    [Fact]
    public void Broken_json_reports_the_line()
    {
        KazoWebSiteDeclaration declaration = KazoWebSiteFile.Parse(UserPath, "{\n  \"name\": \"docs\",\n  oops\n}", KazoWebSiteScope.User);

        Assert.Null(declaration.Site);
        Assert.False(declaration.IsValid);
        Assert.StartsWith("invalid JSON (line 3)", Assert.Single(declaration.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_name_must_match_the_site_name()
    {
        KazoWebSiteDeclaration declaration = KazoWebSiteFile.Parse(UserPath, """{"name":"blog","root":"/srv/blog","bindings":["http://localhost:8081/"]}""", KazoWebSiteScope.User);

        Assert.Contains("the file of site 'blog' must be named 'blog.site'", declaration.Errors);
    }

    [Theory]
    [InlineData("http://localhost:80/")]
    [InlineData("http://intranet.hack/")]
    [InlineData("http://*:443/")]
    public void A_user_site_cannot_bind_a_privileged_port(string binding)
    {
        KazoWebSite site = new() { Name = "docs", Root = "/home/alice/www", Bindings = [binding] };

        Assert.Contains(KazoWebSiteFile.Validate(site, KazoWebSiteScope.User), error => error.Contains("is privileged", StringComparison.Ordinal));
        Assert.Empty(KazoWebSiteFile.Validate(site, KazoWebSiteScope.System));
    }

    [Theory]
    [InlineData("", "/srv/www", "http://localhost:8080/", "not a valid site name")]
    [InlineData("my site", "/srv/www", "http://localhost:8080/", "not a valid site name")]
    [InlineData("docs", "srv/www", "http://localhost:8080/", "absolute directory path")]
    [InlineData("docs", "/", "http://localhost:8080/", "cannot be a site root")]
    [InlineData("docs", "/srv/../etc", "http://localhost:8080/", "cannot be a site root")]
    [InlineData("docs", "/srv/www", "https://localhost:8443/", "https")]
    public void Validation_explains_each_problem(string name, string root, string binding, string expected)
    {
        KazoWebSite site = new() { Name = name, Root = root, Bindings = [binding] };

        Assert.Contains(KazoWebSiteFile.Validate(site, KazoWebSiteScope.System), error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_bindings_and_bad_default_documents_are_refused()
    {
        KazoWebSite site = new()
        {
            Name = "docs",
            Root = "/srv/www",
            Bindings = ["http://localhost:8080/", "http://LOCALHOST:8080/"],
            DefaultDocuments = ["index.html", "../secret"]
        };

        IReadOnlyList<string> errors = KazoWebSiteFile.Validate(site, KazoWebSiteScope.User);

        Assert.Contains(errors, error => error.Contains("listed twice", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("'../secret' is not a default document", StringComparison.Ordinal));
        Assert.Contains("needs at least one binding", Assert.Single(KazoWebSiteFile.Validate(site with { Bindings = [], DefaultDocuments = ["index.html"] }, KazoWebSiteScope.User)), StringComparison.Ordinal);
    }

    [Fact]
    public void Directories_follow_the_linux_convention()
    {
        Assert.Equal("/home/alice/.config/kazoweb/sites", KazoWebSiteFile.GetUserSitesDirectory("/home/alice/"));
        Assert.Equal("/etc/kazoweb/sites/docs.site", KazoWebSiteFile.GetPath(KazoWebSiteFile.SystemSitesDirectory, "docs"));
    }
}
