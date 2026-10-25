# ASP.Hack: `.razor` views, anti-forgery and `wwwroot`

Work item `web/7-razor-views` of the web phase. Decisions: [ADR 0076](../dev/adr/0076-asp-hack-mvc.md)
§5 to §7 and [ADR 0083](../dev/adr/0083-asp-hack-razor-views.md). Foundation: [`asp-hack-mvc.md`](asp-hack-mvc.md)
(pipeline, routing, controllers). Plan: [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md). The
`.cshtml` views, which share this engine and mix with `.razor` views, are described in
[`asp-hack-cshtml-views.md`](asp-hack-cshtml-views.md) (`web/8`, ADR 0086).

## Purpose

A page controller (`HackController`) answers `View()`: a Razor component
from the project's `Views/` folder, compiled at build time, rendered to static HTML by
Blazor's `HtmlRenderer` in the site's process, with its layout, sections
and partials. Forms carry an anti-forgery token
and the project's `wwwroot/` is served read-only by `UseStaticFiles()`.
Nothing is searched by reflection: the source generator of ADR 0082 also writes
the view table.

## Example

```text
Intranet/
  IntranetApp.cs            [AppManifest] partial WebAppDefinition
  Controllers/NotesController.cs
  Views/
    _Imports.razor          @using HackerOs.AppSdk.Web.Views, @using Intranet.Views.Shared
    Shared/_Layout.razor
    Notes/Index.razor, Notes/Edit.razor, Notes/_NoteCard.razor
  wwwroot/css/site.css, wwwroot/js/notes.js
```

```csharp
public override void Configure(HackWebApplicationBuilder builder)
{
    builder.Services.AddSingleton<NoteStore>();
    HackWebApplication app = builder.Build();
    app.UseExceptionPage();
    app.UseStaticFiles();          // wwwroot/ embarqué, avant les contrôleurs
    app.UseCookieSession();
    app.MapControllers();
}

public sealed class NotesController(NoteStore store) : HackController
{
    public IHackActionResult Index() => View(store.All);                 // Views/Notes/Index.razor

    [HttpGet]
    public IHackActionResult Edit(int id) => store.Find(id) is Note n ? View(n) : NotFound();

    [HttpPost, ValidateAntiForgeryToken]
    public IHackActionResult Edit(Note note)
    {
        if (!ModelState.IsValid) { return View(note); }                  // le formulaire ré-affiché avec ses erreurs
        store.Save(note);
        return RedirectToAction(nameof(Index));
    }

    public IHackActionResult Card(int id) => PartialView("_NoteCard", store.Find(id));
}
```

```razor
@* Views/Shared/_Layout.razor *@
@inherits HackLayout
<!DOCTYPE html>
<html><head><title>@(ViewData.Title ?? "Intranet")</title>
<link rel="stylesheet" href="@Url.Content("~/css/site.css")" /></head>
<body><nav><HackLink Action="Index" Controller="Notes">Notes</HackLink></nav>
<main>@Body</main>
<HackRenderSection Name="Scripts" Required="false" /></body></html>

@* Views/Notes/Edit.razor *@
@inherits HackView<Note>
@layout _Layout
@{ ViewData.Title = Model.Title; }
<HackValidationSummary ModelOnly="true" />
<HackForm Action="Edit" RouteValues="@HackRoute.Values(new { id = Model.Id })" class="edit">
    <input name="Title" value="@Model.Title" /> <HackValidationMessage For="Title" />
    <button type="submit">Save</button>
</HackForm>
<HackSection Name="Scripts"><script src="@Url.Content("~/js/notes.js")"></script></HackSection>
```

## Architecture

```text
HackController.View(...)  → HackViewResult                      Views/HackViewResult.cs
  └─ HackViewEngine (service du site)                           Views/HackViewEngine.cs
       ├─ FindView : HackViewTable (générée) : Views/<Contrôleur>/<Nom>, Views/Shared/<Nom>, ou chemin
       ├─ EnsureModelType : HackView<TModel> contre le modèle
       └─ RenderAsync : HtmlRenderer sur la portée DI de la requête
            ├─ passe 1 : la vue (HackViewHost → CascadingValue<HackViewContext> → composant)
            ├─ passe 2..n : chaque @layout, avec le HTML précédent en Body
            └─ sections définies et non rendues → erreur
HackViewContext : requête, action, modèle, ModelState, ViewData, Url, Layout, sections, FindPartial
Helpers (Views/HackViewHelpers.cs) : HackLink, HackForm, HackAntiforgeryToken, HackValidationMessage,
  HackValidationSummary, HackSection, HackRenderSection, HackPartial, HackRoute.Values
Antiforgery/ : HackAntiforgery, HackAntiforgeryOptions, [ValidateAntiForgeryToken], [IgnoreAntiforgeryToken]
Hosting/HackStaticFiles.cs : HackStaticFileOptions, HackStaticFileMiddleware (UseStaticFiles)
Générateur : HackWebGeneratedRoutes.Views, passé par <Nom>WebHost ; build : wwwroot/** embarqué
```

