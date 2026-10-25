# ASP.Hack: `.cshtml` views

Work item `web/8-cshtml-views` of the web phase. Decisions: [ADR 0076](../dev/adr/0076-asp-hack-mvc.md)
§8 and [ADR 0086](../dev/adr/0086-asp-hack-cshtml-views.md). Foundation: [`asp-hack-razor-views.md`](asp-hack-razor-views.md)
(view engine, layouts, sections, anti-forgery, `wwwroot`) and [`asp-hack-mvc.md`](asp-hack-mvc.md).
Plan: [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md).

## Purpose

An ASP.Hack site can write its views as in ASP.NET Core MVC: `.cshtml` files
with `@model`, `@inject`, `@section`, `@functions`, inline
templates, `_Layout.cshtml`, `_ViewStart.cshtml` and `_ViewImports.cshtml`. The
.NET SDK's Razor compiler compiles them at build time, without ASP.NET MVC (which does not
run in the browser): the base class is ASP.Hack's `HackRazorPage<TModel>`.
`View()` finds either `Views/<Controller>/<Action>.razor`
or `.cshtml`, and the two kinds can be mixed (`.cshtml` view in a `.razor`
layout, `.razor` partial in a `.cshtml` view, etc.).

## Feasibility proof

ADR 0076 §8 asked for a proof before the code. It was done with the .NET SDK
10.0.402 on a minimal `Microsoft.NET.Sdk.Razor` project:

1. The Razor source generator compiles a `.cshtml` in MVC mode even without a
   reference to MVC; the C# produced calls methods of the base class
   (`WriteLiteral`, `Write`, `BeginWriteAttribute`, `DefineSection`,
   `PushWriter`...) and names only a few attributes, three namespaces,
   `HelperResult` and the types of the five implicit `@inject`s from ASP.NET.
2. A root `_ViewImports.cshtml` replaces the base class
   (`@inherits HackRazorPage<TModel>`, `TModel` being replaced by the type of
   `@model`) and the five implicit `@inject`s (a property of the same name wins).
3. With these names declared as internal types of the site, a view with `@model`,
   `@inject`, `@section`, `@foreach`, `@if`, `@functions`, a `_ViewStart` and a
   `_Layout` compiles and runs; the naming of classes
   (`AspNetCoreGeneratedDocument.Views_Home_Index`, or the namespace of the
   nearest `@namespace` followed by the folders) was verified.

The interpreted fallback engine (plan question 17) is therefore not built.

## Example

```text
Intranet/
  Controllers/NotesController.cs
  Views/
    _ViewImports.cshtml      @using Intranet.Models
    _ViewStart.cshtml        @{ Layout = "_Layout"; }
    Shared/_Layout.cshtml
    Notes/Index.cshtml, Notes/Edit.cshtml, Shared/_NoteCard.cshtml
  wwwroot/css/site.css, wwwroot/js/notes.js
```

```cshtml
@* Views/Shared/_Layout.cshtml *@
<!DOCTYPE html>
<html>
<head>
    <title>@(ViewData.Title ?? "Intranet")</title>
    <link rel="stylesheet" href="@Url.Content("~/css/site.css")" />
</head>
<body>
    <nav>@Html.ActionLink("Notes", "Index", "Notes")</nav>
    <main>@RenderBody()</main>
    @RenderSection("Scripts", required: false)
</body>
</html>

@* Views/Notes/Index.cshtml *@
@model IReadOnlyList<Note>
@inject NoteStore Store
@{ ViewData.Title = "Notes"; }
<h1>@Model.Count notes</h1>
<ul>
    @foreach (Note note in Model)
    {
        @await Html.PartialAsync("_NoteCard", note)
    }
</ul>
@section Scripts { <script src="@Url.Content("~/js/notes.js")"></script> }

@* Views/Notes/Edit.cshtml *@
@model Note
@Html.ValidationSummary(excludePropertyErrors: true)
@using (Html.BeginForm("Edit", "Notes", new { id = Model.Id }))
{
    @Html.LabelFor(m => m.Title)
    @Html.TextBoxFor(m => m.Title)
    @Html.ValidationMessageFor(m => m.Title)
    <button type="submit">Save</button>
}
```

The controller does not change: `return View(store.All);`, `return View(note);`.

## Architecture

