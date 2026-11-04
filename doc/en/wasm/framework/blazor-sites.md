> [Version française](../../../fr/wasm/framework/blazor-sites.md)

# Blazor sites: components drawn by the browser

Work item `web/9-blazor-sites` of the web phase (ASP.Kazo). Decision:
[ADR 0084](../dev/adr/0084-blazor-sites.md) (completes ADR 0073 §4, ADR 0075 §5 and
ADR 0081 §2). Plan: [`asp-kazo-plan.md`](../dev/plans/asp-kazo-plan.md) §5. Builds on
`web/4` ([`browser-html-rendering.md`](../dev/browser-html-rendering.md)) for the
browser, `web/6` ([`asp-kazo-mvc.md`](asp-kazo-mvc.md)) for the site and
`web/10` ([`detached-display-windows.md`](../dev/detached-display-windows.md)) for the
detached window.

## Purpose

An internal site can be **interactive**: its pages are Blazor components
drawn by the Kazo OS browser directly in the page area, like
a Blazor WebAssembly site in a real browser.

- The site remains an ordinary `Web` app (ADR 0073): it listens, serves its MVC
  or minimal routes, and additionally declares a **root component**
  (`AppDefinition.BlazorRoot`).
- The browser recognizes the site's **host page** (it loads
  `_framework/blazor.kazo.js`), reads `blazor.boot.json` and asks the OS to
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
 ├─ GET http://site.hack/page ───────────────► site (processus web, KazoWebHost)
 │   ◄── page hôte (<script src="_framework/blazor.kazo.js">)      │ routes MVC/minimales d'abord,
 ├─ GET /_framework/blazor.boot.json ──────────────────────────────┘ puis page hôte, chargeur, boot.json
 ├─ IKazoWebSiteHost.StartAsync(page, boot, contexte du navigateur)
 │     └─ KazoWebSiteHost (Platform.Blazor) : app Web qui sert l'origine, racine chargée,
 │        AppLifecycleOrchestrator.StartAuxiliaryAsync → processus de rendu (enfant, auxiliaire)
 └─ <KazoWebSiteView Process Url Cookies …>            zone de page, sans iframe
       ├─ CascadingValue IAppExecutionContext (processus de rendu)
       ├─ CascadingValue KazoWebSite (Navigation, Http, Title)
       ├─ KazoSiteErrorBoundary ─► OnFaulted → « Aw, Snap! », processus en défaut
       └─ composant racine du site (KazoSiteRouter → pages @page, layouts, KazoNavLink, KazoPageTitle)
             clics sur <a href> capturés par KazoWebSiteView.razor.js → navigation interne ou du navigateur
