> [Version française](../../../fr/wasm/framework/background-jobs.md)

# Background tasks (`HackBackground`)

Work item `exec/5-background-jobs`, [ADR 0128](../dev/adr/0128-background-jobs-hack-background.md),
[ADR 0140](../dev/adr/0140-background-jobs-implementation-adjustments.md) and
[ADR 0147](../dev/adr/0147-background-task-grace-period-on-app-close.md). Addition to
SDK 1.2, released at the end of the `exec` series (ADR 0132).

## Purpose

A windowed app runs on the browser's main thread, the one that also draws
the desktop. An image filter, a project search or a compression freezes everything
there. `HackBackground.RunAsync` does the opposite of WinForms'
`Control.Invoke`: the window hands the heavy load to another
"thread", here a **child process** of the app, placed in a pool Web Worker
when the host has one, and waits for its result without blocking.

As on Linux, the task is a real process:

- it appears in `ps`, `top` and the task manager under the app, with
  the name `<command>:<task>` (`code-editor:search`, `file-explorer:compress`);
- `kill -9 <pid>` or "End" kills it **without closing the window**;
- when the app closes, it receives a stop request (its token is cancelled)
  and has **5 s** to finish, then it is killed, like `SIGTERM` then `SIGKILL`
  (ADR 0147); a `kill -9` of the app kills it immediately;
- it is subject to process limits (ADR 0125) like the others.

## Usage

### A static method

```csharp
using System.Text.Json.Serialization;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Threading;

[assembly: HackWireContext(typeof(PaintJsonContext))]

public sealed record BlurInput(byte[] Pixels, int Width, int Height, int Radius);

[JsonSerializable(typeof(BlurInput))]
[JsonSerializable(typeof(byte[]))]
internal sealed partial class PaintJsonContext : JsonSerializerContext;

public static class ImageJobs
{
    public static async Task<byte[]> Blur(BlurInput input, HackBackgroundContext context)
    {
        for (int row = 0; row < input.Height; row++)
        {
            // ... une ligne ...
            context.Report((row + 1d) / input.Height, "Blur");
            await context.CheckpointAsync();
        }

        return input.Pixels;
    }
}

// Dans la fenêtre :
byte[] blurred = await HackBackground.RunAsync<BlurInput, byte[]>(
    ImageJobs.Blur, new BlurInput(pixels, width, height, 3),
    new Progress<HackBackgroundProgress>(report => { _progress = report; StateHasChanged(); }),
    new HackBackgroundOptions { Name = "blur" },
    cancellationToken);
```

Accepted forms (`HackerOs.AppSdk.Threading.HackBackground`):

| Form | Task |
|---|---|
| `RunAsync<TInput, TResult>(job, input, …)` | `Func<TInput, HackBackgroundContext, Task<TResult>>` or `Func<…, TResult>` |
| `RunAsync<TInput>(job, input, …)` | `Func<TInput, HackBackgroundContext, Task>` or `Action<TInput, HackBackgroundContext>` |
| `RunAsync<TJob, TInput, TResult>(input, …)` | a class `TJob : HackBackgroundJob<TInput, TResult>, new()` |

Each form takes `IProgress<HackBackgroundProgress>? progress`,
`HackBackgroundOptions? options` (`Name`, `Nice`) and `CancellationToken`.

### The task context

`HackBackgroundContext`:

| Member | Role |
|---|---|
| `ProcessId` | PID of the task's process. |
| `CancellationToken` | Cancelled by the caller's token, by `kill` without `-9`, and when the app closes. |
| `Report(fraction, message)` | Progress from `0` to `1` (clamped); at most 10 reports per second, the latest wins, the one before the end is always delivered. `NaN` throws `ArgumentOutOfRangeException`. |
| `CheckpointAsync()` | Voluntary checkpoint (ADR 0069): throws if the task is cancelled and yields the thread beyond 8 ms. Essential on the main thread, useful in a worker. |

Inside the task, the rest of the SDK (`HackFile`, `HackDirectory`, `HackProcess`,
`HackEnvironment`…) works through the ambient context of the task's process:
same user, same session, same capabilities granted as the app.

### Result and errors

