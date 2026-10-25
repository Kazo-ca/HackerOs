# Follow-ups of phase 5: limits, OOM killer, windows in workers, Server host, cross-origin isolation

**Status:** plan **approved by Benoit on 2026-10-17** (answers to the questions in
section 6). No code is written. Benoit asked for "plan first, but
do it later after the migration": this plan **is executed after the end of the
wasm v3 migration** ([`wasm-v3-completion-plan.md`](wasm-v3-completion-plan.md),
branch `v3/base`). No work item starts before that plan is
finished.

**Update of 2026-10-18:** the wasm v3 migration and series v3.1 are
finished. The integration branch `exec/base` is created from `v3.1/base`,
and the ADR work item (`exec/0-adr`, carried out on branch `exec/1-adr`) wrote
ADRs [0124](../adr/0124-remote-event-and-watch-gateways.md) to
[0132](../adr/0132-exec-series-sdk-1-2-and-merge-rules.md) (section 7); ADR
[0133](../adr/0133-window-rendering-in-worker-verdict.md) concludes the trial of
rendering in a worker (`exec/6`): **we stop**, `exec/7` does not happen. Benoit answered on
2026-10-18 the four questions that arose from writing the ADRs (section 6.1):
ADR [0134](../adr/0134-automatic-server-connection-and-status-icon.md) records them, and ADR [0135](../adr/0135-no-game-in-this-repository.md) removes the
game from the documentation.

It takes up the points that [`execution-isolation-plan.md`](execution-isolation-plan.md)
(phase 5, ADR 0066 to 0072) left out of scope (its section 7), as well as
those that the phase 5 ADRs and the worker implementation noted as
"later".

## 1. Purpose

- **Constrain** processes, and not only measure them: per-process limits in the style of
  `ulimit`/`setrlimit`, and an **OOM killer** when the simulated system's memory
  is exhausted.
- **Take off the main thread what stays there out of necessity**: services that
  need typed events or file subscriptions, third-party apps, and
  above all the logic of **window apps**.
