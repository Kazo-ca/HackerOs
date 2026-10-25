> [Version française](../../../fr/wasm/dev/detached-display-windows.md)

# Detached windows

The surface contract for apps (`IDisplaySurfaceService`, `DisplaySurfaceContent`, events, `HackBrowser.OpenDetachedAsync`) is in [`display-surface-api.md`](../framework/display-surface-api.md).

**Web phase, work item `web/10-detached-window`** ([`asp-hack-plan.md`](plans/asp-hack-plan.md),
section 5). Decision: [ADR 0078](adr/0078-detached-display-windows.md),
refined by [ADR 0081](adr/0081-detached-display-windows-adjustments.md).

## Purpose

A web page can leave the HackerOS desktop and open in a **host browser
window**: the user keeps the OS in one tab and the page in another window,
which can be placed on another monitor. This is not a second HackerOS: the
window starts neither .NET nor an OS. It is a **display surface** of the main
instance, like a second screen plugged into the same machine: the main
instance draws on it, receives its clicks and keys, and keeps serving the page
(network, cookies, `localStorage`).

The mechanism is general: any Blazor content of an app window can be
displayed in a surface. It prepares the **second screen** that Benoit wants
later: a desktop drawn in a surface.

## Architecture

```
OS window (main tab)                                  Detached window (window.open, same origin)
──────────────────────────────────                    ─────────────────────────────────────────────
BrowserWindow                                          <body class="hackeros-surface">
 ├─ placeholder "page open in a window"                 └─ .hackeros-surface-root
 └─ <DisplaySurfaceContent Surface=…> ── moves ──►         └─ .surface-content (rendered by the main
      window bar + page area                                   instance: bar, page, isolated iframe)
           │                                                       │ clicks, keys (relayed listeners)
IDisplaySurfaceService (Blazor SDK)                                │ page bridge messages
 └─ DisplaySurfaceService (Platform.Blazor) ◄── displaySurfaces.js ◄┘ (delivered to the main window)
         │                                       │  styles copied, size, focus, close
         └─ notification if the window is blocked └─ displaySurfaceRelay.js (loaded in the surface)
HackerOs.Platform.Blazor.lib.module.js (JS initializer, before Blazor):
  `document` listeners re-attached on each surface; `[_bl_…]` also searched in surfaces
```

| File | Role |
|---|---|
| `Platform/HackerOs.Platform.Blazor/Display/DisplaySurfaceService.cs` | Implementation: opens, tracks and closes surfaces; publishes the "window blocked" notification. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/displaySurfaces.js` | `window.open`, surface document (`<base>`, copied and kept-up-to-date styles, appearance inherited from the anchor), content move, `resize`/`focus`/`blur`/close events, messages delivered to the main window. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/displaySurfaceRelay.js` | Loaded in the surface: post a message "from" the surface. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/displaySurface.css` | Base style of a surface document. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/HackerOs.Platform.Blazor.lib.module.js` | JS initializer (`beforeStart`, `beforeWebStart`): relay of listeners and `@ref`. |
| `Apps/System/HackerOs.Apps.Browser/BrowserWindow.razor(.cs,.css)` | `⧉` button, placeholder, detached window bar, reattachment, `--detached` argument. |
| `Apps/System/HackerOs.Apps.Browser/BrowserPageView.razor.js` | Bridge responses sent by the relay when the page is in a surface. |

## How it works

### Opening a surface

- **Opened**: the module builds the document (title, `<base>` on the OS
  address, desktop stylesheets copied and updated when they change, `<html>`
  attributes), starts relaying the listeners and waits for the message relay.
  Result `Opened` with the surface (`surface-1`, `surface-2`…).

### Drawing in a surface

What makes Blazor work in another document (ADR 0081 §2):

- elements are created by the main document and then adopted by the
  surface's;
- the JS initializer attaches to each surface the listeners that Blazor (and
  MudBlazor) attach to `document`;
- a `@ref` whose element is in a surface is found by the `[_bl_<id>]`
  selector, also searched in the surfaces.

### The browser

- The `⧉` button (`#browser-btn-detach`) opens a surface and moves the page
  area into it: a bar (scheme icon, title, address, `↻`, `⇲ Reattach`) and the
  page. The OS browser window keeps its address bar, its developer tools and
  its status bar; the page area there shows "This page is open in a detached
  window" with **Show the window** and **Bring the page back here**.
- The detached window title follows the page title.
- Navigating from the OS (address bar, bookmarks, back) or from the page
  (links, forms) changes the page in the detached window.
- **Reattach** (in the detached window), `⧉` again or **Bring the page back
  here** reattach the tab; closing the detached window does too.
- The isolated page bridge (ADR 0075) follows the iframe: the surface delivers
  its messages to the main window, and the responses go out through the
  surface relay, the only sender the bridge accepts.
- An iframe that changes document starts from scratch: on detaching as on
  reattaching, the page is rewritten from the document already received,
  with no new request, with its current cookies and `localStorage`.

## Key decisions

- **One instance, several surfaces** (ADR 0078 §1): no second .NET instance,
  no second circuit; works with the three hosts.
- **Contract in the Blazor SDK** (ADR 0081 §1): base apps reach the OS only
  through the SDK.
- **Portal rather than a second rendering engine** (ADR 0081 §2): the content
  is rendered by the main engine and moved; the JS initializer fills in both
  of Blazor's "single document" assumptions.
- **Page rewritten on move** (ADR 0081 §4): rather than keeping an iframe that
  cannot be moved alive.
- **No capability**: a surface shows what the app already shows.

## Known limits

- A tab can only have one detached window; the OS browser has only one tab
  per window.
- On mobile, the host browser opens the window as a tab.

## Tests

- `Tests/HackerOs.Platform.Blazor.Tests/Display/DisplaySurfaceServiceTests.cs`:
  opening, blocked window and notification, host without JavaScript, events
  and closing by the user, closing by the OS, title, mounting and focus.
- `Tests/HackerOs.AppSdk.Tests/Apps/HackBrowserTests.cs`: launch arguments,
  empty address, missing browser.
- `Tests/HackerOs.UI.E2E.Tests/BrowserDetachedWindowTests.cs` (real Chromium,
  `test/test` host): detach a page, check its script, its `fetch`, the style
  copying; navigate from the OS and post a form in the detached window; click
  **Reattach** in the detached window (proof of ADR 0078 §4); close the window
  by hand and get the page back.

## Task list

- [x] `IDisplaySurfaceService` / `IDisplaySurface` contract in the Blazor SDK.
- [x] `DisplaySurfaceContent` portal.
- [x] `DisplaySurfaceService` and `displaySurfaces.js` module (document,
  styles, events, closing, closed OS tab).
- [x] JS initializer: `document` listeners and `@ref` in surfaces.
- [x] Relay of isolated page bridge messages.
- [x] Browser: `⧉`, placeholder, detached window bar, reattach,
  page rewrite, tracked title, `--detached`.
- [x] `HackBrowser.OpenAsync` and `OpenDetachedAsync`.
- [x] Notification and banner when the window is blocked.
- [x] Unit and E2E tests.
- [x] ADR 0081, this document, index, plan, SDK references.
