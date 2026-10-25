> [Version française](../../../../fr/wasm/dev/adr/0072-worker-processes-implementation-adjustments.md)

# ADR 0072: adjustments to the implementation of processes in Web Workers

## Status

Accepted on 2026-10-16.

- **Plan phase:** phase 5: app execution
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)), work item
  `rebuild/5-worker-processes`.
- **Completes:** ADR 0070 and ADR 0071. Supersedes ADR 0070 §1 (shell scripts),
  §2 (pipes in worker memory) and clarifies §4, §5 and ADR 0071 §3 on the
  points below; the rest of these ADRs is unchanged.

## Context

The implementation of ADR 0070 ([`worker-processes.md`](../worker-processes.md))
ran into points that the ADRs do not settle, or settle in a way
that would require moving the shell into the workers. They are recorded here
rather than modifying the accepted ADRs.

## Decision

### 1. The shell stays on the main thread

- `hsh`, its interpreter, its variables, its jobs and its **scripts** stay
  on the main thread; only the external commands it launches go into a
  worker. A script therefore does not block the OS: each of its commands runs
  in a worker, and a script's loop yields between two commands.
- Built-in commands (`cd`, `export`, `jobs`, `kill`, `read`, `exit`...) and
  the commands that drive the shell, elevation or the screen (`sudo`, `nano`,
  `top`, `htop`, `watch`, `which`, `help`) declare `MainThread`, with the
  reason as a comment in their definition. `kill` therefore responds even when
  all workers are blocked.

### 2. Pipes of a pipeline

The stages of a pipeline are indeed placed on the same worker (ADR 0070 §2),
but their **pipes remain the OS's** on the main thread: each stage
writes and reads through messages, with a 64 KiB credit in flight per stream. Keeping the
pipe in the worker would require moving the interpreter there (point 1), and
`/proc/<pid>/fd`, the counted bytes and `SIGPIPE` would remain to be rebuilt.
A consumer that closes causes `RemoteBrokenPipeException` (a
`HackIOException`) to be thrown in the producer, which exits with 141.

### 3. What a worker does not offer

In a worker, the gateways for typed events (`HackEvents`), file
subscriptions (`Watch`), the shell host, administration and
the simulated browser's HTTP page throw `NotSupportedException`. An app
that needs them declares `MainThread` (ADR 0070 §1, last sentence). This is the
case of the sample service, which publishes typed events.

### 4. Killing a process from a worker

`IAppProcessGateway` gains `KillAsync(pid, reason, ct)`, a default method that
falls back to `Kill`. `HackProcess.KillAsync` uses it: in a worker, the call
waits for the OS's response instead of an impossible synchronous call.

### 5. Escalation

- Step 3 (ADR 0069) in a worker: the OS sends `kill` to the worker, which drops
  the process and confirms. Without confirmation within **1 s**, the OS moves to step 4
  and terminates the worker (the worker's thread is blocked).
- The shell writes "Killed" to the error output of a command that exited with
  137 and the reason `Killed` or `Timeout`, whether it ran in a worker or
  not (ADR 0070 §5 provided this for neighbors).

### 6. "Free" and the time of a blocked worker

- A pool worker is **free** (ADR 0071 §3) when it is ready and its
  last heartbeat is less than 750 ms old (3 beats); a worker
  executing a long slice stops beating, so it is no longer
  free.
- The time during which a worker that hosts processes stops beating is
  counted as **busy**: `utime` of its processes (shared), core of
  `/proc/stat`, "CPU" of the task manager. Without this, an infinite loop
  would display 0% until its next heartbeat.
- `cpu0` of `/proc/stat` adds up the main thread and the worker of core 0.

### 7. Hosts

`test/test` runs the Ecosystem's `Program.cs` on the browser side: it has the
workers like the Ecosystem. `HackerOs.Server` has none and writes it once
to `/var/log/syslog` (ADR 0070 §6); `/proc/hackeros` does not exist there.

## Consequences

- A pipeline costs one message round trip per write, absorbed by the
  64 KiB credit; output parity is tested for the 63 commands.
- 14 commands stay on the main thread; they only drive the
  shell or the screen.
- Services that need typed events stay on the main thread
  until a remote event gateway exists.

## References

- [`worker-processes.md`](../worker-processes.md)
- ADR 0067, 0069, 0070, 0071.
