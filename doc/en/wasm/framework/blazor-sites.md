> [Version française](../../../fr/wasm/framework/blazor-sites.md)

# Blazor sites: components drawn by the browser

Work item `web/9-blazor-sites` of the web phase (ASP.Hack). Decision:
[ADR 0084](../dev/adr/0084-blazor-sites.md) (completes ADR 0073 §4, ADR 0075 §5 and
ADR 0081 §2). Plan: [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md) §5. Builds on
`web/4` ([`browser-html-rendering.md`](../dev/browser-html-rendering.md)) for the
browser, `web/6` ([`asp-hack-mvc.md`](asp-hack-mvc.md)) for the site and
`web/10` ([`detached-display-windows.md`](../dev/detached-display-windows.md)) for the
detached window.

## Purpose

An internal site can be **interactive**: its pages are Blazor components
drawn by the HackerOS browser directly in the page area, like
a Blazor WebAssembly site in a real browser.

- The site remains an ordinary `Web` app (ADR 0073): it listens, serves its MVC
  or minimal routes, and additionally declares a **root component**
  (`AppDefinition.BlazorRoot`).
- The browser recognizes the site's **host page** (it loads
  `_framework/blazor.hack.js`), reads `blazor.boot.json` and asks the OS to
  start a **renderer process** (`--type=renderer`, like Chrome) under the
  identity of the site and of the tab's user.
- The site **navigates by the tab's URL**: its links change the address without a
  request, back and forward stay inside the site, the address bar and
  Reload make a real request.
- The site has **no `filesystem.*` capability** (it declares none); it
  calls its own routes through `Site.Http`, an HTTP client on its address
  with the tab's cookies.
- An exception in the site gives the browser's "Aw, Snap!" page; the OS is
  never affected.

The sample site **Blazor Sample Site** (`http://blazor-sample.hack/`) is
installed in both hosts: counter without reload, page with a route parameter
that calls the site's API, page that crashes.

## Architecture

```text
 Navigateur (fenêtre, processus du navigateur)
 ├─ GET http://site.hack/page ───────────────► site (processus web, HackWebHost)
 │   ◄── page hôte (<script src="_framework/blazor.hack.js">)      │ routes MVC/minimales d'abord,
 ├─ GET /_framework/blazor.boot.json ──────────────────────────────┘ puis page hôte, chargeur, boot.json
 ├─ IHackWebSiteHost.StartAsync(page, boot, contexte du navigateur)
 │     └─ HackWebSiteHost (Platform.Blazor) : app Web qui sert l'origine, racine chargée,
 │        AppLifecycleOrchestrator.StartAuxiliaryAsync → processus de rendu (enfant, auxiliaire)
 └─ <HackWebSiteView Process Url Cookies …>            zone de page, sans iframe
       ├─ CascadingValue IAppExecutionContext (processus de rendu)
       ├─ CascadingValue HackWebSite (Navigation, Http, Title)
       ├─ HackSiteErrorBoundary ─► OnFaulted → « Aw, Snap! », processus en défaut
       └─ composant racine du site (HackSiteRouter → pages @page, layouts, HackNavLink, HackPageTitle)
             clics sur <a href> capturés par HackWebSiteView.razor.js → navigation interne ou du navigateur
```