```

| File | Role |
|---|---|
| `Shared/Kazo.Os.App.Abstractions/Definition/AppDefinition.cs` | `BlazorRoot` (`Type?`); the generator writes `web.blazor { assembly, type }` (schema, serializer, validator `manifest.web.blazor.assembly`). |
| `Shared/Kazo.Os.AppSdk/Net/Hosting/KazoBlazorBoot.cs` | `KazoBlazorBootManifest` (`appId`, `appName`, `entryAssembly`, `rootComponent`), paths `_framework/blazor.kazo.js` and `_framework/blazor.boot.json`. |
| `Shared/Kazo.Os.AppSdk/Net/KazoHttp.cs` | `KazoHttpClient.BaseAddress`: relative URIs resolved like `HttpClient.BaseAddress`. |
| `Shared/Kazo.Os.AppSdk.Web/Hosting/KazoBlazorSiteEndpoints.cs` | The three responses of a site with a Blazor root, last (`MapFallback`, order `1 000 000`); `IsHostPage(html)`. |
| `Shared/Kazo.Os.AppSdk.Web/Hosting/KazoWebApplication.cs`, `KazoWebHost.cs` | `MapFallback(pattern, handler)`; `CreateApplication` adds the Blazor endpoints after `Configure`. |
| `Shared/Kazo.Os.AppSdk.Web.Blazor/Sites/` | Blazor SDK for sites: `IKazoWebSiteHost`, `IKazoWebSiteProcess`, `KazoWebSiteView`, `KazoWebSite`, `KazoSiteNavigationManager`, `KazoSiteRouter`, `KazoSiteLayoutBase`, `KazoNavLink`, `KazoPageTitle`, `KazoSiteErrorBoundary`. |
| `Platform/Kazo.Os.Platform.Blazor/Sites/KazoWebSiteHost.cs` | Implementation: finds the app by `web.urls`, refuses a `boot.json` from another app, loads the assembly (lazily), checks the root, starts the renderer process; `RendererProcess` relays `Exited`. |
| `Platform/Kazo.Os.Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs` | `StartAuxiliaryAsync(appId, principal, parentPid, argv)`: `Auxiliary` process, the site's capabilities minus `network.listen*`, tracked like a window (`StopAsync`). |
| `Platform/Kazo.Os.Platform.Core/Processes/InMemoryProcessManager.cs` | `ProcessStartRequest.Auxiliary`; `TryGetSingleton` ignores auxiliary processes. |
| `Apps/System/Kazo.Os.Apps.Browser/BrowserWindow.razor(.cs)` | Host page detection, `TryStartSiteAsync`, `ShowsSite` branch, internal navigation (back/forward), "Aw, Snap!", process stop when leaving the page; page area rendered in a single place through `DisplaySurfaceContent`. |
| `Apps/System/Kazo.Os.Apps.Browser/BrowserErrorPages.cs` | `ForRendererCrash` (`STATUS_ACCESS_VIOLATION`, `RESULT_CODE_KILLED`), `ForSiteNotStarted` (`ERR_SITE_NOT_STARTED`). |
| `Shared/Kazo.Os.AppSdk.Blazor/Display/DisplaySurfaceContent.razor.cs` | `Surface` becomes optional: without a surface, the content stays in place; content that is always rendered by the portal keeps its components on detach. |
| `Apps/Samples/Kazo.Os.Samples.BlazorSite/` | Sample site: `BlazorSampleSite` (definition, `/api/greeting/{name}`), `App.razor`, `Layout/MainLayout`, `Pages/Index`, `Pages/Hello`, `Pages/Crash`. Referenced by `OS/Kazo.Os.Ecosystem` and `test/test`. |

### Renderer process lifecycle

1. The browser receives a `text/html` that contains `_framework/blazor.kazo.js`
   and reads `blazor.boot.json` (visible in the Network tab).
2. `KazoWebSiteHost` finds the installed `Web` app one of whose `web.urls` URLs
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
   (`IKazoWebSiteProcess.StopAsync`); an unhandled exception stops it in
   `Fault`; `kill` or the task manager cancel its token, the view
   receives `Exited` and the browser shows "Aw, Snap!" (`RESULT_CODE_KILLED`).

### Navigation

- `KazoWebSiteView.razor.js` listens to `click` in the capture phase on the page
  area: a `<a href>` of the site is stopped before the OS's Blazor router (which
  listens on `document`) and handed to `OnLinkClicked`. Modified clicks, secondary
  button, `download`, `javascript:` pass through.
- Within the site and without `forceLoad`: `KazoSiteNavigationManager.SetLocation`
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

    public override void Configure(KazoWebApplicationBuilder builder)
    {
        KazoWebApplication app = builder.Build();
        app.MapGet("/api/notes", () => KazoResults.Json(...));   // routes de données
        app.MapControllers();                                     // MVC éventuel
        // la page hôte, blazor.kazo.js et blazor.boot.json sont ajoutés après Configure
    }
}
```

The project references `Kazo.Os.AppSdk.Web` and `Kazo.Os.AppSdk.Web.Blazor`;
`_Imports.razor` makes the components inherit from `KazoComponentBase`
(`@inherits KazoComponentBase`) and imports `Kazo.Os.AppSdk.Web.Blazor.Sites`.

### Root component, pages, layout

```razor
@* App.razor *@
<KazoSiteRouter AppAssembly="typeof(App).Assembly" DefaultLayout="typeof(MainLayout)">
    <NotFound><p>Rien ici.</p></NotFound>
</KazoSiteRouter>

@* Layout/MainLayout.razor *@
@inherits KazoSiteLayoutBase
<nav><KazoNavLink href="/" Match="NavLinkMatch.All">Accueil</KazoNavLink> <KazoNavLink href="/notes">Notes</KazoNavLink></nav>
<main>@Body</main>

@* Pages/Note.razor *@
@page "/notes/{Id:int}"
<KazoPageTitle Title="@($"Note {Id}")" />
@code {
    [Parameter] public int Id { get; set; }
    [CascadingParameter] KazoWebSite Site { get; set; } = default!;
    protected override async Task OnInitializedAsync() =>
        _note = await Site.Http.GetFromJsonAsync($"api/notes/{Id}", NotesJson.Default.Note);
}
```

