> [Version française](../../../fr/wasm/dev/process-priority-and-unresponsive-windows.md)

# `nice` priority and windows that do not respond

This document covers the implementation of the `nice` priority and of the detection of windows that do not respond. What an app sees and must do (`HackProcess.Nice` API, priority rules, the "Not responding" box, limits of a loop on the main thread) is in [`../framework/app-responsiveness-api.md`](../framework/app-responsiveness-api.md).

Implementation of [ADR 0127](adr/0127-priority-and-not-responding-dialog.md),
work item `exec/4-not-responding-ui` of the "follow-ups of step 5" series
([`execution-followups-plan.md`](plans/execution-followups-plan.md)).

## Purpose

Two behaviors of a real system, simulated without a time quantum:

1. **The `nice` priority**: each process has a niceness from `-20` to `19`
   (0 by default), inherited by its children, which you choose with `nice` and
   change with `renice`. It **only orders the worker queue**;
   it never slows down a running process.
2. **The window that does not respond**: when an app stops responding, its window
   takes the suffix "(Not responding)", its content turns gray and a box
   "\"Calculator\" is not responding. You can wait for it to respond or
   end the application." offers **Wait** or **End application**,
   like Windows ("Wait / Close the program") or GNOME ("Force Quit /
   Wait").

## 1. `nice` priority

### What the user sees

```sh
$ nice                      # la gentillesse du shell
0
$ nice -n 10 find / -name '*.log'   # lance find à 10
$ nice -n -5 make           # sans sudo : refusé, lancé à la valeur courante
nice: cannot set niceness: Permission denied
$ sudo nice -n -5 make      # ADR 0136 : make tourne en root à -5
$ renice -n 5 -p 42
42 (process ID) old priority 0, new priority 5
$ ps -l
F S UID   PID  PPID  C PRI  NI     TIME CMD
0 R alice   1     0  0  80   0 00:00:00 org.hackeros.terminal
$ ps -o pid,ni,comm
```

- `ps -l` and `ps -o … ni …` display `NI` and `PRI` (`80 + nice`, like
  procps); `top` and `htop` display `PR` (`20 + nice`) and `NI`.
- `/proc/<pid>/stat` carries the priority (`20 + nice`) in field 18 and the
  niceness in field 19, as in `proc(5)`.
- The task manager has a **Priority** column, hidden by default, that
  the "Priority" button of the Processes tab toolbar displays.

The priority rules (range, inheritance, permissions, effect on the worker queue, main thread) are in [`../framework/app-responsiveness-api.md`](../framework/app-responsiveness-api.md).

### Architecture

| Element | Project | Role |
| --- | --- | --- |
| `ProcessNice` | `Shared/HackerOs.Simulation.Abstractions` | Bounds, `Clamp`, `ToPriority` (`20 + nice`). |
| `IProcessManager.GetNice/SetNice`, `ProcessStartRequest.Nice` | same | Default members (0 / not supported) for process tables that do not have them. |
| `InMemoryProcessManager` | `Platform/HackerOs.Platform.Core` | Keeps the value in the process entry, inherits it from the parent. |
| `IAppProcessGateway.GetNice/SetNice`, `AppProcessGateway` | Abstractions / Core | `setpriority(2)` checks (root, owner, capabilities). |
| `IShellSession.RunNicedAsync`, `ShellInterpreter.RunNicedAsync` | AppSdk / Core | Runs a command at the requested niceness (child of the `nice` process). |
| `nice`, `renice` | `Apps/Commands/HackerOs.Commands` | Commands (main thread: they need the shell or the process table). |
| `ps`, `top`, `htop` | same | `NI` and `PRI`/`PR` columns. |
| `ProcFileSystemProvider` | Core | Fields 18 and 19 of `/proc/<pid>/stat`. |
| `HackProcess.Nice`, `HackProcess.BasePriority`, `HackProcess.ParseStatNice` | `Shared/HackerOs.AppSdk` | Read from `/proc/<pid>/stat`. |
| `WorkerRunQueue` | `Platform/HackerOs.Platform.WorkerHost` | Queue of commands waiting for a worker, ordered by niceness then arrival. |
| `ProcessTable.ShowPriority`, `ProcessColumn.Priority` | `Apps/System/HackerOs.Apps.SystemMonitor` | Priority column hidden by default. |

## 2. Window that does not respond

### When a window is judged unresponsive

All app windows share the browser's main thread with the
window manager. While an app holds it, **nothing else
runs**, not even the detection: the verdict comes as soon as the thread is
released, based on what the CPU accountant measured (ADR 0067) and on the
wait time of the user's inputs. A window is unresponsive in one
of the three cases of ADR 0127 §2 (threshold `UnresponsiveThresholdMs`, 5 s):

1. its process is "Not responding" for the accountant (a slice longer
   than the threshold) **and** has consumed CPU since the last
   sample (a 5 s slice that has just ended, or a blocked worker process
   whose heartbeat keeps counting);
2. an input (click, key) delivered to the window waited longer than the threshold
   while that window's process held the thread for at least half of
   that wait;
3. a slice longer than the threshold has just ended and the user acted
   on the window during that slice: the box is displayed at the end of the
   slice.

The state ends when the window processes an input again under the threshold,
when the process finishes a short slice, or when it has been **idle**
for a whole threshold (an idle process responds immediately). An app that froze
the OS for 6 s and then recovered therefore shows the box for a few seconds, then
it disappears by itself, like the ghost window of Windows when the app
starts processing its messages again.

Without CPU measurement (`Cpu:Enabled = false`), no wait can be
attributed to a process: no window is marked.

### What the user sees (window)

The "(Not responding)" suffix, the grayed content, the "Wait | End application" box and the close button are described in [`../framework/app-responsiveness-api.md`](../framework/app-responsiveness-api.md).

### Setting

```ini
# /etc/hackeros/window-manager.config (et ~/.config/hackeros/window-manager.config)
[unresponsive]
dialog = true
```

`dialog = false` turns off the automatic opening of the box: the suffix and the
gray remain, and the close button still opens the box. The system file is created
at first startup with the value commented out; an administrator can lock the
key with `[locked]` (ADR 0044). The threshold remains that of the CPU measurement
(`HackerOs:ResourceAccounting:Cpu:UnresponsiveThresholdMs`).

### Architecture

| Element | Project | Role |
| --- | --- | --- |
| `WindowRuntimeState.IsResponding`, `SetWindowRespondingCommand`, `WindowRespondingChangedEvent` | `Platform/HackerOs.Windowing.Abstractions` / `Core` | Display state of the window, like a window manager's "hung" flag. |
| `WindowChrome` | `Platform/HackerOs.Windowing.Blazor` | Localized suffix (`Chrome.NotResponding`). |
| `WindowHost.Overlay`, `DesktopArea.WindowOverlay`, `SingleSurfaceArea.WindowOverlay` | same | Layer placed over the content of **one** window; class `is-not-responding` (gray), `aria-busy`. |
| `WindowResponsivenessMonitor` | `Platform/HackerOs.Platform.Blazor/Windows` | The three cases, the end of the state, Wait, End, the close button; without a timer or JavaScript, tested with a fake clock. |
| `WindowResponsivenessHost` (+ `.razor.js`) | same | Samples the monitor every second and passes it the input wait (`performance.now() - event.timeStamp`), measured in the capture phase on `.window-content`. |
| `UnresponsiveWindowDialog` (+ `.razor.css`, `.razor.js` for the focus trap), `UnresponsiveWindowOverlay` | same | The box and its wiring into each window's layer. |
| `WindowManagerSettingsDocuments` | `Platform/HackerOs.Platform.Core/Shell` | `window-manager` document (`[unresponsive] dialog`). |
| `DesktopShell`, `MobileShell` | `Platform/HackerOs.Platform.Blazor/Shell` | Place the layer and the host; the close button goes through `InterceptClose`. |

The detached windows of ADR 0078 currently host only browser tabs
(iframes), not app windows: the box lives in the window
component, and a future detached surface that hosted an app
will render it in its own surface with the same layer.

## Implementation decisions

- **No quantum**: niceness orders a queue, nothing more
  (ADR 0127 §1). The main thread has no scheduler to weight.
- **`nice` on a command that drives its shell** (`nice watch …`,
  `nice nice`): the command receives the shell as if it were launched
  directly (ADR 0051 §3). Special built-ins (`cd`, `export`…) remain
  not found by `nice`, as with GNU.
- **`sudo nice -n -5 …`** (ADR 0136, Benoit's answer): `sudo` accepts
  `nice`, the only exception to commands that drive the shell. `nice` runs
  under the user and passes `sudo`'s root ticket to its command, which
  runs as root and can receive a lower value; the ticket remains
  single-use. `sudo renice` still works for an already launched process.
- **`renice` on the main thread**: a worker does not yet have a gateway
  to the process table's priorities (ADR 0072).
- **Attribution by measured CPU**: a wait is charged to a window
  only if its own process consumed at least half of that wait;
  a service or another app that freezes the thread does not get the window
  that received the click blamed.
- **Sticky flag of the accountant**: the "Not responding" of ADR 0067 stays
  raised until the next short slice. The monitor follows it only if it is
  accompanied by consumed CPU, and remembers the number of slices already
  "acknowledged" by a fast input, so that an old flag does not flag
  a window that is responding.
- **The window closes through its process**: End does not force the
  window to close; it is the end of the process that closes it, as for
  the task manager's "End task".

## Tests

| Project | Coverage |
| --- | --- |
| `HackerOs.Platform.Core.Tests` (`InMemoryProcessManagerTests.Nice`, `ProcFileSystemProviderTests`) | Value 0, inheritance, explicit value, bounds, finished process, gateway root/owner/capability rules, fields 18 and 19 of `stat`. |
| `HackerOs.Platform.WorkerHost.Tests` (`WorkerRunQueueTests`, `CommandParityTests`) | Queue order (niceness then arrival, bounds), always-ready worker, parity of `nice`, `renice` and `ps -o` in a worker. |
| `HackerOs.Commands.Wave5.Tests` (`PsCommandTests`) | `ps -l` (`PRI` 85 for `nice` 5), `ps -o pid,ni,comm`, unknown field. |
| `HackerOs.AppSdk.Tests` (`HackProcessNiceTests`) | Reading of field 19, name with parentheses, short or invalid line, bounds. |
| `HackerOs.Apps.SystemMonitor.Tests` (`ProcessTableTests`) | Priority column hidden by default, sorting, return to CPU sorting when it is hidden. |
| `HackerOs.Windowing.Core.Tests` (`WindowRespondingTests`) | Flag, single event, title unchanged, persistence until cleared. |
| `HackerOs.Platform.Blazor.Tests` (`WindowResponsivenessMonitorTests`) | The three cases with a fake clock, old flag ignored, wait charged to another process, end by fast input, short slice or inactivity, Wait then return, close button, End (2 s, `Terminated`), without measurement, window without a process. |
| `HackerOs.E2E.Tests` (`IndexedDbBrowserContractTests.Unresponsive`, scenario `?scenario=unresponsive` of the browser harness) | In Chromium: suffix, gray, box, focus on Wait, `Tab` trapped, axe with no serious violation, `Escape`, return of the box, close button, End; real freeze of the main thread with a pending click (case 3) then end of the state. |

## Task list

- [x] Niceness in the process table: range, inheritance, `GetNice`/`SetNice`.
- [x] Process gateway: root, owner and capability checks.
- [x] `nice` and `renice` (en/fr), `IShellSession.RunNicedAsync`.
- [x] `ps -l`, `ps -o`, `top`, `htop`: `NI` and `PRI`/`PR`.
- [x] `/proc/<pid>/stat` fields 18 and 19; `HackProcess.Nice`/`BasePriority`.
- [x] Worker queue ordered by niceness (`WorkerRunQueue`), always-ready worker left to interactive commands.
- [x] Priority column hidden by default in the task manager.
- [x] Window `IsResponding` state, command and event.
- [x] Localized suffix, grayed content (without fade under reduced animations).
- [x] Monitor of the three cases, input probe, Wait | End box, close button.
- [x] `[unresponsive] dialog` setting of `window-manager.config`.
- [x] Unit, parity, browser and accessibility tests.
- [x] Documentation (`cpu-accounting.md`, `window-model.md`, `task-manager.md`, `linux-commands.md`, `worker-processes.md`, `virtual-proc-and-dev.md`, `settings-files.md`, SDK guide and reference) and index.
- [ ] Background tasks of ADR 0128 ordered by the same queue (work item `exec/5`).
