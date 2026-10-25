> [Version française](../../../../fr/wasm/dev/adr/0086-asp-hack-cshtml-views.md)

# ADR 0086: ASP.Hack `.cshtml` views compiled by the Razor compiler, without ASP.NET MVC

## Status

Accepted on 2026-10-16, with the `web/8-cshtml-views` work item.

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Completes:** [ADR 0076](0076-asp-hack-mvc.md) §8 (the feasibility proof succeeded:
  the fallback interpreted engine is not built); [ADR 0083](0083-asp-hack-razor-views.md)
  (same view engine, same generated table, same sections).
- **Amends:** [ADR 0007](0007-enforce-collocated-razor-assets.md): the build check
  also covers `.cshtml` files (§6); [ADR 0083](0083-asp-hack-razor-views.md): a missing key
  of `ViewData` reads as `null`, as in ASP.NET (§3).
- **Documentation:** [`asp-hack-cshtml-views.md`](../../framework/asp-hack-cshtml-views.md).

## Context

ADR 0076 §8 asks for a proof before the code: compile a `.cshtml` at build time
with `@model`, `@inject`, `@section`, `_Layout` and `_ViewStart` against a base
class of ASP.Hack, without referencing ASP.NET MVC (which does not run in Blazor
WebAssembly).

The proof (.NET SDK 10.0.402, `Microsoft.NET.Sdk.Razor` project) shows that the
SDK's Razor source generator **always** compiles a `.cshtml` in MVC mode,
even without a reference to MVC, and that the C# produced depends on ASP.NET only
by names, never by code:

- two assembly and class attributes:
  `Microsoft.AspNetCore.Razor.Hosting.RazorCompiledItemAttribute` and
  `RazorCompiledItemMetadataAttribute`;
- the attribute `Microsoft.AspNetCore.Mvc.Razor.Internal.RazorInjectAttribute` on
  each `@inject` property;
- three `using`s (`Microsoft.AspNetCore.Mvc`, `.Rendering`, `.ViewFeatures`);
- the default base class `Microsoft.AspNetCore.Mvc.Razor.RazorPage<TModel>`
  and five implicit `@inject`s (`Html`, `Json`, `Component`, `Url`,
  `ModelExpressionProvider`) whose types are MVC's;
- `Microsoft.AspNetCore.Mvc.Razor.HelperResult` for an inline template
  (`@<b>@item</b>`).

Everything else is a method call on the base class (`WriteLiteral`,
`Write`, `BeginWriteAttribute`, `DefineSection`, `PushWriter`...). A
`_ViewImports.cshtml` can replace the base class (`@inherits` with the
`TModel` token, which `@model` replaces) and each of the implicit `@inject`s (a
property of the same name wins). The proof therefore succeeded.

## Decision

### 1. Compilation

The SDK's Razor compiler compiles the project's `.cshtml` files at build time, as in
ASP.NET Core. ASP.Hack provides neither a compiler nor a view analyzer.

### 2. What the build adds to a site

For a project that references `HackerOs.AppSdk.Web` and contains `.cshtml` files, the
target `_HackerOsAspHackRazorViews` of `build/HackerOs.Apps.targets` adds:

- a **root** `_ViewImports.cshtml` (SDK file, `build/asp-hack/_ViewImports.cshtml`,
  linked into the project under that name): `@inherits HackRazorPage<TModel>` and the five
  implicit `@inject`s replaced by ASP.Hack's helpers;
- a C# file (`build/asp-hack/HackRazorCompat.cs`) of types **internal to the
  site**, with the names the generated C# expects: the attributes above, the class
  `HelperResult` (derived from `HackHelperResult`), an empty type in each of the
  three `using` namespaces, and the two assembly attributes that the
  SDK writes for a project that has `.cshtml` files
  (`ProvideApplicationPartFactoryAttribute`, `RelatedAssemblyAttribute`).

