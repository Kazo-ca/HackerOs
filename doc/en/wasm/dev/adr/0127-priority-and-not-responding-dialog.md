> [Version française](../../../../fr/wasm/dev/adr/0127-priority-and-not-responding-dialog.md)

# ADR 0127: `nice` priority without a quantum, and "The application is not responding: Wait | End the application" dialog

## Status

Accepted on 2026-10-18.

- **Plan:** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  decisions 5 and 6 (answers Q3 and Q4); implemented by
  `exec/4-not-responding-ui`.
- **Complements:** ADR 0067 §4 ("the OS does nothing more": it now
  displays the dialog described here), ADR 0069 §3 (escalation), ADR 0071 (pool
  queue).

## Context

- Blazor WebAssembly has only one .NET thread per runtime: nothing can preempt
  a slice. Benoit rejected an imposed quantum (Q4): it would be misleading, since
  it would only affect code that calls `CheckpointAsync`.
- ADR 0067 marks a process "Not responding" when a slice exceeds
  `UnresponsiveThresholdMs` (5 s): during the block in a worker, after
  the fact on the main thread. The OS offers the user nothing.
- Benoit wants the browsers' dialog: "The application is not responding:
  Wait | End the application" (Q3), in place of an OOM killer active
  by default.

## Decision

### 1. `nice` and `renice`

- `nice` value from `-20` to `19`, `0` by default, inherited by children.
  Commands `nice [-n N] command…` and `renice -n N -p pid…`; lowering the
  value (higher priority) requires an administrator, like Linux.
- Real effect, and the only one: the **order of the pool queue** (ADR 0071) and of
  the tasks of ADR 0128 (the smallest value goes first, then arrival
  order) and the choice of worker when several are free (a process with a positive
  `nice` avoids the always-ready worker as long as another is free). On
  the main thread, `nice` has no effect; the documentation says so.
- Display: `NI` column of `ps -l`, `ps -o ni` and `top`/`htop`, field 19 of
  `/proc/<pid>/stat`, "Priority" column (hidden by default) of the task
  manager.

### 2. When a window is "not responding"

The window manager marks a process's window as **not responding**
in one of these cases:

1. the process is "Not responding" in the sense of ADR 0067 §4 (slice of
   more than 5 s; seen during the block when the window's logic is in
   a worker, ADR 0129);
2. an input delivered to the window (click, key, close request) has been waiting
   for more than 5 s while **this process's slices** occupy the main
   thread (loop cut into short slices that never yields
   to its own window's events);
3. on the main thread, a slice of more than 5 s **has just finished** and
   the user tried to act on the window during the block: the dialog
   is displayed at the end of the slice, like Windows after a freeze.

The state ends when the window processes its inputs again in less than the threshold.

### 3. What the user sees

- The displayed title receives the suffix "(Not responding)" / "(Ne répond pas)"
  depending on the session language (ADR 0104). This is a window manager
  display: the title set by the app (`SetWindowTitleAsync`, ADR 0117) does not
  change and `WindowTitleChangedEvent` is not raised.
- The window content is greyed out (without animation if reduced motion is
  requested, ADR 0105).
- A dialog attached to that window only (the rest of the desktop remains
  usable): ""Calculator" is not responding. You can wait for it to
  respond or end the application." with **Wait** (default button,
  `Esc`) and **End the application**. `role="alertdialog"`, focus trapped,
  tested by the accessibility suite.
- Clicking the close button of a non-responding window opens the same dialog.
- **Wait** closes the dialog; if the window is still not responding 5 s
  later, it comes back.
- **End the application** follows the escalation of ADR 0069 with the reason
  `Terminated` and a grace period of **2 s** (the process is already not
  responding), then step 3, and step 4 in a worker. Detached
  windows (ADR 0078) display the dialog in their own surface.
- The dialog, the suffix and the greying are platform components in
  native Blazor with isolated CSS (window chrome, AGENTS.md II.1).

### 4. What the OS cannot do, and says so

A loop on the main thread that never yields freezes the whole OS:
no HackerOS dialog can be displayed, it is the real browser's dialog
that appears. [`cpu-accounting.md`](../cpu-accounting.md) and the SDK guide
say so and point to `HackBackground.RunAsync` (ADR 0128) and
`CheckpointAsync` (ADR 0069).

### 5. Settings

`UnresponsiveThresholdMs` (ADR 0066) serves the three cases of §2. The dialog is
turned off in `/etc/hackeros/window-manager.config` (`[unresponsive]
dialog=true`); when turned off, the suffix and the greying remain.

## Consequences

- No app to modify. `ps`, `top`, `htop` and the task manager
  gain `NI`; `nice` and `renice` join the commands.
- Tests: unit (detection of the three cases with a simulated clock), UI (dialog,
  Wait, End, close button), accessibility (axe, keyboard), `nice` parity.
- Docs: [`cpu-accounting.md`](../cpu-accounting.md),
  [`window-model.md`](../../framework/window-model.md), [`task-manager.md`](../task-manager.md),
  [`linux-commands.md`](../linux-commands.md).

## References

- ADR 0066, 0067, 0069, 0071, 0078, 0104, 0105, 0117, 0128, 0129.
- `nice(1)`, `renice(1)`, `proc(5)` (`stat`, field 19).
