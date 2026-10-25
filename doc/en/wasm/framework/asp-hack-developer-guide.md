> [Version française](../../../fr/wasm/framework/asp-hack-developer-guide.md)

# ASP.Hack developer guide

Work item `web/11-sample-final`, the last of the web phase ([`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md)).
This guide shows how to write an **internal site** for HackerOS with ASP.Hack, by
following the **Intranet** example (`Apps/Samples/HackerOs.Samples.Intranet`,
`http://intranet.hack/`), which brings together all the building blocks of the phase. Decisions
specific to this work item: [ADR 0087](../dev/adr/0087-asp-hack-intranet-sample-adjustments.md).
Reference for each type: [`sdk-reference.md`](sdk-reference.md#websites-asphack).
Guide to windowed apps, commands and services: [`sdk-developer-guide.md`](sdk-developer-guide.md).

## Purpose

ASP.Hack takes up the way of writing an ASP.NET Core MVC site, without ASP.NET (which
does not run in the browser): a startup class, middlewares,
controllers with `[HttpGet]`/`[HttpPost]`... attributes, `.cshtml` or
`.razor` views, a `wwwroot`, a cookie session, and, if wanted, interactive
Blazor pages. The site is a **`Web` kind app**: an OS process that listens on a port, started on the first request, visible in `ps`, the
task manager and `netstat -tlnp`.

| Building block | Work item, document | In the example |
|---|---|---|
| Real HTTP bodies | `web/2`, [`http-bodies.md`](http-bodies.md) | JSON, forms, CSS and JS served with their `Content-Type` |
| Listening, DNS, on-demand activation | `web/3`, [`listening-ports-and-dns.md`](listening-ports-and-dns.md) | `intranet.hack` resolved by the manifest, site started by `curl` or the browser |
| Isolated HTML page, `fetch` bridge | `web/4`, [`browser-html-rendering.md`](../dev/browser-html-rendering.md) | `health.js`, `people.js`, `notes.js` |
| Pipeline, routing, controllers, session | `web/6`, [`asp-hack-mvc.md`](asp-hack-mvc.md) | `IntranetSite.Configure`, `Controllers/` |
| `.razor` views, anti-forgery, `wwwroot` | `web/7`, [`asp-hack-razor-views.md`](asp-hack-razor-views.md) | `Views/Notes/`, `Views/Shared/_Layout.razor`, `wwwroot/` |
| `.cshtml` views | `web/8`, [`asp-hack-cshtml-views.md`](asp-hack-cshtml-views.md) | `Views/Home/`, `Views/People/`, `_ViewStart.cshtml` |
| Interactive Blazor site | `web/9`, [`blazor-sites.md`](blazor-sites.md) | `Board/` under `/board` |
| Detached window | `web/10`, [`detached-display-windows.md`](../dev/detached-display-windows.md) | the browser's `⧉` button, on any page |
| Static sites without code | `web/5`, [`static-sites.md`](../dev/static-sites.md) | (another kind of site: user files served by `hackwebd`) |
| Site database, debug log, autofill | `web/12`, [`asp-hack-site-data.md`](asp-hack-site-data.md) | notes in the IndexedDB database `hackweb.org.hackeros.samples.intranet`, `ILogger<NotesController>`, titles suggested by the browser |

## Visiting the example

Launch a host (`dotnet run --project OS/HackerOs.Ecosystem`, see
[`building-and-testing.md`](../dev/building-and-testing.md)), open the OS
browser and type `intranet.hack`.

| Address | What you see | What it shows |
|---|---|---|
| `/` | Announcements, counters, site status | `.cshtml` view in a `.razor` layout; `health.js` calls `/health` through `fetch` |
| `/Home/Profile` | Choosing a name | `Html.BeginForm`, `TextBoxFor`, validation, anti-forgery token, name kept in session |
| `/People` | Directory | search done by the server (`?q=`), then filtered by `people.js` on `/api/people` at each keystroke |
| `/Notes` | Team wiki | `.razor` views, `_NoteCard` partial, creation/editing/deletion through forms (post/redirect/get), star that pins through `PATCH /api/notes/{id}`; notes kept in the site database (`HackWebDb`), which survive the site stopping and restarting |
| `/board` | Task board | Blazor site drawn by the browser: add, move, delete a card without reloading, link to `/board/tasks/{id}` |
| `/api/notes`, `/api/people`, `/api/tasks`, `/api/me`, `/health` | JSON | full REST API (`GET`, `POST`, `PUT`, `PATCH`, `DELETE`, RFC 9457 problems) |

In an OS terminal:

```text
$ curl http://intranet.hack/health
{"status":"up","notes":3,"tasks":4,"startedAt":"2026-10-17T00:42:10+00:00"}
$ curl -i -X POST -H 'Content-Type: application/json' -d '{"title":"From curl"}' http://intranet.hack/api/notes
HTTP/1.1 201 Created
Location: http://intranet.hack/api/notes/4
...
$ netstat -tlnp        # le site écoute :80, sous l'identifiant de l'app
$ ps aux | grep intranet
```

`F12` in the browser opens the **Network** tab (every `fetch` from the scripts
goes through it) and **Console** (the scripts' `console.log`).

## 1. Creating the project

A site is a project under `Apps/` (like any shipped app), using the Razor SDK:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.Web" Version="10.0.13" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\Shared\HackerOs.App.Abstractions\HackerOs.App.Abstractions.csproj" />
    <ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk\HackerOs.AppSdk.csproj" />
    <ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk.Web\HackerOs.AppSdk.Web.csproj" />
    <!-- Seulement pour des pages Blazor interactives : -->
    <ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk.Blazor\HackerOs.AppSdk.Blazor.csproj" />
    <ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk.Web.Blazor\HackerOs.AppSdk.Web.Blazor.csproj" />
  </ItemGroup>
</Project>
```

The build (`build/HackerOs.Apps.targets`) adds by itself, because the project
references `HackerOs.AppSdk.Web`:

- the route generator (ADR 0082): controller table, view table and
  `<Definition>WebHost` entry class;
- the manifest generator: `manifest.json` (kind `web`, `web.urls`,
  `web.blazor`);
- the embedding of `wwwroot/**` as assembly resources;
- for `.cshtml`: the root `_ViewImports.cshtml` and the Razor compiler's
  compatibility types (ADR 0086). Do **not** put a
  `_ViewImports.cshtml` at the project root (build error): use
  `Views/_ViewImports.cshtml`.

Layout of the example:

```text
HackerOs.Samples.Intranet/
  IntranetSite.cs                 [AppManifest] partial WebAppDefinition, Configure
  Models/                         Note, Person, BoardTask, ProfileForm, IntranetJsonContext
  Services/                       NoteStore, TaskBoard, StaffDirectory, AnnouncementFeed, IntranetSession
  Controllers/                    HomeController, NotesController, PeopleController (pages)
  Controllers/Api/                NotesApiController, PeopleApiController, TasksApiController
  Views/
    _Imports.razor                @using des vues .razor
    _ViewImports.cshtml           @using des vues .cshtml
    _ViewStart.cshtml             @{ Layout = "_Layout"; }
    Shared/_Layout.razor(.cs)     layout commun aux deux genres de vues
    Home/Index.cshtml, Home/Profile.cshtml, People/Index.cshtml
    Notes/Index.razor, Notes/Details.razor, Notes/Edit.razor, Notes/_NoteCard.razor
  wwwroot/css/site.css, wwwroot/js/health.js, people.js, notes.js
  Board/                          partie Blazor : BoardApp (racine), BoardLayout, Pages/BoardPage, Pages/TaskPage
```

## 2. Declaring the site: `WebAppDefinition`

```csharp
[AppManifest]
public sealed partial class IntranetSite : WebAppDefinition   // partial : le build écrit IntranetSiteWebHost
{
    public override string Id => "org.hackeros.samples.intranet";
    public override string Name => "Intranet";
    public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
    public override Type? BlazorRoot => typeof(BoardApp);           // facultatif : pages Blazor
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.NetworkListen, AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkSimulatedWrite];

    public override void Configure(HackWebApplicationBuilder builder) { ... }
}
```

- **`Urls`**: the addresses that the OS DNS associates with the site (`.hack` domain
  for an internal site, ADR 0074). The port comes from the URL (80 by default; a
  port < 1024 is granted automatically on its own `Urls`).
- **Capabilities**: `network.listen` to listen; `network.simulated.*` if the
  Blazor pages call the site's API. Never `filesystem.*`: the generator
  refuses it (`HOSAPP0050`), a site's data does not go into the OS
  files (ADR 0073 §3).
- `Kind` (`Web`), the category (`internet`) and the visibility (`Hidden`: a site
  is not opened from the launcher) are fixed by `WebAppDefinition`.

## 3. Startup: services, pipeline, endpoints

`Configure` runs in the site's process at each startup, in the shared
`web` worker when the host has one. The order is that of ASP.NET Core:

```csharp
public override void Configure(HackWebApplicationBuilder builder)
{
    builder.Services.AddSingleton<NoteStore>();                 // un par processus du site
    builder.Services.AddSingleton<TaskBoard>();
    builder.Json.SerializerOptions.TypeInfoResolverChain.Insert(0, IntranetJsonContext.Default);

    HackWebApplication app = builder.Build();
    app.UseExceptionPage();          // une exception devient une page d'erreur au style console
    app.UseStaticFiles();            // wwwroot/ embarqué, avant le routage des contrôleurs
    app.UseCookieSession();          // HttpContext.Session, cookie .HackSession
    app.Use(async (context, next) => { context.Response.Headers["X-Powered-By"] = "ASP.Hack"; await next(); });

    app.MapGet("/health", context => HackResults.Json(..., IntranetJsonContext.Default.SiteStatus));
    app.MapGet("/api/me", context => HackResults.Json(new Visitor(IntranetSession.DisplayName(context)), ...));
    app.MapControllers();            // routes d'attribut + {controller=Home}/{action=Index}/{id?}
    // Avec BlazorRoot, la page hôte Blazor est ajoutée après Configure, en dernier recours.
}
```

- Register a `JsonSerializerContext` (source generation) for all the
  exchanged types: the site is compiled for trimming like any app.
- A singleton lives as long as the site's process; a `Scoped` service lives for
  the duration of a request (the controllers and the `@inject`s of the views of the same
  request receive the same instance).

## 4. Page controllers

`HackController` (ASP.NET's `Controller`): `View()`, `PartialView()`,
`ViewData`, `RedirectToAction`, binding of complex types from the
form.

```csharp
public sealed class NotesController(NoteStore store, ILogger<NotesController> logger) : HackController
{
    public async Task<IHackActionResult> Index(CancellationToken cancellationToken) =>
        View(await store.AllAsync(cancellationToken));                       // Views/Notes/Index.razor

    [HttpGet]
    public IHackActionResult Create() => View("Edit", new Note());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IHackActionResult> Create(Note note, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) { return View("Edit", note); }               // ré-affiché avec ses erreurs
        Note created = await store.AddAsync(note, cancellationToken);         // base IndexedDB du site
        logger.LogInformation("Note {NoteId} created", created.Id);           // /var/log/hackweb/<id>.log
        return RedirectToAction(nameof(Details), new { id = created.Id });   // post/redirect/get
    }
}
```

- An action can be asynchronous (`Task<IHackActionResult>`, `Task<T>` for
  an API); a `CancellationToken` parameter receives `RequestAborted`. The
  `Async` suffix of a method name is removed from the action name, as in
  ASP.NET Core.

- An action without an attribute route follows the conventional route:
  `/Notes/Edit/4` → `NotesController.Edit(4)`. Two actions of the same name are
  told apart by `[HttpGet]`/`[HttpPost]`.
- `[ValidateAntiForgeryToken]` refuses a `POST` without a valid token (`400`). The
  OS browser keeps the anti-forgery cookie between the page and the
  submission; `curl -d` without a token receives `400`, as on an ASP.NET site.
- Validation comes from the model's `DataAnnotations` (`[Required]`,
  `[StringLength]`, `[Display(Name)]`...).
- Session: `HttpContext.Session.SetString("displayName", ...)`, read everywhere
  through `HttpContext.Session.GetString(...)` (`IntranetSession` in the example).

## 5. REST API

`HackApiController` (ASP.NET's `[ApiController]`): JSON body by default,
automatic `400` with an RFC 9457 problem if `ModelState` is invalid, and
error statuses without a body turned into problems. An API action must have
an attribute route (`HOSWEB0007`).

```csharp
[Route("api/notes")]
public sealed class NotesApiController(NoteStore store) : HackApiController
{
    [HttpGet] public IReadOnlyList<Note> List([FromQuery] string? q = null) => ...;
    [HttpGet("{id:int}", Name = "GetNote")]
    public IHackActionResult Get(int id) => store.Find(id) is Note note ? Ok(note) : NotFound();
    [HttpPost]
    public IHackActionResult Create(Note note) =>
        CreatedAtAction(nameof(Get), new { id = store.Add(note).Id }, note);      // 201 + Location absolue
    [HttpPut("{id:int}")] public IHackActionResult Replace(int id, Note note) => ...;
    [HttpPatch("{id:int}")]
    public IHackActionResult Patch(int id, NotePatch patch)
    {
        Note note = ...;                                   // fusion du corps partiel dans l'entité
        if (!TryValidateModel(note)) { return ValidationProblem(); }              // ADR 0087 §3
        return Ok(store.Update(note));
    }
    [HttpDelete("{id:int}")] public IHackActionResult Delete(int id) => store.Remove(id) ? NoContent() : NotFound();
}
```

A known path with the wrong method answers `405` with `Allow`
(`DELETE /api/notes`). `OPTIONS` answers `204`, `HEAD` uses the `GET`.

## 6. Views

The two kinds of views mix; choose by taste, the engine is the same.

| | `.cshtml` (ADR 0086) | `.razor` (ADR 0083) |
|---|---|---|
| Model | `@model HomeViewModel` | `@inherits HackView<Note>` |
| Layout | `_ViewStart.cshtml` (`Layout = "_Layout"`) or `Layout = "..."` | `@layout _Layout` |
| Link | `@Html.ActionLink("Notes", "Index", "Notes")` | `<HackLink Action="Index" Controller="Notes">` |
| Form | `@using (Html.BeginForm("Profile", "Home")) { ... }` | `<HackForm Action="Edit" RouteValues="...">` |
| Bound field | `@Html.TextBoxFor(m => m.Name)`, `ValidationMessageFor` | `<input name="Title" value="@Model.Title" />`, `<HackValidationMessage For="Title" />` |
| Section | `@section Scripts { ... }` | `<HackSection Name="Scripts">` |
| Partial | `@await Html.PartialAsync("_Card", x)` | `<HackPartial Name="_NoteCard" Model="note" />` |

In the example, the layout is a `.razor` component (`Views/Shared/_Layout.razor`,
class `HackLayout`, code in `_Layout.razor.cs`); `.cshtml` views find it
through `_ViewStart.cshtml`, `.razor` views through `@layout _Layout`. It
renders the title (`ViewData.Title`), the navigation (active link based on
`Request.RouteValues["controller"]`), the session name and the view's
`Scripts` section.

Rules to know:

- No inline style or script (ADR 0007, checked at build time for
  `.razor` and `.cshtml`): CSS goes in `wwwroot/css`, JavaScript in
  `wwwroot/js`, referenced through `Url.Content("~/...")`. `<script src>` without a body
  is allowed. Scoped CSS (`.razor.css`) does not apply to a view: the page
  is rendered in the browser's iframe, not in the OS.
- `View("text")` is a **view name**; for a `string` model, go through
  a model class.
- A value written by C# is HTML-encoded (`·` becomes `&#xB7;`, which the
  browser displays normally).

## 7. Page JavaScript

An HTML page is displayed in an isolated iframe (ADR 0075): its JavaScript
runs, but everything it requests goes through the OS browser and the
simulated network. `fetch`, `XMLHttpRequest`, `document.cookie`, `localStorage`,
`history.replaceState` work; `WebSocket`, `window.open` do not.

```js
// wwwroot/js/notes.js : épingle une note sans recharger la page
const response = await fetch(api + "/" + card.dataset.noteId, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ pinned: pinned })
});
```

- API URLs pass from the server to the script through `data-*` attributes
  (`data-api="@Url.Content("~/api/notes")"`), not through inline script.
- Plan for the no-script case (`GET` form of `/People`): the server already
  renders the result.
- A script navigation (`location.href = ...`) cannot be followed
  in the iframe (ADR 0079): update the page in place, or use a
  link or a form.

## 8. Interactive Blazor pages

`BlazorRoot` gives the site a root component. Paths that no route of the
site takes (`/board`, `/board/tasks/5`) answer the Blazor host page; the
browser recognizes it, starts a **renderer process** for the site and draws
the component in the page area, without an iframe (ADR 0084).

```razor
@* Board/BoardApp.razor *@
@inherits HackComponentBase
<HackSiteRouter AppAssembly="typeof(BoardApp).Assembly" DefaultLayout="typeof(BoardLayout)">
    <NotFound>...</NotFound>
</HackSiteRouter>

@* Board/Pages/BoardPage.razor *@
@page "/board"
@inherits HackComponentBase
<HackPageTitle Title="Board - Intranet" />
...
```

```csharp
// BoardPage.razor.cs
[CascadingParameter] private HackWebSite? Site { get; set; }

IReadOnlyList<BoardTask>? tasks = await Site.Http.GetFromJsonAsync("api/tasks", IntranetJsonContext.Default.IReadOnlyListBoardTask);
HackHttpResponse created = await Site.Http.PostAsync("api/tasks", HackJsonContent.Create(draft, IntranetJsonContext.Default.BoardTask));
```

- Each component carries `@inherits HackComponentBase` (or `HackSiteLayoutBase`
  for a layout): its events run under the identity of the renderer
  process. The rule is checked per file (`AmbientFrameSourceTests`).
- Pages have no access to the site's services: they call its API through
  `Site.Http`, with the **tab's cookies**. The board thus reads `/api/me`
  and sees the name chosen on `/Home/Profile`: same origin, same session.
- A page is recreated on each navigation: durable state lives in an object
  of the layout cascaded to the pages (`BoardSession`).
- **Linking MVC and Blazor** (ADR 0087 §1): a link from an MVC page to `/board`
  is an ordinary link; a link from a Blazor page to an MVC page carries
  `target="_top"` (`<a href="/Notes" target="_top">`), otherwise the Blazor router
  takes it for one of its own pages. This is Blazor's rule: a link to another
  frame is not intercepted.
- Do not declare an `@page` route that overlaps an MVC route: the site's routes
  take precedence over the host page.

## 9. Testing a site

Three levels, all in the example:

1. **In memory** (`Tests/HackerOs.Samples.Intranet.Tests`, like ASP.NET Core's
   `TestServer`): the site is built without an OS or listener and receives
   requests.

   ```csharp
   HackWebApplication app = HackWebHost.CreateApplication(
       new IntranetSite(), HackWebGeneratedRoutes.Table, views: HackWebGeneratedRoutes.Views);
   HackWebResponseData home = await app.ProcessAsync(HackWebRequestData.Create("GET", "http://intranet.hack/"));
   ```

   `HackWebGeneratedRoutes` is internal to the site's assembly:
   `<InternalsVisibleTo Include="HackerOs.Samples.Intranet.Tests" />`.
   `IntranetClient` keeps cookies there like a tab and finds the
   anti-forgery field of a form.
2. **In a full OS** (`Tests/HackerOs.Platform.WorkerHost.Tests/MvcSiteTests`):
   `curl` starts the site on demand, on the main thread and in the `web`
   worker.
3. **In Chromium** (`Tests/HackerOs.UI.E2E.Tests/BrowserIntranetTests`): the
   OS browser walks through the example (scripts, form, session, Blazor
   board, return through `target="_top"`). `BrowserSiteDataTests` checks there
   the site's IndexedDB database, autofill, the debug log
   and Diagnostics.

In memory, `HackWebDb` is an in-memory database specific to the application: tests
have nothing to configure ([`asp-hack-site-data.md`](asp-hack-site-data.md#outside-hackeros-and-in-tests)).

## 10. Installing the site in the OS

A built-in site is a `ProjectReference` of the Ecosystem host (the
`BuiltInApplications.json` catalog is drawn from it) and of `test/test` with
`<BlazorWebAssemblyLazyLoad Include="Mon.Site.dll" />`: the assembly is
loaded only on the first request. Without `IsSystemApp="true"`, the site is
uninstallable like an ordinary app. The Ecosystem tests count the
manifests and the lazy assemblies (`BuildKnownLazyAppsTests`,
`EcosystemBootCoordinatorTests`): update them.

## 11. Debugging

| Need | Tool |
|---|---|
| Is the site running? | `ps aux`, task manager, `netstat -tlnp`, `cat /proc/net/tcp` |
| Does the name resolve? | `nslookup intranet.hack`, `/etc/hosts` wins |
| What the server answers | `curl -i`, browser source view (`Ctrl+U`) |
| Script requests and errors | `F12`: Network and Console tabs |
| Server exception | `UseExceptionPage()` displays it; otherwise a bare `500` and a line in the OS log |
| Site traces | `builder.Logging.EnableDebugLog()`, then `tail /var/log/hackweb/<id>.log`: requests, `ILogger<T>`, exceptions |
| Contents of the site database | app "IndexedDB Diagnostic", *Website databases* section (stores, record count, size; clearing) |
| Blazor page that crashes | the browser's "Aw, Snap!"; the renderer process ends in fault, the OS carries on |
| Restarting the site | `kill` the process: the next request restarts it (in-memory sessions and data lost, `HackWebDb` database kept) |

## Known limits

- **Data.** The example's notes are in its `HackWebDb` database (ADR 0088);
  the board cards, announcements and directory stay in the memory of the
  site's process and return to their initial state when it stops.
- Forms of a Blazor site do not have the browser's autofill
  (ADR 0088 §6).
- No tag helpers, Razor Pages (`@page` in a `.cshtml`), `TempData`
  or `ViewBag`; no `Development`/`Production` environments.
- The `Site.Http` requests of Blazor pages do not appear in the Network
  tab.
- Each `curl` has its own cookie jar. To replay a session, pass the
  cookie by hand: `curl -H 'Cookie: .HackSession=…'` is sent to the site,
  merged with the jar (what is typed wins), to the requested host only,
  and never stored (ADR 0089).
- Compiling a site **inside the OS** remains out of scope (ADR 0076 §8).

## Tasks

- [x] Read the documents and ADR 0073 to 0086 of the web phase.
- [x] Intranet example: definition, services, page and API controllers, `.cshtml` and `.razor` views in a common layout, `wwwroot` (CSS, three scripts), session, anti-forgery, Blazor board.
- [x] Links from a Blazor site to its MVC pages through `target` (ADR 0087 §1).
- [x] Views compilable in a shipped app: base classes without trimming annotation (ADR 0087 §2).
- [x] `TryValidateModel` on controllers (ADR 0087 §3).
- [x] Example installed in the Ecosystem and `test/test`, Ecosystem tests updated.
- [x] In-memory tests (`HackerOs.Samples.Intranet.Tests`) and Chromium E2E (`BrowserIntranetTests`).
- [x] `web/12`: example notes in `HackWebDb`, debug log, `GET /api/notes?author=` filter through the `byAuthor` index ([`asp-hack-site-data.md`](asp-hack-site-data.md)).
- [x] This guide, `sdk-reference.md`, ADR 0087, plan, index and phase documents updated.
