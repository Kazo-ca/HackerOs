# ADR 0122: a window remembers its size, not its position

## Status

Accepted on 2026-10-17 (v3.1 series, fix to `v3.1/2-window-geometry`),
on Benoit's answer to question 1 of the thread "V3.1 1": "1.c", that is
"size only, as under GNOME".

- **Replaces:** [ADR 0116](0116-window-geometry-restore.md) §1 ("size,
  position and maximized state"), the `x` and `y` keys of §2, and the position
  correction and the second-instance offset of §4 (points 3 and 4).
- **Keeps:** the rest of ADR 0116: file
  `~/.local/state/hackeros/windows/<appId>.config`, maximized state, write
  debouncing (§3), size correction (§4 point 2),
  `WindowOptions.RememberGeometry`, what is not covered (§5).
- **Document:** [`window-geometry.md`](../window-geometry.md).

## Context

ADR 0116 recommended remembering everything, like Windows and macOS, and the work item
`v3.1/2-window-geometry` (PR #170) delivered it that way. Benoit chose the other
model: under GNOME, the window manager does not place a window back, and
a GTK app only remembers its size and maximized state (gedit, Nautilus). This
model is more faithful to the Linux desktop that HackerOS imitates.

## Decision

- An app window remembers its **normal size** and its **maximized state**.
  It always opens at its cascade position (`InitialBounds`), with the size
  read, corrected as before (sizes declared today, work area,
  never below the minimum).
- The file now contains only `width`, `height` and `maximized`:

  ```ini
  # Window size of org.hackeros.text-editor (ADR 0116, ADR 0122)
  width=900
  height=640
  maximized=false
  ```

- A file written before this ADR (with `x=` and `y=`) remains readable: its size
  is taken, its position ignored, and the next write rewrites it without
  those keys.
- A move alone no longer triggers a write; only a size change,
  maximize, restore and close do.
- A second window of the same app takes the next cascade position, like
  any new window, with the same size read.

## Consequences

- `SavedWindowGeometry` carries `Width`, `Height` and `IsMaximized` (no more
  `Bounds`); `WindowLaunchCoordinator.RestoredBounds` takes the size read and
  the cascade index; `MinimumVisibleTitleBarWidth` disappears. These types are
  in `HackerOs.Platform.Blazor`, outside the SDK promise (ADR 0114 §2), and
  were not yet published (`PublicAPI.Unshipped.txt`).
- `WindowOptions.RememberGeometry` (SDK 1.1) does not change: when `false`, the
  window always opens at its declared size.
- Unit and E2E tests adapted: the window reopens with the same size, at its
  cascade position.

## Rejected options

- **Size, position and maximized state** (ADR 0116 §1): what Windows and
  macOS do, rejected by Benoit in favor of the GNOME model.
- **Also remember the position without reading it back**: a file that keeps
  data that is never used is confusing.
