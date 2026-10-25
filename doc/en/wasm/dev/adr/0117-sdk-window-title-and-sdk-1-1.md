> [Version française](../../../../fr/wasm/dev/adr/0117-sdk-window-title-and-sdk-1-1.md)

# ADR 0117: window title set by the app and release of SDK 1.1

## Status

Accepted on 2026-10-17 (work item `v3.1/1-adr`, for the work items
`v3.1/3-window-title` and `v3.1/4-sdk-1-1`). Two questions are asked to
Benoit in the work item thread (§1 and §5); what follows applies the
recommended answer while awaiting it.

- **Fixes the gap with:** [ADR 0103](0103-hack-paint-image-files-and-theme-editor.md)
  §1 ("window title `nom.png — Hack Paint`, with `*` when the image is
  modified"), never implemented for lack of an API.
- **Applies:** [ADR 0114](0114-sdk-1-0-freeze.md) §1 (an addition goes into
  `PublicAPI.Unshipped.txt`; a minor release moves it into `Shipped`)
  and [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md) §3 (minimum
  SDK version written in the manifest).
- **Complements:** [ADR 0095](0095-window-events-sizes-and-keyboard.md) §1
  (window hooks in `WindowAppBase`) and
  [ADR 0081](0081-detached-display-windows-adjustments.md) (detached
  windows).
- **Plan:** [`v3-1-series-plan.md`](../plans/v3-1-series-plan.md); gap "no
  API for the title" of [`wasm-v3-final-status.md`](../plans/wasm-v3-final-status.md).

## Context

- `WindowRuntimeState.Title` (`Platform/HackerOs.Windowing.Abstractions/WindowRuntimeState.cs:98`)
  is set at opening by `WindowLaunchCoordinator` with the app's name and
  no longer changes. The title bar, the taskbar and the Alt+` switcher
  (`WindowManagerOverlays.razor`) read it.
- Neither `WindowAppBase` nor `IAppExecutionContext` offers a way to
  change it. Hack Paint therefore displays the file name in its own toolbar; the
  text editor, Code Editor and the theme editor do not display the
  open document in the title.
- Detached windows already have an internal `SetTitleAsync`
  (`DisplaySurfaceService.cs:234`) that changes the title of the browser
  window; sites (`HackWebSite.SetTitle`) and terminal tabs
  (`TerminalPanel.OnTitleChanged`) have their own titles, unrelated to the
  window.
- Under X11 or Wayland, the app itself sets the whole title (`_NET_WM_NAME`,
  `xdg_toplevel.set_title`); the window manager displays it as is.
- The SDK is frozen at 1.0.0 (ADR 0114): any added API must ship in a
  minor, 1.1.0.

## Decision

### 1. The app sets the whole title (question 1, default applied)

- The app provides the full title; the OS adds nothing to it. The convention of the
  base apps is `document — App` (`photo.png — Hack Paint`), with `*` at the
  start when the document has unsaved changes
  (`*photo.png — Hack Paint`), like GNOME and KDE.
- An empty or `null` title gives the default title: the app's localized name
  (`AppManifest.Name`, ADR 0111).
- The title is sanitized before display: control characters removed, spaces
  at the ends trimmed, at most 256 characters (beyond that, cut with `…`).
- **Spoofing:** as under X11, an app can choose a misleading title. The OS's
  sensitive windows (authentication agent, approval of a dangerous
  capability, ADR 0049) remain drawn by the OS, with its own frame, and never take
  an app's title; the taskbar always shows the icon of the owning app
  next to the title.

### 2. SDK API

In `HackerOs.AppSdk.Blazor`, on `WindowAppBase`:

```csharp
/// Titre affiché par le gestionnaire de fenêtres (barre de titre, barre des tâches, Alt+`).
protected string WindowTitle { get; }

/// Change le titre ; null ou vide rend le nom de l'app.
protected ValueTask SetWindowTitleAsync(string? title, CancellationToken cancellationToken = default);
```

- The call goes through a platform gateway (`IAppWindowGateway`,
  internal), never through a `HackerOs.Windowing.*` type in the SDK
  (ADR 0055). No capability is required: an app only changes the title
  of its own windows.
- Called before the first render (in `OnAppInitializedAsync`), the title is
  set before the window is displayed, without flicker.
- The call only targets the window of the instance that calls it, never
  another window of the same app.
- An app without an interface (kind `Terminal` or `Service`) does not have this API;
  a terminal tab's title remains that of `TerminalPanel`.

### 3. Platform side

- `WindowRuntimeState.Title` becomes modifiable through
  `WindowRuntime.SetTitle(windowId, title)`, which publishes a new event
  `WindowTitleChanged`. All surfaces that display the title (title
  bar, taskbar, switcher, window menu) subscribe to it. These types
  are outside the SDK promise (ADR 0114 §2); their additions still go
  into `PublicAPI.Unshipped.txt`.
- **Accessibility:** the window's accessible name (`aria-label` of the frame)
  follows the title. The change is not announced by a screen reader (no
  `aria-live` region), like a title change under Orca.
- **Detached windows:** the title is also set on the browser window
  that carries the surface, through the existing `SetTitleAsync`.
- **Host browser tab:** the page's `<title>` element remains the OS's
  (`HackerOS`); only the title of detached windows follows the app.

### 4. Base apps that adopt it

- **Hack Paint:** `nom.png — Hack Paint`, `*` when the image is modified
  (ADR 0103 §1); the file name leaves the toolbar.
- **Text editor, Code Editor, theme editor:** same convention with
  the open file (Code Editor: the file of the active tab, then the name of the
  open folder, like VS Code).
- **File explorer:** the name of the current folder (`Documents —
  Explorateur de fichiers`).
- The other apps keep their name.

### 5. Release of SDK 1.1: a separate work item at the end of the series (question 2, default applied)

The v3.1 series ends with a small work item `v3.1/4-sdk-1-1`, after all
the others and after `v3/base` is merged into `v3.1/base` (see
[`v3-1-series-plan.md`](../plans/v3-1-series-plan.md)):

- each `PublicAPI.Unshipped.txt` is moved into its `PublicAPI.Shipped.txt`
  (sorted lines, no duplicates), as for 1.0 (ADR 0114 §1). This covers
  the additions of the v3.1 series (`WindowOptions.RememberGeometry`, ADR 0116;
  `SetWindowTitleAsync`, this document) and those that arrived from `v3/base`
  (per-user associations, `passwd`);
- `build/Versions.props`: `HackerOsVersion` = `1.1.0`. The generated manifests
  then carry `sdkCompatibility.minimumVersion: 1.1.0`;
- the compatibility app compiled at **1.0.0** (`Tests/fixtures/sdk-compat`)
  stays as it is and must still load and run on the 1.1 OS: this is
  the proof of the promise "1.x runs on 1.y, y ≥ x" (ADR 0114 §2, §4). The
  package is not rebuilt;
- the "Breaks outside the SDK" section of [`sdk-packages.md`](../../framework/sdk-packages.md)
  lists the `*REMOVED*` lines of the series, and
  [`building-on-hackeros.md`](../../framework/building-on-hackeros.md) says what 1.1
  adds;
- the work item redoes the full verification of `v3/16-final` (Debug and
  Release build, all suites, `build/publish-trimmed.sh`, `build/verify-hosts.sh`)
  and updates the status report [`wasm-v3-final-status.md`](../plans/wasm-v3-final-status.md).

## Consequences

- `v3.1/3-window-title` adds the API, the gateway, the `WindowRuntime`
  event, the subscription of the surfaces (title bar, taskbar,
  switcher, detached windows) and the adoption by the
  base apps. Unit tests: title sanitizing, fallback to the app name,
  title set before the first render, a single window targeted. E2E test: open
  an image in Hack Paint, see its name in the title bar and the
  taskbar, modify it and see `*`.
- `app-sdk.md`, `sdk-reference.md`, `sdk-developer-guide.md`, `window-model.md`
  and `hack-paint.md` describe the API and the convention.
- The game repository (ADR 0092) can use the API by compiling against 1.1.0; its
  apps compiled at 1.0.0 keep running.

## Rejected options

- **The OS always adds " — App name"** (question 1): prevents an app from
  impersonating another in the title bar, but imposes a format
  that real window managers do not impose, and doubles the name when
  the app already writes it.
- **A `[Parameter]` property or a `<HackWindowTitle>` component** (like Blazor's
  `<PageTitle>`): convenient in markup, but a second path for the
  same thing; it may come later, built on
  `SetWindowTitleAsync`.
- **Bump the version to 1.1.0 in `v3.1/3-window-title`** (question 2): one
  work item fewer, but the additions that arrived from `v3/base` after it would ship
  in a 1.2, and the full verification would be done on an incomplete
  series.
