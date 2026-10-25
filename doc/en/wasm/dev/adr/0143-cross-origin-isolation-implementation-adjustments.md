> [Version française](../../../../fr/wasm/dev/adr/0143-cross-origin-isolation-implementation-adjustments.md)

# ADR 0143: adjustments to the implementation of cross-origin isolation

## Status

Accepted on 2026-10-18, work item `exec/9-cross-origin-isolation`.

- **Completes:** ADR 0131 (optional cross-origin isolation). Clarifies what
  the code had to settle and departs from ADR 0131 on four points: `MemSource`
  (§6), what the rings carry (§4), the Ecosystem launched by `dotnet run` (§3)
  and the parity suite (§9).
- **Doc:** [`worker-processes.md`](../worker-processes.md) (section
  "Cross-origin isolation" and the audit report).

## Context

ADR 0131 fixes the `HackerOs:CrossOriginIsolation` setting, which sets the
headers, the audit, and what isolation brings: shared rings, synchronous calls
in a worker, memory measured by the browser, fine-grained timers. While
writing it, several details appeared: where the audit constant lives, how the
page learns why it is not isolated, how to keep message order between ring and
`postMessage`, how a worker waits without the main thread waiting, and how to
attribute memory to a worker.

## Decision

### 1. Audit constant and setting

- `CrossOriginIsolationPolicy.AuditPassed` (`HackerOs.Platform.Core`,
  namespace `Isolation`) is the build constant of ADR 0131 §1. The service
  worker keeps a copy (`crossOriginIsolationAuditPassed`), since it cannot
  read an assembly; a unit test checks that both say the same thing.
- The audit has passed (§10): `AuditPassed = true`, so `auto` means `on`.
- `Parse` reads `auto`, `on`/`true`, `off`/`false`; any other value means
  `auto`. The three hosts ship `"CrossOriginIsolation": "auto"`.

### 2. Who sets the headers, and what they cover

- **Ecosystem service worker**: it reads the setting in `appsettings.json`
  (from cache, otherwise from the network) and adds `COOP: same-origin`,
  `COEP: credentialless` and `Cross-Origin-Resource-Policy: same-origin` to the
  same-origin responses it serves (navigation, assets, `_framework`),
  never to opaque responses. It rereads the setting on each navigation.
- **`require-corp` fallback**: the page asks the service worker what it
  sent (`COI_STATE`). If the service worker sends `credentialless` and the
  page is not isolated, the page asks it for `require-corp` (`COI_STRICT`), the
  choice is kept in the service worker's cache, and the page reloads
  **only once** (guard in `sessionStorage`).
- **`test/test` and `HackerOs.Server`**: a middleware placed right after
  `builder.Build()` adds `COOP` and `COEP: credentialless` to all
  responses (`CrossOriginIsolationPolicy.ServerHeaders`), according to the
  host's setting. These two hosts have no `require-corp` fallback.
- **Single reload on first load**: this is the one that already existed
  in `index.html` (`controllerchange`), with no new code.

### 3. Deviation: the Ecosystem run by `dotnet run` is not isolated

`index.html` does not register the service worker on `localhost` or
`127.0.0.1`. On these addresses, nothing sets the headers, so the Ecosystem launched
by `dotnet run` (or served by a local static server) is not isolated:
`/proc/hackeros/workers` says `missing-headers` and the OS works as before.
To try isolation, serve the publication on `127.0.0.2` (a secure
context where the service worker registers), or run `test/test`. Question 2
below.

### 4. Deviation: the rings carry all protocol messages

ADR 0131 §4 speaks of the standard streams. The rings actually carry **all**
the protocol's text messages (outputs, acknowledgments, calls, results), one
1 MiB SPSC ring per direction in a `SharedArrayBuffer`, with length frames.

- **Order**: the reader drains its ring before processing any message received by
  `postMessage`. A message too large for the free space goes through
  `postMessage`, and the order never changes.
- **Nobody blocks on a ring**: a full ring falls back to
  messages; the output credit (`OutputAck`, ADR 0070) remains the flow
  control. Same credit, same `SIGPIPE`, same `/proc/<pid>/fd` as before.
- **Doorbell**: the writer sends a small `postMessage` only when the
  ring was idle.
- The page writes to the ring toward the worker only after the worker's `ready`
  message announcing `rings: true`; a worker that could not open
  its rings stays on messages.

### 5. Synchronous calls

- One shared cell per worker (header `[state, expected call, length,
  answered call]`, 1 MiB of response). The worker marks the expected call, sends it
  (`"s": true` in the `call` message), then waits with `Atomics.wait`. The page
  never waits: it writes the response and wakes the worker
  (`ISynchronousWebWorker.AnswerSynchronousCall`).
- **Timeout**: 10 s (`WorkerRpcClient.SynchronousCallTimeout`), then
  `TimeoutException`. A late response finds the cell idle and is
  discarded. A response larger than 1 MiB gives an `IOException` in the worker.
- **Members**: those that ADR 0124 §5 refused, `ListProcesses` (new
  operation `proc.list`) and `Kill` of another process. The other synchronous
  members keep their refusal. Without isolation, the refusal and its message do
  not change.
