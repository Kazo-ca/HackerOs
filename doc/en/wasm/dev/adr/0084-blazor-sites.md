> [Version française](../../../../fr/wasm/dev/adr/0084-blazor-sites.md)

# ADR 0084: interactive Blazor sites drawn by the browser

## Status

Accepted on 2026-10-16, with the `web/9-blazor-sites` work item.

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Completes:** ADR 0073 §1 and §4 (Blazor site of a `Web` app), ADR 0075 §5
  (choice of view according to the response), ADR 0081 §2 (Blazor content in a
  detached window). Replaces no decision.
- **Documentation:** [`blazor-sites.md`](../../framework/blazor-sites.md).

## Context

ADR 0073 §4 says that a Blazor site resembles a windowed app, but that its
root component is drawn in the browser's page area, that it navigates
through the tab's URL and that it has no file access. It does not say
how the browser knows that an address is a Blazor site, under what identity this code
runs, nor how navigation, links and errors behave, whereas Blazor's
`NavigationManager`, `Router` and `PageTitle` belong to the OS itself (a single
rendering engine for the whole desktop).

## Decision

### 1. Declaration

`AppDefinition.BlazorRoot` (`Type?`, `null` by default) designates the root
component of a `Web` app. The manifest generator writes it into
`web.blazor` (`assembly`, `type`), omitted without a root to keep the bytes of the
other manifests. The validator requires the type to be in the entry point's
assembly (`manifest.web.blazor.assembly`), like embeddable components
(ADR 0063): lazy loading brings only that DLL. A site can be
MVC only, Blazor only, or both (ADR 0073 §1).

### 2. The site answers like a Blazor WebAssembly site

For a site that has a Blazor root, `HackWebHost` adds after
`Configure` three responses, with the lowest priority (MVC routes
go first):

- the **host page** (`index.html` of a real Blazor site) for any
  `GET`/`HEAD` request with no endpoint whose last segment has no extension
  (`MapFallbackToFile` of ASP.NET Core): `<div id="app">`, then
  `<script src="_framework/blazor.hack.js">`;
- `/_framework/blazor.hack.js`, the loader, which in another browser
  (or a page without the OS) only displays that the site requires the HackerOS
  browser;
- `/_framework/blazor.boot.json`: app identifier, name, root component,
  entry assembly.

`curl` therefore sees the same thing as with a real Blazor site: an almost
empty page that loads a script.

### 3. The browser starts the site like a rendering engine

When a `text/html` response loads `_framework/blazor.hack.js`, the browser
reads `blazor.boot.json` (visible in the Network tab), then asks the OS to
start the site through `IHackWebSiteHost` (Blazor SDK, implemented by the
platform, like `IHackComponentHost`). The OS:

1. finds the `Web` app that declares the page's origin and path in
   `web.urls`, and refuses if `blazor.boot.json` names another app (a page cannot
   start another site's code);
2. loads its assembly (lazy loading) and verifies that the root is a
   component;
3. starts a **rendering process**, child of the browser's process, like
   `chrome --type=renderer`: same app identifier as the site, the tab's user,
   `argv` `[<host>, --type=renderer, <url>]`, capabilities granted to the
   site **minus** `network.listen*` (client code does not listen). This process
   is **auxiliary**: it is never taken for the site's instance, so
   the server's on-demand activation (ADR 0074 §3) does not change. It runs
   on the main thread, where the browser draws it (ADR 0073 §1).

The rendering process terminates when the tab changes document, closes or
reloads the page (a reload starts a new process, like a
browser reloads the runtime).

### 4. Rendering in the page area

`HackWebSiteView` (Blazor SDK) draws the root component directly in the page
area, without an iframe: it is installed C# code, at the same trust level as
a windowed app, limited by its capabilities, and its style is isolated
by scoped CSS. The JavaScript of HTML pages stays in the isolated iframe of
ADR 0075. The component receives as cascading values the rendering process's execution
context (components derived from `HackComponentBase` call the SDK under
the site's identity) and an `HackWebSite`: address, navigation, HTTP client,
title.

An unhandled exception from a site component is stopped by an
`ErrorBoundary`: the rendering process goes into a fault state, the tab shows the
"Aw, Snap!" page and the developer tools console shows the exception. Killing the
rendering process (task manager, `kill`) gives the same page. The rest
of the OS is never affected.

### 5. Navigation through the tab's URL

Blazor's `NavigationManager`, `Router`, `NavLink` and `PageTitle` are
those of the OS (DI of the shared rendering engine): a site must not use them.
The SDK provides their tab-specific equivalents:

- `HackSiteNavigationManager` derives from `NavigationManager`: `NavigateTo`,
  `LocationChanged`, `BaseUri`, `ToBaseRelativePath` work as in
  Blazor, on the tab's address;
- `HackSiteRouter` finds pages by their `@page` directives in
  the site's assembly (Blazor templates: `{id:int}`, `{x?}`, `{*rest}`), with
  `Found`/`NotFound` and a default layout;
- `HackNavLink` and `HackPageTitle` replace `NavLink` and `PageTitle`.

The `<a href>` links drawn by the site are intercepted in the page area
(component's JS module): a link to an address of the site becomes an internal
navigation, added to the tab's history without a request, like
`history.pushState`; a link elsewhere, or `forceLoad`, is a browser
navigation. Back and forward between two entries of the same site stay in
the site; the address bar and Reload make a real request.

### 6. Network and cookies

`HackWebSite.Http` is an `HackHttpClient` whose `BaseAddress` is the site's
address (new SDK property, like `HttpClient.BaseAddress`), which shares the
tab's cookie jar and runs under the rendering process's identity:
a `GET api/notes` call goes through the simulated network and reaches the same site, with
the session opened by its pages.

### 7. Detached window

The page area of a Blazor site moves into the detached window through the
portal of ADR 0081 §2, without reloading: unlike an HTML page, the
site keeps its state (counters, forms in progress).

## Consequences

- `Web` apps gain an optional `web.blazor` manifest; the schema, the
  serializer and the validator know it.
- `ProcessStartRequest` gains `Auxiliary`: `TryGetSingleton` ignores
  auxiliary processes.
- `DisplaySurfaceContent.Surface` becomes optional (section 7): the
  browser renders its page area only once through the portal, and a
  Blazor site keeps its state on detaching.
- The sample site `HackerOs.Samples.BlazorSite` is installed in both
  hosts (hidden app, `http://blazor-sample.hack/`): it is the only Blazor site
  that the browser can serve as long as no app declares one.
- A Blazor site uses the SDK's navigation components, not Blazor's;
  an `[Inject] NavigationManager` in a site gets the OS's.
  The guide says so, and the "Aw, Snap!" page replaces Blazor's global
  error banner.
- An HTML form without an `@onsubmit` handler is not submitted (no
  document navigation in the OS): a site posts through `EditForm` or `Http`.
- The site's `Http` requests are not in the Network tab (only the
  document and `blazor.boot.json` are); to be revisited if the need arises.
