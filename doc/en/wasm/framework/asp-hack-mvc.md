> [Version française](../../../fr/wasm/framework/asp-hack-mvc.md)

# ASP.Hack MVC: pipeline, routing, controllers and session

Work item `web/6-mvc-core` of the web phase. Decisions: [ADR 0076](../dev/adr/0076-asp-hack-mvc.md)
(ASP.Hack MVC) and [ADR 0082](../dev/adr/0082-asp-hack-route-source-generator.md) (route table
produced by a source generator). Plan:
[`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md). Network foundation: [`listening-ports-and-dns.md`](listening-ports-and-dns.md).

## Purpose

`Shared/HackerOs.AppSdk.Web` makes it possible to write an internal HackerOS site like
an ASP.NET Core MVC site: a startup class, middlewares, controllers
with `[HttpGet]`/`[HttpPost]`/`[HttpPut]`/`[HttpPatch]`/`[HttpDelete]`,
model binding, `DataAnnotations` validation, JSON, text,
file or redirect results, per-request dependency injection and a cookie
session. The site is an app of kind `web` (ADR 0073): the OS starts it on the
first access to one of its URLs and its code runs in the shared `web` worker.

`.razor` views, `View()`, `PartialView()`, anti-forgery and `wwwroot`:
[`asp-hack-razor-views.md`](asp-hack-razor-views.md) (`web/7`, ADR 0083). `.cshtml`
views: [`asp-hack-cshtml-views.md`](asp-hack-cshtml-views.md) (`web/8`, ADR 0086).
Out of this work item: Blazor sites (`web/9`).

## Example

```csharp
[AppManifest]
public sealed partial class IntranetApp : WebAppDefinition   // partial : le build donne l'EntryPoint
{
    public override string Id => "com.example.intranet";
    public override string Name => "Intranet";
    public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];

    public override void Configure(HackWebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<NoteStore>();
        builder.Json.SerializerOptions.TypeInfoResolverChain.Insert(0, IntranetJsonContext.Default);
        HackWebApplication app = builder.Build();
        app.UseExceptionPage();
        app.UseCookieSession();
        app.Use(async (context, next) => { context.Response.Headers["X-Powered-By"] = "ASP.Hack"; await next(); });
        app.MapGet("/health", () => HackResults.Ok("up"));
        app.MapControllers();          // routes d'attribut + {controller=Home}/{action=Index}/{id?}
    }
}

[Route("api/[controller]")]
public sealed class NotesController(NoteStore store) : HackApiController
{
    [HttpGet] public IReadOnlyList<Note> List([FromQuery] bool? done = null) => ...;
    [HttpGet("{id:int}", Name = "GetNote")]
    public IHackActionResult Get(int id) => store.Find(id) is Note n ? Ok(n) : NotFound();
    [HttpPost] public IHackActionResult Create(Note note) => CreatedAtAction(nameof(Get), new { id = store.Add(note).Id }, note);
}

public sealed class HomeController : HackController
{
    public string Index() => "home";
    [HttpPost, ActionName("Contact")]
    public IHackActionResult ContactPost(ContactForm form) =>
        ModelState.IsValid ? RedirectToAction(nameof(Index)) : BadRequest(ModelState);
}
```

`curl http://intranet.hack/api/notes/1` starts the site, then passes it the
request through the local socket, as for any site (`web/3`).

## Architecture

```text
HackHttpListener (worker web)                                  web/3
  └─ HackWebHost.ProcessRequestAsync            Hosting/       requête du listener → HackWebRequestData
       └─ HackWebApplication.ProcessAsync                       une portée DI par requête, HackHttpContext
            ├─ routage : HackEndpointRouter.Match               Routing/  404, 405 + Allow, OPTIONS 204, HEAD → GET
            ├─ middlewares (Use, UseMiddleware<T>, UseCookieSession, UseStatusCodePages)
            └─ endpoint
                 ├─ route minimale (MapGet/MapPost/... ou HackRequestDelegate)
                 └─ action : HackActionInvoker.InvokeAsync      Mvc/
                      ├─ CreateController (généré)              services de la portée
                      ├─ BindArguments (généré) → HackModelBinder, ModelState
                      ├─ 415, puis 400 automatique d'une API
                      ├─ filtres : contrôleur, globaux, contrôleur (attribut), action
                      ├─ Invoke (généré)
                      └─ IHackActionResult.ExecuteResultAsync → HackHttpResponse
UseExceptionPage : toute exception du pipeline → page d'erreur (sinon « 500 Internal Server Error » nu)
```

