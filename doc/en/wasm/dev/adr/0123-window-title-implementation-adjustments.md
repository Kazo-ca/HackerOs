> [Version française](../../../../fr/wasm/dev/adr/0123-window-title-implementation-adjustments.md)

# ADR 0123: implementation adjustments for the window title

## Status

Accepted on 2026-10-17 (work item `v3.1/3-window-title`).

- **Adjusts:** [ADR 0117](0117-sdk-window-title-and-sdk-1-1.md) §2 to §4, without
  changing its decision (the app sets the whole title, the OS sanitizes and displays it).
- **Applies:** [ADR 0114](0114-sdk-1-0-freeze.md) §1: all additions go into
  `PublicAPI.Unshipped.txt` and will ship in 1.1 (work item `v3.1/4-sdk-1-1`).
- **Document:** [`window-model.md`](../../framework/window-model.md) §4.

## Context

ADR 0117 describes the API (`WindowTitle`, `SetWindowTitleAsync`), an "internal"
gateway `IAppWindowGateway`, the runtime command and the apps that
adopt it. The implementation had to specify how the gateway reaches the app,
how an app composes the `document — App` convention with the translated name, and
what happens to the titles that some apps already displayed themselves.

## Decision

### 1. `IAppWindowGateway` is public, passed by value in a fixed cascade

- The interface is in `HackerOs.AppSdk.Blazor` and public: `WindowAppBase` (SDK)
  calls it and the platform (`HackerOs.Platform.Blazor`) implements it; no
  `InternalsVisibleTo` links these two packages. Like `IWindowAppEventTarget`,
  an app neither implements nor calls it: it goes through `WindowAppBase`.
- It is **not** in dependency injection. `WindowAppRenderer` creates one
  instance per window (`AppWindowGatewayFactory.Create(windowId, manifest)`) and
  gives it to the app through a fixed `CascadingValue<IAppWindowGateway>`, next to the execution
  context. An app can therefore only obtain the gateway of its own
  window: nothing lets it name another (§2 of ADR 0117, "a single
  window targeted").
- The cascading value is set before the app component is created: a
  title set in `OnAppInitialized[Async]` is applied before the first render.

### 2. `document — App` convention provided by the SDK

- `IAppWindowGateway.DefaultTitle` returns the window's default title: the app's name
  in the desktop language (ADR 0104 §4), the one that
  `WindowLaunchCoordinator.DefaultTitle(manifest)` gives to a new window.
- `WindowAppBase.FormatWindowTitle(document, modified)` composes
  `document — App` with this name, preceded by `*` if `modified`, or the name alone without
  a document. The base apps use it; an app remains free to write another title.

### 3. Behavior details

- `IAppWindowGateway.SetTitleAsync` returns the displayed title once sanitized;
  `WindowAppBase` ignores a call that asks again for the title already requested, so that an
  app can call it after each render without a round trip.
- Sanitizing (`WindowTitles.Sanitize`, Windowing.Abstractions) never splits
  a UTF-16 surrogate pair in two; the 256 character limit counts the
  ellipsis.
- An already closed window is not renamed: the call does nothing, like a
  property written on a destroyed X11 window.
- Without a window manager (tests, prerendering), `WindowTitle` returns the last
  requested title, trimmed at the ends, or the manifest name.

### 4. Detached windows

The surfaces tracked by `TrackDisplaySurface` take the app's title
only after a call to `SetWindowTitleAsync` with a non-empty title; a
surface tracked afterwards receives the current title. The browser, which gives its
surface the page title and does not call `SetWindowTitleAsync`, keeps its
behavior.

### 5. Titles already displayed by apps

- **Hack Paint:** the title leaves the app (ADR 0117 §4); the
  "opening… / saving…" indicator moves to the status bar.
- **Text editor:** its menu banner keeps the document name (with `•`
  when it is modified, and the load error alert), which its E2E tests
  read; the window carries `*notes.txt — Éditeur de texte`.
- **Theme editor:** the toolbar keeps the title, identical to that of the
  window.
- **Code Editor:** its banner displays the window title; the separator
  becomes the em dash and `*` marks the modified active tab
  (`*Program.cs — src — Éditeur de code`). The keys `Title.Folder`,
  `Title.Document` and `Title.DocumentFolder` of its resources are removed.
- **File explorer:** `/` for the root, the name of the trash when
  it is displayed.

### 6. Mobile desktop

`MobileShell` also passes the factory to `WindowAppRenderer`: the title set by
the app appears in the mobile desktop's recents list as in the desktop
taskbar.

## Consequences

- Surface added (in `PublicAPI.Unshipped.txt`): Blazor SDK
  (`IAppWindowGateway`, `WindowAppBase.WindowTitle`, `SetWindowTitleAsync`,
  `FormatWindowTitle`), Windowing (`SetWindowTitleCommand`,
  `WindowTitleChangedEvent`, `WindowTitles`, `WindowRuntime.SetTitle`), platform
  (`AppWindowGatewayFactory`, `WindowAppRenderer.WindowGateways`,
  `WindowLaunchCoordinator.DefaultTitle`) and the overloads added by three base
  apps.
- A host that composes `DesktopShell` or `MobileShell` itself must register
  `AppWindowGatewayFactory` (done by `AddHackerOsEcosystem`).

## Rejected options

- **Gateway injected by DI with the instance identifier as a parameter:** an
  app could have injected the service and named another app's instance;
  a registry of contexts would have been needed to refuse it.
- **`InternalsVisibleTo` from the SDK to the platform:** ties two separately
  published packages and hides an interface that the platform must be able to implement.