## Views

- **Base classes** (`HackerOs.AppSdk.Web.Views`): `HackView` (untyped
  `Model`), `HackView<TModel>` (typed `Model`), `HackLayout` (`Body`). All
  expose `ViewContext`, `HttpContext`, `Request`, `ViewData`, `ModelState`,
  `Url`, `Layout`. Any component can also serve as a view (without these
  shortcuts): `View<MyComponent>(model)`.
- **Lookup.** `View()` takes the action name; `View("Edit")` searches
  `Views/<Controller>/Edit.razor` then `Views/Shared/Edit.razor`;
  `View("~/Views/Notes/Edit.razor")` or `View("/Views/Notes/Edit")` designates a
  path. A view that is not found is an `InvalidOperationException` that lists
  the locations searched (`500` error page with `UseExceptionPage`).
- **Model.** `View(model)`; a `HackView<TModel>` view refuses a model of a
  different type (`null` is accepted for a reference type). Careful, as in
  ASP.NET MVC: `View("text")` is a **view name**; for a `string` model,
  write `View(viewName: null, model: text)` or use a model class.
- **`ViewData`.** `HackViewDataDictionary` (case-insensitive keys, `Title`) shared
  by the controller, the view, its partials and its layouts. A missing key reads as
  `null` (`web/8`, ADR 0086), like `ViewData["x"]` in ASP.NET.
