# ADR 0128: heavy background tasks (`HackBackground.RunAsync`)

## Status

Accepted on 2026-10-18.

- **Plan:** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  decision 7, step 1 (Benoit's answer Q5: "the equivalent of `Invoke` in
  WinForms, but the other way around"); implemented by `exec/5-background-jobs`,
  after `exec/1-remote-gateways`.
- **Complements:** ADR 0070 §1 (window apps stay on the main
  thread, but their computations can leave it), ADR 0124 (assembly loading
  and serializable payloads).

## Context

A window app runs on the main thread, because a worker has no
DOM. A heavy loop (image filter, project search, compression)
therefore freezes the whole desktop there. WinForms handles the opposite case with
`Control.Invoke`: a worker thread asks the interface thread to
run a delegate. Benoit wants the opposite: the interface asks
"another thread" to run the heavy load.

A worker is another .NET runtime with its own memory: a lambda that
captures window objects cannot be sent there. The task must therefore
be nameable (a static method or a class) and its data
serializable.

## Decision

### 1. API (SDK 1.2 addition, ADR 0132)

```csharp
namespace HackerOs.AppSdk.Threading;

public static class HackBackground
{
    // Méthode statique : Compress(input, context) dans l'assembly de l'app.
    public static Task<TResult> RunAsync<TInput, TResult>(
        Func<TInput, HackBackgroundContext, Task<TResult>> job,
        TInput input,
        IProgress<HackBackgroundProgress>? progress = null,
        HackBackgroundOptions? options = null,
        CancellationToken cancellationToken = default);

    // Classe de tâche : une instance est créée dans le worker.
    public static Task<TResult> RunAsync<TJob, TInput, TResult>(
        TInput input, /* mêmes paramètres */)
        where TJob : HackBackgroundJob<TInput, TResult>, new();
}
```

- Variants without a result and synchronous ones (`Func<TInput, HackBackgroundContext,
  TResult>`), same shape.
- `HackBackgroundContext`: `CancellationToken`, `Report(fraction, message)`,
  `CheckpointAsync()`, `ProcessId`. The rest of the SDK (`HackFile`, `HackProcess`,
  `HackEnvironment`…) works in the task through the remote gateways.
- `HackBackgroundOptions`: `Name` (displayed in `ps`), `Nice` (ADR 0127).
- Usage:

  ```csharp
  byte[] blurred = await HackBackground.RunAsync(ImageJobs.Blur, new BlurInput(pixels, radius),
      progress, cancellationToken: ct);
  ```

### 2. Rules checked at build time

A Roslyn analyzer shipped with `HackerOs.AppSdk` (codes `HOSBG0001` and
following, chosen by the work item) rejects:

- a delegate that is not a **static method group** (lambda, local method
  that captures, instance method);
- a `TInput` or `TResult` absent from the `JsonSerializerContext` declared by
  `[assembly: HackWireContext(…)]` (ADR 0124 §2);
- a method that is not in the calling app's assembly.

At runtime, the OS redoes the check (a call through reflection does not
go through the analyzer) and throws `ArgumentException` on a mismatch.

### 3. Process model

- Each call creates a **child process** of the calling app: same
  user, same session, same granted capabilities, copied environment,
  name `<app command>:<Name or method>`. It appears in `ps`, `top` and
  the task manager under the app.
- It runs in the **pool** (ADR 0071); the queue follows `nice` (ADR 0127). At
  most `MaxConcurrentJobsPerApp` (number of virtual cores by default) tasks
  of the same app in parallel; the following ones wait.
- It follows the stop of ADR 0069: token cancellation through `cancellationToken`,
  `SIGTERM` (reason `DependencyStop`) when the app closes, `SIGKILL` through
  `kill` or the task manager **without closing the window**.
- It is subject to the limits (ADR 0125) and to the OOM killer (ADR 0126) like
  any process; in a worker, these limits are strict.

### 4. Result and errors

- The task returns the deserialized result; progress is delivered in the
  caller's synchronization context, at most 10 times per second.
- Cancellation: `OperationCanceledException`. Killed task:
  `HackProcessKilledException` (reason `Killed`, `OutOfMemory`,
  `CpuLimitExceeded`…). Task exception: `HackBackgroundJobException`
  with the original type, message and stack (the object does not cross).
- Input or result of more than 16 MiB (the maximum file size,
  ADR 0094): refused. Beyond 1 MiB, the guide recommends passing a
  path and reading the file in the task.

### 5. Without a worker: same contract on the main thread

When there is no worker (tests without a local loop, browser without
Web Worker, host that turns them off), the task runs on the main thread, as a
child process, **with the same serialization** of the input and result:
an app that works in tests works in a worker. `/proc/<pid>/status`
indicates `Host: MainThread`. Calling `context.CheckpointAsync()` there keeps the
desktop alive.

### 6. From a process already in a worker

A command or service in a worker can also call `RunAsync`: the
request goes through the `bg.run` operation to the OS, which places the task as
above. This is the only child launch allowed from a worker
(`StartChild` remains refused, ADR 0124 §5).

## Consequences

- New analyzer library in the `HackerOs.AppSdk` package,
  tested like the ASP.Hack route generator (ADR 0082).
- Compiled sample in `Apps/Samples` (a window that launches a long computation
  with progress, cancellation and `kill`).
- Migration of the base apps: CodeEditor search, explorer compression and copies,
  Hack Paint filters; each goes through
  `HackBackground.RunAsync` or keeps its place with the reason written.
- Docs: new `background-jobs.md`, [`sdk-developer-guide.md`](../../framework/sdk-developer-guide.md),
  [`sdk-reference.md`](../../framework/sdk-reference.md),
  [`worker-processes.md`](../worker-processes.md).

## References

- ADR 0069, 0070, 0071, 0082, 0094, 0124, 0125, 0126, 0127, 0132.
- `Control.Invoke` (WinForms), `Worker` (HTML), `Task.Run`.