| File | Role |
|---|---|
| `Shared/HackerOs.App.Abstractions/Definition/AppDefinition.cs` | `BlazorRoot` (`Type?`); the generator writes `web.blazor { assembly, type }` (schema, serializer, validator `manifest.web.blazor.assembly`). |
| `Shared/HackerOs.AppSdk/Net/Hosting/HackBlazorBoot.cs` | `HackBlazorBootManifest` (`appId`, `appName`, `entryAssembly`, `rootComponent`), paths `_framework/blazor.hack.js` and `_framework/blazor.boot.json`. |
| `Shared/HackerOs.AppSdk/Net/HackHttp.cs` | `HackHttpClient.BaseAddress`: relative URIs resolved like `HttpClient.BaseAddress`. |
| `Shared/HackerOs.AppSdk.Web/Hosting/HackBlazorSiteEndpoints.cs` | The three responses of a site with a Blazor root, last (`MapFallback`, order `1 000 000`); `IsHostPage(html)`. |
| `Shared/HackerOs.AppSdk.Web/Hosting/HackWebApplication.cs`, `HackWebHost.cs` | `MapFallback(pattern, handler)`; `CreateApplication` adds the Blazor endpoints after `Configure`. |
| `Shared/HackerOs.AppSdk.Web.Blazor/Sites/` | Blazor SDK for sites: `IHackWebSiteHost`, `IHackWebSiteProcess`, `HackWebSiteView`, `HackWebSite`, `HackSiteNavigationManager`, `HackSiteRouter`, `HackSiteLayoutBase`, `HackNavLink`, `HackPageTitle`, `HackSiteErrorBoundary`. |
| `Platform/HackerOs.Platform.Blazor/Sites/HackWebSiteHost.cs` | Implementation: finds the app by `web.urls`, refuses a `boot.json` from another app, loads the assembly (lazily), checks the root, starts the renderer process; `RendererProcess` relays `Exited`. |
| `Platform/HackerOs.Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs` | `StartAuxiliaryAsync(appId, principal, parentPid, argv)`: `Auxiliary` process, the site's capabilities minus `network.listen*`, tracked like a window (`StopAsync`). |
| `Platform/HackerOs.Platform.Core/Processes/InMemoryProcessManager.cs` | `ProcessStartRequest.Auxiliary`; `TryGetSingleton` ignores auxiliary processes. |
| `Apps/System/HackerOs.Apps.Browser/BrowserWindow.razor(.cs)` | Host page detection, `TryStartSiteAsync`, `ShowsSite` branch, internal navigation (back/forward), "Aw, Snap!", process stop when leaving the page; page area rendered in a single place through `DisplaySurfaceContent`. |
| `Apps/System/HackerOs.Apps.Browser/BrowserErrorPages.cs` | `ForRendererCrash` (`STATUS_ACCESS_VIOLATION`, `RESULT_CODE_KILLED`), `ForSiteNotStarted` (`ERR_SITE_NOT_STARTED`). |
| `Shared/HackerOs.AppSdk.Blazor/Display/DisplaySurfaceContent.razor.cs` | `Surface` becomes optional: without a surface, the content stays in place; content that is always rendered by the portal keeps its components on detach. |
| `Apps/Samples/HackerOs.Samples.BlazorSite/` | Sample site: `BlazorSampleSite` (definition, `/api/greeting/{name}`), `App.razor`, `Layout/MainLayout`, `Pages/Index`, `Pages/Hello`, `Pages/Crash`. Referenced by `OS/HackerOs.Ecosystem` and `test/test`. |

### Renderer process lifecycle

1. The browser receives a `text/html` that contains `_framework/blazor.hack.js`
   and reads `blazor.boot.json` (visible in the Network tab).
2. `HackWebSiteHost` finds the installed `Web` app one of whose `web.urls` URLs
   has the page's scheme, host and port; it refuses if `boot.json` names another
   app, if the app has no `web.blazor`, or if the type is not a
   component.
3. `StartAuxiliaryAsync` starts the process: child of the browser's
   process, `argv` `[<app>, --type=renderer, <url>]`, the tab's user,
   capabilities granted to the site minus `network.listen` and
   `network.listen.privileged`. It appears in the task manager under
   the site's app identifier; `TryGetSingleton` ignores it, so the site's
   on-demand activation (ADR 0074 §3) is not affected.
4. The process ends when the tab leaves the document, closes, or reloads
   (`IHackWebSiteProcess.StopAsync`); an unhandled exception stops it in
   `Fault`; `kill` or the task manager cancel its token, the view
   receives `Exited` and the browser shows "Aw, Snap!" (`RESULT_CODE_KILLED`).

### Navigation

- `HackWebSiteView.razor.js` listens to `click` in the capture phase on the page
  area: a `<a href>` of the site is stopped before the OS's Blazor router (which
  listens on `document`) and handed to `OnLinkClicked`. Modified clicks, secondary
  button, `download`, `javascript:` pass through.
