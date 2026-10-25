# ADR 0069: requested stop, voluntary checkpoints and escalation

## Status

Accepted on 2026-10-16.

- **Plan phase:** phase 5: app execution
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)).
- **Completes:** ADR 0012 (process lifecycle), ADR 0056 §4
  (`HackProcess`). Does not contradict ADR 0058 §2: "End task" keeps its
  confirmation, only its stop mode changes (section 4).
- **Used by:** ADR 0070 (processes in Web Workers).

## Context

The OS already knows how to politely stop a process: `StopAsync` moves it to
`Stopping`, cancels `IAppExecutionContext.CancellationToken`, waits for a grace
period, then forces it (`InMemoryProcessManager.StopAsync`). This is `SIGTERM` then
`SIGKILL`. `KillAsync` is `kill -9`.

Three things are missing:

1. The code does not know **why** or **in how long** it will be forced:
   it only sees a canceled token.
2. A long-running computation has no simple way to **yield control**. In a Web
   Worker, this is serious: a worker has only one thread, and the message "stop
   pid 42" is never processed as long as a process in the worker loops without
   yielding. The token therefore cannot be canceled.
3. "End task" in the task manager currently does a `kill -9`
   (`EndTaskRunner` calls `HackProcess.KillAsync`): the app has no chance
   to terminate cleanly.

Benoit proposed that a token warn the process before it is killed, so
that it stops its tasks and avoids bringing down its worker along with its neighbors.

## Decision

### 1. Requested stop: reason and deadline

`IAppExecutionContext` receives:

```csharp
/// Levé une fois, quand l'OS demande l'arrêt (SIGTERM), avant l'annulation du jeton.
event EventHandler<HackStopRequestedEventArgs>? StopRequested;
```

`HackStopRequestedEventArgs` carries `Reason` (`ProcessExitReason`:
`CloseRequested`, `Terminated`, `Logout`, `Shutdown`) and `Deadline`
(the instant when the OS will force, on the simulated clock). The SDK also exposes it
through `HackProcess.Current.StopRequested`, modeled on
`IHostApplicationLifetime.ApplicationStopping`.

Order: move to `Stopping`, raise `StopRequested`, cancel
`CancellationToken`, wait until `Deadline`. The new value
`ProcessExitReason.Terminated` designates a `SIGTERM` sent by another
process (`kill`, "End task").

### 2. Voluntary checkpoint

```csharp
for (...)
{
    await HackProcess.CheckpointAsync(cancellationToken);
}
```

- If a stop is requested, throws `OperationCanceledException`.
- If the current slice exceeds 8 ms (the budget of a frame at 60 fps), yields
  control through `await Task.Yield()`, which lets UI events
  and OS messages through; otherwise, returns immediately, with no
  allocation.
- Nothing requires calling it: it is not an imposed quantum, it is a
  good-citizen gesture. Long built-in commands (`find`, `grep -r`, `zip`, `du`)
  call it in their loops.
- It knows the slice duration from ADR 0067. If CPU measurement is
  disabled (ADR 0066), it keeps its own slice timestamp.

### 3. Escalation

When the OS stops a process:

1. **`SIGTERM`**: `StopRequested`, token canceled, grace period (5 s for
   `kill` and "End task", the `StopAsync` value otherwise).
2. **The process terminates**: end, with no effect on the others.
3. **Deadline passed, but the thread running it still responds**: the process
   ignores its token without blocking. The OS moves it to `Exited (Killed)`, frees its
   resources, closes its windows and **drops its continuations**: the process's
   scheduler (a `TaskScheduler` installed in its context) refuses any
   new task, and the OS gateways throw
   `HackProcessKilledException` on any call from that pid. This is a local
   `SIGKILL`, with no collateral damage.
4. **The thread no longer responds**: on the main thread, nothing is possible
   (the OS is frozen with it, see ADR 0067 §4). In a Web Worker, the OS terminates
   the worker (ADR 0070).

### 4. Command vocabulary

| Action | Signal | Behavior |
|---|---|---|
| `kill <pid>`, `kill -15`, `kill -TERM` | `SIGTERM` | sections 1 and 3 |
| `kill -9`, `kill -KILL` | `SIGKILL` | immediate, cannot be intercepted, no `StopRequested` |
| Task manager "End task" | `SIGTERM` | **changes**: `EndTaskRunner` goes from `KillAsync` to `TerminateAsync` |
| Task manager "Force kill" (new, context menu) | `SIGKILL` | `KillAsync` |
| window close | `SIGTERM`, reason `CloseRequested` | `CloseMainWindowAsync`, unchanged |

`HackProcess.TerminateAsync(TimeSpan? timeout)` is added: it is the
`SIGTERM` of another process, under the `process.manage` capability (ADR 0056).

## Consequences

- An app that listens to `StopRequested` or its token terminates cleanly in
  all cases.
- The shell (`kill` without option) keeps its behavior; "End task" becomes
  polite.
- The work item adds `TerminateAsync`, `CheckpointAsync`, `StopRequested`,
  `ProcessExitReason.Terminated` and `HackProcessKilledException` to the SDK, to
  be documented in `app-sdk.md` and `sdk-reference.md`.

## References

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- `Platform/HackerOs.Platform.Core/Processes/InMemoryProcessManager.cs`,
  `Apps/System/HackerOs.Apps.SystemMonitor/Processes/EndTaskRunner.cs`,
  `Apps/Commands/HackerOs.Commands/Kill/KillCommand.cs`.
- `signal(7)`: `SIGTERM`, `SIGKILL`.
- ADR 0012, 0056, 0058, 0066, 0067, 0070.
