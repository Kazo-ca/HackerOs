# ADR 0043: System.IO-style `Hack*` file API and ambient execution context

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 1, Files ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 5.1, decisions D2 to D5).
- **Replaces:** no ADR. **Complements:** ADR 0008 (the engine remains the only implementation; this API is a facade on top of it).

## Context

Writing a string to a file currently takes four steps: `CreateAsync`
with mode bits, `StatAsync` to get the revision, `WriteAsync` with the
expected revision, and a hand-written `IFileSystemContentSource` class (see
`Apps/System/HackerOs.Apps.TextEditor/TextEditorWindow.razor`). Each call also
requires carrying the app's `IAppExecutionContext` all the way to the
`IAppFileSystemGateway` gateway.

The engine (`IFileSystemService`, optimistic revisions, transactions, mounts) is
solid and stays in place (D2). What is missing is a facade familiar to a
.NET developer: the same shape as `System.IO`, static, asynchronous, with exceptions.

## Decision

### 1. Location and dependencies

- Project: `Shared/HackerOs.AppSdk` (no Blazor dependency, usable by
  window, terminal and service apps).
- Folder `Shared/HackerOs.AppSdk/IO/`, public namespace **`HackerOs.AppSdk.IO`**.
- The ambient context lives in `Shared/HackerOs.AppSdk/HackExecutionContext.cs`,
  namespace **`HackerOs.AppSdk`**.
- The facade calls **only** `IAppFileSystemGateway` and
  `IAppFileSystemWatchGateway` of the current context. It references neither
  `HackerOs.Platform.Core` nor `IFileSystemService`. Capabilities, authority,
  mode bits, elevation tickets (ADR 0042) and the permission error
  notification (`IAppPermissionErrorGateway`) are therefore enforced
  exactly as today.

### 2. Ambient execution context

```csharp
namespace HackerOs.AppSdk;

public static class HackExecutionContext
{
    // Context of the app running the current code. Throws
    // HackExecutionContextMissingException if there is none.
    public static IAppExecutionContext Current { get; }

    // Same, but null instead of an exception.
    public static IAppExecutionContext? CurrentOrNull { get; }

    // Current directory of the running code (absolute canonical path).
    public static string WorkingDirectory { get; }

    // Sets the context for the duration of the scope (Dispose restores the previous one).
    public static IDisposable Enter(IAppExecutionContext context, string? workingDirectory = null);
}

public sealed class HackExecutionContextMissingException : InvalidOperationException;
```

- Storage: a `static readonly AsyncLocal<HackAmbientFrame?>` field, where
  `HackAmbientFrame` is an internal `record` `(IAppExecutionContext App, string
  WorkingDirectory)`. The `AsyncLocal` follows `await`, `Task.Run` and
  `System.Threading.Timer`, since these capture the `ExecutionContext`.
- A null `workingDirectory` means the home folder `/home/{UserId}` is used.
- **No context:** an explicit error
  (`HackExecutionContextMissingException`). It **never** falls back to a
  system or anonymous context.
- **Why `Enter` is public:** authority comes from the gateways, which only
  the platform factory (`AppExecutionContextFactory`) builds. An app
  that built its own `IAppExecutionContext` would only get its
  own fake gateways, with no rights at all. A public `Enter` is therefore
  safe, and it makes the facade testable without `InternalsVisibleTo`.

**Entry points where the OS sets the context** (exhaustive list to
implement):

| App type | Where | Current directory |
|---|---|---|
| Terminal | `AppLifecycleOrchestrator`, around `app.ExecuteAsync(terminalContext, ...)` | `TerminalExecutionContext.WorkingDirectory` |
| Service | `ServiceAppBase.RunAsync` and `ServiceAppBase.StopAsync` (scope opened in the base class, with the received context) | home folder |
| Window | `WindowAppBase` (`Shared/HackerOs.AppSdk.Blazor`): `SetParametersAsync` (already sealed), and explicit reimplementation of `IHandleEvent.HandleEventAsync` and `IHandleAfterRender.OnAfterRenderAsync`, each wrapped in `Enter(AppContext)` | home folder |
| Child components | new base class `HackComponentBase : ComponentBase` in `HackerOs.AppSdk.Blazor`, with `[CascadingParameter] IAppExecutionContext`, which wraps its events and lifecycle the same way. `WindowAppRenderer` (`Platform/HackerOs.Platform.Blazor/Windows/`) surrounds the app with a `CascadingValue` (`IsFixed="true"`) carrying its context. | the parent's |
| Gateway callbacks | `AppClockGateway.Schedule`, `AppEventGateway.Subscribe` (both paths), `AppFileSystemWatchGateway`, `AppNotificationGateway` actions: each gateway, bound to a single instance, wraps the handler call in `Enter` with its own context | home folder |

