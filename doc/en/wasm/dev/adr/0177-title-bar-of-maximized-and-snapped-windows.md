> [Version française](../../../../fr/wasm/dev/adr/0177-title-bar-of-maximized-and-snapped-windows.md)

# ADR 0177: title bar of maximized and snapped windows

## Status

Accepted on 2026-10-28. Benoit reported the bug and asked for the two behaviors
("when maximized or snapped, moving it brings it back to the normal state";
"double click on the title bar toggles normal/maximize"). He chose "Maximize, like
Windows" for the Maximize button of a snapped window. Replaces the decision
"Snapping keeps no restore geometry" of
[`window-model.md`](../../framework/window-model.md) (ADR 0095) and completes
[ADR 0163](0163-window-pointer-preview-and-invariant-coordinates.md) (move gesture).

## Context

- On a maximized or snapped window, Close and Maximize seemed to do nothing; only
  Minimize answered, and right click on the icon ▸ Close worked.
- Cause: the resize handles (`resize-top`, `resize-top-right`, `resize-right`,
  `z-index: 2`) sit over the title bar buttons. A maximized or snapped window
  touches the top of the screen, so the pointer often lands on that band. The click
  hits a handle, which does nothing on a maximized window. Measured in Chromium:
  23% (HackerOS theme) to 48% (Windows 98) of the Close button is covered, 10 to
  20% of the others.
- Snapping (`snap-left`, `snap-right`) resized a normal window with
  `ResizeWindowCommand` and kept no geometry: nothing gave back the size from
  before.
- Dragging the title bar of a maximized window did nothing, and neither did a
  double click on the title bar.

## Decision

1. **Buttons above the handles.** `.window-actions` has `position: relative;
   z-index: 3`. A click on a button never starts a resize.
2. **No handles on a maximized window.** `WindowChrome` draws them only for a
   resizable `Normal` window (a snapped window keeps them).
3. **Snapping keeps the earlier geometry.** New command
   `SnapWindowCommand(WindowId, WindowBounds)` and property
   `WindowRuntimeState.IsSnapped`. The window stays `Normal`; `RestoreBounds` keeps
   the geometry from before the first snap (or from before the maximize when it was
   maximized). `WindowRuntime` rules:
   - `RestoreWindowCommand` on a snapped window gives that geometry back;
   - maximizing a snapped window keeps that geometry: restoring afterwards undoes
     the snap (Benoit's "Maximize, like Windows" choice);
   - minimizing then restoring a snapped window puts it back on its half screen;
   - `MoveWindowCommand` takes it off its tile and keeps its current size
     (keyboard move);
   - events: `WindowResizedEvent` and `WindowResizeCompletedEvent` (plus
     `WindowVisualStateChangedEvent` when it was maximized).
   `Super+Down` (`restore-or-minimize`) restores a snapped window instead of
   minimizing it. `WindowGeometryRecorder` reads `IsSnapped` instead of guessing a
   half screen from its coordinates.
4. **Dragging the title bar of a maximized or snapped window restores it.** After
   4 px of travel (a click or a double click does not restore it), the script calls
   `WindowChrome.ReportDragRestoreAsync`: the window gets its earlier geometry back
   and is placed so that the pointer keeps its relative place on the title bar, then
   the gesture goes on as a normal move (ADR 0163). `WindowHost` exposes
   `data-window-snapped="true"`.
5. **Double click on the title bar** (outside the buttons): toggles maximized ↔
   normal, through the same path as the button
   (`WindowChrome.ReportTitleDoubleClickAsync`). A snapped window is maximized.
   Nothing for a non-resizable window (except restoring it when maximized).

## Consequences

- Public API added (in `PublicAPI.Unshipped.txt`):
  `HackerOs.Windowing.Abstractions.SnapWindowCommand`,
  `WindowRuntimeState.IsSnapped`, `WindowChrome.ReportDragRestoreAsync` and
  `WindowChrome.ReportTitleDoubleClickAsync`. Additions only, nothing changes for
  apps.
- The window menu keeps "Maximize" for a snapped window.
- E2E test `WindowTitleBarTests` (buttons clicked at their top edge, double click,
  drag from maximized and from snapped) and `WindowRuntime` tests.