| Folder | Content |
|---|---|
| `Hosting/` | `WebAppDefinition`, `HackWebApplicationBuilder`, `HackWebApplication`, `HackWebHost`, `HackWebJsonOptions`. |
| `Http/` | `HackHttpContext`, `HackHttpRequest`, `HackHttpResponse`, `HackHeaderDictionary`, `HackValueCollection` (request, form), cookies, `HackWebRequestData` / `HackWebResponseData` (transport-independent exchange). |
| `Routing/` | `HackRouteTemplate` (parser shared with the generator), `HackRoutePattern` (matching, URL generation), `HackEndpoint`, `HackEndpointRouter`, `HackEndpointDataSource`, `HackLinkGenerator`, `HackRouteValueDictionary`. |
| `Mvc/` | Attributes, `HackControllerBase` / `HackController` / `HackApiController`, `HackModelBinder`, `HackModelStateDictionary`, filters, results, `HackUrlHelper`, `HackActionInvoker`, `HackResults`, `HackControllerActionDescriptor` contract. |
| `Sessions/` | `HackSession`, `HackSessionOptions`, `HackSessionStore`. |
| `Diagnostics/` | `HackExceptionPage` (console-style error page). |
| `Tools/HackerOs.Tools.WebRouteGenerator` | Roslyn source generator (ADR 0082): route table and `<Name>WebHost`, errors `HOSWEB0001` to `HOSWEB0007` (`HOSWEB0008`: `.razor` and `.cshtml` view at the same path, ADR 0086). |

## Startup

- `WebAppDefinition`: `Kind` is `Web` (sealed), `Category` `internet`,
  `LaunchVisibility` `Hidden`, `Urls` to be provided. `Configure(builder)` runs in
  the site's process at each startup; by default it calls
  `builder.Build().MapControllers()`.
- `HackWebApplicationBuilder`: `Services` (`IServiceCollection`), `Json`
  (`HackWebJsonOptions.SerializerOptions`, web defaults: camelCase), `Mvc`
  (`HackMvcOptions.Filters`, global filters), `Build()`. The provider is
  built with `ValidateScopes` and `ValidateOnBuild`.
- `HackWebApplication`: `Use(...)` (two forms, as in ASP.NET Core),
  `UseMiddleware<T>` (`IHackMiddleware`), `UseExceptionPage`,
  `UseStatusCodePages`, `UseCookieSession(options)`, `Map`, `MapMethods`,
  `MapGet`/`MapPost`/`MapPut`/`MapPatch`/`MapDelete`, `MapControllers`,
  `MapControllerRoute(name, pattern[, defaults])`, `MapDefaultControllerRoute`,
  `ProcessAsync(HackWebRequestData)`, `UnhandledException` event.
- `MapControllers()` adds the default conventional route
  `{controller=Home}/{action=Index}/{id?}` if no `MapControllerRoute` is
  called, when the pipeline is frozen (first request).
- `HackWebHost.CreateApplication(definition, routes)` builds the application
  without an OS or listener: this is how tests call a site
  (`ProcessAsync(HackWebRequestData.Create("GET", "http://intranet.hack/"))`),
  like ASP.NET Core's `TestServer`.

## Routing

ASP.NET Core templates: literals (case-insensitive), `{p}`, `{p?}`, `{p=default}`,
`{*rest}` / `{**rest}`, constraints `int`, `long`, `bool`, `guid`, `double`,
`decimal`, `float`, `datetime`, `alpha`, `required`, `min(n)`, `max(n)`,
`range(a,b)`, `length(n)`/`length(a,b)`, `minlength(n)`, `maxlength(n)`,
`regex(...)` (case-insensitive, 100 ms timeout). `{{` and `}}` escape a brace.
No complex segments.

Choice of the endpoint, like ASP.NET Core endpoint routing:

1. lowest `Order` (attribute routes 0, conventional routes 1, 2...);
2. then the most specific template, segment by segment: literal, constrained
   parameter, parameter, optional, catch-all;
