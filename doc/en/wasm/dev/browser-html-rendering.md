> [Version française](../../../fr/wasm/dev/browser-html-rendering.md)

# Browser HTML rendering

**Web phase, work item `web/4-browser-html`** ([`asp-hack-plan.md`](plans/asp-hack-plan.md),
section 5). Decision: [ADR 0075](adr/0075-browser-html-rendering.md), adjusted
by [ADR 0079](adr/0079-browser-html-rendering-adjustments.md). Builds on
the real HTTP bodies of [`http-bodies.md`](../framework/http-bodies.md).

## Purpose

Before this work item, the browser only drew structured `SimulatedPage`s
and displayed raw HTML as text. Now, a `text/html` response
(from a simulated-network site or a local `file://` file)
becomes a **real web page**: its CSS applies, its JavaScript runs,
its `fetch`, its `XMLHttpRequest`, its forms, its cookies and its
`localStorage` work. The page remains a prisoner: it reaches neither
HackerOS, nor the Internet, nor the user's files. Everything it
asks for goes through the browser, which decides in C#, as a real browser
applies the same-origin policy and CORS.

The browser also gains:

- `file://` URLs (file or "Index of" listing of a folder);
- the source view (`view-source:` or `Ctrl+U`);
- developer tools with a **Network** tab and a **Console**
  tab (`F12`);
- Chromium-style error pages (`ERR_NAME_NOT_RESOLVED`,
  `ERR_CONNECTION_REFUSED`, `ERR_FILE_NOT_FOUND`, HTTP errors);
- downloads to `~/Downloads` and PDF display.

## Architecture

```
BrowserWindow ── NavigateAsync ──► BrowserResourceLoader ──► SdkBrowserTransport
     │                                  │  (journal Network/Console)   ├─ http(s) : HackHttpClient + cookies de l'onglet
     │                                  ▼                              └─ file:   : HackFile / HackDirectoryInfo
     │                            DevToolsLog ◄── DevToolsPanel
     ▼
HtmlPageComposer (AngleSharp) ── SubresourceFetcher + CssResourceRewriter
     │   CSS, scripts, images, polices… inlinés en URL data:, en-tête CSP, <base>, script du pont
     ▼
BrowserPageView (.razor.js) ── iframe sandbox="allow-scripts allow-forms", srcdoc
     ▲            │ postMessage (jeton)
     │            ▼
PageBridgeHandler ◄── PageBridge.js (dans la page) : fetch, XHR, liens, formulaires,
     │                 cookies, localStorage, console, history, titre, raccourcis
     └── PageStorage (~/.local/share/hackeros/browser/storage/<site>/local-storage.json)
```

All files are under `Apps/System/HackerOs.Apps.Browser/`:

| Folder / file | Role |
|---|---|
| `Net/BrowserTransport.cs` | `BrowserRequest`, `BrowserResponse`, `BrowserFetchResult`, `IBrowserTransport`, `BrowserErrorCodes` codes. |
| `Net/SdkBrowserTransport.cs` | Real transport: `HackHttpClient` (tab cookies or empty container depending on credentials, forbidden headers filtered) and `file:` (file or listing). |
| `Net/BrowserUrl.cs` | Address bar (`view-source:`, `about:blank`, `/…` and `~/…` paths, HackerSearch search), relative resolution, origin, per-site storage key. |
| `Net/DirectoryListingPage.cs` | "Index of" page of a local folder. |
| `Net/BrowserResourceLoader.cs` | Single pass-through point: each request produces a Network line and, on failure, a "Failed to load resource" Console message. |
| `Rendering/HtmlPageComposer.cs` | Parses the HTML (AngleSharp), inlines subresources, removes `<base>` and frames, adds charset, `<base href>`, CSP and the bridge script. |
| `Rendering/SubresourceFetcher.cs` | Loads and caches subresources (at most 300 requests and 32 MiB per page); refuses `file:` from a web page. |
| `Rendering/CssResourceRewriter.cs` | Rewrites `url()` and `@import` (depth 4) into data URLs. |
| `Bridge/PageBridge.js` | Embedded resource injected into the page: replaces `fetch`, `XMLHttpRequest`, `document.cookie`, `localStorage`/`sessionStorage`, intercepts links and forms, relays the console and errors. |
| `Bridge/PageBridgeContracts.cs` | Bridge messages (camelCase JSON, `PageBridgeJsonContext`). |
| `Bridge/PageBridgeHandler.cs` | C# side of the bridge: `fetch` rules (credentials, CORS, opaque responses), document cookies, resources loaded afterwards. |
| `Storage/PageStorage.cs` | `localStorage` persisted per site (5 MiB quota), `sessionStorage` in the tab's memory. |
| `DevTools/*` | Bounded log (500 entries) and Network/Console panel. |
| `BrowserPageView.razor(.js)` | Creates the isolated iframe, relays messages, detects a script navigation. |
| `BrowserErrorPages.cs`, `BrowserDownloads.cs` | Error pages and downloads. |

