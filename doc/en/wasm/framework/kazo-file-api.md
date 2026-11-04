> [Version française](../../../fr/wasm/framework/kazo-file-api.md)

# `Kazo*` file API and ambient execution context

Implementation of section 5.1 of the [rebuild plan](../dev/plans/ecosystem-rebuild-plan.md),
following [ADR 0043](../dev/adr/0043-hack-file-api-and-ambient-context.md).

## Goal

Give apps a file API familiar to a .NET developer, modeled on
`System.IO`, without passing a context or handling revisions by hand:

```csharp
using Kazo.Os.AppSdk.IO;

await KazoFile.WriteAllTextAsync("~/notes.txt", "Bonjour");
string texte = await KazoFile.ReadAllTextAsync("~/notes.txt");
string[] logs = await KazoDirectory.GetFilesAsync("/var/log", "*.log");
```

The calling app is identified by the ambient context that the OS sets. All
calls go through the app's `IAppFileSystemGateway` gateway: capabilities,
authority, mode bits, elevation tickets (ADR 0042) and permission error
notification remain applied exactly as before.

## Architecture

| Item | Location | Role |
|---|---|---|
| `KazoExecutionContext` | `Shared/Kazo.Os.AppSdk/KazoExecutionContext.cs` | Ambient context (`AsyncLocal`): `Current`, `CurrentOrNull`, `WorkingDirectory`, `Enter(context, workingDirectory)`. Since phase 3, `WorkingDirectory` is the process's directory (`KazoEnvironment`, [`process-environment.md`](../dev/process-environment.md)), unless a directory is set by `Enter`. Since phase 5, its `AsyncLocal` can carry the resource-measurement context-switch hook, installed at composition by `Kazo.Os.Platform.ResourceAccounting` ([`resource-accounting.md`](../dev/resource-accounting.md)); an open scope prevents installing it. |
| `IAppExecutionContext.HomeDirectory` | `Shared/Kazo.Os.AppSdk/IAppExecutionContext.cs` | Home folder `/home/<login>` (default member `/home/{UserId}`, replaced by the platform). Used for `~` and the default current directory. |
| `IAppExecutionContext.IsRootElevated` | `Shared/Kazo.Os.AppSdk/IAppExecutionContext.cs` | Launch as full root through `sudo` (ADR 0042), default member `false`. Does not change `HomeDirectory`; used for the effective user's trash (`/root/.local/share/Trash`, [`trash.md`](../dev/trash.md)). |
| `KazoFile`, `KazoDirectory`, `KazoPath` | `Shared/Kazo.Os.AppSdk/IO/` | Static facades. `KazoPath` is pure, except `GetFullPath(path)`. |
| `KazoFileSystemInfo`, `KazoFileInfo`, `KazoDirectoryInfo` | `IO/KazoFileSystemInfo.cs` | Metadata snapshots (`GetAsync`, `RefreshAsync`). |
| `KazoFileStream` | `IO/KazoFileStream.cs` | Read stream (wraps the engine's read) or write stream (memory buffer published in a single atomic write). |
| `KazoFileSystemWatcher` | `IO/KazoFileSystemWatcher.cs` | `Created`, `Changed`, `Deleted`, `Renamed` events on top of `IAppFileSystemWatchGateway`. |
| `KazoWriteOptions` | `IO/KazoWriteOptions.cs` | Advanced API: `ExpectedRevision`, `CreateMode`. |
| `Kazo*` exceptions | `IO/KazoExceptions.cs` | `KazoIOException` and its derived classes, `KazoUnauthorizedAccessException`. |
| `KazoFileSystemErrorTranslator` (internal) | `IO/KazoFileSystemErrorTranslator.cs` | `FileSystemErrorCode` → exception translation table. |
| `KazoFileSystemCore` (internal) | `IO/KazoFileSystemCore.cs` | Common plumbing: stat, read, publishing with retry, copy across mounts, enumeration. |

### Entry points where the OS opens the context

| Kind of code | Where | Current directory |
|---|---|---|
| Terminal command | `AppLifecycleOrchestrator`, around `ExecuteAsync` | that of the command's process (copied from the shell at creation) |
| Service | `ServiceAppBase.RunAsync` and `StopAsync` | home folder |
| Window | `WindowAppBase`: `SetParametersAsync`, `IHandleEvent.HandleEventAsync`, `IHandleAfterRender.OnAfterRenderAsync` | home folder |
| Child component | `KazoComponentBase` (new, `Kazo.Os.AppSdk.Blazor`) receives the context through the `CascadingValue` set by `WindowAppRenderer` | that of the window |
| Gateway callbacks | `AppClockGateway.Schedule` and `AppEventGateway.Subscribe` (kernel path and topic path) enter their app's context (`AppAmbientContextBinding`) | home folder |

The ADR's other gateways have no callback to wrap:
`IAppFileSystemWatchGateway` returns a channel read by the app's code (the
`KazoFileSystemWatcher` reads it in a loop that inherits the context of
`StartAsync`), and notification actions come back to the app through the event
bus, already covered.

