# ADR 0078: detached display windows (preparation for the second screen)

## Status

Accepted on 2026-10-16 (proposed the same day, then validated by Benoit with the
questionnaire of section 4 of the plan).

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Depends on:** ADR 0075 (isolated iframe).

## Context

Benoit wants to open a site in a new window, outside the OS: one window
with the OS, the other with the same OS but showing only the page. He later wants
a second screen in another window; this work item must prepare for it.

Starting a second instance of HackerOS in the new window would give a
second OS: its processes, memory and state would be separate, and
`HackerOs.Server` accepts only one active circuit.

## Decision

### 1. A single instance, several display surfaces

The detached window is opened by the main instance
(`window.open` on an empty same-origin page) and **driven by it**. It
does not start a .NET runtime. The OS remains unique: processes, files and
network live in the main instance.

### 2. The display link

`IDisplaySurfaceService` (Platform) manages the surfaces: the main one (the
desktop) and the detached surfaces. Each surface has an identifier, a size,
a title, a state (open, closed by the user, lost). Opening must
come from a user gesture (rule of popup
blockers). The service's JS module creates the surface's document, places the
content in it and relays the surface's events (close, resize,
focus). Closing the OS or logging out closes the surfaces.

### 3. Web pages in a detached surface

"Open in detached window" (tab menu, and a new `HackBrowser.OpenDetached(url)`
facade of the SDK) moves the tab into a surface: the isolated iframe of ADR 0075 is
created there, and its bridge still talks to the main instance's browser.
The surface's title bar shows the address; "Reattach" puts the tab back
in the browser.

### 4. What remains to be proven

Drawing a Blazor component (interactive Blazor site of ADR 0073 §4, or later
a desktop on a second screen) in a detached surface requires that Blazor's
rendering engine write into another document's DOM and receive events
there. The `web/10-detached-window` work item makes this proof; if it
succeeds, Blazor sites also open in a detached window, otherwise they stay
in the OS browser and an ADR describes the other path before the second
screen.

## Consequences

- The second screen will be one more surface for the window manager.
- No second instance, no second circuit: compatible with the three
  hosts.
- The host browser may block `window.open`; the OS then displays a
  notification that explains how to allow it.
