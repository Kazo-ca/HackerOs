# ADR 0071: always a worker ready for the next request

## Status

Accepted on 2026-10-16.

- **Plan phase:** phase 5: app execution
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)).
- **Completes:** ADR 0070. Supersedes its sections 2 (when the pool starts)
  and 3 (spare reserved for dedicated processes) on the points below; the
  rest of ADR 0070 is unchanged.

## Context

ADR 0070 starts the pool at boot and keeps a spare for dedicated processes
only. Two cases still make a request wait for a cold start (0.5
to 2 s):

- all pool workers are busy, for example one of them is blocked by
  a loop: a new command would wait behind it;
- the spare has just been taken by a service, and a second request arrives
  before it is replaced.

Benoit wants **there to always be a worker available for the next
request**, once the system has started.

## Decision

### 1. Invariant

Once the system has started, the OS keeps **at least one ready worker**: runtime
loaded, SDK assemblies loaded, no process. Each time a ready worker
is taken, the OS immediately launches another one in the background.

### 2. "System started"

Starting the workers must not slow down the boot. The first ready worker
is launched when the session is open, the desktop displayed and the startup apps
launched (end-of-boot signal of `EcosystemBootCoordinator`), then
as soon as the browser is idle (`requestIdleCallback`, with a maximum delay
of 2 s). The pool of ADR 0070 §2 starts at the same time, worker by worker,
behind the first ready worker.

Before this signal, a request that needs a worker starts one itself
and waits for it.

### 3. Who takes the ready worker

- a `Dedicated` process (ADR 0070 §3);
- a command or pipeline when **no pool worker is free**
  (none has its thread available: it is running a slice, or it is "Not
  responding"). The ready worker then joins the pool for the duration of that
  process: the pool overflows beyond the number of virtual cores, like a
  machine that adds hardware threads under load, and returns to its size
  when the extra worker has stayed empty for 30 s;
- a pool worker terminated during an escalation (ADR 0069 §3, step 4): the
  ready worker replaces it immediately.

### 4. Ceiling

The total number of workers is capped at `navigator.hardwareConcurrency` (at
least 2). At the ceiling, the invariant is suspended: a new request waits
for a worker to become free, and the `/var/log/syslog` log notes it once
("worker pool saturated"). The invariant resumes as soon as a worker becomes free.

### 5. Visibility

- `/proc/hackeros/workers` (new, read-only) lists each worker: role
  (`pool`, `dedicated`, `ready`), state, hosted processes, runtime memory;
- the task manager displays the workers in the Performance tab, along with
  the cores.

## Consequences

- A request after boot never pays a cold start, except at the ceiling.
- The base memory cost becomes "pool + 1 worker", i.e. about
  (`CpuCapacity` + 1) × 15 to 40 MB.
- The tests of the `5-worker-processes` work item cover the invariant (taking,
  replacement, overflow, return to size, ceiling, request before the end
  of boot).

## References

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- `OS/HackerOs.Ecosystem/EcosystemBootCoordinator.cs`.
- ADR 0067, 0069, 0070.
