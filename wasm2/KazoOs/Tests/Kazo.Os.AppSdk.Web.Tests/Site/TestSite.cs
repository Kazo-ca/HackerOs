using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Antiforgery;
using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.AppSdk.Web.Tests.Views.Pages;
using Kazo.Os.AppSdk.Web.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.AppSdk.Web.Tests.Site;

/// <summary>The test site: services, middlewares, controllers, minimal routes (ADR 0076 §2).</summary>
[AppManifest]
public sealed partial class TestSite : WebAppDefinition
{
    public override string Id => "org.test.site";

    public override string Name => "Test site";

    public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];

    public override void Configure(KazoWebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<NoteStore>();
        builder.Services.AddScoped<RequestCounter>();
        builder.Json.SerializerOptions.TypeInfoResolverChain.Insert(0, TestSiteJsonContext.Default);
        builder.Mvc.Filters.Add(new TraceFilter("global"));
        KazoWebApplication app = builder.Build();
        app.UseExceptionPage();
        app.UseStaticFiles();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Powered-By"] = "ASP.Kazo";
            context.Items["endpoint"] = context.Endpoint?.DisplayName;
            await next();
        });
        app.UseCookieSession();
        app.MapGet("/health", () => KazoResults.Ok("up"));
        app.MapGet("/boom", _ => Boom());
        app.MapPost("/echo", async context =>
        {
            await context.Response.WriteAsync($"{context.Request.ContentType}|{context.Request.ReadBodyAsString()}");
        });
        app.MapControllers();
    }

    private static IKazoActionResult Boom() => throw new InvalidOperationException("kaboom");
}

/// <summary>A note of the API.</summary>
public sealed class Note
{
    public int Id { get; set; }

    [Required]
    [StringLength(40, MinimumLength = 2)]
    public string Title { get; set; } = string.Empty;

    public bool Done { get; set; }
}

/// <summary>A partial update.</summary>
public sealed record NotePatch(string? Title, bool? Done);

/// <summary>Source-generated JSON metadata of the site (what a trimmed site registers).</summary>
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(NotePatch))]
[JsonSerializable(typeof(List<Note>))]
[JsonSerializable(typeof(Note[]))]
[JsonSerializable(typeof(IReadOnlyList<Note>))]
internal sealed partial class TestSiteJsonContext : JsonSerializerContext;

/// <summary>A singleton service.</summary>
public sealed class NoteStore
{
    private readonly List<Note> _notes = [new() { Id = 1, Title = "First" }, new() { Id = 2, Title = "Second", Done = true }];

    public IReadOnlyList<Note> All => _notes;

    public Note? Find(int id) => _notes.FirstOrDefault(note => note.Id == id);

    public Note Add(Note note)
    {
        note.Id = _notes.Count == 0 ? 1 : _notes.Max(existing => existing.Id) + 1;
        _notes.Add(note);
        return note;
    }

    public bool Remove(int id) => _notes.RemoveAll(note => note.Id == id) > 0;
}

/// <summary>A scoped service: one instance per request.</summary>
public sealed class RequestCounter
{
    private static int s_instances;

    public RequestCounter() => Instance = Interlocked.Increment(ref s_instances);

    public int Instance { get; }
}

/// <summary>A filter that records the order filters run in (header <c>X-Trace</c>).</summary>
public sealed class TraceFilter(string name) : KazoActionFilterAttribute
{
    public string Name { get; } = name;

    public override void OnActionExecuting(KazoActionExecutingContext context) => Append(context.HttpContext, $"{Name}>");

    public override void OnActionExecuted(KazoActionExecutedContext context) => Append(context.HttpContext, $"<{Name}");

    private static void Append(KazoHttpContext context, string step) =>
        context.Response.Headers["X-Trace"] = (context.Response.Headers["X-Trace"] ?? string.Empty) + step;
}

/// <summary>A filter that answers <c>403</c> without running the action when <c>?deny=1</c>.</summary>
public sealed class DenyFilterAttribute : KazoActionFilterAttribute
{
    public override void OnActionExecuting(KazoActionExecutingContext context)
    {
        if (context.HttpContext.Request.Query["deny"] == "1")
        {
            context.Result = new KazoContentResult { Content = "denied", StatusCode = 403 };
        }
    }
}

/// <summary>An HTTP API (attribute routes, JSON body, problem details).</summary>
[Route("api/[controller]")]
[TraceFilter("controller", Order = 0)]
public sealed class NotesController(NoteStore store, RequestCounter counter) : KazoApiController
{
    [HttpGet]
    public IReadOnlyList<Note> List([FromQuery] bool? done = null) =>
        done is null ? [.. store.All] : [.. store.All.Where(note => note.Done == done)];

    [HttpGet("{id:int}", Name = "GetNote")]
    [TraceFilter("action")]
    public IKazoActionResult Get(int id) => store.Find(id) is Note note ? Ok(note) : NotFound();

