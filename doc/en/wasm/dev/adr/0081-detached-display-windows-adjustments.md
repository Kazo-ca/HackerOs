# ADR 0081: detached windows, implementation adjustments

## Status

Accepted on 2026-10-16, with the `web/10-detached-window` work item.

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Completes:** ADR 0078 (detached display windows). Replaces none
  of its decisions; clarifies where the contracts live and settles the proof of its
  §4.

## Context

ADR 0078 places `IDisplaySurfaceService` in the platform and leaves open
a question: can Blazor's rendering engine write into the DOM of another
document and receive its events? The implementation also showed
three constraints that the ADR did not describe: the SDK boundary, the way
Blazor finds elements, and the bridge of the isolated pages of ADR 0075.

## Decision

### 1. The contract is in the Blazor SDK, the implementation in the platform

The browser is a base app: it gets OS services only through
the SDK (ADR 0055, diagnostic `HOSAPP0031`). The contract (`IDisplaySurfaceService`,
`IDisplaySurface`, `DisplaySurfaceOptions`, `DisplaySurfaceOpenResult`) and the
`DisplaySurfaceContent` component therefore go into `HackerOs.AppSdk.Blazor`
(namespace `.Display`), like `IDialogService`. The implementation
`DisplaySurfaceService` stays in `HackerOs.Platform.Blazor` and
`AddHackerOsEcosystem` registers it (session scope, one circuit on the
server). The desktop is the implicit main surface; the service manages
only the others.

### 2. Blazor draws in the detached window through a portal (proof succeeded)

A surface's content is rendered in the main instance's component tree,
then its element is moved into the detached window
(`DisplaySurfaceContent`). The engine keeps its references to the nodes and keeps
updating them wherever they are. Two Blazor mechanisms
nevertheless assume a single document; a JS initializer of the library
(`HackerOs.Platform.Blazor.lib.module.js`, supported by the three hosts
without manual reference) extends them before Blazor starts:

- **Events.** Blazor delegates all events through listeners placed
  on `document`. The initializer remembers each listener placed on `document` and
  also places it on the document of each open surface.
- **`@ref`.** Blazor finds the element of an `ElementReference` through
  `document.querySelector('[_bl_<id>]')`. The initializer also searches in the
  surface documents when this precise selector finds nothing.

The elements are still created by the main document, so Blazor's
`instanceof Element` tests hold. The E2E test
`BrowserDetachedWindowTests` clicks a Blazor button in the detached window:
it verifies the proof at each Blazor update. The Blazor sites of
ADR 0073 §4 can therefore open in a detached window, and the second screen
will be a surface that displays a desktop.

### 3. Isolated pages keep their bridge

A page's bridge (ADR 0075 §3) talks to its parent, which becomes the detached
window:

- the detached window hands the main window the messages it
  receives, where the iframe's host listens for them;
- the bridge accepts a response only from its parent: the main window
  answers through a relay loaded in the detached window
  (`displaySurfaceRelay.js`), whose message therefore originates from it.

### 4. Moving an iframe reloads the page

A browser destroys the context of an iframe removed from its document. On
detaching as on reattaching, the OS browser rewrites the page from
the document already received, without a new request to the server (a `POST`
is not resent), with the current cookies and `localStorage`. The page's
JavaScript state is lost, as after a cache-served reload.

### 5. End of a surface

- The user closes the detached window: state `ClosedByUser`, the page
  returns to the OS browser's window.
- The window leaves the surface's document (the user types another
  address there): state `Lost`, the OS closes the window and the page returns.
- The owner closes the surface, or closes itself: state `Closed`.
- Reloading or closing the OS tab closes all surfaces; closing the
  session closes the apps, hence their surfaces.

### 6. Blocked window

If the host browser refuses the window, the service itself publishes the
notification of ADR 0078 (source `org.hackeros.display`, without requesting
`notifications.post` from the app) and the OS browser displays a banner.

## Consequences

- The detached window depends on two internal details of Blazor (delegation on
  `document`, `_bl_` selector). The E2E test watches them; if a Blazor version
  changes them, the initializer is the only place to adapt.
- Listeners placed on `document` by other libraries (MudBlazor)
  are also placed on the surfaces: a click in a surface closes, for
  example, a menu open on the desktop, as a click elsewhere on the
  same screen would.
- An app can detach any part of its window with
  `DisplaySurfaceContent`, without a capability: the window opens no new
  access, it shows what the app already shows.