JavaScript callbacks (`[JSInvokable]`) do not carry the `AsyncLocal`. An app's
`[JSInvokable]` method must call
`HackExecutionContext.Enter(AppContext)` itself. This is documented in the SDK.

The phase 1 current directory is the terminal's or the home folder.
Phase 3 (D13) will replace it with the process's current directory, without
changing this API.

### 3. Public types

All asynchronous (D4): no synchronous I/O method, since IndexedDB is
asynchronous and the browser has only one thread. Paths are `string`s,
as in `System.IO`. Relative paths and `~` are resolved by
`HackPath.GetFullPath`.

| Type | .NET equivalent | Members |
|---|---|---|
| `static class HackFile` | `File` | `ReadAllTextAsync(path, Encoding? = null, ct)`, `ReadAllLinesAsync`, `ReadAllBytesAsync`, `WriteAllTextAsync(path, contents, Encoding? = null, ct)`, `WriteAllLinesAsync`, `WriteAllBytesAsync`, `AppendAllTextAsync`, `AppendAllLinesAsync`, `ExistsAsync`, `CopyAsync(src, dest, overwrite = false)`, `MoveAsync(src, dest, overwrite = false)`, `DeleteAsync`, `OpenReadAsync` → `HackFileStream`, `OpenWriteAsync` → `HackFileStream` (replaces), `OpenAppendAsync` → `HackFileStream`, `GetUnixFileModeAsync`, `SetUnixFileModeAsync(path, UnixFileMode)`. Each write also accepts a `HackWriteOptions` overload. |
| `static class HackDirectory` | `Directory` | `CreateDirectoryAsync` (creates missing parents, like .NET), `ExistsAsync`, `GetFilesAsync(path, searchPattern = "*", SearchOption = TopDirectoryOnly)`, `GetDirectoriesAsync(...)`, `GetFileSystemEntriesAsync(...)`, `EnumerateFileSystemEntriesAsync(...)` → `IAsyncEnumerable<string>`, `MoveAsync`, `DeleteAsync(path, recursive = false)` |
| `static class HackPath` | `Path` | Synchronous and pure: `Combine`, `Join`, `GetFileName`, `GetFileNameWithoutExtension`, `GetExtension`, `ChangeExtension`, `GetDirectoryName`, `IsPathRooted`, `DirectorySeparatorChar = '/'`, `GetFullPath(path)` (reads the ambient context for `~` and the current directory), `GetFullPath(path, basePath)` (pure) |
| `abstract class HackFileSystemInfo` | `FileSystemInfo` | `FullName`, `Name`, `Extension`, `Exists`, `CreationTimeUtc`, `LastWriteTimeUtc`, `UnixFileMode`, `Owner`, `Group`, `Revision` (the engine's optimistic revision), `IsSymbolicLink`, `LinkTarget`, `RefreshAsync()` |
| `sealed class HackFileInfo` | `FileInfo` | + `Length`, `Directory`, `DirectoryName`, `OpenReadAsync`, `OpenWriteAsync`, `CopyToAsync`, `MoveToAsync`, `DeleteAsync`; `static ValueTask<HackFileInfo> GetAsync(path)` (initial snapshot) |
| `sealed class HackDirectoryInfo` | `DirectoryInfo` | + `Parent`, `CreateAsync`, `GetFilesAsync`, `GetDirectoriesAsync`, `GetFileSystemInfosAsync`, `MoveToAsync`, `DeleteAsync(recursive)`; `static ValueTask<HackDirectoryInfo> GetAsync(path)` |
| `sealed class HackFileStream : Stream` | `FileStream` | Read: wraps the stream of `FileSystemContentReadHandle`. Write: in-memory buffer, published by **a single** atomic `WriteAsync` in `FlushAsync()` or `DisposeAsync()`. |
| `sealed class HackFileSystemWatcher : IAsyncDisposable` | `FileSystemWatcher` | `Path`, `Filter`, `IncludeSubdirectories`, `StartAsync()`, events `Created`, `Changed`, `Deleted`, `Renamed` |
| `sealed record HackWriteOptions` | n/a | Advanced API: `long? ExpectedRevision` (strict conflict control, for the text editor), `UnixFileMode? CreateMode` (bits of the created file) |

Permissions use the BCL enumeration `System.IO.UnixFileMode`. No custom
type is invented.

### 4. Creation, umask and revisions

- **Fixed phase 1 umask: `022`.** File created as `0644`, folder as `0755`,
  unless `HackWriteOptions.CreateMode` says otherwise. Phase 3 may make the
  umask specific to each process.
- **`WriteAllText`/`WriteAllBytes`/`OpenWrite`:** `StatAsync`. If the file
  does not exist, `CreateAsync` then `WriteAsync`. If it exists, `WriteAsync` with
  `expectedRevision` equal to the revision read. On `RevisionConflict`, the
  revision is re-read and the call is retried **only once**. A second conflict throws
  `HackFileConflictException`.
- **`AppendAll*`:** read, concatenate, then write with the revision read,
  with the same single-retry rule. The engine has no native append;
  the deduplicated block storage only rewrites the last block.
- **With `HackWriteOptions.ExpectedRevision`:** no retry. A conflict
  throws `HackFileConflictException` immediately.
- **Encoding:** UTF-8 without BOM by default. The published
  `FileSystemContentDescriptor` is UTF-8 text for the text APIs and binary for the byte APIs.
- **`CopyAsync`/`MoveAsync` across two mounts** (`CrossMountOperation`): the
  facade copies the stream then deletes the source (for a move), like
  `File.Move` between volumes in .NET.
- `HackFile.DeleteAsync` deletes **permanently**. The trash arrives in
  phase 3 (D15) with `HackTrash`. It will not change this method.

### 5. Exceptions (D5)

Hierarchy, in `HackerOs.AppSdk.IO`:

```text
System.IO.IOException
└── HackIOException                     (+ FileSystemErrorCode? ErrorCode, string? Path)
    ├── HackFileNotFoundException
    ├── HackDirectoryNotFoundException
    └── HackFileConflictException       (+ long? CurrentRevision)
System.UnauthorizedAccessException
└── HackUnauthorizedAccessException     (+ ErrorCode, Path, string? RequiredCapability)
```

Since they derive from BCL types, an ordinary `catch (IOException)` works.

Translation of `FileSystemErrorCode` (internal class
`HackFileSystemErrorTranslator`):

| Code | Exception |
|---|---|
| `NotFound` (file operation) | `HackFileNotFoundException` |
| `NotFound` (folder operation, or missing parent during creation) | `HackDirectoryNotFoundException` |
| `AlreadyExists`, `DirectoryNotEmpty`, `NotDirectory`, `SymbolicLinkLoop`, `SymbolicLinkLimitExceeded`, `InvalidContent`, `ProviderUnavailable` | `HackIOException` |
| `NotFile` (reading or writing a folder, like .NET) | `HackUnauthorizedAccessException` |
| `PermissionDenied`, `CapabilityDenied`, `AuthorityDenied`, `ProtectedEntry` | `HackUnauthorizedAccessException` |
| `RevisionConflict` | `HackFileConflictException` (after the retry rule) |
| `InvalidPath`, `InvalidName`, `RootContainmentViolation` | `ArgumentException` (BCL, like .NET) |
| `UnsupportedOperation` | `NotSupportedException` (BCL) |
| `Cancelled` | `OperationCanceledException` (BCL) |
| `CrossMountOperation` | none: handled by the facade (section 4) |

An unknown code added later yields `HackIOException`.

### 6. Contract tests

In `Tests/HackerOs.AppSdk.Tests/IO/`, with a fake `IAppExecutionContext` and
the in-memory file system of `HackerOs.Tests.Support`:

- every public method and every row of the translation table;
- the single retry on conflict;
- the umask;
- the missing context that throws an exception;
- propagation of the `AsyncLocal` across `await`;
- restoration of the previous context on `Dispose`.

## Consequences

- Writing a file fits on one line: `await HackFile.WriteAllTextAsync("~/notes.txt", text);`.
- All app code must run under an `Enter` scope. An omission in an entry
  point is seen right away (`HackExecutionContextMissingException`) instead
  of running with the wrong rights.
- Existing apps keep working with `AppContext.FileSystem`. Migrating the
  base apps to `Hack*` happens in phase 4.
- The facade adds a `StatAsync` before each write. This is acceptable: the
  gateway was already called several times per write in the current apps.
- A writing `HackFileStream` keeps the content in memory until
  publication, because the browser has no temporary file. A very large
  file written as a stream therefore costs its size in memory.

## References

- ADR 0008: virtual file system model.
- ADR 0042: `sudo` and administrator approval prompt.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 4 (D2 to D5) and 5.1.
- `Shared/HackerOs.Simulation.Abstractions/Gateways/AppGatewayContracts.cs` (`IAppFileSystemGateway`).
- `Shared/HackerOs.Simulation.Abstractions/FileSystem/FileSystemResults.cs` (`FileSystemErrorCode`).