These `Microsoft.AspNetCore.*` names never leave the site: they are internal,
the `HackerOs.AppSdk.Web` library declares none of them, and a site cannot
reference ASP.NET MVC. The project must not have its own root
`_ViewImports.cshtml` (build error): its imports go in
`Views/_ViewImports.cshtml`. The `RAZORSDK1004` warning ("no MVC
support") is silenced: the absence of MVC is intended.

### 3. Base class and activation

`HackRazorPage` / `HackRazorPage<TModel>` (`HackerOs.AppSdk.Web.Views`) offers what
the generated C# calls and what an MVC view uses: `Model`, `ViewData`,
`ViewContext`, `Context`, `Request`, `ModelState`, `Layout` (a view name),
`RenderBody()`, `RenderSection(...)`, `IsSectionDefined`, `IgnoreBody`,
`IgnoreSection`. `ViewData["x"]` of a missing key is `null` (for both
kinds of views), as in ASP.NET. The engine creates the page, fills the `[RazorInject]` properties
(a page helper, otherwise a service from the request scope, like ASP.NET's
`RazorPageActivator`), then calls `ExecuteAsync`. HTML encoding
follows MVC (`Write` encodes, `WriteLiteral` does not, `IHackHtmlContent` content is
written as is; an attribute whose only value is `null` or `false`
disappears, `true` writes the attribute name).

### 4. View table

The route generator (ADR 0082) also reads the `.cshtml` files of `Views/` and adds
their class to `HackWebGeneratedRoutes.Views`, named as the Razor compiler
names it (`AspNetCoreGeneratedDocument.Views_Home_Index`, or the namespace of the
nearest `@namespace` followed by the folders). `_ViewImports.cshtml` files are not
views. A `.razor` and a `.cshtml` at the same path are a
`HOSWEB0008` error.

### 5. Rendering

`HackViewEngine` renders both kinds of views with the same algorithm as
ADR 0083: the view first, then each layout around the previous HTML, with
the same `ViewData` and the same section dictionary. The kinds can be mixed:
a `.cshtml` view can have a `.razor` layout and vice versa, a partial can
be of either kind. `.cshtml` specifics:

- the `_ViewStart.cshtml` files of `Views/` then of the folders down to the view's
  run before a view (not before a partial or a layout) and give it
  its initial value for `Layout`;
- `Layout = "_Layout"` is looked up as a view name (controller's folder,
  then `Shared/`);
- a `@section` section is rendered to HTML as soon as the page that defines it ends
  (ASP.NET renders it when the layout asks for it; the result is the same except for
  side effects); a nested layout can redefine a section it has rendered.

### 6. Rule of ADR 0007

The build check of ADR 0007 (no `<style>`, `style=`,
`<script>` with a body or `on...=` attribute) also covers `.cshtml` files: the
CSS and JavaScript of a view go in the site's `wwwroot`, as for a
`.razor` view (ADR 0083 §6).

### 7. Limits retained

No tag helpers (`asp-action`, `asp-for`): the SDK does not find them without
MVC; the HTML helpers (`Html.ActionLink`, `Html.BeginForm`, `Html.TextBoxFor`...)
and the `.razor` components of ADR 0083 (`@await Component.InvokeAsync<HackLink>(...)`)
replace them. No Razor Pages (`@page`), `ViewBag`, `TempData` or
MVC view components. A hard-coded `~/` in an attribute is not resolved
(no `UrlResolutionTagHelper`): use `@Url.Content("~/...")`.

## Consequences

- An ASP.NET developer finds their `.cshtml` files again: `@model`, `@inject`,
  `@section`, `@functions`, inline templates, `_Layout`, `_ViewStart`,
  `_ViewImports`, `Html.*`.
- Compilation **in the OS** remains possible later: the base class and
  the compatibility types are specific to ASP.Hack.
- The fallback interpreted engine (question 17 of the plan) is not built.
- If an SDK version changes the names expected by the generated C#, a
  site's build fails with a missing-type error: it is enough to add the name to the
  compatibility file.
