> [Version française](../../../../fr/wasm/dev/adr/0163-window-pointer-preview-and-invariant-coordinates.md)

# ADR 0163: move preview and invariant coordinates

## Status

Accepted on 2026-10-22. Amends the restriction of [ADR 0009](0009-window-runtime-strategy.md)
on DOM projection during a move gesture; the window state stays in C#.

## Context

The chrome queued an interop call and a desktop render for each
`pointermove`. The pointer could thus get far ahead of the window.
With a French culture, the `data-window-*` attributes rendered decimal numbers
with a comma. On the next gesture, `Number("48,25")` produced `NaN`,
serialized as `null`; the C# `double` parameter could no longer be deserialized.

## Decision

- The four numeric attributes of the frame are rendered in invariant culture.
  The module refuses non-finite values before the interop; C# also validates its inputs.
- The chrome gives focus at the start of the gesture. During a **move**, the
  browser projects only a temporary CSS translation, at most once per
  `requestAnimationFrame`. It modifies neither the geometry attributes, nor the
  runtime, nor the state files. On release, it transmits a single cumulative delta
  to C#, which applies `MoveWindowCommand` and the work area constraints.
  The preview may exceed this area; the validated position is brought back into it on release.
- This projection is a targeted exception to the rule of ADR 0009 forbidding
  any DOM mutation by the pointer module. Identity, dimensions, constraints,
  focus, order and validated position remain the property of the C# runtime.
- **Resizing** keeps its constraints in C# live. The module keeps
  at most one sample in flight and the most recent one pending, synchronized with the
  browser's frames. The last position precedes the single resize-end
  notification (ADR 0095).
- `pointerup`, `pointercancel` and loss of capture end the gesture exactly once.
  Cancellation and loss of capture keep the last valid delta. Removing the
  component releases the listeners, the capture and the preview without a callback to C#.
- Positions are still not saved (ADR 0122). No server required.

## Verification

`WindowPointerGeometryTests` covers decimals under `fr-FR`, `fr-CA`, `en-US`
and non-finite values. `WindowPointerGestureTests` exercises the real module
in Chromium: successive moves, validation on release, lost
capture, removal during a gesture and slow interop during a resize.
The OS scenario `WindowGeometryRestoreTests` redoes two successive moves
before checking the restored size.

## Tasks

- [x] Fix the numeric boundary and remove the dynamic style from the Razor markup.
- [x] Project the move locally and validate its position on release.
- [x] Bound the resize updates and release finished gestures.
- [x] Add the regression tests and update the windowing documents.