- **Example**: `sample-cmd --processes` and `sample-cmd --kill <pid>` call
  these members; `HostSmoke` uses them.

### 6. Deviation: `MemSource` stays `measured`

ADR 0131 §4 says that `MemSource` indicates the memory measured by the browser.
`MemSource` in `/proc/<pid>/status` keeps `measured`: the task manager and
`HostSmoke` compare this value exactly, and the memory of a
process remains a layered estimate (ADR 0068) even when the worker's memory
comes from the browser. The detail is in `/proc/hackeros/workers`
(`memory-measure: browser|estimated`). Question 1 below.

- **Attribution**: each worker's URL carries `?hackeros-worker=N`; the
  service worker ignores it for its cache (`ignoreSearch`). The pool asks
  `performance.measureUserAgentSpecificMemory()` every 20 s
  (`WorkerPoolOptions.MemoryMeasurePeriod`) through `IWebWorkerMemoryMeter`.
- A measurement is valid for three periods; without it, we fall back to the
  heartbeat memory. Per-process accounting and the OOM killer
  receive it through `WorkerConnection.RuntimeMemoryBytes` (ADR 0126 §6). The
  browser guard still compares the managed heap (ADR 0142 §4).

### 7. Timers

Nothing to wire up: the slices of ADR 0067 read `Stopwatch.GetTimestamp`,
that is `performance.now()`, which the browser rounds to 5 µs in an isolated
page instead of 100 µs. The probe measures the real resolution and
`/proc/hackeros/workers` shows it (`timer-resolution-us`).

### 8. What the page says, and where

- At startup, `crossOriginIsolation.js` (`HackerOs.Infrastructure.Browser`)
  returns what it found; `CrossOriginIsolationProbe.Read` turns it into a
  `CrossOriginIsolationStatus`. Reasons, in order: `insecure-context`,
  `setting`, `browser` (the service worker set the headers and the browser
  ignored them), `missing-headers` (nothing set them); `not-browser` outside a
  browser.
- `/var/log/syslog` receives an `isolation: …` line (category `kernel`).
- `/proc/hackeros/workers` starts with `cross-origin-isolated`, then
  `isolation-reason`, `isolation-setting`, `embedder-policy`, `shared-pipes`,
  `sync-calls`, `memory-measure`, `timer-resolution-us`, before `capacity`.
- Diagnostic reads this file through `HackFile` and shows an "Isolated" or
  "Not isolated:" badge with the reason, all lines in the tooltip. Reading
  `/proc` requires `filesystem.system.read`, a dangerous capability: Diagnostic,
  already subject to administrator approval for
  `system.storage.inspect`, declares it in addition, and the administrator approves it
  at the next launch (ADR 0049). Without it, or without a worker pool, no
  badge.

### 9. Deviation: parity under rings goes through `HostSmoke`

The rings are JavaScript: the command parity suite, which runs
in .NET loopback workers, cannot exercise them. Instead:
`sharedRing.js` has its tests under Node (order, wraparound, full ring,
synchronous cell), the synchronous calls have loopback tests, and `HostSmoke`
replays under isolation a long pipe (`seq 30000 | wc -l`) and
`sample-cmd --processes` in the isolated scenarios of `verify-hosts.sh`
(`ecosystem-isolated`, `test-host`, `server`) and non-isolated ones (the
Ecosystem scenarios on `127.0.0.1`, `ecosystem-isolation-off`).

### 10. Audit

Done in Chromium with isolation (report in
[`worker-processes.md`](../worker-processes.md)): nothing broke, so
`auto = on`. The entire UI E2E suite runs isolated, since `test/test` now
sets the headers.

## Questions asked to Benoit

1. **`MemSource` when the browser measures the worker.** A (applied):
   `measured` stays, the detail is in `/proc/hackeros/workers`. B: new
   value `browser`, the task manager and `HostSmoke` are adapted.
2. **Isolation of the Ecosystem launched locally (`dotnet run`).** A (applied):
   not isolated, as before; try on `127.0.0.2` or with `test/test`.
   B: register the service worker on `localhost` too, at the risk of serving
   cached files during development.
3. **Browser without `credentialless` (Safari).** A (applied, ADR 0131 §2):
   `require-corp` fallback, the page is isolated but external images without CORP
   in the simulated browser's pages are blocked. B: no fallback, the page stays
   non-isolated and keeps those images.

## Consequences

- `build/verify-hosts.sh` gains `ecosystem-isolated` and
  `ecosystem-isolation-off` (on `127.0.0.2`) and checks the isolation of each
  scenario (`HACKEROS_SMOKE_EXPECT_ISOLATION`). A scenario that modifies
  `appsettings.json` recomputes its fingerprint in `service-worker-assets.js`.
- Docs: [`worker-processes.md`](../worker-processes.md),
  [`memory-accounting.md`](../memory-accounting.md),
  [`cpu-accounting.md`](../cpu-accounting.md),
  [`building-and-testing.md`](../building-and-testing.md),
  [`server-hosting.md`](../server-hosting.md),
  [`virtual-proc-and-dev.md`](../virtual-proc-and-dev.md).

## References

- ADR 0067, 0068, 0070, 0124, 0126, 0130, 0131, 0142.