3. then an endpoint limited to certain methods before an "all methods" endpoint;
4. then registration order. Two indistinguishable candidates:
   `HackAmbiguousRouteException` (500).

A known path but a refused method gives `405` with `Allow`; `OPTIONS`
without an endpoint gives `204` with `Allow`; `HEAD` uses the `GET` endpoint and
returns the headers without a body.

Attribute routes: `[Route]` on the controller (prefix) and on the action,
`[HttpGet("...")]`... (`Name`, `Order`), tokens `[controller]`, `[action]`,
`[area]`, absolute template `/...` or `~/...` which ignores the prefix. An action
without an attribute route in a view controller follows the conventional routes
(`controller` and `action` as required values). An action of a
`HackApiController` must have an attribute route (`HOSWEB0007`).

URL generation: `Url.Action(action, controller, values)`,
`Url.RouteUrl(name, values)`, `Url.Content("~/...")`, `Url.IsLocalUrl`; default
values at the end of the path are omitted, extra values go into
the query (`/Home/Details/7?sort=asc`).

## Controllers

- A controller is a public, non-abstract, non-generic class derived
  from `HackControllerBase`, without `[NonController]`. Its name loses the
  `Controller` suffix. Its actions: public, non-generic instance methods,
  without `[NonAction]`, other than framework members and `Dispose`.
  `[ActionName]` renames an action.
- `HackController` (pages): `Json(...)`, `OnActionExecuting` /
  `OnActionExecuted` (the controller is the first filter).
- `HackApiController`: JSON body by default for complex types, automatic `400`
  (`application/problem+json`, `errors`) if `ModelState` is
  invalid, and any client error status without a body (`NotFound()`,
  `BadRequest()`...) becomes a `HackProblemDetails` (RFC 9457: `type`,
  `title`, `status`, `traceId`).
- Members of `HackControllerBase`: `HttpContext`, `Request`, `Response`,
  `RouteValues`, `ModelState`, `Url`, and the helpers `Ok`, `NoContent`,
  `StatusCode`, `Content`, `File`, `Redirect`, `RedirectPermanent`,
  `LocalRedirect` (refuses an external URL), `RedirectToAction`, `Created`,
  `CreatedAtAction` (absolute URL), `NotFound`, `BadRequest` (including
  `BadRequest(ModelState)`), `Unauthorized`, `Forbid`, `Conflict`, `Problem`,
  `ValidationProblem`, and `TryValidateModel(model, prefix)` to validate a
  model built or merged by the action (a `PATCH`, ADR 0087 §3).
- An action returns `void`, `Task`, `ValueTask`, an `IHackActionResult`, an object
  (plain text for a `string`, JSON otherwise, `204` for `null`) or a task of
  one of them. A `CancellationToken` parameter receives `RequestAborted`.

## Model binding and validation

| Parameter | Source |
|---|---|
| Simple type (`IParsable<T>`, `string`, `enum`, `Nullable<T>`, array) | form, then route, then query (as in ASP.NET Core); route then query for an API |
| `[FromRoute]`, `[FromQuery]`, `[FromForm]`, `[FromHeader]` (`Name =`) | the named source |
| Complex type of an API, or `[FromBody]` | JSON body (`415` if the body is not JSON) |
| Complex type of a view controller | form properties (`Name=...&Tags=a&Tags=b`, checkbox `on`) |
| `[FromServices]` | request DI scope |
| `CancellationToken`, `HackHttpContext` | special values |

A non-nullable parameter without a default value is required. The `DataAnnotations`
attributes of the parameter and of the model's properties are checked;
errors go into `ModelState` (key: parameter or property name).

## Filters

`IHackActionFilter` (`OnActionExecuting`, `OnActionExecuted`) and
`IHackAsyncActionFilter`, as an attribute (`HackActionFilterAttribute`, `Order`) or
global (`builder.Mvc.Filters`). Order: the controller itself, then global ones,
controller attributes, action attributes; the return goes in reverse
order. A filter that sets `context.Result` short-circuits the action.

## Session

`app.UseCookieSession()`: `.HackSession` cookie (`HttpOnly`, `SameSite=Lax`,
`Path=/`), identifier of 32 random bytes in base64url, values in the memory
of the site's process, expiry after 20 minutes of inactivity. The cookie is
set only if the session is written. `HttpContext.Session`: `GetString`,
`SetString`, `GetInt32`, `SetInt32`, `TryGetValue`, `Set`, `Remove`, `Clear`,
`Keys`, `Id`, `IsNew`. The session disappears when the site stops (like ASP.NET Core's
in-memory session).