- Within the site and without `forceLoad`: `HackSiteNavigationManager.SetLocation`
  (the site's `LocationChanged`), then `OnLocationChanged` → the browser updates
  the address and history (`pushState`/`replaceState`), no request.
- Elsewhere, or `forceLoad`: `OnNavigate` → ordinary browser navigation,
  which stops the renderer process.
- A link whose `target` exists and is not `_self` (`_top`, `_blank`, a name)
  is a document navigation even toward the site, as in Blazor
  (ADR 0087 §1): this is how a Blazor page reaches the MVC pages of the same
  site (`<a href="/Notes" target="_top">` in the Intranet example).
- Back/forward: if the target is in the live site, the browser changes
  the view's address (`Url`), which calls `SetLocation`; otherwise a real request.
  Reload always makes a real request (new renderer process).

## Usage

### Declaring a Blazor site

```csharp
[AppManifest]
public sealed partial class Intranet : WebAppDefinition
{
    public override string Id => "com.example.intranet";
    public override string Name => "Intranet";
    public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
    public override Type? BlazorRoot => typeof(App);
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.NetworkListen, AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkSimulatedWrite];

    public override void Configure(HackWebApplicationBuilder builder)
    {
        HackWebApplication app = builder.Build();
        app.MapGet("/api/notes", () => HackResults.Json(...));   // routes de données
        app.MapControllers();                                     // MVC éventuel
        // la page hôte, blazor.hack.js et blazor.boot.json sont ajoutés après Configure
    }
}
```

The project references `HackerOs.AppSdk.Web` and `HackerOs.AppSdk.Web.Blazor`;
`_Imports.razor` makes the components inherit from `HackComponentBase`
(`@inherits HackComponentBase`) and imports `HackerOs.AppSdk.Web.Blazor.Sites`.

### Root component, pages, layout

```razor
@* App.razor *@
<HackSiteRouter AppAssembly="typeof(App).Assembly" DefaultLayout="typeof(MainLayout)">
    <NotFound><p>Rien ici.</p></NotFound>
</HackSiteRouter>

@* Layout/MainLayout.razor *@
@inherits HackSiteLayoutBase
<nav><HackNavLink href="/" Match="NavLinkMatch.All">Accueil</HackNavLink> <HackNavLink href="/notes">Notes</HackNavLink></nav>
<main>@Body</main>

@* Pages/Note.razor *@
@page "/notes/{Id:int}"
<HackPageTitle Title="@($"Note {Id}")" />
@code {
    [Parameter] public int Id { get; set; }
    [CascadingParameter] HackWebSite Site { get; set; } = default!;
    protected override async Task OnInitializedAsync() =>
        _note = await Site.Http.GetFromJsonAsync($"api/notes/{Id}", NotesJson.Default.Note);
}
```

| Type (`HackerOs.AppSdk.Web.Blazor.Sites`) | Role |
|---|---|
| `HackWebSite` (cascaded) | `AppId`, `Name`, `Navigation`, `Http` (`BaseAddress` = the site's address, the tab's cookies), `Title`, `SetTitle`, `TitleChanged`. |
| `HackSiteNavigationManager` | The tab's `NavigationManager`: `Uri`, `BaseUri`, `NavigateTo(uri, forceLoad, replace)`, `LocationChanged`, `ToBaseRelativePath`; `IsWithinSite(uri)`. |
| `HackSiteRouter` | `AppAssembly`, `DefaultLayout`, `NotFound`; Blazor `@page` templates (`{id:int}`, `{x?}`, `{*rest}`), `[Layout]` and nested layouts, converted parameters (`string`, integers, `bool`, `Guid`, `decimal`, `double`, `float`, `DateTime`, `DateOnly`, `TimeOnly`, nullables). |
| `HackSiteLayoutBase` | Base of a layout (`Body`). |
| `HackNavLink` | `Href`, `Match` (`Prefix`/`All`), `ActiveClass`, free attributes. |
| `HackPageTitle` | `Title` → tab title. |
| `HackWebSiteView` | The browser's view: `Process`, `Url`, `Cookies`, `OnLocationChanged`, `OnNavigate`, `OnTitleChanged`, `OnFaulted`. Apps do not need to use it. |
| `IHackWebSiteHost`, `IHackWebSiteProcess` | Contract implemented by the platform, used by the browser. |

The site's components derive from `HackComponentBase`: their events and
lifecycle run under the renderer process's identity, so
`Site.Http`, `HackDns`, etc. go through its capabilities.

### What a site does not do

- Inject Blazor's `NavigationManager`, `Router`, `NavLink`, `PageTitle`: those
  are the OS's. The SDK provides their equivalents above.
- Declare `filesystem.*`: the Blazor root has no more rights than the site.
- Post a `<form>` without a handler: there is no document navigation in
  the OS; a site posts through `EditForm`/`@onsubmit` and `Site.Http`.

## Key decisions

- **No iframe**: installed C# code, limited by its capabilities, drawn by
  the OS's rendering engine (ADR 0073 §1); the JavaScript of HTML pages stays
  isolated (ADR 0075).
- **Auxiliary renderer process** rather than the site's instance: clear
  identity in `ps` and the task manager, independent stop, on-demand
  activation intact.
- **The site answers like a Blazor WebAssembly site** (host page, loader,
  `boot.json`): `curl` sees the same thing as with a real Blazor site, and the
  browser has no list of apps to consult before requesting the page.
- **Link interception in the capture phase**: the only way to get in front of
  the OS's router without touching the host.
- **Page area rendered in a single place** (browser): `DisplaySurfaceContent`
  accepts a null `Surface`, so that a Blazor site keeps its components and
  their state on detach and reattach (ADR 0084 §7). The HTML page is still
  rewritten on move (ADR 0081 §4).
- **Sample site installed**: the only way to see it in both hosts
  (the catalog comes from the Ecosystem's `BuiltInApplications.json`); hidden from the
  launcher like any `Web` app.

## Known limits

- The site's requests through `Site.Http` do not appear in the Network tab
  (only the document and `blazor.boot.json` do).
- No `[SupplyParameterFromQuery]`: parameters come from the path.
- A single Blazor site per tab; one tab per window (as before).
- A Blazor site is recognized by its host page: a site that writes its own HTML
  page with `_framework/blazor.hack.js` is treated as a Blazor site.
- Renderer processes run on the main thread (ADR 0073 §1).

## Tests

| Suite | What is checked |
|---|---|
| `Tests/HackerOs.App.Abstractions.Tests` | `BlazorRoot` → `web.blazor` (mapper, serializer, schema `web-blazor.valid.json`, validator). |
| `Tests/HackerOs.AppSdk.Web.Tests/Hosting/BlazorSiteEndpointsTests.cs` | Host page for paths without an extension, `404` with an extension, site routes first, `boot.json`, `blazor.hack.js`, site without a root. |
| `Tests/HackerOs.AppSdk.Web.Blazor.Tests` | Router (templates, precedence, conversion), navigation (`NavigateTo`, `SetLocation`, `IsWithinSite`), `HackWebSite` (title, client). |
| `Tests/HackerOs.Platform.Core.Tests` | `TryGetSingleton` ignores auxiliaries; `StartAuxiliaryAsync` (parent, `argv`, capabilities, not the site's instance, stop in `Fault`). |
| `Tests/HackerOs.Platform.Blazor.Tests/HackWebSiteHostTests.cs` | App found by origin, `boot.json` of another app refused, root missing or not a component, stop and `Exited`. |
| `Tests/HackerOs.Network.Tests` | `HackHttpClient.BaseAddress`. |
| `Tests/HackerOs.Platform.WorkerHost.Tests/BlazorSiteTests.cs` | `curl` on a site with a Blazor root: host page, `boot.json`, loader, API, `404`, main thread and worker. |
| `Tests/HackerOs.UI.E2E.Tests/BrowserBlazorSiteTests.cs` | Chromium: sample site drawn without an iframe, counter, link without reload, API, back/forward inside the site, "Aw, Snap!", reload, leaving the site. |

## Tasks

- [x] ADR 0084.
- [x] `BlazorRoot`, `web.blazor` manifest (schema, serializer, validator, mapper).
- [x] `ProcessStartRequest.Auxiliary`, `TryGetSingleton`, `StartAuxiliaryAsync`.
- [x] Host page, loader and `boot.json` (`HackBlazorSiteEndpoints`, `MapFallback`).
- [x] `HackHttpClient.BaseAddress`.
- [x] `HackerOs.AppSdk.Web.Blazor` library (view, site, navigation, router, layout, link, title, error boundary).
- [x] `HackWebSiteHost` (Platform.Blazor), registration in the Ecosystem.
- [x] Browser: startup, internal navigation, back/forward, "Aw, Snap!", stop, detached window without state loss.
- [x] Sample site in both hosts.
- [x] Unit, `curl` and E2E tests.
- [x] Documentation and index.
