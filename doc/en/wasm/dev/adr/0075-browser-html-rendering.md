> [Version française](../../../../fr/wasm/dev/adr/0075-browser-html-rendering.md)

# ADR 0075: browser HTML rendering in an isolated iframe

## Status

Accepted on 2026-10-16 (proposed the same day, then validated by Benoit with the
questionnaire of section 4 of the plan).

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Depends on:** ADR 0074 §1 (real HTTP bodies).

## Context

The HackerOS browser only draws structured `SimulatedPage` objects. To
serve HTML, CSS and JavaScript, it must display a real web page.
Benoit chose isolation: a site's JS must reach neither HackerOS nor
the Internet.

## Decision

### 1. An iframe without same origin

The page is written into an `iframe sandbox="allow-scripts allow-forms"` through
`srcdoc`, **without** `allow-same-origin`: its origin is opaque, it can
read neither the HackerOS DOM, nor its IndexedDB, nor its cookies. The JS module of the
page view (`BrowserPageView.razor.js` in the Browser app, ADR 0007) is the only
code that talks to it, through `postMessage`.

### 2. The browser loads subresources itself

Like a real browser, the Browser app:

1. receives the HTML through `HackHttpClient` (or `HackFile` for `file://`);
2. parses it in C# (AngleSharp library, WebAssembly-compatible);
3. loads through the simulated network each `<link rel=stylesheet>`, `<script src>`,
   `<img>`, `<source>`, font and `url()` of stylesheets, resolving
   relative URLs against the page address;
4. replaces them with `blob:`/`data:` URLs and writes the final document into
   the iframe.

The browser's developer tools exist from v1, with two
tabs: **Network** (each request with method, status, type, size and
duration) and **Console** (output of `console.*` and JS errors of the page,
relayed by the bridge).

### 3. The page bridge

A small script injected at the top of the document replaces:

- `fetch` and `XMLHttpRequest`: sent to the browser, which passes them through
  `HackHttpClient` (all methods, headers, bodies), then returned to the page;
- link clicks and form submissions (`GET` and `POST`): tab
  navigation;
- `document.cookie`: read from the tab's `HackCookieContainer`, written through
  messages;
- `localStorage`/`sessionStorage` (forbidden in an opaque origin): per-site
  storage, kept by the browser in
  `~/.local/share/hackeros/browser/storage/<host>/`;
- resources added by JS after loading (`img.src`, new
  `<link>`): a `MutationObserver` has them loaded through the bridge.

`WebSocket`, `EventSource`, `window.open` and geolocation are refused for
now (error in the page console).

### 4. `file://`

`file:///home/user/site/index.html` is read by `HackFile` with the rights of
the user (the browser is a system app). Relative resources are
read from the same folder. The page has no network bridge to `localhost` except
through its absolute URLs.

### 5. Content types

| `Content-Type` | Rendering |
|---|---|
| `text/html` | iframe (§1 to §3) |
| `SimulatedPage` (existing sites) | `SimulatedPageRenderer` (unchanged) |
| `image/*`, `text/plain`, `application/json`, `application/pdf` | built-in viewer |
| other | download to `~/Downloads` |

## Consequences

- New AngleSharp dependency in the Browser app only.
- The JS code stays minimal and belongs to the Browser app; nothing is added to the
  hosts. In `HackerOs.Server` (Interactive Server rendering), the bridge works
  the same way: the JS runs in the browser, the C# on the server.
- The links and forms of `SimulatedPage` pages do not change.
