using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Web.Hosting;
using HackerOs.AppSdk.Web.Mvc;

namespace HackerOs.Template.Site;

/// <summary>
/// The site's manifest and start-up. The OS starts it on the first request to one of its
/// <see cref="Urls"/>, opened in the KazoOS browser. The build generates <c>manifest.json</c> and
/// <c>TemplateSiteWebHost</c>, the entry point that serves the site.
/// </summary>
[AppManifest]
public sealed partial class TemplateSite : WebAppDefinition
{
    /// <inheritdoc />
    public override string Id => "com.example.template-app";

    /// <inheritdoc />
    public override string Name => "HackerOs.Template.Site";

    /// <inheritdoc />
    public override string PublisherId => "pub.template";

    /// <inheritdoc />
    public override string Description => "A web site served inside KazoOS by ASP.Hack.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Urls => ["http://template-app.hack/"];

    /// <summary>The site listens on its address.</summary>
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.NetworkListen];

    /// <summary>Services, then the middleware pipeline, then the endpoints.</summary>
    /// <param name="builder">The site's builder.</param>
    public override void Configure(HackWebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        HackWebApplication app = builder.Build();

        app.UseExceptionPage();
        app.UseStaticFiles();
        app.MapGet("/", () => HackResults.Text(
            """
            <!DOCTYPE html>
            <html>
            <head><title>HackerOs.Template.Site</title><link rel="stylesheet" href="/css/site.css" /></head>
            <body><h1>HackerOs.Template.Site</h1><p>Served by ASP.Hack. Try <a href="/api/time">/api/time</a>.</p></body>
            </html>
            """,
            "text/html"));
        app.MapGet("/api/time", () => HackResults.Text(TimeProvider.System.GetUtcNow().ToString("o")));
    }
}
