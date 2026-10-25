# Web phase: ASP.Hack, internal HackerOS websites

**Status:** plan validated by Benoit on 2026-10-16 (answers to the questionnaire of
22 questions, section 4.1). The decisions are in ADR 0073 to 0078,
accepted the same day.

Integration branch: `web/base`, created from `main` after the merge of
`rebuild/base` (PR #131, phases 1 to 5). Each work item starts from `web/base`, its
branch is called `web/<n>-<sujet>` and its PR targets `web/base`. Nothing is merged
into `main` without Benoit.

## 1. Purpose

Give HackerOS **internal websites**, served by the OS and browsed with
its browser:

- **ASP.Hack projects** written in C#, modeled on ASP.NET MVC
  (controllers, routes, `GET`/`POST`/`PUT`/`PATCH`/`DELETE`, model binding,
  views), on top of the public SDK;
- **interactive Blazor sites**, close to a windowed app, but drawn
  in the browser instead of the window manager and without access to the
  file system;
- **static sites** (HTML, CSS, JavaScript) declared from the file
  system and managed by an IIS-style app;
- opening a local `.html` (`file://`) in the browser;
- opening a page in a **detached window**, outside the OS, which
  prepares a future second screen.

## 2. Starting point (verified on `web/base`)

- The simulated sites (HackMail, HackerSearch, forum, darknet, bank) are
  `ISimulatedWebsiteController` instances registered at host startup
  (`Platform.Core/Network/Websites/`). Their routing is minimal (`Get`/`Post`,
  exact path or `{param}` on one segment).
- `SimulatedHttpResponse` does not carry HTML: it carries a structured
  `SimulatedPage` (sections), drawn by `SimulatedPageRenderer.razor`. There is no way
  to return HTML, CSS, JS, JSON or bytes.
- The network SDK (`HackHttpClient`, `IAppNetworkGateway`, ADR 0055, 0056) can
  send all HTTP methods, with a fallback to the real network through the
  optional server relay (`network.real.access`). No app can
  listen on a port; `netstat` has nothing to show; there is no
  `/etc/hosts`.
- Services run in a dedicated worker since phase 5 (ADR 0070); windowed
  apps stay on the main thread.

## 3. Principles

1. **Mimic the Metal.** A site is a process that listens on a port, visible
   in the task manager, `ps` and `netstat -tlnp`; a name goes through a
   DNS resolver; a closed port answers `ERR_CONNECTION_REFUSED`; the browser
   itself loads the subresources of a page.
2. **Public SDK only.** ASP.Hack is a library of the SDK
   (`HackerOsSdkLibrary`); a site references neither Platform Core nor the
   internal services (ADR 0055, codes `HOSAPP0030/0031`).
3. **Resemble ASP.NET without copying it.** Same ideas and same attribute
   names (`[HttpGet]`, `[Route]`, `[FromBody]`), base classes prefixed
   `Hack` (`HackController`, `HackHttpContext`); only the useful subset
   (section 6 of ADR 0076).
4. **A site's JS is isolated.** It never touches HackerOS or the Internet:
   everything goes through the simulated network.
5. **No server required.** Everything runs in the Ecosystem host. The optional
   server serves only as a relay to the real network, when it is there.
6. **Door open to compilation in the OS.** Nothing assumes that a site is
   necessarily compiled with the solution: the deployment model and the
   `.cshtml` view engine are chosen so that a future version compiles a project
   from the code editor.
7. **The three hosts stay operational** after each work item
   (`build/verify-hosts.sh`), and each work item updates the documentation and
   the `docs/README.md` index.

## 4. Decision register

| # | Decision (Benoit's answers of 2026-10-16) | ADR |
|---|---|---|
| 1 | New `Web` app kind: an MVC site, a Blazor site or both; each app declares its URLs in its manifest. The (current) statically registered sites remain supported alongside the process sites. | 0073 |
| 2 | An app site (project) is **external**: served read-only from the project resources, under the name it declares. A site created by the user is **hosted in the OS**, from its files, on `localhost`. | 0073, 0077 |
| 3 | A site has no `filesystem.*` capability; an interactive Blazor site has no access to files either. | 0073 |
| 4 | OS DNS resolver (`hackdns` service): `/etc/hosts`, manifest URLs, declared static sites, simulated network, then fallback to the server relay if available. | 0074 |
| 5 | `HackHttpListener` (modeled on `System.Net.HttpListener`), `network.listen` capability, ports < 1024 reserved for the administrator, table of listening ports, on-demand activation (like systemd sockets), real HTTP bodies (`Content-Type` + bytes). | 0074 |
| 6 | The browser renders real HTML in an isolated iframe (`sandbox="allow-scripts"`, without same origin), itself loads subresources through the simulated network and relays `fetch`, `XMLHttpRequest`, links and forms; `file://` reads the files of the OS. | 0075 |
| 7 | ASP.Hack MVC: middleware pipeline, attribute routing and conventional routing generated at build time, model binding, results, per-request DI, cookie session. | 0076 |
| 8 | Two view engines: `.razor` components rendered to HTML (`HtmlRenderer`), and `.cshtml` views compiled at build time by the Razor compiler with an ASP.Hack base class (after a proof of feasibility); `.cshtml` compilation in the OS later. | 0076 |
| 9 | Static sites declared in files (`/etc/hackweb/sites/*.site` by the administrator, `~/.config/hackweb/sites/*.site` by any user on a port ≥ 1024), served by the `hackwebd` service, managed by the "Web Server Manager" app in the style of IIS, which only drives the OS through the SDK. | 0077 |
| 10 | Detached window: a page opens in a window of the host browser, outside the OS; this window is driven by the main instance (display link), without a second independent OS. The same link will serve the second screen. | 0078 |
| 11 | A site's data is not in the OS files: a database if needed in the local database of the host browser (IndexedDB), named automatically after the app by a helper of the web SDK; only cookies, autofill and the debug logs enabled by the site go into the OS files (Benoit, 2026-10-16 16:44Z, replaces the private folder validated at 16:34Z). | 0073 |
| 12 | Internal sites use the `.hack` domain (`intranet.hack`); as on a real machine, `/etc/hosts` always wins. | 0074 |

### 4.1 Questionnaire of 2026-10-16

Benoit kept all the recommendations, with a clarification on question 3.

| # | Question | Answer | ADR |
|---|---|---|---|
| 1 | App kind | new `Web` kind | 0073 |
| 2 | MVC and Blazor in the same app | allowed (MVC routes first) | 0073 |
| 3 | Where the server code runs | worker, **a single worker shared by all sites** | 0073 |
| 4 | Starting a site | on demand | 0074 |
| 5 | Ports < 1024 | administrator approval | 0074 |
| 6 | HTTPS | accepted and treated as HTTP | 0074 |
| 7 | HTML parsing | C#, AngleSharp | 0075 |
| 8 | `localStorage` of a page | OS files, with cookies | 0075 |
| 9 | A site's database | API modeled on IndexedDB + typed helper | 0073 |
| 10 | Database on uninstall | erased after confirmation | 0073 |
| 11 | Databases visible in the OS | in Diagnostic | 0073 |
| 12 | WebSocket, EventSource, `window.open` | refused in v1 | 0075 |
| 13 | Developer tools | Network and Console in v1 | 0075 |
| 14 | Route table | generated at build time | 0076 |
| 15 | Session | in process memory | 0076 |
| 16 | Type names | `Hack*` classes, ASP.NET attributes | 0076 |
| 17 | `.cshtml` if the proof fails | in-house interpreted engine | 0076 |
| 18 | Static site format | JSON (`.site`) | 0077 |
| 19 | Who publishes a static site | any user (port ≥ 1024, `~/.config`), administrator for `/etc` | 0077 |
| 20 | Identity of `hackwebd` | `www-data` account | 0077 |
| 21 | Detached window | driven by the main OS | 0078 |
| 22 | First work item | plan order | n/a |

## 5. Work items

| Branch | Content | Depends on |
|---|---|---|
| `web/1-adr` | This plan, ADR 0073 to 0078, index. | n/a |
| `web/2-http-bodies` | Real HTTP bodies in the simulated contracts and the SDK (`Content-Type`, bytes, headers); `SimulatedPage` stays for existing sites; `curl` displays the raw body. | `1` |
| `web/3-listener-dns` | `HackHttpListener`, `network.listen`, port table, `hackdns`, `/etc/hosts`, manifest `Urls`, on-demand activation, `netstat -tlnp`, `/proc/net/tcp`, shared web worker and remote gateway for a listener in this worker. | `2` |
| `web/4-browser-html` | Isolated iframe, subresource loader, `fetch`/XHR/forms bridge, `file://`, "source" tab, network errors. | `2` |
| `web/5-static-sites` | `.site` files, `hackwebd` service (static files, index, MIME types, 404), Web Server Manager app, opening a folder as a site. | `3`, `4` |
| `web/6-mvc-core` | `HackerOs.AppSdk.Web` library: `WebAppDefinition`, `HackWebApplication`, middlewares, route generator, controllers, binding, JSON/Content/File/Redirect results, per-request DI, session. | `3` |
| `web/7-razor-views` | `.razor` views to HTML, layout, partials, `Model`, URL helpers, anti-forgery, project `wwwroot` served read-only. | `6` |
| `web/8-cshtml-views` | Proof of feasibility then `.cshtml` views compiled at build time (`@model`, `@inject`, `@section`, `_Layout`, `_ViewStart`). If the proof fails, a replacement ADR is written before the code. | `7` |
| `web/9-blazor-sites` | Interactive Blazor sites drawn in the page area of the browser, URL navigation, without `filesystem.*` capability. | `4`, `6` |
| `web/10-detached-window` | Detached window driven by the main instance (display link), for HTML pages first. | `4` |
| `web/11-sample-final` | "Intranet" sample app (pages, REST API, JS, Blazor), guide `asp-hack-developer-guide.md`, `sdk-reference.md`, final verification. | all |
| `web/12-site-data` | Added after closing (ADR 0087 §4): `HackWebDb` database of a site, debug logs `/var/log/hackweb/<id>.log`, browser autofill (ADR 0073 §3, ADR 0088). | `11` |

Order: `2`; then `3` and `4` in parallel; then `5`, `6` and `10`; then `7`
and `9`; then `8`; then `11`.

## 6. Out of scope (for now)

- Compiling an ASP.Hack project or a `.cshtml` view **in the OS** (Roslyn in the
  browser): the engine and the deployment allow it, the work item will come
  later.
- Migrating HackMail, HackerSearch and the other registered sites to ASP.Hack.
- Second desktop screen (the display link of ADR 0078 prepares it).
- Real HTTPS, certificates, full authentication (roles, OAuth), OpenAPI,
  WebSockets.

## 7. Open questions

None. The last two were settled by Benoit on 2026-10-16 (decisions
11 and 12 of section 4).

## 8. Task list

### Preparation
- [x] Questions to Benoit and answers (2026-10-16).
- [x] Merge of `rebuild/base` into `main` (PR #131) and creation of `web/base`.
- [x] This plan and ADR 0073 to 0078.
- [x] Validation of the plan and the ADRs by Benoit (questionnaire, 2026-10-16).

### Work items
- [x] `web/2-http-bodies` ([`http-bodies.md`](../../framework/http-bodies.md))
- [x] `web/3-listener-dns` ([`listening-ports-and-dns.md`](../../framework/listening-ports-and-dns.md))
- [x] `web/4-browser-html` ([`browser-html-rendering.md`](../browser-html-rendering.md), ADR 0079)
- [x] `web/5-static-sites` ([`static-sites.md`](../static-sites.md), ADR 0080)
- [x] `web/6-mvc-core` ([`asp-hack-mvc.md`](../../framework/asp-hack-mvc.md), ADR 0082)
- [x] `web/7-razor-views` ([`asp-hack-razor-views.md`](../../framework/asp-hack-razor-views.md), ADR 0083)
- [x] `web/8-cshtml-views` ([`asp-hack-cshtml-views.md`](../../framework/asp-hack-cshtml-views.md), ADR 0086: proof succeeded, no interpreted engine)
- [x] `web/9-blazor-sites` ([`blazor-sites.md`](../../framework/blazor-sites.md), ADR 0084)
- [x] `web/10-detached-window` ([`detached-display-windows.md`](../detached-display-windows.md), ADR 0081)
- [x] `web/11-sample-final` ([`asp-hack-developer-guide.md`](../../framework/asp-hack-developer-guide.md), ADR 0087: Intranet sample, `target` links of Blazor sites, views compilable in a shipped app, `TryValidateModel`; the `HackWebDb` database of ADR 0073 §3, a site's debug logs and autofill are done by `web/12`)
- [x] `web/12-site-data` ([`asp-hack-site-data.md`](../../framework/asp-hack-site-data.md), ADR 0088: `hackweb.<id>` database through an OS gateway, Diagnostic and uninstall, `ILogger<T>` to `/var/log/hackweb/<id>.log`, browser form history; the Intranet keeps its notes in its database)
