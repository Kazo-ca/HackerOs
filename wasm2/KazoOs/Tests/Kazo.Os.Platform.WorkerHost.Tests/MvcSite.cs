using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Web.Antiforgery;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// An ASP.Hack MVC site (ADR 0076) run as a real web app: the route generator compiled
/// <see cref="MvcSiteWebHost"/>, the entry point of its manifest in <see cref="TestCommands"/>.
/// </summary>
public sealed partial class MvcSite : WebAppDefinition
{
    public override string Id => "org.test.mvc";

    public override string Name => "mvc";

    public override IReadOnlyList<string> Urls => ["http://mvc.hack/"];

    public override void Configure(HackWebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<MvcCounter>();
        HackWebApplication app = builder.Build();
        app.UseExceptionPage();
        app.UseStaticFiles();
        app.UseCookieSession();
        app.MapControllers();
    }
}

/// <summary>The model of the <c>Page</c> view.</summary>
public sealed record Greeting(string Who);

/// <summary>A singleton shared by every request of the site.</summary>
public sealed class MvcCounter
{
    private int _value;

    public int Next() => Interlocked.Increment(ref _value);
}

/// <summary>An API controller of the site.</summary>
[Route("api/[controller]")]
public sealed class ItemsController(MvcCounter counter) : HackApiController
{
    [HttpGet("{id:int}")]
    public IHackActionResult Get(int id) => id == 0 ? NotFound() : Content($"item {id} #{counter.Next()}");

    [HttpPost]
    public IHackActionResult Create([FromForm] string name) => Created($"/api/items/{name.Length}", null);
}

/// <summary>A page controller on the default conventional route.</summary>
public sealed class HomeController : HackController
{
    public string Index() => $"home in={(HackExecutionContext.Current.GetType().Name == "RemoteAppExecutionContext" ? "worker" : "main")}";

    public IHackActionResult Visits()
    {
        int visits = (HttpContext.Session.GetInt32("visits") ?? 0) + 1;
        HttpContext.Session.SetInt32("visits", visits);
        return Content($"visits={visits}");
    }

    [ValidateAntiForgeryToken]
    public IHackActionResult Go() => RedirectToAction(nameof(Index));

    /// <summary>A Razor view with its layout (web/7, ADR 0083).</summary>
    public IHackActionResult Page(string? who) => View(new Greeting(who ?? "stranger"));

    /// <summary>A .cshtml view with its _ViewStart and .cshtml layout (web/8, ADR 0086).</summary>
    public IHackActionResult Classic(string? who) => View(new Greeting(who ?? "stranger"));

    /// <summary>Counts a first visit then redirects to <see cref="Visits"/>: the second hop carries the session cookie.</summary>
    public IHackActionResult Enter()
    {
        HttpContext.Session.SetInt32("visits", 1);
        return RedirectToAction(nameof(Visits));
    }
}
