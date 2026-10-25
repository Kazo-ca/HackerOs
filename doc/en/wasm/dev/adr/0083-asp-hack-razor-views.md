> [Version française](../../../../fr/wasm/dev/adr/0083-asp-hack-razor-views.md)

# ADR 0083: ASP.Hack `.razor` views, anti-forgery and embedded `wwwroot`

## Status

Accepted on 2026-10-16, with the `web/7-razor-views` work item.

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Completes:** [ADR 0076](0076-asp-hack-mvc.md) §5 (`View`, `PartialView`), §6
  (anti-forgery token) and §7 (`.razor` views); [ADR 0073](0073-web-apps.md)
  §2 (project `wwwroot`, read-only); [ADR 0082](0082-asp-hack-route-source-generator.md)
  (the generator also produces the view table).
- **Amends:** [ADR 0007](0007-enforce-collocated-razor-assets.md): the build check
  admits a `<script src="...">` reference without a body (§6).
- **Documentation:** [`asp-hack-razor-views.md`](../../framework/asp-hack-razor-views.md).

## Context

ADR 0076 §7 sets the principle: a view is a Razor component compiled at build time,
rendered as static HTML by `HtmlRenderer`, found by the convention
`Views/<Controller>/<Action>.razor`. It remains to decide how `View()` finds the
component without reflection (the route generator excluded reflection at
startup, ADR 0082), how a layout, sections and partials
work with a single-pass Blazor rendering, how the anti-forgery
token travels in the simulated network, and how a project's `wwwroot`
is served while a site has no `filesystem.*` capability
(ADR 0073 §3).

## Decision

### 1. Rendering

`HackViewEngine` renders a view with `Microsoft.AspNetCore.Components.Web.HtmlRenderer`
(static, without circuit or interactivity), whose services are the **request's DI
scope**: a view's `@inject` receives the same instances as the
controller. The page context (`HackViewContext`: request, action, model,
`ModelState`, `ViewData`, URL, layout, sections) is provided to every component of
the page by a fixed cascading value.

### 2. View table generated at build time

The source generator of ADR 0082 reads the `.razor` files that the Razor SDK
passes it as `AdditionalFiles` (with their relative `TargetPath` path)
and writes `HackWebGeneratedRoutes.Views : HackViewTable`, one
`HackViewDescriptor("Home/Index", typeof(Views.Home.Index))` per file under
`Views/` (except `_Imports.razor`). It names the class as the Razor compiler
does (`<RootNamespace>.<folders>.<file>`, sanitized identifiers, or the
`@namespace` of the file or of an `_Imports.razor` above it). The generated host
`<Name>WebHost` passes the table to `HackWebHost`. Only the table is read at
runtime; a component outside `Views/` remains usable by type
(`View<MyComponent>(model)`).

### 3. View, model, layout, sections, partials

- Base classes: `HackView` (untyped model), `HackView<TModel>` (typed
  `Model`; a model of another type is an explicit error, as in ASP.NET
  MVC), `HackLayout` (`@Body`). Any component can serve as a view; these classes
  add the MVC shortcuts (`ViewData`, `Url`, `ModelState`, `Request`).
- **The layout renders after the view**, in a separate pass that receives the view's
  HTML as `Body`, then the layout's layout, and so on (as a
  `.cshtml` executes before its `_Layout`). Thus the view can set
  `ViewData["Title"]`, change `Layout` or define a section that the layout
  will read. The layout comes from `@layout` (attribute `[Layout]`), modifiable by the view.
- Sections: `<HackSection Name>` in the view, `<HackRenderSection Name Required>`
  in the layout; a section defined and never rendered, or required and absent,
  is an error, as in ASP.NET MVC. A layout without `@Body` is an error.
- Partials: `<HackPartial Name Model>` looks for the view in the controller's
  folder then `Shared/`, and renders it **in the same pass** with a
  derived context (same request and `ViewData`, other model). `PartialView()` renders a view
  without layout or section checks.
- Lookup: `View()` → `Views/<Controller>/<Action>`, then `Views/Shared/<Action>`;
  a path (`~/Views/X/Y.razor`, `/Views/X/Y`) is taken as is. The error
  lists the locations searched.

### 4. `HackController`

`View()`, `View(model)`, `View(name)`, `View(name, model)`, `View<TView>(model)`,
the equivalent `PartialView`s, `ViewData`. `HackApiController` has no views.
As in ASP.NET MVC, `View("text")` designates a **view name**: a `string`
model is passed through `View(viewName: null, model: text)` or in a class.

### 5. Anti-forgery

`HackAntiforgery` (site service) follows the ASP.NET Core scheme: a 32-byte
random secret in a `.HackAntiforgery` cookie (`HttpOnly`,
`SameSite=Strict`, `Path=/`) and, in each form, a request token
`nonce ‖ HMAC-SHA256(key, secret ‖ nonce)` (field `__RequestVerificationToken`,
or header `RequestVerificationToken` for a script). The key is drawn at
startup of the site's process and lives in memory: after a restart, an
already displayed form is refused (`400`) and must be reloaded, as without a
persistent key ring. `[ValidateAntiForgeryToken]` is an action filter
(`400 Bad Request` text, reason included; `GET`/`HEAD`/`OPTIONS`/`TRACE` never
verified); `[IgnoreAntiforgeryToken]` cancels it for an action. `<HackForm>`
writes the hidden field unless `Antiforgery="false"` or `Method="get"`;
`<HackAntiforgeryToken>` provides it to a hand-written `<form>`.

### 6. Embedded `wwwroot`, served read-only

- `build/HackerOs.Apps.targets` embeds `wwwroot/**` of a project that references
  `HackerOs.AppSdk.Web` as `wwwroot/<path>` resources (property
  `HackerOsEmbedWebRoot` to force or forbid), and removes them from the content:
  a site's `wwwroot` is never a static web resource of the host
  (`_content/`). The Razor class library of a windowed app keeps its usual
  `wwwroot`.
- `app.UseStaticFiles()` (`HackStaticFileMiddleware`) serves these resources:
  `Content-Type` by extension (`SimulatedFileTypes`), `ETag`, `304`,
  `Cache-Control: public, max-age=3600`, `index.html` of a folder, `405` other than
  `GET`/`HEAD`; a request with no file continues to the controllers. The
  names are read once at startup and compared exactly (`..` cannot reach
  anything).
- The check of ADR 0007 (no inline CSS/JS in a `.razor`) now admits
  `<script src="...">` **without a body**: a layout thus links the scripts
  of the site's `wwwroot`, like any web page; `<style>`, `style=`, `<script>`
  with a body and `onxxx=` remain refused.

## Consequences

- `HackerOs.AppSdk.Web` references `Microsoft.AspNetCore.Components.Web` (for
  `HtmlRenderer` and the components); test projects that have views
  move to the `Microsoft.NET.Sdk.Razor` SDK.
- `HackWebApplicationBuilder` and `HackWebHost` gain an overload with
  `HackViewTable` and the definition type (`wwwroot` assembly); the
  old signatures remain (empty table, no `wwwroot`).
- The `web/8` work item (`.cshtml`) can reuse `HackViewEngine.FindView`,
  `HackViewContext` and anti-forgery: only the view's base class
  changes.
- Accepted limits: no `TempData` or dynamic `ViewBag`; no nested
  `@section` in a partial; a `@namespace` declared elsewhere than in the file or an
  `_Imports.razor` (for example in `.csproj`) is not
  read; `HEAD` of a static file does not send `Content-Length`.