`SimulatedFileTypes` (`Shared/HackerOs.Simulation.Abstractions/Network/`)
gives the MIME type of a local file from its extension.
`HackCookieContainer.Remove` (SDK) deletes a cookie expired by the page.

## How it works

### Navigation

`BrowserWindow.NavigateAsync` cancels the previous navigation, clears the developer
tools, then asks the loader for the document. The response is
displayed according to its type:

- `SimulatedPage`: historical structured rendering (`SimulatedPageRenderer`), its
  forms become `POST` navigations;
- HTTP error status without a displayable body: error page;
- `Content-Disposition: attachment` or binary body: saved to
  `~/Downloads` (unique name `name (n).ext`);
- `text/html`: composed then displayed in the iframe;
- other type: `ResponseBodyViewer` (text, JSON, image, PDF).

The history distinguishes `Push`, `Replace` (redirect, `history.replaceState`)
and `Traverse` (previous/next), which fixes the old bug that truncated
the "next" history.

### The isolated page

The iframe is in `srcdoc` with `sandbox="allow-scripts allow-forms"`, without
`allow-same-origin`: the page has an opaque origin and can read neither the DOM
of HackerOS nor its storage. A CSP meta forbids any connection
(`connect-src 'none'`, `img-src data: blob:`…): the page loads nothing
itself, everything it needs has been inlined as `data:` URLs or goes through
the bridge.

The bridge communicates through `postMessage` with a token specific to each document.
A response that arrives after the tab has changed page is discarded.

### What the page's JavaScript sees