A `[JSInvokable]` method does not receive the `AsyncLocal`: it calls
`KazoExecutionContext.Enter(AppContext)` itself. Without a context, the API throws
`KazoExecutionContextMissingException`; it never falls back to a system
identity.

## Usage

### Paths

- Absolute (`/etc/hosts`), relative to the current directory (`notes.txt`, `../x`),
  or in the home folder (`~`, `~/docs`).
- `KazoPath.GetFullPath(path, basePath)` resolves without reading the context.
- Listings return canonical absolute paths, sorted by name (ordinal),
  in depth for `SearchOption.AllDirectories`, without following symbolic
  links to folders. Patterns (`*`, `?`) are case-sensitive.

### Writing, umask and revisions

- Fixed phase 1 umask: file created as `0644`, folder as `0755`
  (`KazoWriteOptions.CreateMode` or `CreateDirectoryAsync(path, mode)` for anything
  else). Only the `0777` bits are accepted.
- A write reads the current revision, creates the file if it is missing, writes, and
  retries **only once** on conflict. A second conflict throws
  `KazoFileConflictException`.
- Appending (`AppendAll*`, `OpenAppendAsync`) rereads the content on each attempt: a
  concurrent modification is never overwritten.
- Strict control (text editor): `ExpectedRevision` disables the retry.
  The overloads with `KazoWriteOptions` return the validated `KazoFileInfo`,
  whose `Revision` is used for the next write.

```csharp
KazoFileInfo fichier = await KazoFileInfo.GetAsync(chemin);
// ... l'utilisateur modifie ...
try
{
    fichier = await KazoFile.WriteAllTextAsync(chemin, texte,
        new KazoWriteOptions { ExpectedRevision = fichier.Revision });
}
catch (KazoFileConflictException conflit)
{
    // Le fichier a changé ailleurs : conflit.CurrentRevision
}
```

- Text: UTF-8 without BOM by default; an encoding passed explicitly writes its
  preamble (like .NET). Lines end with `\n`. Text files
  are published with the `text/plain` type.