| Case | What the caller sees | Exit code |
|---|---|---|
| Success | The deserialized result | `0` |
| Task exception | `HackBackgroundJobException` (`OriginalTypeName`, `Message`, `OriginalStackTrace`): the object does not cross | `1` |
| Cancellation | `OperationCanceledException` | `130` |
| Killed (`kill -9`, task manager, OOM) | `HackProcessKilledException` with `Reason` | `137` |
| Refused (method not found, undeclared type, other assembly, more than 16 MiB) | `ArgumentException` | `127` |

`HackProcessKilledException` derives from `OperationCanceledException`: put
its `catch` **before** the cancellation one.

### Showing and killing a task from the window

An app sees and kills its own descendants without `process.list` or
`process.manage` (ADR 0140 §7):

```csharp
await AppContext.Processes.KillAsync(ProcessId.FromInt64(pid));
```

The task can give its PID in its progress (`$"pid {context.ProcessId} · …"`),
as the sample window does.

### Testing an app that uses `HackBackground`

Without the OS (test context such as `MinimalAppExecutionContext`), the default
gateway runs the task **inline**, in the test's process, with the
same serialization of the input and the result and the same errors. A test that
passes this way passes identically in a worker.

```csharp
using IDisposable scope = HackExecutionContext.Enter(new MinimalAppExecutionContext(manifest));
PrimeCountResult result = await HackBackground.RunAsync<PrimeCountInput, PrimeCountResult>(
    SampleJobs.CountPrimes, new PrimeCountInput(100_000));
```

## Rules checked at build time

The `HackerOs.Tools.BackgroundAnalyzer` analyzer is shipped in the
`HackerOs.AppSdk` package (`analyzers/dotnet/cs`); the repository's apps receive it through
`build/HackerOs.Apps.targets`. All its rules are errors.

| Code | Refusal |
|---|---|
| `HOSBG0001` | The task is not a static method group: lambda (even `static`), anonymous method, instance method, non-`static` local function, delegate passed through a variable. |
| `HOSBG0002` | The input or result type is not reached from a `[JsonSerializable]` of a context declared by `[assembly: HackWireContext(...)]`. A type reached through an array, a generic argument or a public property of a root is accepted, as `GetTypeInfo` finds it. |
| `HOSBG0003` | The task method or class is in an assembly other than the app's. |
| `HOSBG0004` | The method is generic, or belongs to a generic type. |

The OS redoes the checks at run time (a call through reflection does not pass
the analyzer) and throws `ArgumentException`.

## Architecture

```
Fenêtre (fil principal)                       OS (fil principal)                         Web Worker du pool
HackBackground.RunAsync ──► IAppExecutionContext.Background
   sérialise l'entrée          AppBackgroundGateway ──► BackgroundJobService
   (HackWireContext)                                    vérifie, crée le processus enfant,
                                                        attend une place (par utilisateur et app)
                                                        ├─ pas de worker : la tâche sur le fil principal,
                                                        │  dans son processus (son planificateur)
                                                        └─ WorkerRemoteProcessHost ── start k=2 ──► WorkerProcessHost
                                                                                       ◄── bg.progress ── tâche
                                                                                       ◄── exit jr/jf ───
   ◄── résultat désérialisé, progression postée dans le contexte de la fenêtre
```

- **SDK** (`Shared/HackerOs.AppSdk/Threading`): `HackBackground`,
  `HackBackgroundContext`, `HackBackgroundJob<,>`, `IAppBackgroundGateway`,
  `HackBackgroundWire` (`HackWireContext` contexts, 16 MiB at most),
  `HackBackgroundJobRunner` (binding of a named target), default inline
  gateway.
- **OS** (`Platform/HackerOs.Platform.Core/Execution/BackgroundJobService.cs`):
  child process (kind `Terminal`, `argv[0]` = name), trust context
  of the child through the factory, queue per user and app pair
  (`MaxConcurrentJobsPerApp`, number of virtual cores), `GetJobs` and
  `TryGetJob` for `/proc` and the task manager.
