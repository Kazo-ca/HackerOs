using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.AppSdk.Web.Generated;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Mvc;

namespace Kazo.Os.AppSdk.Web.Tests.Hosting;

/// <summary>
/// What a site with a Blazor root answers besides its own routes (ADR 0084 §2): the host page, the
/// loader script and the boot manifest, after every MVC route.
/// </summary>
public sealed class BlazorSiteEndpointsTests : IAsyncLifetime
{
    private const string Site = "http://blazor.hack";
    private KazoWebApplication _app = null!;

    public Task InitializeAsync()
    {
        _app = KazoWebHost.CreateApplication(new BlazorTestSite(), KazoWebGeneratedRoutes.Table);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private Task<KazoWebResponseData> Get(string path) => _app.ProcessAsync(KazoWebRequestData.Create("GET", Site + path));

    [Theory]
    [InlineData("/")]
    [InlineData("/notes/5")]
    [InlineData("/deep/path/")]
    public async Task Paths_without_extension_get_the_host_page(string path)
    {
        KazoWebResponseData response = await Get(path);

        Assert.Equal(200, response.StatusCode);
        Assert.StartsWith("text/html", response.ContentType, StringComparison.Ordinal);
        string html = response.ReadAsString();
        Assert.Contains("<div id=\"app\">", html, StringComparison.Ordinal);
        Assert.Contains("<script src=\"_framework/blazor.hack.js\">", html, StringComparison.Ordinal);
        Assert.Contains("<title>Blazor test site</title>", html, StringComparison.Ordinal);
        Assert.True(KazoBlazorSiteEndpoints.IsHostPage(html));
    }

    [Fact]
    public async Task The_site_routes_win_over_the_host_page()
    {
        KazoWebResponseData api = await Get("/api/ping");
        KazoWebResponseData head = await _app.ProcessAsync(KazoWebRequestData.Create("HEAD", Site + "/"));
        KazoWebResponseData post = await _app.ProcessAsync(KazoWebRequestData.Create("POST", Site + "/notes/5"));

        Assert.Equal("pong", api.ReadAsString());
        Assert.Equal(200, head.StatusCode);
        // The host page is a GET/HEAD fallback: a POST to a page path is not routed.
        Assert.NotEqual(200, post.StatusCode);
    }

    [Fact]
    public async Task A_path_with_an_extension_is_a_missing_file()
    {
        KazoWebResponseData response = await Get("/css/site.css");

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task The_boot_manifest_names_the_app_and_its_root_component()
    {
        KazoWebResponseData response = await Get("/_framework/blazor.boot.json");

        Assert.Equal(200, response.StatusCode);
        Assert.StartsWith("application/json", response.ContentType, StringComparison.Ordinal);
        KazoBlazorBootManifest? boot = JsonSerializer.Deserialize(response.Body, KazoBlazorBootJsonContext.Default.KazoBlazorBootManifest);
        Assert.NotNull(boot);
        Assert.Equal("org.test.blazor", boot.AppId);
        Assert.Equal("Blazor test site", boot.AppName);
        Assert.Equal(typeof(BlazorTestSite).FullName, boot.RootComponent);
        Assert.Equal(typeof(BlazorTestSite).Assembly.GetName().Name + ".dll", boot.EntryAssembly);
    }

    [Fact]
    public async Task The_loader_script_is_served_as_javascript()
    {
        KazoWebResponseData response = await Get("/_framework/blazor.hack.js");

        Assert.Equal(200, response.StatusCode);
        Assert.StartsWith("text/javascript", response.ContentType, StringComparison.Ordinal);
        Assert.Contains("blazor.boot.json", response.ReadAsString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_site_without_root_has_no_host_page()
    {
        await using KazoWebApplication mvcOnly = KazoWebHost.CreateApplication(new Site.TestSite(), KazoWebGeneratedRoutes.Table);

        KazoWebResponseData response = await mvcOnly.ProcessAsync(KazoWebRequestData.Create("GET", "http://intranet.hack/no-such-page"));

        Assert.Equal(404, response.StatusCode);
        Assert.False(KazoBlazorSiteEndpoints.IsHostPage("<html><body>plain</body></html>"));
    }
}

/// <summary>A site with a Blazor root (any type stands for the component here) and one API route.</summary>
[AppManifest]
public sealed partial class BlazorTestSite : WebAppDefinition
{
    public override string Id => "org.test.blazor";

    public override string Name => "Blazor test site";

    public override IReadOnlyList<string> Urls => ["http://blazor.hack/"];

    public override Type? BlazorRoot => typeof(BlazorTestSite);

    public override void Configure(KazoWebApplicationBuilder builder)
    {
        KazoWebApplication app = builder.Build();
        app.MapGet("/api/ping", () => KazoResults.Text("pong"));
    }
}
