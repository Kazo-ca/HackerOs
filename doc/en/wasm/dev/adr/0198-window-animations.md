> [Version française](../../../../fr/wasm/dev/adr/0198-window-animations.md)

# ADR 0198: window animations

## Status

Accepted on 2026-10-30, work item 3 of TODO issue
[#242](https://github.com/Kazo-ca/HackerOs/issues/242) (points 5, 6 and 7 of the decisions
taken with Benoit on 2026-10-29), `todo/` series. Implementation:
[`window-animations.md`](../window-animations.md).

## Context

- Windows appear, disappear and change size at once. A minimized window gets the `hidden`
  attribute right away (`WindowHost.razor`).
- Window geometry is written by `WindowHost.razor.js` into CSS variables (`--window-x`…).
  A mouse move is a browser `translate` preview, committed by C# on release
  ([ADR 0163](0163-window-pointer-preview-and-invariant-coordinates.md)).
- Each screen has its taskbar and window buttons
  ([ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md)). Windows are moved into
  the layer of their screen by a portal (ADR 0178 §3).
- The "Reduce motion" setting exists ([ADR 0105](0105-accessibility-preferences.md)): it
  sets `data-motion="reduced"`, sets the transition tokens to `0ms` and cuts CSS
  animations down to 0.01 ms.

## Decision

1. **What is animated.** Open, close, minimize, restore from the taskbar, maximize and
   restore the size, and snap (to the left or right, by keyboard or by dragging). Mouse
   moves and resizes are not animated: they already follow the pointer.
2. **Minimize into the button of its screen.** The window shrinks and fades into its
   button on the taskbar of **its** screen (the button carries `data-taskbar-window-id`).
   Without a visible button, it goes to the bottom of its screen. The `hidden` attribute
   is set only when the animation ends; a restore during the animation cancels it.
   Restore plays the same animation backwards, from the button.
3. **Maximize, restore and snap**: the window goes from its old box to the new one
   ("FLIP": the new box is laid out at once, a transform makes it start from the old one).
   When a drag ends in a snap, the animation starts where the window was drawn during the
   drag.
4. **Close**: like a compositor that keeps the last frame of a destroyed window, the OS
   fades a **frozen copy** of the window while Blazor removes the real one at once. The
   copy is hidden from assistive technologies (`aria-hidden`, `inert`), has no `id` and
   no `data-window-*`, and never takes the pointer. Its `iframe`s, videos and sounds are
   replaced by empty blocks (nothing reloads or plays); its `canvas`es keep their last
   picture.
5. **Style**: fast and plain, a fade and a slight scale, **180 ms** by default, curve
   `cubic-bezier(0.2, 0, 0, 1)`.
6. **Themes**: three CSS variables a theme or a theme pack can redefine:
   `--hos-window-anim-duration` (`180ms`), `--hos-window-anim-easing` and
   `--hos-window-anim-scale` (`0.95`). The duration is a catalog token, editable in the
   theme editor (Animations group); the curve and the scale are for theme sheets only,
   because the catalog validator accepts named curves only (`ease`, `ease-out`…).
7. **Reduce motion** turns everything off, with no new setting: `data-motion="reduced"`
   and `prefers-reduced-motion: reduce` set `--hos-window-anim-duration` to `0ms`, and a
   zero duration means no animation (the window behaves as before).
8. **Mobile**: a host without window chrome (one pinned surface, `ShowChrome` set to
   `false`) animates nothing.
9. **Code**: the `HackerOs.Windowing.Blazor` package only. `WindowHost` picks the
   animation of each render from the window's state change and passes it to
   `projectWindowGeometry`; the script plays it with the browser's Web Animations API (the
   boxes to measure are out of reach of C#). Nothing changes in the app SDK;
   the package's public API only gains the `WindowHost.OnParametersSet` override
   (`PublicAPI.Unshipped.txt`). The taskbar only adds the `data-taskbar-window-id` attribute to its buttons.

## Consequences

- Tests that minimize a window must wait for the `hidden` attribute (Playwright already
  waits): it comes ~180 ms later.
- A closed window stays visible ~180 ms as an inert copy, absent from the accessibility
  tree and from `[data-window-id]` queries.
- The first render of a window is transparent (`is-opening`) until its animation starts,
  so it never shows one frame before it.
