# ADR 0070: headless processes in Web Workers

## Status

Accepted on 2026-10-16.

- **Plan phase:** phase 5: app execution
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)). Last
  work item of the phase: it depends on ADR 0067 to 0069.
- **Lifts:** the exclusion "execution isolation (watchdog, Web Workers)" of
  section 9 of [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md).

## Context

An infinite synchronous loop on the main thread freezes the whole OS, and nothing on
that thread can interrupt it (ADR 0067 §4). The only way to isolate a
process is to run it **elsewhere**: a Web Worker, which can be forcibly stopped
by `worker.terminate()`.

A Web Worker runs its own .NET runtime:

- about 15 to 40 MB and 0.5 to 2 s of cold start, even with files
  cached;
- no DOM access: windowed apps stay on the main thread;
- everything the OS owns (files, `/proc`, clipboard, network) goes through
  messages.

One worker per process would make the shell unusable (`ls | grep | wc`
would start three runtimes). A pool alone would bring down the neighbors on each
forced kill. Benoit validated a hybrid model, made safe by the escalation of
ADR 0069.

## Decision

### 1. Who runs where

| Kind of process | Execution host |
|---|---|
| Windowed app, embedded component | main thread (unchanged) |
| Command, pipeline, shell script | **pool** worker |
| Service, declared long-running task | **dedicated** worker |

The manifest declares the desired host:
`[AppManifest(Execution = AppExecutionHost.Pool | Dedicated | MainThread)]`.
Default: `MainThread` for a windowed app, `Pool` for a command,
`Dedicated` for a service (`ServiceAppBase`). An app can keep
`MainThread` if it needs a service that the worker does not offer.

### 2. The pool: one worker per virtual core

- The pool has `CpuCapacity` workers (`VirtualHardwareProfile`, rounded up,
  at least 1, at most `navigator.hardwareConcurrency - 1`). These are the
  **cores** of the simulated machine: `/proc/cpuinfo` and `/proc/stat` list them,
  and ADR 0067 counts their time.
- They start at boot, in the background, after session opening.
- The OS scheduler places each new process on the least loaded worker.
  The stages of a single pipeline go on the **same** worker, so that
  their pipes stay in memory.

### 3. Dedicated workers

- A spare worker, already started, is kept ready: a dedicated process starts
  without waiting, and the OS starts a new spare behind it.
- A dedicated process that terminates frees its worker.

### 4. The OS seen from a worker

- The worker loads the SDK and app assemblies, and a **process
  host** (`HackerOs.Platform.WorkerHost`) that implements
  `IAppExecutionContext` through **remote gateways**: each SDK call
  becomes a message to the OS on the main thread, which applies the
  same capability checks as today. The SDK does not change: a
  command does not know where it runs.
- The standard streams (stdin, stdout, stderr) are message-based pipes.
- The JavaScript (worker creation, `postMessage`, `terminate`) is a module
  in the `wwwroot/` of `HackerOs.Infrastructure.Browser`, imported by the C#
  code. No host references it (AGENTS.md).

### 5. Heartbeat and escalation

- Each worker sends a heartbeat every 250 ms, with its CPU times
  and the memory of its runtime (ADR 0067 and 0068).
- With no heartbeat for `UnresponsiveThresholdMs`, the worker's active processes
  become **Not responding** (ADR 0067 §4), visible **during** the blocking.
- Stopping a process follows ADR 0069 §3. At step 4 (silent worker after the
  grace period), the OS terminates the worker:
  - the other processes of that worker move to `Exited (Killed)` with a
    message in `/var/log/syslog` ("killed: worker 2 terminated to stop pid
    42") and their shell displays `Killed`;
  - a pool worker is restarted in its place.
- A worker terminates its dedicated process the same way, with no neighbor.

### 6. Outside the browser

`HackerOs.Server` (Interactive Server rendering) has no .NET Web Worker: all
processes stay in the server process there. `AppExecutionHost` is
ignored there, with an information line in the log. This may change in
an ADR specific to this host.

## Consequences

- A command or service that loops no longer freezes the OS, and can always
  be killed.
- Starting the pool costs about `CpuCapacity` × 15 to 40 MB of browser
  memory.
- The price is a layer of remote gateways, to be maintained in parity with the
  local gateways. A contract test runs the same SDK scenarios
  in both modes.
- The first version may limit the pool to built-in commands
  (`HackerOs.Commands`), then open it to other apps.

## References

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- ADR 0012, 0050, 0054 (detached shell), 0055, 0067, 0068, 0069.
