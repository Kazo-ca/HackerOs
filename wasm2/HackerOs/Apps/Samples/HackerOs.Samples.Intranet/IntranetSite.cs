using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Web.Data;
using HackerOs.AppSdk.Web.Hosting;
using HackerOs.AppSdk.Web.Mvc;
using HackerOs.Samples.Intranet.Board;
using HackerOs.Samples.Intranet.Models;
using HackerOs.Samples.Intranet.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HackerOs.Samples.Intranet;

/// <summary>
/// Manifest and startup of "Intranet", the sample of the ASP.Hack web phase
/// (<c>doc/wasm/framework/asp-hack-developer-guide.md</c>): one web app at <c>http://intranet.hack/</c> that the OS
/// starts on the first request (ADR 0073, 0074).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>/</c>, <c>/Home/Profile</c>, <c>/People</c>: <c>.cshtml</c> views (ADR 0086);</item>
/// <item><c>/Notes</c>: <c>.razor</c> views (ADR 0083), forms with anti-forgery and validation;</item>
/// <item><c>/api/notes</c>, <c>/api/people</c>, <c>/api/tasks</c>: a JSON REST API (ADR 0076, 0082);</item>
/// <item><c>/css</c>, <c>/js</c>: the embedded <c>wwwroot</c>, whose scripts run in the browser's isolated page (ADR 0075);</item>
/// <item><c>/board</c>: an interactive Blazor site drawn by the browser (ADR 0084), on the same origin
/// so it shares the tab's cookies with the MVC pages.</item>
/// </list>
/// The build generates <c>manifest.json</c>, the route and view tables and <c>IntranetSiteWebHost</c>,
/// the entry point that serves the site.
/// </remarks>
[AppManifest]
public sealed partial class IntranetSite : WebAppDefinition
{
    /// <summary>The address the site declares in its manifest.</summary>
    public const string Address = "http://intranet.hack/";

    /// <inheritdoc />
    public override string Id => "org.hackeros.samples.intranet";

    /// <inheritdoc />
    public override string Name => "Intranet";

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description =>
        "Sample ASP.Hack intranet: MVC pages in .cshtml and .razor, a REST API, JavaScript, a cookie session and a Blazor task board";

    /// <inheritdoc />
    public override IReadOnlyList<string> Urls => [Address];

    /// <summary>The task board under <c>/board</c>: the browser draws it when it receives the host page.</summary>
    public override Type? BlazorRoot => typeof(BoardApp);

    /// <summary>Listening for the server side; simulated network for the board's <c>Site.Http</c> calls.</summary>
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.NetworkListen, AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkSimulatedWrite];

    /// <summary>
    /// Runs in the site process at every start (in the shared web worker when the host has one):
    /// services, then the middleware pipeline in order, then the endpoints. The Blazor host page,
    /// <c>_framework/blazor.hack.js</c> and <c>blazor.boot.json</c> are added after this method, as a
    /// fallback behind every route declared here.
    /// </summary>
    /// <param name="builder">The site's builder.</param>
    public override void Configure(HackWebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Services: singletons live as long as the site process; controllers get them by constructor.
        // The notes live in the site's browser database hackweb.<id> (ADR 0088), not in the OS files.
        builder.Services.AddHackWebDb(NoteStore.Configure);
        builder.Services.AddSingleton<NoteStore>();
        builder.Services.AddSingleton<TaskBoard>();
        builder.Services.AddSingleton<StaffDirectory>();
        builder.Services.AddSingleton<AnnouncementFeed>();
        builder.Json.SerializerOptions.TypeInfoResolverChain.Insert(0, IntranetJsonContext.Default);

        // Debug log /var/log/hackweb/org.hackeros.samples.intranet.log: requests, ILogger<T> lines, errors.
        builder.Logging.EnableDebugLog();

        HackWebApplication app = builder.Build();

        // Pipeline: errors as a page, files of wwwroot before routing, then the session cookie.
        app.UseExceptionPage();
        app.UseStaticFiles();
        app.UseCookieSession();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Powered-By"] = "ASP.Hack";
            await next();
        });

        // Endpoints: a minimal route, then every controller (attribute routes and the default route).
        DateTimeOffset startedAt = TimeProvider.System.GetUtcNow();
        app.MapGet("/health", async context =>
        {
            NoteStore notes = context.RequestServices.GetRequiredService<NoteStore>();
            TaskBoard board = context.RequestServices.GetRequiredService<TaskBoard>();
            int noteCount = await notes.CountAsync(context.RequestAborted);
            return HackResults.Json(new SiteStatus("up", noteCount, board.Count, startedAt), IntranetJsonContext.Default.SiteStatus);
        });
        // The board's pages call it with the tab's cookies: the same session as the MVC pages.
        app.MapGet("/api/me", context =>
            HackResults.Json(new Visitor(IntranetSession.DisplayName(context)), IntranetJsonContext.Default.Visitor));
        app.MapControllers();
    }
}
