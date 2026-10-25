> [Version française](../../../../fr/wasm/dev/adr/0131-cross-origin-isolation.md)

# ADR 0131: optional cross-origin isolation (COOP/COEP), shared pipes and fine-grained measurements

## Status

Accepted on 2026-10-18.

- **Plan:** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  decisions 9 and 10 (answers Q6 "optional, on by default if the audit
  passes" and Q8 "no .NET threads"); implemented by
  `exec/9-cross-origin-isolation`, after `exec/3-oom-killer` and
  `exec/5-background-jobs`.
- **Complements:** ADR 0072 §2 (pipes through messages), ADR 0068 (a
  worker's memory), ADR 0067 (slice precision), ADR 0124 §5 (synchronous calls
  refused in a worker), ADR 0126 §6.

## Context

Without the `Cross-Origin-Opener-Policy` and
`Cross-Origin-Embedder-Policy` headers, `crossOriginIsolated` is false: no
`SharedArrayBuffer`, no `performance.measureUserAgentSpecificMemory()`,
`performance.now()` rounded to 100 µs. Workers talk to the OS through
`postMessage` only, which forbids any synchronous call from a worker to the
OS. The Ecosystem is a PWA that must work on static hosting
that does not allow setting headers, and offline.

## Decision

### 1. Setting

`HackerOs:CrossOriginIsolation` in the host's `appsettings.json`: `auto`
(default), `on` or `off`.

- `auto` is `on` if the audit of §3 has passed for this version, `off`
  otherwise. The audit result is a build constant written by
  `exec/9`, with the report in
  [`worker-processes.md`](../worker-processes.md).
- The OS **always** decides on `crossOriginIsolated` in the page, never on
  the setting alone: a lost header (proxy, browser) gives the exact fallback.

### 2. Who sets the headers

- `COOP: same-origin`; `COEP: credentialless` (a third-party resource without CORS
  is loaded without cookies instead of being blocked), `require-corp` for a
  browser that does not know `credentialless`.
- **Ecosystem**: the PWA's service worker (`service-worker.published.js`)
  adds the headers to the responses it serves, navigation included; at the first
  load without an active service worker, the page registers then reloads
  **once**. This works offline and on static hosting.
- **`test/test` and `HackerOs.Server`**: a middleware sets the same headers
  (the service worker remains, as for the Ecosystem served by the server, ADR
  0130).
- `off`: no header, and the service worker adds none.

### 3. Audit before enabling by default

`exec/9` checks with isolation, and fixes in the work item or keeps
`auto = off`:

| Surface | Risk |
|---|---|
| HTML rendering of the simulated browser (isolated iframe, ADR 0075, 0079) and ASP.Hack sites | iframes and embedded resources under COEP |
| Detached windows (`window.open`, ADR 0078) | link with the opener window under COOP |
| Fallback to the real network and external images of the browser | resources without CORS |
| Monaco workers (CodeEditor), xterm.js (Terminal) | scripts and workers under COEP |
| Downloads (`download.js`), `.hosbak` backup | `blob:` URLs |
| PWA installation, service worker update | single reload of §2 |

### 4. What isolation brings (decision 10)

- **Ring pipes** in a `SharedArrayBuffer` between each worker and the
  OS's JavaScript layer, in place of messages for standard streams;
  same credit, same `SIGPIPE`, same `/proc/<pid>/fd`.
- **Synchronous calls** from a worker to the OS through `Atomics.wait` **in the
  worker only** (the main thread never waits). The synchronous members
  refused by ADR 0124 §5 (`Kill`, `ListProcesses`) become
  possible in a worker when isolation is active; without it, they remain
  refused with the same message.
- **Real memory** of a worker through `measureUserAgentSpecificMemory()` where
  the browser offers it; `MemSource` indicates it (ADR 0066), and the OOM killer
  uses it (ADR 0126 §6).
- **Timers** of 5 µs for the slices of ADR 0067.
- **No .NET WebAssembly threads** (`WasmEnableThreads`): Blazor does not
  support them, and they forbid blocking the main thread. The
  `SharedArrayBuffer` is used only between workers and the JavaScript layer.

### 5. Visibility

`/proc/hackeros/workers` starts with `cross-origin-isolated: yes|no`;
Diagnostics displays it, with the reason when it is `no` (setting, missing
header, browser).

## Consequences

- `build/verify-hosts.sh` gains a walkthrough of the published Ecosystem with and without
  isolation, `crossOriginIsolated` checked in the page, and the command
  parity suite replayed with shared pipes.
- Apps that load an external resource (browser, CodeEditor) are
  checked with isolation; what breaks is fixed or prevents `auto = on`.
- Docs: [`worker-processes.md`](../worker-processes.md),
  [`memory-accounting.md`](../memory-accounting.md),
  [`cpu-accounting.md`](../cpu-accounting.md),
  [`building-and-testing.md`](../building-and-testing.md) (static
  hosting, headers).

## References

- ADR 0066, 0067, 0068, 0072, 0075, 0078, 0079, 0124, 0126, 0130.
- HTML: `crossOriginIsolated`, COOP, COEP `credentialless`;
  `performance.measureUserAgentSpecificMemory()`.