    [HttpPost]
    public IKazoActionResult Create(Note note)
    {
        Note created = store.Add(note);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    public async Task<IKazoActionResult> PatchAsync(int id, [FromBody] NotePatch patch, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (store.Find(id) is not Note note)
        {
            return NotFound();
        }

        note.Title = patch.Title ?? note.Title;
        note.Done = patch.Done ?? note.Done;
        return Ok(note);
    }

    [HttpDelete("{id:int}")]
    [DenyFilter]
    public IKazoActionResult Delete(int id) => store.Remove(id) ? NoContent() : NotFound();

    [HttpGet("search")]
    public object Search([Range(1, 10)] int page, string term, [FromHeader(Name = "X-Client")] string? client, Priority priority = Priority.Normal, int[]? ids = null) =>
        $"term={term} page={page} client={client ?? "-"} priority={priority} ids={string.Join(',', ids ?? [])}";

    [HttpGet("scope")]
    public string Scope([FromServices] RequestCounter other) => other.Instance == counter.Instance ? "same" : "different";

    [HttpGet("/absolute/{*rest}")]
    public string Rest(string? rest) => $"rest={rest}";

    [HttpGet("throw")]
    public string Throw() => throw new FormatException("bad format");

    [NonAction]
    public string Helper() => "not an action";
}

public enum Priority
{
    Low,
    Normal,
    High
}

/// <summary>A form model of a page controller.</summary>
public sealed class ContactForm
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Range(18, 120)]
    public int Age { get; set; }

    public bool Subscribe { get; set; }

    public string[] Tags { get; set; } = [];
}

/// <summary>Pages with the default conventional route (<c>{controller=Home}/{action=Index}/{id?}</c>).</summary>
public sealed class HomeController : KazoController
{
    public string Index() => "home";

    public IKazoActionResult Details(int id) => Content($"<h1>Note {id}</h1>", "text/html; charset=utf-8");

    [HttpGet]
    public string Contact() => "contact form";

    [HttpPost]
    [ActionName("Contact")]
    public IKazoActionResult ContactPost(ContactForm form)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        return RedirectToAction(nameof(Thanks), new { name = form.Name, tags = form.Tags.Length, subscribe = form.Subscribe });
    }

    public string Thanks(string name, int tags, bool subscribe) => $"thanks {name} ({tags} tags, subscribe={subscribe})";

    public IKazoActionResult Visits()
    {
        int visits = (HttpContext.Session.GetInt32("visits") ?? 0) + 1;
        HttpContext.Session.SetInt32("visits", visits);
        return Content($"visits={visits}");
    }

    public IKazoActionResult Link() => Content(Url.Action(nameof(Details), "Home", new { id = 7, sort = "asc" }) ?? "none");

    public IKazoActionResult NoteLink() => Content(Url.RouteUrl("GetNote", new { id = 3 }) ?? "none");

    public IKazoActionResult Download() => File("hello"u8.ToArray(), "text/plain", "hello.txt");

    public IKazoActionResult Away() => LocalRedirect("https://evil.example/");

    public override void OnActionExecuting(KazoActionExecutingContext context)
    {
        if (context.HttpContext.Request.Query["stop"] == "1")
        {
            context.Result = Content("stopped by controller");
        }
    }
}

/// <summary>A controller of pages rendered by Razor views (web/7, ADR 0083).</summary>
public sealed class PagesController(NoteStore store) : KazoController
{
    public IKazoActionResult Index() => View(store);

    public IKazoActionResult Details(int id) => store.Find(id) is Note note ? View(note) : NotFound();

    [HttpGet]
    public IKazoActionResult Edit() => View(new ContactForm { Name = "Ada", Age = 36 });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IKazoActionResult Edit(ContactForm form)
    {
        if (!ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, "Please fix the errors below.");
            return View(form);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public string Open() => "open";

    public IKazoActionResult Plain() => View();

    public IKazoActionResult PlainModel() => View("Plain", "model");

    public IKazoActionResult Partial(int id) => PartialView("_NoteCard", store.Find(id));

    public IKazoActionResult Typed() => View<Loose>();

    public IKazoActionResult Shared() => View("Error");

    public IKazoActionResult Absolute() => View("~/Views/Shared/Error.razor");

    public IKazoActionResult Missing() => View("Nowhere");

    public IKazoActionResult WrongModel() => View("Details", "not a note");

    public IKazoActionResult Broken() => View();

    public IKazoActionResult NoBody() => View();

    public IKazoActionResult Injected() => View();

    public IKazoActionResult Status() => new KazoViewResult { ViewName = "Plain", StatusCode = 418 };
}

/// <summary>A controller of pages rendered by .cshtml views (web/8, ADR 0086).</summary>
public sealed class CshtmlController(NoteStore store) : KazoController
{
    public IKazoActionResult Index() => View(store);

    public IKazoActionResult Details(int id) => store.Find(id) is Note note ? View(note) : NotFound();

    [HttpGet]
    public IKazoActionResult Edit() => View(new ContactForm { Name = "Ada", Age = 36 });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IKazoActionResult Edit(ContactForm form)
    {
        if (!ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, "Please fix the errors below.");
            return View(form);
        }

        return RedirectToAction(nameof(Index));
    }

    public IKazoActionResult WithRazorLayout() => View();

    public IKazoActionResult FromRazor() => View();

    public IKazoActionResult NoLayout() => View();

    public IKazoActionResult Card(int id) => PartialView("_CshtmlCard", store.Find(id));

    public IKazoActionResult WrongModel() => View("Details", "not a note");

    public IKazoActionResult NoBody() => View();

    public IKazoActionResult MissingSection() => View();

    public IKazoActionResult Orphan() => View();

    public IKazoActionResult Absolute() => View("~/Views/Cshtml/NoLayout.cshtml");
}

/// <summary>A site without exception page nor controllers: a failure is a bare <c>500</c>.</summary>
[AppManifest]
public sealed partial class BareSite : WebAppDefinition
{
    public override string Id => "org.test.bare";

    public override string Name => "Bare site";

    public override IReadOnlyList<string> Urls => ["http://bare.hack/"];

    public override void Configure(KazoWebApplicationBuilder builder)
    {
        KazoWebApplication app = builder.Build();
        app.MapGet("/fail", () => throw new InvalidOperationException("hidden"));
    }
}
