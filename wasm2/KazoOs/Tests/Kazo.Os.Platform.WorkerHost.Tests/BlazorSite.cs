using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Mvc;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// An ASP.Hack site with a Blazor root (ADR 0084) run as a real web app: besides its API it serves
/// the host page, the loader script and the boot manifest, as <c>curl</c> sees them. Any type stands
/// for the root component here: the browser is not part of these tests.
/// </summary>
public sealed partial class BlazorSite : WebAppDefinition
{
    public override string Id => "org.test.blazor";

    public override string Name => "blazor";

    public override IReadOnlyList<string> Urls => ["http://blazor.hack/"];

    public override Type? BlazorRoot => typeof(BlazorSite);

    public override void Configure(KazoWebApplicationBuilder builder)
    {
        KazoWebApplication app = builder.Build();
        // No MapControllers: the generated route table is the test assembly's, MvcSite's controllers included.
        app.MapGet("/api/time", () => KazoResults.Text("now"));
    }
}
