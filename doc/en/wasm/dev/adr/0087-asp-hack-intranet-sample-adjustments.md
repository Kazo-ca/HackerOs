# ADR 0087: Intranet sample, end-of-web-phase adjustments

## Status

Accepted on 2026-10-17, with the `web/11-sample-final` work item.

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md), §5).
- **Completes:** ADR 0084 §5 (Blazor site navigation), ADR 0083 and 0086
  (view base classes), ADR 0076 §6 (retained subset of ASP.NET).
  Replaces no decision; also records what the phase did not deliver from
  ADR 0073 §3.

## Context

The last work item of the web phase brings all the building blocks together in a single site,
the **Intranet** sample (`http://intranet.hack/`): MVC pages in `.cshtml` and
`.razor` under the same layout, REST API, JavaScript from `wwwroot`, cookie
session, anti-forgery forms and a Blazor task board under
`/board`. It is the first **shipped** site (project under `Apps/`, analyzed for
trimming like any app) that has views, and the first that mixes MVC pages and
Blazor pages on the same origin. Four points emerged.

## Decision

### 1. A link with a `target` other than `_self` leaves the Blazor site

ADR 0084 §5 makes any link to the site's address an internal navigation.
A site that also has MVC pages (ADR 0073 §2: "MVC and Blazor in the same
app") then had no simple link to them: the site's router answered
"page not found". Like Blazor, which does not intercept a link whose target
is another frame, the page area now treats an `<a>` whose `target` attribute
exists and is not `_self` (`_top`, `_blank`, a name) as a document
navigation, with `forceLoad`. One tab per window: the page
opens in that tab (like `target="_blank"` in an HTML page, ADR 0079).
`HackWebSiteView.OnLinkClicked` receives this flag from the JS module.

The sample writes `<a href="/Notes" target="_top">`; in the other direction, an
ordinary link from an MVC page to `/board` loads the host page and starts the Blazor
site.

### 2. View base classes no longer carry a trimming annotation

Views are named by `typeof()` with `DynamicallyAccessedMemberTypes.All`
(generated view table, `RazorCompiledItemAttribute`, `[Layout]`). The trimmer
then considers every member of the view **and of its base classes** as read
by reflection, and flags any annotated member (`IL2110`, `IL2111`). Test
projects do not analyze trimming: the fault only appeared with a shipped app,
where it breaks the build (warnings as errors).

- `HackViewBase.Layout` is no longer annotated; its `set` suppresses `IL2067` with the
  same justification as `HackViewEngine.LayoutOf` (a layout is a component
  of the view's assembly, named by `typeof()` as in `[Layout]`).
- `HackRazorPage.RenderAsync` fills its `@inject`s before moving to an
  async method: the annotated `pageType` parameter no longer becomes a field
  of the state machine.

Rule going forward: no member of a view or component base class
(`HackViewBase`, `HackRazorPage`, `HackComponentBase`...) carries
`[DynamicallyAccessedMembers]`; the annotation stays on the parameters of the
OS methods that receive a type (`HackViewDescriptor`, `View<T>()`).
The Intranet sample, compiled with the Ecosystem host, verifies the rule at each
build.

### 3. `TryValidateModel` on controllers

A `PATCH` merges a partial body into the stored entity then must verify
the result with the full model's `DataAnnotations` rules, which
`ControllerBase.TryValidateModel` does in ASP.NET Core. `HackControllerBase`
receives `TryValidateModel<TModel>(model, prefix)` (type annotated
`PublicProperties`, errors in `ModelState`, returns `ModelState.IsValid`),
added to the subset of ADR 0076 §6.

### 4. The sample is installed in the hosts; its data stays in memory

`HackerOs.Samples.Intranet` is, like the Blazor sample site (ADR 0084), a
built-in non-system `Web` app, hidden from the launcher, referenced by the Ecosystem and
`test/test` (lazy loading). Its notes and cards are singletons
of the site's process: they return to their initial state when the OS stops the
site.

The site database decided by ADR 0073 §3 (host IndexedDB, imposed name
`hackweb.<id>`, `HackWebDb`, erasure on uninstallation, listing in
Diagnostic) was assigned to no work item of the plan and does not exist. Likewise,
the debug logs of an ASP.Hack site (`/var/log/hackweb/<id>.log`) and the
browser's autofill are not implemented. These decisions
remain valid; they require a separate work item.

## Consequences

- A mixed site links its two halves with ordinary links; the rule of
  §1 is Blazor's, so an ASP.NET developer knows it.
- Any shipped app can have `.razor` and `.cshtml` views; a regression
  of §2 breaks the Ecosystem build.
- The developer guide ([`asp-hack-developer-guide.md`](../../framework/asp-hack-developer-guide.md))
  builds on the sample and flags §4 in its limits.