```text
Build (build/HackerOs.Apps.targets, cible _HackerOsAspHackRazorViews)
  si le projet référence HackerOs.AppSdk.Web et a des .cshtml :
    + build/asp-hack/_ViewImports.cshtml lié en _ViewImports.cshtml racine
      (@inherits HackRazorPage<TModel>, Html/Json/Component/Url/ModelExpressionProvider)
    + build/asp-hack/HackRazorCompat.cs (types internes aux noms Microsoft.AspNetCore.*)
  compilateur Razor du SDK → classe par .cshtml
  générateur de routes (ADR 0082) → HackWebGeneratedRoutes.Views (genres Component et RazorPage)

Rendu (Shared/HackerOs.AppSdk.Web/Views/)
  HackViewEngine.RenderAsync
    ├─ vue .cshtml : _ViewStart.cshtml de Views/ puis des dossiers → Layout initial
    │                HackRazorPage.RenderAsync : @inject remplis, ExecuteAsync, sections → HTML
    ├─ vue .razor  : passe HtmlRenderer (ADR 0083)
    └─ layouts de l'intérieur vers l'extérieur, de l'un ou l'autre genre
HackRazorPage / HackRazorPage<TModel>    HackRazorPage.cs
HackHtmlHelper<TModel>, HackHtmlForm, HackJsonHelper, HackViewComponentHelper,
  HackModelExpressionProvider<TModel>    HackHtmlHelper.cs
IHackHtmlContent, HackHtmlString, HackHelperResult   HackHtmlContent.cs
HackViewKind, HackViewDescriptor.Kind, HackViewTable.Find(Type)   HackViewTable.cs
```

## `.cshtml` pages

- **Base class.** Every page derives from `HackRazorPage<TModel>` (without `@model`,
  `TModel` is `dynamic`, as in ASP.NET; prefer `@model`). Members:
  `Model`, `ViewData`, `ViewContext`, `Context` (the request), `Request`,
  `ModelState`, `Layout`, `Output`, `RenderBody()`, `RenderSection(...)`,
  `RenderSectionAsync(...)`, `IsSectionDefined`, `IgnoreBody`, `IgnoreSection`.
- **Encoding.** `@value` is HTML-encoded (accented letters stay
  readable); `IHackHtmlContent` (`Html.Raw`, `RenderBody()`, helpers,
  inline templates) and `MarkupString` are written as is. Attributes as in
  ASP.NET: a `null` part disappears with its space (`class="note @(x ? "done" : null)"`),
  an attribute whose only value is `null` or `false` disappears, `true` writes
  the name (`checked="@note.Done"` → `checked="checked"`).
- **`@inject`.** Resolved in the request's DI scope (the same instances as
  the controller). A missing service is an error that names the property.
- **`_ViewImports.cshtml`.** The one at the project root is provided by the SDK
  (a project that has one fails at build time); put your `@using`, `@inject` or
  `@namespace` in `Views/_ViewImports.cshtml` or a subfolder.
- **`_ViewStart.cshtml`.** Those of `Views/` and then of each folder down to that
  of the view run before a view (not before a partial or a layout); their
  `Layout` becomes the initial value of the view's. Their output is ignored.
- **Layout.** `Layout = "_Layout"` is a view name searched the way `View()` does
  (controller folder, then `Shared/`), or a path
  (`"~/Views/Shared/_Layout.cshtml"`); `Layout = null`: no layout. A `.cshtml`
  layout must call `RenderBody()` (or `IgnoreBody()`). A layout can
  have its own `Layout` (nesting) and redefine a section it
  rendered, for the layout around it:
  `@section Scripts { @RenderSection("Scripts", required: false) ... }`.
- **Sections.** `@section Name { ... }`; `@RenderSection("Name")` (required by
  default) or `RenderSection("Name", required: false)`. A section that is defined but
  never rendered is an error, as for `.razor` views. Sections are
  rendered to HTML as soon as the page that defines them ends.
- **`ViewData`.** Shared with the controller and the layouts; a missing key reads
  as `null` (`ViewData["Starts"]`), for both kinds of views.

## Helpers