- Have **`HackerOs.Server`** serve the same WebAssembly OS as
  the Ecosystem, and keep on the server only its API (relay, synchronization,
  authentication): workers and measurements there become identical to the
  other hosts (Benoit's decision, question 7).
- Study then enable, as an option, **cross-origin isolation** (COOP/COEP), which
  opens `SharedArrayBuffer`, real memory measurement per worker and precise
  timers.

## 2. Starting point

On `web/base` as of 2026-10-17 (phase 5 and web phase merged):

| Subject | State | Reference |
|---|---|---|
| Limits | None. Phase 5 set "measure, do not constrain": no quantum, no memory limit, no OOM killer, no "Force Quit" window. | `execution-isolation-plan.md` §3 and §7, ADR 0068 §3 |
| Measurements | CPU (`utime`/`stime`, compositor) and memory in four layers, optional at build and in `appsettings.json`. In-memory counters, reset at startup. | ADR 0066 to 0068, [`cpu-accounting.md`](../cpu-accounting.md), [`memory-accounting.md`](../memory-accounting.md) |
| Stop | `StopRequested`, `CheckpointAsync`, four-step escalation; step 4 (terminate the worker) exists only in a worker. On the main thread, a blocked process still freezes the OS. | ADR 0069, ADR 0072 §5 |
| Workers | Commands in a pool (49 out of 63) and services in a dedicated worker, each with its .NET runtime (~15 to 40 MB per worker). The pool preloads only `HackerOs.Commands.dll`. | ADR 0070 to 0072, [`worker-processes.md`](../worker-processes.md) |
| What a worker does not offer | `HackEvents`, `Watch`, shell host, administration and the simulated browser's HTTP page throw `NotSupportedException`. The sample service and `hackwebd` therefore stay on `MainThread`. | ADR 0072 §3 |
| Window apps | Still on the main thread (no DOM in a worker). A loop in a window freezes the desktop; "Not responding" is displayed only after the end of the slice. | ADR 0070 §1, ADR 0067 §4 |
| `HackerOs.Server` | Measurements off by default, no worker: `AppExecutionHost` is ignored, one line in `/var/log/syslog`, no `/proc/hackeros`. | ADR 0066 §3, ADR 0067 §6, ADR 0070 §6, ADR 0072 §7 |
| Cross-origin | No host sends `Cross-Origin-Opener-Policy` or `Cross-Origin-Embedder-Policy`; `crossOriginIsolated` is false, so no `SharedArrayBuffer`, no `performance.measureUserAgentSpecificMemory()`, rounded `performance.now()`. Exchanges with workers go through `postMessage` (credit of 64 KiB per stream). | ADR 0072 §2 |
| Trimming | `PublishTrimmed=False`: the cost of a worker depends on it. The point belongs to the wasm v3 migration, not to this plan. Done by `v3/14` (2026-10-17, [`trimmed-publish.md`](../trimmed-publish.md)): 50.9 → 26.5 MiB of WebAssembly. | `/mnt/project-files/wasm-v3/wasm-v3-remaining-work.md` no. 12 |

### 2.1 What v3 and v3.1 changed (2026-10-18, `exec/base`)

| Subject | Change | Effect on this plan |
|---|---|---|
| Gateways of a worker | Catalog and default applications, intents, service control and notification, clipboard, notifications, listening ports, a site's database now go through messages. Still refused: `HackEvents`, `Watch`, synchronous `StartChild`, `Kill` and `ListProcesses`, `WithSelectedHandle`. | Decision 1 is limited to events and watching (ADR 0124). |
| SDK | Frozen at 1.0 (ADR 0114), then 1.1.0 published (ADR 0117); `PublicAPI.Shipped.txt` checked at build. | The series' additions go in `Unshipped` and are published as 1.2 by `exec/10-final` (ADR 0132). |
| Localization, accessibility | English and French (ADR 0104), accessibility preferences and axe tests (ADR 0105). | The "not responding" box and the OOM killer notifications are translated and tested (ADR 0126, 0127). |
| Window title | Set by the app (`SetWindowTitleAsync`, ADR 0117). | The "(Not responding)" suffix is a window manager display (ADR 0127). |
| Server relay | HTTP bodies, TCP and UDP over WebSocket (ADR 0102, 0108). | Nothing to change: the relay is already an API, independent of how the server serves the OS (ADR 0130). |
| Installation at runtime | Still deferred (ADR 0094). | The pool opens only to assemblies known at build (ADR 0124 §4). |

## 3. Principles

Those of phase 5 remain, except the first, which this plan partly replaces.

1. **Constrain only where the constraint is real.** In a worker,
   an exceeded limit really terminates the process. On the main thread, a
   limit goes through the stop request and escalation (ADR 0069), and the
   documentation says it is "best effort". We never display a
   protection that the OS cannot hold.
2. **Optional, like the measurements.** Each constraint can be turned off in
   `appsettings.json` (host setting) or in `/etc` (simulated OS setting,
   ADR 0044) depending on its nature; turned off, we return to the current behavior. A
   limit based on a measurement is inactive when that measurement is off
   (`CpuSource`/`MemSource: simulated`).
3. **Mimic the Metal.** `setrlimit`, `ulimit`, `/proc/<pid>/limits`,
   `/etc/security/limits.conf`, `SIGXCPU`, `oom_score`, `oom_score_adj`, the
   kernel's "Out of memory: Killed process" line, a display server that
   receives a client's drawings (X11, Wayland).
4. **The SDK does not change for existing apps.** An existing app keeps
   its behavior without changing a line. The new features (a window's
   worker process, reaction to a limit) are additions.
5. **Existing apps follow each new feature in the same work item**
   (section 5.1), as in phase 5.
6. **The three hosts stay operational after each work item.**
   `build/verify-hosts.sh` passes before any merge, with the work item's options
   on and off. The Ecosystem still works without a server
   and offline (AGENTS.md, section I).
7. **No game in this repository** ([ADR 0135](../adr/0135-no-game-in-this-repository.md)): this plan provides for nothing of the sort.

## 4. Decision register

Decisions approved by Benoit on 2026-10-17 (section 6). Each becomes an
ADR decision in `exec/0-adr`; ADR numbers are assigned at the time
of writing (section 7).

| # | Decision | Question |
|---|---|---|
| 1 | Remote gateways for `HackEvents` and `Watch`; the sample service and `hackwebd` move to a worker; the pool loads on demand the assembly of any command or service, not only `HackerOs.Commands.dll`. | n/a |
| 2 | Per-process limits in the style of `setrlimit`: `RLIMIT_CPU` (`SIGXCPU` then `SIGKILL`), a memory limit on measured `VmRSS`, `RLIMIT_NOFILE`, `RLIMIT_NPROC`. `ulimit` command (built into the shell), `prlimit`, `/proc/<pid>/limits`, `/etc/security/limits.conf`. Inherited by children. | Q1 |
| 3 | OOM killer: when the simulated system's available memory falls below a threshold, the OS chooses the victim by `oom_score` (resident + `oom_score_adj`), kills it and writes it to the kernel log. **Off by default**: it is enabled in `/etc` (simulated OS setting) and only if memory is measured. `MemTotal` stays the **simulated** value of the hardware profile; occupation is measured, or estimated as accurately as possible when an exact measurement is missing (real memory per worker with the isolation of decision 9). | Q2, Q3 |
| 4 | Guard against the real browser: when the heap of the main runtime or of a worker approaches the real limit of the page, the OS warns (log, notification); if the OOM killer is enabled, it acts even if the simulated `MemTotal` is not reached. | Q2, Q3 |
| 5 | No imposed quantum; `nice` and `renice` only set the order of the pool queue and the choice of worker. | Q4 |
| 6 | Unresponsive window, as in a browser: title "(Not responding)", grayed window and box **"The application is not responding: Wait \| End application"**, as soon as the desktop can redraw (window whose logic is in a worker, or end of a long slice). "End application" follows the escalation of ADR 0069. | Q3 |
| 7 | Window apps, in two steps. **Step 1** (Benoit's idea, the opposite of WinForms' `Invoke`): an SDK function runs the heavy load in a worker, for example `await HackBackground.RunAsync(MyJobs.Compress, input, progress, ct)`. Since the worker has its own memory, the task is a **static** method (or a task class) of the app's assembly, with a serializable input and result, a progress and a cancellation token; an analyzer refuses at build a lambda that captures variables. The task has the window's user and rights, appears in `ps` as a child of the app and can be killed without closing the window. **Step 2**: trial of Blazor rendering in a worker, render batches applied by the main thread in the style of a display server; nothing is delivered to apps if the trial fails. | Q5 |
| 8 | `HackerOs.Server` serves the OS **in WebAssembly** (like `test/test`) and keeps only its API on the server side (relay, synchronization, authentication, ADR 0028); the Interactive Server mode of ADR 0027 and the direct injection of ADR 0036 are removed. Workers, measurements and `/proc/hackeros` there become those of the Ecosystem. | Q7 |
| 9 | Optional cross-origin isolation, **on by default if the audit passes**: COOP/COEP headers sent by the hosts and by the PWA's service worker (hence offline and on static hosting), `COEP: credentialless` where the browser allows it, otherwise `require-corp`, detection through `crossOriginIsolated`, exact fallback to the current behavior. | Q6 |
| 10 | With isolation: pipes as `SharedArrayBuffer` rings between workers and the main thread, synchronous calls from the worker to the OS (`Atomics.wait` in the worker only), real memory per worker through `measureUserAgentSpecificMemory()`, timers at 5 µs. **No .NET WebAssembly threads** (`WasmEnableThreads`). | Q6, Q8 |

## 5. Work items

Branches `exec/<n>-<topic>`, each starting from the integration branch
`exec/base` (created on 2026-10-18 from `v3.1/base`), and its PR targeting it
(question Q9, ADR 0132); the thread
merges itself when the build and tests pass locally, never into
`main`. The phase's ADRs are written and accepted before the code (`exec/0-adr`), as for the
previous phases.

| Branch | Content | Depends on |
|---|---|---|
| `exec/0-adr` | **Done** (branch `exec/1-adr`, 2026-10-18): ADRs 0124 to 0132 for decisions 1 to 10, update of this plan and of the index. | end of the wasm v3 migration |
| `exec/1-remote-gateways` | Decision 1: remote gateways `HackEvents` (subscription on the worker side, delivery by message, unsubscription at process end) and `Watch`; parity tests for these two gateways; sample service and `hackwebd` in a worker; on-demand loading of any app assembly in the pool; a command from a third-party sample project run in a worker. | `0` |
| `exec/2-process-limits` | Decision 2: limits contract in Simulation Abstractions, reading of `/etc/security/limits.conf` (and `limits.d/`), inheritance at `fork`, `ulimit` and `prlimit`, `/proc/<pid>/limits`, `SIGXCPU`, strict enforcement in a worker and best effort on the main thread, exit 152 (`SIGXCPU`) and 137; `EMFILE`/`EAGAIN` error for `NOFILE`/`NPROC` through the gateways. | `0` |
| `exec/3-oom-killer` | Decisions 3 and 4: enabling setting in `/etc` (off by default), available memory threshold, `oom_score`, `oom_score_adj` (editable file, `-1000` exempts, reserved to the administrator to lower), choice and kill, kernel line in `/var/log/kern.log` and `syslog`, `dmesg`, notification to the user for a killed window, guard against the real browser, `oom_kill` counter in `/proc/vmstat`. | `2` |
| `exec/4-not-responding-ui` | **Done** (2026-10-18, [`process-priority-and-unresponsive-windows.md`](../process-priority-and-unresponsive-windows.md)). Decisions 5 and 6: "(Not responding)" window and box "The application is not responding: Wait \| End application", `nice`/`renice` and "NI" column of `ps`/`top`, pool queue order. | `0` |
| `exec/5-background-jobs` | Decision 7, step 1: `HackBackground.RunAsync` (static method or task class, serializable input and result, progress, cancellation), analyzer that refuses a capturing lambda, child process of the app with its identity and rights, fallback to the main thread when there is no worker (tests, host without workers), sample in `Apps/Samples`, migration of the base apps' long operations, SDK guide and reference. | `1` |
| `exec/6-remote-renderer-poc` | Decision 7, step 2: proof of feasibility of a Blazor `Renderer` in a worker, render batches serialized to the main thread, DOM events sent back to the worker; cost measurement. Result: an ADR "we go" or "we stop". Nothing is delivered to apps if the proof fails. | `5` |
| `exec/7-window-workers` | Only if `6` succeeds: `AppExecutionHost.Dedicated` allowed for a window without its own JavaScript interop, keyboard, focus and dialog handling, tests on the Calculator. **Removed**: [ADR 0133](../adr/0133-window-rendering-in-worker-verdict.md) concludes "we stop". | `6` |
| `exec/8-server-wasm` | **Done** (2026-10-18, [`server-hosting.md`](../server-hosting.md), [ADR 0137](../adr/0137-server-hosting-implementation-adjustments.md)): published Ecosystem files served as is. Decision 8: `HackerOs.Server` serves the OS in WebAssembly (`InteractiveWebAssembly` rendering or published Ecosystem files, according to the ADR), removal of the Interactive Server mode and direct injection, the OS calls the server's API over HTTP like the Ecosystem, automatic connection to the server that served the OS and status icon in the notification area (ADR 0134), `build/verify-hosts.sh` and `HackerOs.HostSmoke.Tests` adapted (workers and `/proc/hackeros` present on this host), hosting docs up to date. | `0` |
| `exec/9-cross-origin-isolation` | Decisions 9 and 10: compatibility audit (iframes of ASP.Hack sites and the browser, detached windows, fallback to the real network, Monaco workers, downloads, PWA), headers in the three hosts and the service worker, fallback, then shared pipes, synchronous calls, real memory per worker and timers. | `3`, `5` |
| `exec/10-final` | Publication of SDK 1.2 (ADR 0132), compatibility app 1.1; end-to-end verification, documents up to date (`system-metrics.md`, `task-manager.md`, `worker-processes.md`, `app-sdk.md`, `sdk-reference.md`, `linux-commands.md`, `virtual-proc-and-dev.md`, `building-and-testing.md`), index, this list. | all |

Order:

1. `exec/0-adr`.
2. In parallel: `exec/1-remote-gateways`, `exec/2-process-limits`,
   `exec/4-not-responding-ui`, `exec/8-server-wasm`.
3. In parallel: `exec/3-oom-killer`, `exec/5-background-jobs`.
4. `exec/6-remote-renderer-poc`, then `exec/7-window-workers` if decided.
5. `exec/9-cross-origin-isolation` (it gives the real per-worker measurement to
   the OOM killer, and must verify window workers if there are any).
6. `exec/10-final`.

### 5.1 What each work item does for existing apps

| Work item | Migration of existing apps |
|---|---|
| `exec/1-remote-gateways` | Review each app on `MainThread` because of a missing gateway (sample service, `hackwebd`) and move it to a worker when the reason disappears; update the reason comment of the others. |
| `exec/2-process-limits` | Each command and service treats `EMFILE`/`EAGAIN` as an ordinary I/O error (message and exit code). Long commands already have `CheckpointAsync` (phase 5): verify that a CPU limit stops them cleanly. |
| `exec/3-oom-killer` | Apps that keep state (editors, CodeEditor, explorer, Settings) keep their recovery draft when they are killed by the OOM killer as by "Force kill"; critical system services receive a negative `oom_score_adj` in their definition. |
| `exec/4-not-responding-ui` | No app modification; the task manager and `top` display "NI". |
| `exec/5-background-jobs` | Inventory of the long operations of window apps (CodeEditor search, explorer zip and copies, Hack Paint computations); each goes through `HackBackground.RunAsync` or keeps its place with the reason written down. |
| `exec/7-window-workers` | Inventory of window apps without their own JavaScript interop; those that pass the tests in a worker change `AppExecutionHost`, the others keep `MainThread` with the reason. |
| `exec/8-server-wasm` | No app modification; apps that used direct injection of the server's services (ADR 0036) go through the HTTP API. |
| `exec/9-cross-origin-isolation` | Each app that loads an external resource (browser, CodeEditor) is verified with isolation enabled; what breaks is fixed in the work item or prevents enabling isolation by default. |

### 5.2 Host verification

`build/verify-hosts.sh` (phase 5) gains, over the course of the work items:

- a run with low limits (`ulimit -t 1` then `stress`, exit 152);
- a run of the OOM killer with a reduced simulated `MemTotal`;
- on the Server host served in WebAssembly, `/proc/hackeros/workers` present and `kill -9` of a loop;
- a run of the published Ecosystem with and without cross-origin isolation, with
  `crossOriginIsolated` verified in the page.

## 6. Questions settled

Put to Benoit on 2026-10-17 in the thread "Plan: follow-ups of step 5", with
the context and consequences of each choice; his answers (05:55 to 06:19 UTC):

| # | Question | Benoit's answer | Consequence in this plan |
|---|---|---|---|
| Q1 | Limits of a process on the main thread | **Best effort** | Strict in a worker; on the main thread, stop request then escalation (ADR 0069), stated in `/proc/<pid>/limits` and the documentation. |
| Q2 | Total memory of the OOM killer | **Simulated (estimate if possible)** | `MemTotal` of the simulated profile; occupation measured or estimated as accurately as possible (decision 3). |
| Q3 | OOM killer active by default | **Off**; open instead a window like browsers do: "The application is not responding: Wait \| End application" | OOM killer to enable in `/etc`; box of decision 6. |
| Q4 | Imposed quantum | **No** | Decision 5. |
| Q5 | Window apps in a worker | An SDK function that runs the heavy load on another thread, the opposite of `Invoke`; then **keep the trial** of rendering in a worker | Decision 7, work items `exec/5` and `exec/6`. |
| Q6 | Cross-origin isolation | **Optional, on by default if the audit passes** | Decisions 9 and 10. |
| Q7 | Isolation on the Server host | **Server = OS in WebAssembly + API** (Benoit thought the server already hosted a WebAssembly client) | Decision 8, work item `exec/8-server-wasm`; replaces ADRs 0027 and 0036. |
| Q8 | .NET WebAssembly threads | **No** | Decision 10. |
| Q9 | Branches | **`exec/<n>-<topic>`** | Section 5; integration on `exec/base` (card "Start the follow-ups of step 5 now", answer "Yes", 2026-10-18). |

### 6.1 Questions that arose from writing the ADRs (2026-10-18)

Put to Benoit in the thread "ADRs of the follow-ups of step 5", with the context
and consequences of each choice; answers of 2026-10-18 (ADR 0134).

| # | Question | Choice (⭐ recommended) | Benoit's answer | ADR |
|---|---|---|---|---|
| E1 | How the server serves the OS | Published Ecosystem files as is ⭐ \| Blazor Web App in `InteractiveWebAssembly` like `test/test` | **A** | 0130 §2 |
| E2 | Does the OS served by the server connect to it | Prefilled address, the user chooses to connect ⭐ \| Nothing special, manual entry | **A**, clarified: automatic connection without asking, status icon in the notification area | 0134 (replaces 0130 §4) |
| E3 | Default limits | Linux values for `nofile` and `nproc`, CPU and memory unlimited ⭐ \| Everything unlimited | **A** | 0125 §6 |
| E4 | Publication of SDK 1.2 | Once, at the end (`exec/10`) ⭐ \| One minor per work item that adds an API | **A**; and **No game in this repository** in the docs and memory | 0132 §2, 0135 |

## 7. ADRs of the series

Written by `exec/0-adr` on 2026-10-18 (numbers 0124 to 0135; 0133 remains reserved):

| ADR | Content | Completes or replaces |
|---|---|---|
| [0124](../adr/0124-remote-event-and-watch-gateways.md) Remote event and watch gateways | decision 1 | partly replaces ADR 0072 §3, completes 0070 |
| [0125](../adr/0125-process-resource-limits.md) Per-process limits | decision 2 | replaces the phase 5 principle "measure, do not constrain" for limits, completes 0067, 0068, 0069 |
| [0126](../adr/0126-oom-killer.md) OOM killer | decisions 3 and 4 | replaces ADR 0068 §3 |
| [0127](../adr/0127-priority-and-not-responding-dialog.md) Priority and unresponsive window | decisions 5 and 6 | completes ADR 0067 §4 |
| [0128](../adr/0128-background-jobs-hack-background.md) Heavy background tasks (`HackBackground`) | decision 7, step 1 | completes ADR 0070 §1 |
| [0129](../adr/0129-window-rendering-in-worker-trial.md) Trial of window rendering in a worker, criteria; **0133** (reserved) decision after the trial | decision 7, step 2 | 0133 replaces ADR 0070 §1 (windows) if we go |
| [0130](../adr/0130-server-serves-webassembly-os-and-api.md) `HackerOs.Server` served in WebAssembly | decision 8 | replaces ADR 0027 and ADR 0036; makes moot ADR 0066 §3 (Server default), 0067 §6, 0070 §6, 0072 §7 |
| [0131](../adr/0131-cross-origin-isolation.md) Cross-origin isolation | decisions 9 and 10 | completes ADR 0072 §2 (pipes) and ADR 0068 (real memory per worker) |
| [0132](../adr/0132-exec-series-sdk-1-2-and-merge-rules.md) SDK 1.2 additions, branches and merge bar | sections 5 and 8 | applies ADRs 0114 and 0117 |
| [0133](../adr/0133-window-rendering-in-worker-verdict.md) Verdict of the window-rendering-in-worker trial: we stop | decision 7, step 2 (`exec/6`) | does not replace ADR 0070 §1; `exec/7` removed |
| [0134](../adr/0134-automatic-server-connection-and-status-icon.md) Automatic server connection and status icon | answers E1 to E4 | replaces ADR 0130 §4 |
| [0135](../adr/0135-no-game-in-this-repository.md) No game in this repository | answer E4 | replaces ADR 0092 |

## 8. Task list

### Preparation
- [x] Write this plan (2026-10-17, thread "Plan: follow-ups of step 5").
- [x] Have the plan approved and settle questions Q1 to Q9 with Benoit (2026-10-17, section 6).
- [x] Wait for the end of the wasm v3 migration (finished on 2026-10-17, then series v3.1).
- [x] Create `exec/base` from `v3.1/base` (2026-10-18).
- [x] `exec/0-adr`: write and have the ADRs of section 7 accepted (0124 to 0132, 2026-10-18).
- [x] Benoit's answers to questions E1 to E4 (section 6.1, ADR 0134 and 0135).

### Remote gateways
- [x] `HackEvents` and `Watch` in a worker, parity tests (`exec/1-remote-gateways`, ADR 0138).
- [x] Sample service and `hackwebd` in a worker (`exec/1-remote-gateways`, ADR 0138).
- [x] Pool opened to any command or service assembly (`exec/1-remote-gateways`, ADR 0138).

### Limits and OOM killer
- [x] Limits contract, `limits.conf`, inheritance, `ulimit`, `prlimit`, `/proc/<pid>/limits` (`exec/2-process-limits`, ADR 0139).
- [x] `SIGXCPU`, memory limit, `EMFILE`/`EAGAIN` (`exec/2-process-limits`, ADR 0139).
- [x] `oom_score`, `oom_score_adj`, choice and kill, kernel log, `dmesg` (`exec/3-oom-killer`, ADR 0142).
- [x] Guard against the real browser (`exec/3-oom-killer`, ADR 0142).

### Windows
- [x] "(Not responding)" and "Wait \| End application", `nice`/`renice` (`exec/4-not-responding-ui`, ADR 0127, PR #183).
- [x] `HackBackground.RunAsync`, analyzer, sample, migration of the base apps (`exec/5-background-jobs`, ADR 0140).
- [x] Proof of feasibility of rendering in a worker and decision ADR (`exec/6-remote-renderer-poc`, ADR 0133: we stop).
- [x] ~~Windows in a worker (if decided).~~ Moot: ADR 0133 concludes "we stop" (memory of a window worker 66.5 MiB for 40 MiB allowed).

### Server host
- [x] OS served in WebAssembly, API only on the server side, removal of Interactive Server, adapted host verification (`exec/8-server-wasm`, ADR 0134 and 0137).

### Cross-origin isolation
- [x] Compatibility audit (`exec/9-cross-origin-isolation`, ADR 0143: passed, `auto = on`).
- [x] Headers in the hosts and the service worker, fallback (`exec/9-cross-origin-isolation`, ADR 0143).
- [x] Shared pipes, synchronous calls, real memory per worker, timers (`exec/9-cross-origin-isolation`, ADR 0143).

### End
- [x] Publication of SDK 1.2 and compatibility app 1.1 (`exec/10-final`, ADR 0132).
- [x] `build/verify-hosts.sh` extended (section 5.2, done over the course of the work items), final verification (section 11), documents and index (`exec/10-final`).

## 9. Out of scope

- Everything that the wasm v3 migration plans (trimming, localization, `dotnet new`
  templates, backup and restore, etc.).
- Several simultaneous users on the `HackerOs.Server` API, beyond
  what it already does.
- Moving the shell (`hsh`) and its scripts into a worker (ADR 0072 §1 remains
  valid).
- CPU and memory counters persisting from one startup to the next.

## 10. References

- [`execution-isolation-plan.md`](execution-isolation-plan.md) (phase 5) and its
  ADRs [0066](../adr/0066-optional-resource-accounting.md),
  [0067](../adr/0067-measured-cpu-time.md),
  [0068](../adr/0068-measured-process-memory.md),
  [0069](../adr/0069-graceful-stop-and-checkpoints.md),
  [0070](../adr/0070-worker-hosted-processes.md),
  [0071](../adr/0071-always-ready-worker.md),
  [0072](../adr/0072-worker-processes-implementation-adjustments.md).
- [`worker-processes.md`](../worker-processes.md),
  [`cpu-accounting.md`](../cpu-accounting.md),
  [`memory-accounting.md`](../memory-accounting.md),
  [`resource-accounting.md`](../resource-accounting.md),
  [`ecosystem-rebuild-plan.md`](ecosystem-rebuild-plan.md).

## 11. Final verification

Done by `exec/10-final` on 2026-10-18 on `exec/base` (with `exec/1` to
`exec/9`, PRs #177 to #186) plus this work item. Since GitHub Actions does not run, the
bar is the local suite of ADR 0132 §4 (.NET 10, `NuGetAudit=false`,
`MSBUILDDISABLENODEREUSE=1`, E2E suites in series).

| Step | Result |
|---|---|
| SDK 1.2 | `build/tools/ship-public-api.sh`: 795 lines moved from `Unshipped` to `Shipped` (one `*REMOVED*` line, constructor of `EcosystemBootCoordinator`), `HackerOsVersion` = `1.2.0`, manifests generated with `minimumVersion: 1.2.0` ([`sdk-packages.md`](../../framework/sdk-packages.md#sdk-12)) |
| Compatibility app 1.1 | `Contoso.Sdk11Compat.1.1.0.nupkg`, compiled against the packages at the top of `v3.1/base` (window title, `HackUserAccounts.ValidateNewPassword`) |
| Debug and Release build of `HackerOs.sln` | 0 warnings, 0 errors |
| Unit and component tests (42 projects) | 3761 passed, 0 failed (including `HackerOs.SdkCompatibility.Tests` 10/10: apps 1.0.0 and 1.1.0) |
| `HackerOs.E2E.Tests` | 18/18 |
| `HackerOs.UI.E2E.Tests` | 27/27; `WindowKeyboardCommandsTests` failed once, the `test/test` host not having answered within the harness delay (known instability under load), then passed alone twice |
| `HackerOs.Pwa.E2E.Tests` | 6/6 after fixing three tests: since `exec/9`, the service worker keeps the `hackeros-isolation` cache, independent of versions (ADR 0143), which the tests did not know about |
| `HackerOs.Templates.E2E.Tests` | 6/6, including the generated host with compatibility apps 1.0.0 and 1.1.0 in Chromium |
| `build/publish-trimmed.sh` | PASS (136 `.wasm` files, 27.2 MiB, 8.2 MiB Brotli) |
| `build/verify-hosts.sh` | PASS on the 9 runs: `ecosystem`, `ecosystem-build-off`, `ecosystem-settings-off`, `ecosystem-limits-off`, `ecosystem-oom`, `ecosystem-isolated`, `ecosystem-isolation-off`, `test-host`, `server` |

Documents reread and updated: `system-metrics.md`, `task-manager.md`,
`worker-processes.md`, `app-sdk.md`, `sdk-reference.md`,
`sdk-developer-guide.md`, `app-model.md`, `linux-commands.md`,
`virtual-proc-and-dev.md`, `building-and-testing.md`, `sdk-packages.md`,
`building-on-hackeros.md`, `project-overview.md`, `README.md` index.

Still open, with the recommended choice applied while waiting for Benoit: the
cards of the threads "Exec 1" (publication from a worker), "Exec 4"
(`sudo nice`), "Exec 5" (grace period, task PID; settled on
2026-10-19: 5 s and no PID, ADR 0147), "Exec 6"
(stopping the rendering-in-worker trial, `exec/7` does not happen) and "Exec 9"
(four cards on isolation). The merge of `exec/base` into `main` is
Benoit's decision: he asked for it on 2026-10-18 ("we can bring everything back to
main"), and it was done the same day after a new pass of the local
bar; `exec/base` already contained `web/base`, `v3/base` and `v3.1/base`, and
`rebuild/base` was merged into it without changing a file (its PR #133 was
already carried by PR #148).
