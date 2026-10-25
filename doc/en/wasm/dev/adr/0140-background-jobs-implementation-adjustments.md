> [Version française](../../../../fr/wasm/dev/adr/0140-background-jobs-implementation-adjustments.md)

# ADR 0140: adjustments to the implementation of ADR 0128 (background tasks)

## Status

Accepted on 2026-10-18.

- **Work item:** `exec/5-background-jobs` (plan
  [`execution-followups-plan.md`](../plans/execution-followups-plan.md), decision 7,
  step 1).
- **Completes:** ADR 0128, without changing its decision. It records what
  the code had to clarify or do differently, and why. It amends the
  `IAppProcessGateway` rule on one point (section 7).

## Context

ADR 0128 describes `HackBackground.RunAsync`: a static method of the app,
serializable data, a child process placed in the worker pool, an
analyzer at build time. Four constraints weighed on the implementation:

1. An app can run without an OS (its unit tests): `HackBackground` must work
   there with the same contract.
2. The worker protocol (ADR 0070, 0124) only launches app entry points; a task
   is not one.
3. Scheduling by `nice` belongs to `exec/4` (ADR 0127), which runs in
   parallel.
4. The long-running operations of the base apps are not all computation: some
   need the window in the middle (a question to the user).

## Decision

### 1. `IAppExecutionContext.Background` gateway

- The SDK adds `IAppBackgroundGateway` (a single member, `RunAsync` of a
  `HackBackgroundJobRequest`: named target, JSON input, name, `nice`) and the
  default member `IAppExecutionContext.Background`.
- **By default** (a context the OS did not build, a test of an app), the task
  runs **inline** in the calling process, with the same serialization
  and the same errors. An app tested this way behaves identically under the OS.
- The trusted factory (`AppExecutionContextFactory.BackgroundJobs`, an
  `init` property so as not to touch the shipped constructor) wires up
  `BackgroundJobService` from `HackerOs.Platform.Core`.

### 2. Process of a task

- Child process of the app (`ParentPid`), of kind `Terminal` (no window),
  with the app's `AppId`, its user, its session, its resource profile
  and its granted capabilities. `argv[0]` is the name of the task.
- Name: `<app command>:<name>`, where the command is the manifest's `Terminal.Name`
  or the last segment of the ID (`code-editor:search`,
  `file-explorer:compress`). The name is the `Name` option, otherwise the
  method or the class; 1 to 64 characters, with no `:` or control character.
- `/proc/<pid>/status`: `Name:` carries the task name, truncated to
  15 characters like Linux's `TASK_COMM_LEN`; two lines are added after
  `Session:`, `Job:` (the full name) and `Host: MainThread|Worker`. `ps` shows
  the task name in its command column.
- Exit codes: `0` success, `1` exception in the task, `127` task refused
  (nothing valid to run, like a failing `exec`), `130` canceled,
  `137` killed.

### 3. Concurrency and `nice`

- At most `MaxConcurrentJobsPerApp` tasks **per user and app pair**
  (number of virtual cores, rounded up); the following ones wait in
  arrival order.
- `nice` is bounded to `[-20, 19]`; a negative value is brought back to `0` for a
  user without administrator rights or root elevation (`nice(2)`). It is
  recorded (`BackgroundJobInfo.Nice`) but **does not yet order** the
  queue: scheduling by `nice` arrives with `exec/4` (ADR 0127), which will
  wire it up for tasks as well. The `Nice` line of `/proc/<pid>/status` is
  left to `exec/4` to avoid two formats.

### 4. Worker protocol

- Starting a task: `start` message with `k = 2`, the target `jt`
  (`asm`, `ty`, optional `me`, `it`) and the input `ji`. The worker loads
  the assembly (`KnownAssemblies`, ADR 0138), binds the method or the class and
  refuses (code 127) what it does not find.
- End: the `exit` message carries `jr` (JSON result), `jf` (`ty`, `m`, `st`:
  type, message and stack of the original exception) and `jx` (task refused).
- Progress: `bg.progress` note `{f, m}` from the worker to the OS.
- Task launched from a worker (ADR 0128 §6): `bg.run` call (`jt`, `ji`, `n`,
  `ni`, number `j`); the OS places the task and sends its progress back through
  `bgp` messages (`p`, `j`, `f`, `m`), then the result `{r}`.
- Remote errors: `bgfault` (`ty`, `st`) for `HackBackgroundJobException`;
  the `killed` error carries the reason `rs`.

### 5. Errors seen by the caller