| Member | Renders |
|---|---|
| `Html.Raw(html)`, `Html.Encode(v)` | HTML as is; encoded text. |
| `Html.ActionLink(text, action, controller?, routeValues?, htmlAttributes?)`, `Html.RouteLink(...)` | `<a href>` produced by the route table. |
| `using (Html.BeginForm(action?, controller?, routeValues?, method: "post", antiforgery: true, htmlAttributes?))` | `<form action method>`; at the end, the anti-forgery field (except `GET`) and `</form>`. |
| `Html.AntiForgeryToken()` | The hidden field for a hand-written `<form>`. |
| `Html.TextBox/Hidden/Password/TextArea/CheckBox(name, ...)` | Fields; after a refused `POST`, the posted value comes back and the error adds `input-validation-error`. |
| `Html.TextBoxFor/HiddenFor/TextAreaFor/CheckBoxFor(m => m.X)`, `NameFor`, `IdFor`, `ValueFor`, `DisplayNameFor`, `LabelFor` | Fields bound to the model by a member chain (`m => m.Address.City` → `Address.City`); `[Display(Name)]` respected. |
| `Html.ValidationMessage(name)`, `Html.ValidationMessageFor(m => m.X)`, `Html.ValidationSummary(excludePropertyErrors)` | Same HTML as `HackValidationMessage` and `HackValidationSummary` (ADR 0083). |
| `await Html.PartialAsync(name, model?)`, `await Html.RenderPartialAsync(...)` | A `.cshtml` or `.razor` partial (controller folder, then `Shared/`, or path). |
| `Json.Serialize(value)` | JSON with the site's options (camelCase). |
| `@(await Component.InvokeAsync<TComponent>(parameters))` | A Razor component rendered in the page, with the view's context (the `.razor` helpers of ADR 0083 work). The parentheses are needed because of the generic `<`. |
| `Url.Action(...)`, `Url.RouteUrl(...)`, `Url.Content("~/...")` | The site's `IHackUrlHelper`. |

`htmlAttributes` and the route values of an anonymous object go through
`HackRoute.Values(new { @class = "nav" })` (a `_` becomes `-`: `data_id` →
`data-id`).

## Mixing kinds

- A `.cshtml` view can have a `.razor` layout (`Layout = "_Layout"` finds
  `Shared/_Layout.razor`): its sections arrive in `<HackRenderSection>`.
- A `.razor` view can take a `.cshtml` layout by setting
  `Layout = typeof(AspNetCoreGeneratedDocument.Views_Shared__Layout)`; its
  `<HackSection>`s are rendered to HTML before the layout.
- `<HackPartial Name="_Card" />` in a `.razor` also renders a `.cshtml` partial.
- A `.razor` and a `.cshtml` at the same path is the build error `HOSWEB0008`.

## Tests

- `Tests/HackerOs.AppSdk.Web.Tests/Views/CshtmlViewTests`: generated table and
  class names, nested `_ViewStart`, `@model`, `@inject`, layout, title,
  section, conditional attributes, partials of both kinds, inline template,
  `Html.Raw`, `Component.InvokeAsync`, `Json.Serialize`, nested layouts and
  redefined section, mixing in both directions, `Layout = null`,
  `PartialView`, absolute path, model-bound form with anti-forgery
  and redisplayed errors, errors (body not rendered, required section missing,
  orphan section, wrong model), searched locations.
- `Generator/WebRouteGeneratorTests`: `.cshtml` class names (`@namespace`
  of the file and of a `_ViewImports.cshtml` with the folders, special
  characters), excluded imports, `HOSWEB0008`.
- `Tests/HackerOs.Platform.WorkerHost.Tests/MvcSiteTests`: a `.cshtml` view
  with `_ViewStart`, `.cshtml` layout, form and section served by `curl`
  in a full OS, on the main thread and in the `web` worker.

## Known limits

- No tag helpers (`asp-action`, `asp-for`, `<partial>`): use the HTML
  helpers or `Component.InvokeAsync`. A hard-coded `~/` in an attribute
  is not resolved: use `@Url.Content("~/...")`.
- No Razor Pages (`@page`), `ViewBag`, `TempData` or MVC view
  components; `Component` renders Razor components.
- `_ViewStart.cshtml` outside `Views/` is not read.
- Sections are rendered as soon as the page that defines them ends, not on
  the layout's request (same HTML, barring side effects).
- Compiling a `.cshtml` **inside the OS** remains out of scope (ADR 0076 §8).

## Tasks

- [x] Feasibility proof (build-time compilation without MVC) and ADR 0086.
- [x] Root `_ViewImports.cshtml` and compatibility types added at build time, `RAZORSDK1004` as a message.
- [x] `HackRazorPage`/`HackRazorPage<TModel>`: writing, attributes, inline templates, sections, body, activation of `@inject`s.
- [x] `Html`, `Json`, `Component`, `ModelExpressionProvider` helpers.
- [x] Engine: `Component`/`RazorPage` kinds, `_ViewStart`, `.cshtml` layouts, mixing, partials of both kinds, `ViewData` without key → `null`.
- [x] Generator: `.cshtml` views in the table, `HOSWEB0008`.
- [x] ADR 0007 rule extended to `.cshtml`.
- [x] Tests (pipeline, generator, end to end in both modes).
- [x] ADR 0086, this document, index, plan, `asp-hack-mvc.md`, `asp-hack-razor-views.md`.