- **Workers** (`Platform/HackerOs.Platform.WorkerHost`): `start` with `k = 2`,
  `bg.progress`, `bg.run` and `bgp` for a task launched from a worker
  ([`worker-processes.md`](../dev/worker-processes.md#hackbackground-tasks-adr-0128-adr-0140)).
- **`/proc/<pid>/status`**: `Name:` carries the task name (15 characters),
  then `Job:` and `Host: MainThread|Worker`.

## Key decisions

- The task is named (static method or class) and its data goes through
  JSON: a worker is another .NET runtime, without the window's objects.
- Without a worker, same contract on the main thread, with the same serialization:
  an app that works in its tests works in a worker.
- One process per task, child of the app: visible, killable on its own. When the
  app closes, a `DependencyStop` stop request then a kill after
  5 s on the simulation clock (`ProcessStartRequest.ParentExitGracePeriod`,
  ADR 0147, which replaces ADR 0140 §6); the app's `kill -9`, a limit or
  the OOM killer kill it immediately.
- The task's PID is not part of the progress (Benoit's answer,
  ADR 0147 §3): the task puts it in its message if the window needs it.
- `nice` is bounded and becomes the `nice` value of the task's process
  (`ps -o ni`, `/proc/<pid>/stat`); it sets its place in the workers' queue
  (ADR 0127, [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md)).

## Base apps

| Processing | Decision |
|---|---|
| Search in the CodeEditor folder | Task `code-editor:search` ([`code-editor.md`](../dev/code-editor.md)). |
| Compress, Extract, "UnZip Here…" of the explorer | Tasks `file-explorer:compress` and `file-explorer:extract` ([`file-explorer.md`](../dev/file-explorer.md)). |
| Copy, move, paste; download as `.zip` | Stay in the window: OS calls awaited with checkpoints, conflict question in the window; download bytes destined for the window (ADR 0140 §9). |
| Hack Paint | No filter; rotation and cropping remain (buffer up to 64 MiB, beyond the 16 MiB limit) ([`hack-paint.md`](../dev/hack-paint.md)). |

## Example

`Apps/Samples/HackerOs.Samples.WindowApp`, "Background Task" section:
counting prime numbers (`SampleJobs.CountPrimes`) with a progress
bar, "Cancel" (cancellation) and "Kill" (kills the task's process;
the window stays open and shows `killed`).

## Tests

- `Tests/HackerOs.AppSdk.Tests/Threading/HackBackgroundTests.cs`: inline gateway,
  lambda refused, undeclared type, fault, cancellation, progress
  throttling.
- `Tests/HackerOs.AppSdk.Tests/Threading/HackBackgroundAnalyzerTests.cs`: the
  four `HOSBG` rules.
- `Tests/HackerOs.Platform.WorkerHost.Tests/BackgroundJobTests.cs`: each case
  on the main thread **and** in a worker (result, child process,
  progress, task class, fault, cancellation, `kill -9` without closing the app,
  the app killing its own task, `kill -9` of the app, app closing with
  exit `130` or kill at 5 s, concurrency limit,
  `/proc/<pid>/status`, `ps`, refused input, other assembly).
- App tests: search as a task (CodeEditor), compression and extraction
  (explorer), the sample's task.

## Task list of work item `exec/5-background-jobs`

- [x] SDK API (`HackBackground`, context, task class, options, exception), default inline gateway.
- [x] `BackgroundJobService`: child process, trust context, queue per user and app, exit codes.
- [x] Workers: `start` `k = 2`, `bg.progress`, `bg.run`, `bgp`, `bgfault` errors and `rs` reason.
- [x] `/proc/<pid>/status` (`Name`, `Job`, `Host`) and `ps`.
- [x] An app manages its descendants without `process.manage` (ADR 0140 §7).
- [x] Analyzer `HOSBG0001` to `HOSBG0004`, shipped in the SDK package and wired to the repository's apps.
- [x] CodeEditor search, explorer compression and extraction as tasks; reason written down for copies, download and Hack Paint.
- [x] Example in the sample window.
- [x] SDK, analyzer, OS and worker tests, apps.
- [x] Documentation, ADR 0140, index.
- [x] 5 s delay when the app closes, Benoit's answers to the two cards (ADR 0147).