## Contract for `web/7` and `web/9`

`web/7` delivered its part of this contract ([`asp-hack-razor-views.md`](asp-hack-razor-views.md));
the contract remains the reference for `web/9`.

- **Results.** A view is an `IHackActionResult`: `ExecuteResultAsync(HackActionContext)`
  receives `HttpContext`, `RouteValues`, `ActionDescriptor` and `ModelState`, and
  writes to `HttpContext.Response`. `View()` and `PartialView()` will be added to
  `HackController` (the view controller), not to `HackApiController`.
- **View lookup.** `HackControllerActionDescriptor.ControllerName` and
  `ActionName` give `Views/<Controller>/<Action>`; the route generator
  also writes the view table (`HackWebGeneratedRoutes.Views`, ADR 0083 §2).
- **URL.** `IHackUrlHelper` (the controller's `Url`) is the basis of the view
  helpers (`HackLink`, `HackForm`); `HackLinkGenerator` is in the services.
- **Middlewares.** `UseStaticFiles()` will be an ordinary middleware
  (`Use(next => ...)`) that answers before controller routing; an endpoint
  is chosen before the middlewares (`HttpContext.Endpoint`), like
  ASP.NET Core's `UseRouting` at the start of the pipeline.
- **Services.** The whole `web/7` part (view engine, anti-forgery)
  registers in `builder.Services`; the builder already registers
  `HackWebJsonOptions`, `HackMvcOptions`, `HackLinkGenerator` and the session
  store.
- **Blazor (`web/9`, [`blazor-sites.md`](blazor-sites.md)).** A Blazor site
  declares its root component through `BlazorRoot`; `HackWebHost` then serves the
  host page and `blazor.boot.json` as a fallback after the app's routes, which keeps
  `HackWebApplication` for its data routes (`MapGet`, API controllers).

## Tests

- `Tests/HackerOs.AppSdk.Web.Tests`: templates and router
  (`RouteTemplateTests`, `EndpointRouterTests`), full pipeline of a test site
  compiled with the generator (`MvcPipelineTests`: API CRUD, problems,
  automatic `400`, `415`, filters, conventional routes, forms,
  redirects, links, session, files, error page, per-request DI),
  generator driven on code snippets (`WebRouteGeneratorTests`:
  `HOSWEB0001` to `HOSWEB0007`, table and entry produced).
- `Tests/HackerOs.Platform.WorkerHost.Tests/MvcSiteTests`: an MVC site started
  by `curl` in a full OS, on the main thread and in the `web` worker.

## Known limits

- A single template parser: no complex segments, no parameter
  transformers, no nested model binding
  (`Address.City`) or dictionaries.
- `HEAD` does not send a `Content-Length` of the body that would have been produced.
- No environments (`Development`/`Production`) or `TempData`.
- ~~`curl -H 'Cookie: ...'` is ignored by the network gateway (`web/3`).~~
  Fixed by `web/14` (ADR 0089): the header reaches the site, merged with
  the command's cookie jar.

## Tasks

- [x] `HackerOs.AppSdk.Web` library: project, solution, SDK boundary.
- [x] HTTP: context, request, response, headers, cookies, form, transport-independent exchange.
- [x] Routing: templates, constraints, precedence, `405`/`OPTIONS`/`HEAD`, URL generation.
- [x] Source generator (ADR 0082) wired to the apps, errors `HOSWEB0001` to `HOSWEB0007`.
- [x] Controllers, actions, filters, `OnActionExecuting`.
- [x] Model binding, `DataAnnotations`, `ModelState`, automatic `400`, `415`.
- [x] JSON, Content, File, Redirect results (including `LocalRedirect`, `RedirectToAction`), `Created*`, RFC 9457 problems.
- [x] Per-request DI, cookie session, error page.
- [x] `WebAppDefinition`, `HackWebHost` and generated entry; manifest produced by the manifest generator.
- [x] Unit, generator and end-to-end tests (`curl`, two modes).
- [x] ADR 0082, this document, index, plan.