- **Layout.** `@layout _Layout` in the view (or in the folder's `_Imports.razor`);
  a layout can have its own `@layout` (nesting). The layout
  is rendered **after** the view: it sees `ViewData["Title"]` and the sections. The
  view can change `Layout` in its code (`Layout = null`: no layout). A
  layout without `@Body` is an error.
- **Sections.** `<HackSection Name="Scripts">...</HackSection>` in the view,
  `<HackRenderSection Name="Scripts" Required="false" />` in the layout. A
  section that is defined but never rendered, or required but missing, is an error
  (same rules as ASP.NET MVC).
- **Partials.** `<HackPartial Name="_NoteCard" Model="note" />` (without `Model`:
  the page's model); `PartialView(...)` from the controller renders a view
  without a layout (fragment for a script). A typed view can also be used
  as a component: `<_NoteCard Model="note" />`.
- **Injection.** `@inject` receives the services of the request's scope (the
  same instances as the controller).

## URL and form helpers

| Component | Renders |
|---|---|
| `<HackLink Action Controller RouteValues Route Href>` | `<a href="...">`; `href` produced by the route table (`Url.Action`, `Url.RouteUrl`) or `Href` (`~/` resolved). Extra attributes copied. |
| `<HackForm Action Controller RouteValues Route Href Method="post" Antiforgery="true">` | `<form method action>` plus the anti-forgery field (except `GET` or `Antiforgery="false"`). |
| `<HackAntiforgeryToken />` | `<input type="hidden" name="__RequestVerificationToken">` for a hand-written `<form>`. |
| `<HackValidationMessage For="Title" />` | `<span class="field-validation-error">first error</span>`, or an empty `field-validation-valid`. |
| `<HackValidationSummary ModelOnly />` | `<div class="validation-summary-errors"><ul><li>…</li></ul></div>`, nothing if the model is valid. |
| `HackRoute.Values(new { id = 3 })` | The route values of an anonymous object for `RouteValues` (trimming-safe). |

In view code: `Url.Action(...)`, `Url.RouteUrl(...)`, `Url.Content("~/...")`.

## Anti-forgery

`HackAntiforgery` follows ASP.NET Core's scheme (ADR 0083 §5): `.HackAntiforgery`
cookie (`HttpOnly`, `SameSite=Strict`) with a random secret,
request token signed with HMAC-SHA256 per form, constant-time comparison.
The token travels in the `__RequestVerificationToken` field or the
`RequestVerificationToken` header. `[ValidateAntiForgeryToken]` on an action (or a
controller) refuses a request without a valid token with a text `400 Bad Request`
whose body says why; `GET`, `HEAD`, `OPTIONS`, `TRACE` are never
checked; `[IgnoreAntiforgeryToken]` exempts an action. Names can be changed through
`builder.Antiforgery`. The key lives in the process's memory: a restarted site
refuses the forms displayed before (reload the page).

In the OS, `curl` has a cookie jar per execution: `curl -d ... /Notes/Edit/1`
without a token gets `400`, like an ASP.NET site; `-H 'Cookie: .HackAntiforgery=…'`
with the form's token replays a hand-made submission (ADR 0089). The OS
browser keeps cookies between the page and the form submission.

## The project's `wwwroot`

- The build (`build/HackerOs.Apps.targets`, target `_HackerOsEmbedWebRoot`)
  embeds `wwwroot/**` of a project that references `HackerOs.AppSdk.Web` as
  `wwwroot/<path>` resources; `<HackerOsEmbedWebRoot>false</HackerOsEmbedWebRoot>`
  (or `true` for a project that does not reference the library) forces the
  choice. These files are not project content: they do not enter
  the host's static web resources.
- `app.UseStaticFiles()` serves them read-only: `Content-Type` by
  extension, `ETag` (`304` on `If-None-Match`), `Cache-Control: public, max-age=3600`,
  `index.html`/`index.htm` of a folder, `405` other than `GET`/`HEAD`. A request
  with no file continues to the controllers. Options: `Assembly`,
  `ResourcePrefix`, `RequestPath` (`/static`), `DefaultFileNames`, `CacheControl`.
- A layout references these files through `Url.Content("~/css/site.css")`. The
  rule of ADR 0007 allows `<script src="...">` without a body in a `.razor`;
  the JavaScript itself stays in `wwwroot/js/`.

## Tests

- `Tests/HackerOs.AppSdk.Web.Tests` (102): `Views/RazorViewTests` (generated
  table, nested layout, title, sections, partials, helpers, valid and
  invalid form, token in field and in header, refusal, `[IgnoreAntiforgeryToken]`,
  typed view, partial, `Shared/`, absolute path, `@inject`, errors, static
  files, `304`, `405`, paths outside `wwwroot`), `Generator/WebRouteGeneratorTests`
  (view table and class names from the Razor compiler, `@namespace`,
  `_Imports.razor`).
- `Tests/HackerOs.Platform.WorkerHost.Tests/MvcSiteTests`: the view, its layout,
  the `wwwroot` and the refusal of a `POST` without a token through `curl` in a full OS,
  on the main thread and in the `web` worker.

## Known limits

- No `TempData`, dynamic `ViewBag` or environments.
- A `@namespace` set outside the file or a `_Imports.razor` (for example
  in the `.csproj`) is not read by the generator: the view is then
  not found by name (use `View<T>()`).
- `HEAD` of a static file does not send `Content-Length`.
- Views are static HTML: no Blazor events; the `wwwroot` JavaScript and
  interactive Blazor sites (`web/9`) cover interactivity.

## Tasks

- [x] `HackViewEngine`, `HackViewContext`, `HackView`/`HackView<TModel>`/`HackLayout`, nested layouts rendered after the view, sections, partials, `@inject`.
- [x] `HackViewResult`, `View`/`PartialView`/`ViewData` on `HackController`, explicit errors (view not found, model, section, `@Body`).
- [x] Helpers `HackLink`, `HackForm`, `HackAntiforgeryToken`, `HackValidationMessage`, `HackValidationSummary`, `HackSection`, `HackRenderSection`, `HackPartial`, `HackRoute.Values`.
- [x] `HackAntiforgery`, `[ValidateAntiForgeryToken]`, `[IgnoreAntiforgeryToken]`.
- [x] View table in the source generator (`HackWebGeneratedRoutes.Views`), generated host, `HackWebApplicationBuilder`/`HackWebHost` with `HackViewTable`.
- [x] `wwwroot` embedded at build time, `UseStaticFiles()`, ADR 0007 rule adjusted for `<script src>`.
- [x] Pipeline, generator and end-to-end tests (`curl`, two modes).
- [x] ADR 0083, this document, index, plan, `asp-hack-mvc.md`.
