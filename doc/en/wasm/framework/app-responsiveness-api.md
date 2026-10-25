# App priority and responsiveness

This document describes what an app sees and must do to stay responsive: the `nice` priority and the "Not responding" window. The implementation (detection, worker queue, architecture, tests) is in [`../dev/process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md).

## `nice` priority

The niceness (`nice`) of a process ranges from `-20` to `19`, is `0` by default and is inherited by its children. It only orders the workers' queue: it never slows down a process that is running.

### API

- `HackProcess.Nice` (`int`, read-only): niceness of the process, read from `/proc/<pid>/stat` (field 19).
- `HackProcess.BasePriority` (`int`, read-only): priority of the process, `20 + nice` (field 18 of `/proc/<pid>/stat`).
- `HackProcess.ParseStatNice(string)`: extracts the niceness from a `/proc/<pid>/stat` line.
- `IAppProcessGateway.GetNice` / `SetNice` (`Simulation.Abstractions`, through `IAppExecutionContext.Processes`): read or change the niceness of a process. Raising the value is done on one's own process; lowering it, or changing another user's process, requires root. Changing a process that is neither one's own nor one of its children requires `process.manage` (ADR 0140).
- `HackBackgroundOptions.Nice`: niceness of a task launched by `HackBackground.RunAsync`, `0` by default. A negative value is brought back to `0` for a user who is not an administrator.

### Rules

| Rule | Detail |
| --- | --- |
| Range | `-20`..`19`, any value out of range is clamped to the bounds. |
| Inheritance | A child takes its parent's value, unless the launch request provides one (`ProcessStartRequest.Nice`, `AppLaunchRequest.Nice`). |
| Lowering | Only root (`sudo`, ADR 0042) lowers a value (`EACCES` otherwise: "Permission denied"). |
| Other user | Only root changes the value of another user's process (`EPERM`: "Operation not permitted"). |
| Capabilities | Reading another process requires `process.list`; changing a process that is neither one's own nor one of its children requires `process.manage`, like `kill` (ADR 0140). |
| Effect | Order of the workers' queue (ADR 0071), commands and `HackBackground.RunAsync` background tasks (ADR 0128, value `HackBackgroundOptions.Nice`, 0 by default): lowest value first, then arrival order. |
| Always-ready worker | A command with a **positive** value does not take the always-ready worker while another worker is starting: it stays for the next interactive command. |
| Main thread | **No effect.** A main thread process (windows, `MainThread` commands) runs when it is called; there is no scheduler to weight. |

## Window that is not responding

### What the user sees

- The displayed title gets the suffix "(Ne répond pas)" / "(Not responding)",
  in the session's language. This is **display only**: the title set by the
  app (`SetWindowTitleAsync`) does not change and no
  `WindowTitleChangedEvent` is emitted.
- The content turns gray (`filter: grayscale(1) brightness(0.7)`), with a fade
  except under "reduce animations" (ADR 0105).
- The dialog is attached to **that window only**: it covers its
  content, not its title bar; the other windows and the taskbar
  remain usable. `role="alertdialog"`, focus goes to **Wait**,
  `Tab` stays between the two buttons, `Esc` is the same as **Wait**.
- **Wait** closes the dialog; it comes back one threshold later (5 s) if the
  window still does not respond.
- **End the application** follows the escalation of ADR 0069: reason
  `Terminated`, grace period of **2 s**, then forced stop (step 3) and, for
  a worker, termination of the worker (step 4). The window closes when its
  process ends.
- The close button (and `Alt+F4`) of an unresponsive window opens the same dialog instead
  of a close that the frozen app could not handle.
### What the dialog cannot do

A loop that **never yields** on the main thread freezes the whole
OS: neither the dialog nor anything else can be displayed, and it is the browser
that ends up offering its own "Page unresponsive" dialog. Long work
must go to a worker (`HackBackground.RunAsync`, ADR 0128, upcoming) or
yield regularly with `HackProcess.CheckpointAsync` (ADR 0069);
see [`cpu-accounting.md`](../dev/cpu-accounting.md) and
[`sdk-developer-guide.md`](sdk-developer-guide.md).