- `CopyAsync`/`MoveAsync` between two mounts: the facade copies the stream (keeping
  the source file's bits) then deletes the source.
- `KazoFile.DeleteAsync` deletes permanently, like `System.IO`. For a
  reversible deletion, `KazoTrash.MoveToTrashAsync` (phase 3, see
  [`trash.md`](../dev/trash.md)).

### Streams

`OpenWriteAsync` **replaces** the content (unlike `File.OpenWrite`, see
the ADR). The content is published by a single atomic write on `FlushAsync()`
or `DisposeAsync()`. Always use `await using`:

```csharp
await using KazoFileStream flux = await KazoFile.OpenWriteAsync("~/rapport.txt");
await using StreamWriter writer = new(flux);
await writer.WriteLineAsync("ligne");
```

When the OS measures memory (ADR 0068), the buffer of a write stream is
counted in the process's kernel share (`VmData` of `/proc/<pid>/status`)
until the stream is closed, through `IAppFileSystemGateway.ChargeStreamBuffer()`
(a default method that charges nothing: a test gateway has nothing to
do). See [`memory-accounting.md`](../dev/memory-accounting.md).

The synchronous `Flush()` does nothing, and a synchronous `Dispose()` of a stream that
holds unpublished bytes throws `InvalidOperationException` instead of losing
them. The stream keeps the gateway of the app that opened it.

### Total size of a folder (ADR 0185 §5)

`KazoDirectory.GetTotalSizeAsync(path)` (or `KazoDirectoryInfo.GetTotalSizeAsync()`) returns a
`KazoDirectorySize`: the sum of the files' lengths, the number of files and of subfolders, like
`du -sb`, without reading any content. Symbolic links are neither followed nor counted. A subfolder
that cannot be read is skipped and `IsComplete` is then `false`. Under the SDK it is the file service's
`IAppFileSystemGateway.MeasureAsync` (a default member that walks with `StatAsync` and
`EnumerateAsync`, so with the caller's rights; shared algorithm `FileSystemTreeMeasure`). The folder
Properties dialog shows it.

### Watching

```csharp
await using KazoFileSystemWatcher watcher = new("~/inbox", "*.txt");
watcher.Created += (_, e) => Console.WriteLine(e.FullPath);
await watcher.StartAsync();
```

The engine only watches direct children: `IncludeSubdirectories = true`
throws `NotSupportedException`. A move within the folder gives `Renamed`, a
move out gives `Deleted`, a move in gives `Created`.

### Exceptions

| Engine code | Exception |
|---|---|
| `NotFound` (file) | `KazoFileNotFoundException` |
| `NotFound` (folder, or missing parent on creation) | `KazoDirectoryNotFoundException` |
| `NotFile`, `PermissionDenied`, `CapabilityDenied`, `AuthorityDenied`, `ProtectedEntry` | `KazoUnauthorizedAccessException` (with `RequiredCapability`) |
| `RevisionConflict` | `KazoFileConflictException` (after the retry rule) |
| `InvalidPath`, `InvalidName`, `RootContainmentViolation` | `ArgumentException` |
| `UnsupportedOperation` | `NotSupportedException` |
| `Cancelled` | `OperationCanceledException` |
| `CrossMountOperation` | none: copy then delete by the facade |
| Other codes, and any future code | `KazoIOException` |

The exceptions derive from `IOException` and `UnauthorizedAccessException`: an
ordinary `catch` works.

## Key decisions

- **`HomeDirectory` on `IAppExecutionContext`.** The ADR mentions `/home/{UserId}`,
  but `UserId` is an opaque identifier whereas the home folder is
  `/home/<login>`. The context therefore exposes `HomeDirectory`, filled in by the factory
  with `AuthenticatedPrincipal.HomePath`. The `rebuild/1-settings-files` thread adds
  the same member, with the same signature.
- **Info without lazy reading.** `KazoFileInfo`/`KazoDirectoryInfo` read
  nothing implicitly: `GetAsync` or `RefreshAsync`. A missing file reports the
  .NET default values (date 1601-01-01 UTC, `UnixFileMode` at `-1`).
- **Symbolic links.** `IsSymbolicLink` and `LinkTarget` describe the link; the
  other properties describe its target, like `stat`.
- **`File.Move` of a folder** throws `KazoFileNotFoundException`, like .NET;
  `KazoDirectory.MoveAsync` moves a folder or a file.
- **`KazoDirectory.CopyAsync`** (phase 4, `rebuild/4-file-explorer`): recursive
  copy of a folder to a destination that does not exist, a Kazo OS addition (.NET has
  no `Directory.Copy`); the engine copies the tree in one transaction, the facade
  recreates it entry by entry across two mounts. See
  [`file-explorer.md`](../dev/file-explorer.md).
- **Test double.** `FakeAppFileSystemGateway` (`Kazo.Os.Tests.Support`)
  now treats a null expected revision as an unconditional operation
  and returns `NotFile` for reading or writing a folder,
  like the real engine. `MinimalAppExecutionContext` accepts a watch gateway
  and a home folder.

## Tests

- `Tests/Kazo.Os.AppSdk.Tests/IO/`: context (absence, restoration, nesting,
  propagation through `await`, `Task.Run` and `Timer`, services), `KazoPath`, `KazoFile`
  (umask, encoding, single retry, strict revision, streams, copy and
  move across mounts), `KazoDirectory`, infos, each row of the translation
  table, `KazoFileSystemWatcher`.
- `Tests/Kazo.Os.AppSdk.Blazor.Tests/AmbientContextComponentTests.cs`: life
  cycle, events and after-render of a window and of a child `KazoComponentBase`.
- `Tests/Kazo.Os.Platform.Core.Tests`: the factory's `HomeDirectory`, callbacks of
  the clock and event bus, context of a terminal command.

## Task list

- [x] `KazoExecutionContext` and `KazoExecutionContextMissingException`.
- [x] `IAppExecutionContext.HomeDirectory`, filled in by the factory.
- [x] `KazoPath`.
- [x] `Kazo*` exceptions and translation from `FileSystemErrorCode`.
- [x] `KazoFile` (read, write, append, copy, move, delete, streams, permissions).
- [x] `KazoFile.CreateSymbolicLinkAsync(path, target)` (ADR 0064, `ln -s`): like `File.CreateSymbolicLink`, target kept as is, dangling link allowed.
- [x] `KazoDirectory`.
- [x] `KazoFileSystemInfo`, `KazoFileInfo`, `KazoDirectoryInfo`.
- [x] `KazoFileStream`.
- [x] `KazoFileSystemWatcher`.
- [x] Entry points: terminal, service, window, `KazoComponentBase` + `CascadingValue`, clock, event bus.
- [x] Contract tests.
- [ ] Phase 3: current directory per process (D13) and umask per process.
- [ ] Phase 4: migrate the base apps from `AppContext.FileSystem` to `Kazo*`.