- `HackProcessKilledException` gets the `Reason` property and a constructor
  `(processId, reason)`: `Killed` by default, `OutOfMemory` or
  `CpuLimitExceeded` when the OS knows. It derives from
  `OperationCanceledException`: a `catch` must place it before the cancellation
  one.
- Refused task (method not found, undeclared type, other assembly, input
  beyond 16 MiB): `ArgumentException`, before any process when the SDK
  can see it.
- Progress: at most one report per 100 ms, the last one wins and the
  last one before the end is always delivered; `ToString()` gives `42% message`.

### 6. App closing

- When the app ends, the process manager terminates its children with the
  reason `DependencyStop`: the task's token is canceled immediately,
  **with no grace period** (no 5 s wait like a `SIGTERM` followed by a
  `SIGKILL`). With `kill -9` of the app, the tasks are killed too.
- The OS accounting (end of the process, freed slot) does not depend on the
  caller's scheduler: it abandons its continuations once killed
  (ADR 0069), hence the `ConfigureAwait(false)` calls in `BackgroundJobService`.

### 7. An app manages its own children (amends `IAppProcessGateway`)

`StopAsync`, `Kill`, `KillAsync` and `ListProcesses` required
`process.manage` / `process.list` for any process other than its own. A
**descendant** of the app's process (one of its tasks) now counts as
its own: a window can list and kill its tasks without any additional
right, like a Unix parent signaling the processes it launched.
`ListProcesses` remains synchronous and therefore unavailable in a worker; a task
can communicate its PID through its progress (example in the sample window).

### 8. Analyzer

- Project `Tools/HackerOs.Tools.BackgroundAnalyzer` (netstandard2.0), shipped
  in the `HackerOs.AppSdk` package (`analyzers/dotnet/cs`) and wired to the
  repository's apps by `build/HackerOs.Apps.targets`.
- Codes: `HOSBG0001` (not a static method group: lambda, anonymous method,
  instance method, non-`static` local function, delegate passed through a
  variable), `HOSBG0002` (input or result type outside the `HackWireContext`
  contexts), `HOSBG0003` (method or class from another assembly),
  `HOSBG0004` (generic method or method of a generic type).
- A type is accepted if it is a `[JsonSerializable]` root of a declared
  context **or** if it is reached from one of them (array element, generic
  argument, public property), as `JsonSerializerContext.GetTypeInfo` finds it
  at run time.
- A `static` local function is accepted: the compiler turns it into a static
  method that the worker finds by its name.

### 9. Base apps

| Operation | Decision | Reason |
|---|---|---|
| Search in the CodeEditor folder (Ctrl+Shift+F) | `code-editor:search` task | The list of files is built by the window; reading and searching (regular expressions) happen in the task. Unsaved tabs are passed in the input. |
| Compress, Extract and "UnZip Here..." of the explorer | `file-explorer:compress` and `file-explorer:extract` tasks | Compression and decompression are computation; the task reads and writes through its own gateways. Errors come back as a text key, translated by the window. |
| Copy, move, paste in the explorer | Stay in the window | Each item is an awaited call to the OS, with a checkpoint between two (no long computation); a name conflict asks a question in the window between two items, which a task in another process cannot do. |
| Download of a selection as `.zip` | Stays in the window | The bytes must end up in the window for the browser download, and a large selection would exceed the 16 MiB of a result. |
| Hack Paint filters | Not applicable | Hack Paint has no filter. Rotation and cropping remain: a pass of copies, on a buffer that can be 64 MiB (4096 × 4096 in RGBA), four times the limit of an input. A future filter will go through a task and a temporary file. |

The requested example is a "Background Task" section of
`Apps/Samples/HackerOs.Samples.WindowApp` (prime number count with
progress, cancellation and Kill button), rather than a new app: the sample
window is already the one that shows the SDK of a windowed app.

## Consequences

- Documentation: [`background-jobs.md`](../../framework/background-jobs.md) (new),
  [`worker-processes.md`](../worker-processes.md),
  [`sdk-developer-guide.md`](../../framework/sdk-developer-guide.md),
  [`sdk-reference.md`](../../framework/sdk-reference.md), [`code-editor.md`](../code-editor.md),
  [`file-explorer.md`](../file-explorer.md), [`hack-paint.md`](../hack-paint.md).
- Public additions in `PublicAPI.Unshipped.txt` of the SDK, of
  `HackerOs.Platform.Core` and of `HackerOs.Platform.WorkerHost`; published with
  SDK 1.2 in `exec/10` (ADR 0132).
- `exec/4` wires up the `nice` scheduling of tasks and the `Nice` line.
- Two questions for Benoit (grace period when the app closes, PID in the
  progress); the defaults of this ADR apply in the meantime.
