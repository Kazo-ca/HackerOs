# ADR 0147: 5 s grace period for background tasks when their app closes

## Status

Accepted on 2026-10-19, on Benoit's answer.

- **Thread:** "Exec 5: background tasks". These are the two cards left
  open by ADR 0140.
- **Replaces:** ADR 0140 §6 ("with no grace period"). The rest of ADR 0140
  does not change.

## Context

ADR 0140 §6 stopped an app's tasks at the very moment the app
ended. The task's process exited immediately with the reason
`DependencyStop`, and its token was canceled.

A task that writes a file, an archive for example, therefore had no guaranteed
moment to finish cleanly or to delete what it left half
written.

Two questions were put to Benoit:

1. **Grace period when the app closes.**
   - Choice A: immediate stop, as so far.
   - Choice B: 5 s, like a `SIGTERM` followed by a `SIGKILL`.
   - **Answer: B.**
2. **Task PID in `HackBackgroundProgress`.**
   - Choice A: no, the task puts it in its message, as in the sample
     window.
   - Choice B: a field added to the API and the protocol.
   - **Answer: A.**

## Decision

### 1. Parent death signal

`ProcessStartRequest` gains the `init` property `ParentExitGracePeriod`
(`TimeSpan?`, in `HackerOs.Simulation.Abstractions`).

The model is `prctl(PR_SET_PDEATHSIG, SIGTERM)` under Linux, followed by a
`SIGKILL`. The behavior depends on how the parent ends.

**The parent ends without `SIGKILL`.** This is the case of a closed window,
a logout or a disabled app (`Kill` with a reason that is not
a signal). The child process does not disappear with it. It receives
`StopAsync(grace, DependencyStop)`, which means:

- it moves to the `Stopping` state;
- `StopRequested` is raised;
- its token is canceled;
- at the end of the delay, it is killed with the reason `Timeout`, which counts as a
  `SIGKILL` (ADR 0069).

**The parent is killed by a `SIGKILL`.** This is the case of `kill -9`, a limit
or the OOM killer. The child is then killed immediately, as before.

**What the child itself launched.** It remains in charge of it during the delay. What
is still running when it ends is killed.

**A `null` or zero value** keeps the old behavior: the child ends
with its parent.

Only `HackBackground` tasks use this delay. Processes launched
by the shell and other children do not change.

### 2. The tasks

`BackgroundJobService.ParentExitGracePeriod` is **5 s** and is used at the start
of each task. The delay is counted on the simulation clock, like that
of ADR 0069.

A task that respects its token (`HackBackgroundContext.CancellationToken`)
stops during the delay and exits with code `130`. A task that ignores it is
killed after 5 s; its exit reason is then `Timeout`.

Operation is the same on the main thread and in a worker: the
process stop request is forwarded to the worker (ADR 0069, ADR 0070).

### 3. The PID in progress

Nothing changes. `HackBackgroundProgress` does not carry the PID. A window that
wants a "Kill" button gets the task's PID through the progress
message, or through `ListProcesses` on the main thread (ADR 0140 §7).

## Consequences

- `ProcessStartRequest.ParentExitGracePeriod` and
  `BackgroundJobService.ParentExitGracePeriod` are added to
  `PublicAPI.Unshipped.txt`. They will be published with the next minor version, after
  1.2.0. The app SDK (`HackerOs.AppSdk`) does not change.
- During the delay, `ps` and the task manager show the task in the
  `Stopping` state. Its parent no longer exists.
- Documentation: [`background-jobs.md`](../../framework/background-jobs.md).
- Tests:
  - `InMemoryProcessManagerTests`: stop request then kill, and `SIGKILL` of the
    parent;
  - `BackgroundJobTests`, in both modes: a task that respects the
    request exits with `130`, a task that ignores it is killed at 5 s.
