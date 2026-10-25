> [Version française](../../../../fr/wasm/dev/adr/0076-asp-hack-mvc.md)

# ADR 0076: ASP.Hack MVC, pipeline, routing and views

## Status

Accepted on 2026-10-16 (proposed the same day, then validated by Benoit with the
questionnaire of section 4 of the plan).

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Depends on:** ADR 0073 (`Web` kind), ADR 0074 (`HackHttpListener`).

## Context

Benoit wants a layer on top of the SDK that takes up the way of working
of ASP.NET MVC (`GET`, `POST`, `PUT`, `PATCH`, `DELETE` methods...), without
taking all of ASP.NET, with `.cshtml`-style views and a controller behind.
ASP.NET Core itself does not run in Blazor WebAssembly (no Kestrel and no
`Microsoft.AspNetCore.App` reference).

## Decision

### 1. Library

`Shared/HackerOs.AppSdk.Web` (SDK library, `HackerOsSdkLibrary`), which
depends only on `HackerOs.AppSdk`. The site is a process that opens an
`HackHttpListener` on its `Urls` and passes each request to the pipeline.

### 2. Startup and pipeline

```csharp
public override void Configure(HackWebApplicationBuilder builder)
{
    builder.Services.AddSingleton<NotesStore>();
    var app = builder.Build();
    app.UseExceptionPage();     // page d'erreur détaillée en développement
    app.UseStaticFiles();       // wwwroot embarqué, lecture seule (ADR 0073 §2)
    app.UseCookieSession();
    app.Use(async (ctx, next) => { ...; await next(); });
    app.MapControllers();
    app.MapGet("/health", () => HackResults.Ok("up"));   // routes minimales
}
```

A middleware receives `HackHttpContext` (`Request`, `Response`, `Items`,
`RequestServices`, `Session`, `RequestAborted`).

### 3. Controllers and routing

- `HackController` (with views) and `HackApiController` (without views, errors as
  `application/problem+json`).
- Attributes with ASP.NET names: `[Route]`, `[HttpGet]`, `[HttpPost]`,
  `[HttpPut]`, `[HttpPatch]`, `[HttpDelete]`, `[HttpHead]`, `[HttpOptions]`,
  `[NonAction]`. Default conventional route
  `{controller=Home}/{action=Index}/{id?}`.
- Templates: literal segments, `{param}`, `{param?}`, default value,
  `{*rest}`, constraints `int`, `long`, `guid`, `bool`, `alpha`, `min`, `max`,
  `length`, `regex`.
- **The route table is generated at build time** by the manifest generator
  (no reflection at startup): compatible with trimming, faster, and
  route conflicts are build errors (`HOSWEB0001`...). The same
  generator can serve the compiler in the OS.
- Method not allowed: `405` with `Allow`; `HEAD` answers like `GET` without a
  body; `OPTIONS` lists the methods.

### 4. Model binding and validation

Sources: `[FromRoute]`, `[FromQuery]`, `[FromForm]`, `[FromBody]` (JSON through
`System.Text.Json`), `[FromHeader]`, `[FromServices]`, with inference like
ASP.NET (simple types from the route then the request, complex types from the
body for an API, the form for a controller with views). Validation through
`DataAnnotations` into `ModelState`; an `HackApiController` automatically answers `400`
if the model is invalid.

### 5. Results

`IHackActionResult`: `View`, `PartialView`, `Json`, `Content`, `File`,
`Redirect`, `RedirectToAction`, `LocalRedirect`, `StatusCode`, `Ok`, `Created`,
`CreatedAtAction`, `NoContent`, `NotFound`, `BadRequest`, `Unauthorized`,
`Forbid`, `Problem`. An action can also return an object (serialized as JSON)
or a `Task` of these types.

### 6. Services, session, security

- Dependency injection through `Microsoft.Extensions.DependencyInjection`, one
  scope per request; the SDK's `Hack*` facades remain available according to the
  site's capabilities.
- Cookie session (`HackSession`), in the memory of the site's process.
- Anti-forgery token for the views' `POST` forms
  (`[ValidateAntiForgeryToken]`).
- Simple action filters (`IHackActionFilter`, before/after, as an attribute or
  global).
- No full authentication in v1.

### 7. `.razor` views

A view is a Razor component compiled at build time with a `Model` parameter:

```razor
@inherits HackView<HomeModel>
@layout MainLayout
<h1>@Model.Title</h1>
<HackForm Action="Contact" Controller="Home"> ... </HackForm>
```

It is rendered as static HTML by `HtmlRenderer` (.NET, like ASP.NET 8's
server-rendered components), without interactivity. Lookup by
convention `Views/<Controller>/<Action>.razor` (`View()` without a type), or by type
(`View<HomeIndexView>(model)`). Helpers: `Url.Action`, `HackForm`,
`HackLink`, `HackValidationMessage`.

### 8. `.cshtml` views

The Razor compiler can produce C# from a `.cshtml` at build time. The
`web/8-cshtml-views` work item starts with a proof: compile a `.cshtml`
with `@model`, `@inject`, `@section`, `_Layout` and `_ViewStart` against a base
class `HackRazorPage<TModel>` of ASP.Hack, without referencing ASP.NET MVC. If the
proof succeeds, `View()` also finds `Views/<Controller>/<Action>.cshtml`. If
it fails, the other path is already chosen: an interpreted `.cshtml` engine,
specific to ASP.Hack, which covers a subset (`@model`, `@using`, `@if`,
`@foreach`, `@switch`, `@Model.X` expressions, `@section`, `_Layout`,
`_ViewStart`, partials) without arbitrary C# in the view; a short ADR then notes
the limits before the code. Compiling a `.cshtml` **in the OS** (Razor engine and
Roslyn in the browser) is out of scope, but choosing a base class specific to
ASP.Hack makes it possible later.

## Consequences

- The manifest generator gains the generation of routes for `Web` apps.
- MVC sites run in the shared web worker (ADR 0073 §1); their
  views do not need the DOM.
- Attribute names identical to ASP.NET live in the namespace
  `HackerOs.AppSdk.Web.Mvc` and cannot conflict (ASP.NET is not
  referenced).