| Type (`Kazo.Os.AppSdk.Web.Blazor.Sites`) | Role |
|---|---|
| `KazoWebSite` (cascaded) | `AppId`, `Name`, `Navigation`, `Http` (`BaseAddress` = the site's address, the tab's cookies), `Title`, `SetTitle`, `TitleChanged`. |
| `KazoSiteNavigationManager` | The tab's `NavigationManager`: `Uri`, `BaseUri`, `NavigateTo(uri, forceLoad, replace)`, `LocationChanged`, `ToBaseRelativePath`; `IsWithinSite(uri)`. |
| `KazoSiteRouter` | `AppAssembly`, `DefaultLayout`, `NotFound`; Blazor `@page` templates (`{id:int}`, `{x?}`, `{*rest}`), `[Layout]` and nested layouts, converted parameters (`string`, integers, `bool`, `Guid`, `decimal`, `double`, `float`, `DateTime`, `DateOnly`, `TimeOnly`, nullables). |
| `KazoSiteLayoutBase` | Base of a layout (`Body`). |
| `KazoNavLink` | `Href`, `Match` (`Prefix`/`All`), `ActiveClass`, free attributes. |
| `KazoPageTitle` | `Title` → tab title. |
| `KazoWebSiteView` | The browser's view: `Process`, `Url`, `Cookies`, `OnLocationChanged`, `OnNavigate`, `OnTitleChanged`, `OnFaulted`. Apps do not need to use it. |
| `IKazoWebSiteHost`, `IKazoWebSiteProcess` | Contract implemented by the platform, used by the browser. |

The site's components derive from `KazoComponentBase`: their events and
lifecycle run under the renderer process's identity, so
`Site.Http`, `KazoDns`, etc. go through its capabilities.

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
  page with `_framework/blazor.kazo.js` is treated as a Blazor site.
- Renderer processes run on the main thread (ADR 0073 §1).

## Tests

| Suite | What is checked |
|---|---|
| `Tests/Kazo.Os.App.Abstractions.Tests` | `BlazorRoot` → `web.blazor` (mapper, serializer, schema `web-blazor.valid.json`, validator). |
| `Tests/Kazo.Os.AppSdk.Web.Tests/Hosting/BlazorSiteEndpointsTests.cs` | Host page for paths without an extension, `404` with an extension, site routes first, `boot.json`, `blazor.kazo.js`, site without a root. |
| `Tests/Kazo.Os.AppSdk.Web.Blazor.Tests` | Router (templates, precedence, conversion), navigation (`NavigateTo`, `SetLocation`, `IsWithinSite`), `KazoWebSite` (title, client). |
| `Tests/Kazo.Os.Platform.Core.Tests` | `TryGetSingleton` ignores auxiliaries; `StartAuxiliaryAsync` (parent, `argv`, capabilities, not the site's instance, stop in `Fault`). |
| `Tests/Kazo.Os.Platform.Blazor.Tests/KazoWebSiteHostTests.cs` | App found by origin, `boot.json` of another app refused, root missing or not a component, stop and `Exited`. |
| `Tests/Kazo.Os.Network.Tests` | `KazoHttpClient.BaseAddress`. |
| `Tests/Kazo.Os.Platform.WorkerHost.Tests/BlazorSiteTests.cs` | `curl` on a site with a Blazor root: host page, `boot.json`, loader, API, `404`, main thread and worker. |
| `Tests/Kazo.Os.UI.E2E.Tests/BrowserBlazorSiteTests.cs` | Chromium: sample site drawn without an iframe, counter, link without reload, API, back/forward inside the site, "Aw, Snap!", reload, leaving the site. |

## Tasks

- [x] ADR 0084.
- [x] `BlazorRoot`, `web.blazor` manifest (schema, serializer, validator, mapper).
- [x] `ProcessStartRequest.Auxiliary`, `TryGetSingleton`, `StartAuxiliaryAsync`.
- [x] Host page, loader and `boot.json` (`KazoBlazorSiteEndpoints`, `MapFallback`).
- [x] `KazoHttpClient.BaseAddress`.
- [x] `Kazo.Os.AppSdk.Web.Blazor` library (view, site, navigation, router, layout, link, title, error boundary).
- [x] `KazoWebSiteHost` (Platform.Blazor), registration in the Ecosystem.
- [x] Browser: startup, internal navigation, back/forward, "Aw, Snap!", stop, detached window without state loss.
- [x] Sample site in both hosts.
- [x] Unit, `curl` and E2E tests.
- [x] Documentation and index.