| API | Behavior |
|---|---|
| `fetch`, `XMLHttpRequest`, `navigator.sendBeacon` | Sent to the browser. Same origin: full headers. Different origin: CORS checked (`Access-Control-Allow-Origin`, `-Allow-Credentials`, `-Expose-Headers`), `no-cors` gives an opaque response. `file:` always refused. |
| `document.cookie` | Tab cookies for the page's URL (`Max-Age` and `Expires` handled). Empty on a `file:` page. |
| `localStorage` | Persisted per site in `~/.local/share/hackeros/browser/storage/<key>/local-storage.json` (keys `https_host`, `http_host_port`, `file`). 5 MiB quota, `QuotaExceededError` beyond that. |
| `sessionStorage` | In memory, for the lifetime of the tab. |
| Links and forms | Intercepted: `GET`, urlencoded `POST`, multipart, `text/plain`; local `#` fragment; `download` saves the file. |
| `history.pushState`/`replaceState` | Updates the address bar and the history. |
| `document.URL`, `document.referrer` | The page's real URL, not `about:srcdoc`. |
| `console.*`, unhandled errors | Console tab of the developer tools. |
| `alert` | Banner above the page; `confirm` returns `false`, `prompt` `null`. |
| Autocomplete | When a form is submitted, named text fields (neither password, nor hidden, nor `autocomplete="off"`, nor card number) go into `~/.local/share/hackeros/browser/form-history.json`; on focus of a field, its past values are offered through a `<datalist>`; `Ctrl+Shift+Delete` clears the history (ADR 0088 §6, [`asp-hack-site-data.md`](../framework/asp-hack-site-data.md#autofill)). |
| `WebSocket`, `EventSource`, `window.open`, geolocation | Refused. |
| Elements added afterwards (`img.src = …`, `appendChild`) | Loaded by the browser on demand. |

### `file://`

The browser reads local files with the user's rights, limited
to their home folder (the browser is not a system app, ADR 0079). A
folder gives an "Index of" page. A `file:` page can load its relative files
and call a site with `fetch` (subject to CORS, origin `null`); a
web page can never load a `file:` ("Not allowed to load local
resource"). The browser opens `.html` and `.htm` files
(`FileHandlers`).

### Developer tools

`F12` (or the button) opens the bottom panel. **Network** lists each
request: name, method, status, type, size, duration, in red on failure.
**Console** lists the messages of the page and of the browser. The button's badge
counts the errors.

### Detached window

The `⧉` button of the toolbar moves the tab into a window of the host
browser, outside the desktop, still served by this OS browser
window (work item `web/10-detached-window`, see
[`detached-display-windows.md`](detached-display-windows.md)). The page's iframe
is recreated there and its bridge keeps talking to this browser.

## Usage

- Type `example.hackeros/page.html` or a path (`~/Documents/site/index.html`)
  in the address bar.
- `Ctrl+U` or the `</>` button: source view; `F12`: developer tools;
  `F5`: reload; `Alt+←`/`Alt+→`: history. These shortcuts also work
  when the focus is in the page.
- A site that wants to be called from another origin returns
  `Access-Control-Allow-Origin` (HackerSearch does so on `/api/search`).

## Key decisions

- Subresources are inlined by the browser in C# (AngleSharp):
  the page never touches the network, and each request goes through the simulated
  network, hence into the Network log and the app's counters.
- The bridge is a JS file embedded in the assembly, not a host script
  (AGENTS.md); it is injected into the page's document.
- A script navigation (`location.href = …`) cannot be
  intercepted in an iframe without same origin: the frame detects it (a
  load without an announcement from the bridge) and displays a "This page navigated by
  script" page rather than a page without a bridge.
- Adjustments to ADR 0075: see [ADR 0079](adr/0079-browser-html-rendering-adjustments.md).

## Known limitations

- No nested frames (`iframe`, `object`, `embed` become empty).
- Relative `import`s of ES modules do not resolve (the script is a
  `data:` URL).
- No CORS preflight request (`OPTIONS`): only the response
  headers are checked.
- `HttpOnly` is not modeled on the `document.cookie` side.
- `target="_blank"` opens in the same tab.

## Tests

- `Tests/HackerOs.Apps.Browser.Tests`: HTML composition, CSS rewriting, bridge
  (CORS, credentials, cookies), URLs, storage, error pages, listing,
  downloads, developer tools log.
- `Tests/HackerOs.UI.E2E.Tests/BrowserHtmlRenderingTests.cs`: in Chromium,
  web page with CSS, script, `fetch` and cookie; sandbox verified; Network
  tab; source view; XHR and `POST` form; local `file://` site with
  `localStorage`, CORS `fetch`, link and back; DNS error page.
- `Tests/HackerOs.Apps.Browser.Tests/FormHistoryTests.cs` and
  `Tests/HackerOs.UI.E2E.Tests/BrowserSiteDataTests.cs`: form
  history (file, limits, `<datalist>` in Chromium).
- `Tests/HackerOs.UI.E2E.Tests/BrowserHomePageTests.cs`: outside demo mode, the
  browser opens on HackerSearch and a search typed in the address bar
  displays its results (ADR 0085: the default network catalog
  is wired into the OS).
- Test pages: `example.hackeros` (`/page.html`, `/form.html`, `/echo`,
  `/api/ping`) in `SmokeTestNetworkSeed`, and the site
  `~/Documents/e2e-site` of the E2E demo mode.

## Task list

- [x] MIME types of local files (`SimulatedFileTypes`).
- [x] Browser transport (`http(s)` through the SDK, `file:` and listing).
- [x] Address bar URLs, `view-source:`, local paths.
- [x] Subresource loader and CSS rewriting.
- [x] HTML composition (AngleSharp), CSP and bridge injection.
- [x] Isolated iframe and `postMessage` relay (`BrowserPageView.razor.js`).
- [x] Bridge: `fetch`, XHR, links, forms, cookies, storage, console, history.
- [x] `localStorage` persisted per site, `sessionStorage` per tab.
- [x] Network and Console developer tools.
- [x] Network and HTTP error pages, downloads, PDF.
- [x] `Push`/`Replace`/`Traverse` history.
- [x] Test pages in the demo network and local E2E site.
- [x] Unit and E2E tests.
- [x] Documentation, ADR 0079, index.
