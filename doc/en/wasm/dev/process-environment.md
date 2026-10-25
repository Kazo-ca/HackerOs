> [Version française](../../../fr/wasm/dev/process-environment.md)

# Process environment

Work item `rebuild/3-process-environment` of phase 3: ADR 0050, sections 5
and 6, clarified by ADR 0053.

## Goal

Each HackerOS process now carries, as on Linux:

- its **arguments** (`argv`, including the command name);
- its **current directory**, a canonical absolute path;
- its **environment**, a `NAME` → value dictionary.

These values are set at creation (copied from the parent, like `fork`), change
only for the process itself, and are visible in
`/proc/<pid>/cmdline`, `environ` and `cwd`. Apps read and
modify them with `HackEnvironment`, modeled on `System.Environment`.

## Architecture

| Element | Location | Role |
|---|---|---|
| `IProcessEnvironmentTable`, `ProcessEnvironmentSnapshot`, `ProcessEnvironmentRules` | `Shared/HackerOs.Simulation.Abstractions/Processes/ProcessEnvironmentContracts.cs` | Per-PID table: reading (copied snapshot), changing the directory and variables of a PID, rules for names (non-empty, no `=` or NUL) and values (no NUL). |
| `ProcessStartRequest.Arguments`, `WorkingDirectory`, `Environment` | `ProcessManagerContracts.cs` | Values requested at creation. |
| `InMemoryProcessEnvironmentTable` | `Platform/HackerOs.Platform.Core/Processes/` | Implementation. `Create` (copy from the parent, then the request's values) and `Remove` are internal: only the process manager calls them. |
| `InMemoryProcessManager.Environments` | same | Fills the entry in `Start`, empties it on any end (`Complete`, `Fault`, `Kill`, killed descendants, timeout exceeded). |
| `LoginEnvironment`, `LoginEnvironmentProvider` | `Processes/LoginEnvironment.cs` | Rules for the login environment and `/etc/hostname`; read once per session (ADR 0053). |
| `AppLaunchRequest.Environment` | `Lifecycle/AppLaunchContracts.cs` | Variables added by the caller. |
| `AppLifecycleOrchestrator` | `Lifecycle/` | Computes `argv` (`Terminal.Name` from the manifest, otherwise the app identifier, then the arguments) and the starting state (below). |
| `IAppEnvironmentGateway` | `Shared/HackerOs.Simulation.Abstractions/Gateways/AppEnvironmentGatewayContracts.cs` | An app's view of **its own** process. No capability required. |
| `AppEnvironmentGateway` | `Platform/HackerOs.Platform.Core/Execution/` | Gateway bound to the app's PID; `DetachedAppEnvironmentGateway` when there is no table (tests). |
| `IAppExecutionContext.Environment` | `Shared/HackerOs.AppSdk/IAppExecutionContext.cs` | Default detached member (home folder, `HOME`), replaced by the trusted factory. |
| `HackEnvironment` | `Shared/HackerOs.AppSdk/HackEnvironment.cs` | Static SDK facade. |
| `HackExecutionContext.WorkingDirectory` | `Shared/HackerOs.AppSdk/HackExecutionContext.cs` | Reads the process directory, unless `Enter(context, dir)` set one and no `SetCurrentDirectoryAsync` has replaced it since. |
| `ProcFileSystemProvider` | `FileSystem/Virtual/` | `cmdline`, `environ`, `cwd` read from the table on each read. |
| `FileSystemSeeder` | `FileSystem/` | Creates `/etc/hostname` (`hackeros`) and `/etc/environment` (comment) if they are absent, `0644`, root. Never rewrites them. |

### Starting state of a process

1. **Without a parent** (desktop, service, command from an `ExecuteCommandIntent` without
   a calling context): login environment,
   the request's `WorkingDirectory` or the home folder, `PWD` equal
   to that directory.
2. **With a parent**: copy of the parent's directory and environment at the
   time of creation; an explicit `WorkingDirectory` replaces the
   directory and updates `PWD`.
3. In both cases, `AppLaunchRequest.Environment` applies last.

Login environment: `HOME`, `USER`, `LOGNAME`, `SHELL=/bin/hsh`,
`PATH=/bin:/usr/bin` (informational), `PWD`, `LANG=en_US.UTF-8`, then the system
locale (`/etc/locale.conf`), then the `NAME=value` lines of
`/etc/environment` (value optionally in single or double quotes),
then the user's locale (`~/.config/locale.conf`). `HOME`, `USER` and
`LOGNAME` cannot be replaced there; the `locale.conf` files only
set `LANG`, `LANGUAGE` and the `LC_*`. Empty lines and `#` lines are
ignored; an invalid or protected line is ignored with a warning in
`/var/log/syslog`.

`LANG`, `LC_MESSAGES` and `LC_ALL` choose the language of the process's
texts (English or French), `LC_TIME`/`LC_NUMERIC` the format of dates and
numbers; `LANG=fr_FR.UTF-8 help` answers in French in an English session
(ADR 0104, [`localization.md`](../framework/localization.md)).

### `/proc`

| File | Content |
|---|---|
| `/proc/<pid>/cmdline` | each argument followed by NUL (`grep\0-i\0two words\0`) |
| `/proc/<pid>/environ` | each `NAME=value` pair followed by NUL, mode `0400` |
| `/proc/<pid>/cwd` | link to the real current directory |

Without an entry in the table (old wiring, test doubles), it falls back to
the app identifier, an empty file and the home folder.

## Usage

```csharp
string[] argv = HackEnvironment.GetCommandLineArgs();      // ["grep", "-i", "motif"]
string? editor = HackEnvironment.GetEnvironmentVariable("EDITOR");
HackEnvironment.SetEnvironmentVariable("EDITOR", "nano");  // null supprime ; "" est une valeur
await HackEnvironment.SetCurrentDirectoryAsync("Documents"); // relatif, absolu ou ~
string full = HackPath.GetFullPath("notes.txt");           // résolu dans le nouveau répertoire
int pid = HackEnvironment.ProcessId;
string user = HackEnvironment.UserName;                     // nom de connexion
string host = HackEnvironment.MachineName;                  // /etc/hostname
```

All operations target the current process. A command that does
`export` or `cd` does not change the shell that launched it: that is the role of
controlled access to the shell (ADR 0051, `IShellSession`, see
[`shell-commands.md`](shell-commands.md)).

Since ADR 0064, the shell also distinguishes **unexported variables**
(`NAME=value` without `export`): they live in `ShellSession`, not in
the environment of the `hsh` process, and so are neither in
`/proc/<pid>/environ` nor inherited by commands. `export NAME` puts them
there; `NAME=value command` passes `NAME` only to that command
([`shell-interpreter.md`](shell-interpreter.md#variables)).

`TerminalExecutionContext.WorkingDirectory` and `Environment` remain, for
compatibility: they give the process state at the start of the command
(the whole environment, no longer just `HOME`).

The terminal prompt displays the machine name (`alice@hackeros:~$`) read by
`IAppExecutionContext.Environment.MachineName`.

## Key decisions

- **Separate table** rather than fields in `ProcessRecord`, which remains an
  immutable snapshot of the lifecycle (ADR 0050).
- **Copy at creation**: no sharing or live link between parent and child.
- **`SetCurrentDirectoryAsync`** instead of a synchronous `set` (ADR 0053).
- **System files read once per session**, as root (ADR 0053).
- **Empty value kept**: unlike `System.Environment` on
  Windows, `""` does not erase a variable, as on Linux.
- **Shell**: since the `3-shell-interpreter` work item, the shell is the
  process of the app that hosts it; each command is its child and inherits
  its directory (updated by `cd`) and its environment. See
  [`shell-interpreter.md`](shell-interpreter.md).

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Processes/ProcessEnvironmentTests.cs`:
  inheritance, isolation, snapshots, cleanup, validation.
- `Tests/HackerOs.Platform.Core.Tests/Processes/LoginEnvironmentTests.cs`:
  rules for `/etc/environment` and `/etc/hostname`, bootstrap.
- `Tests/HackerOs.Platform.Core.Tests/FileSystem/Virtual/ProcFileSystemProviderTests.cs`:
  `cmdline`, `environ`, `cwd`.
- `Tests/HackerOs.Platform.Core.Tests/Lifecycle/AppLifecycleOrchestratorTests.cs`:
  root and child process end to end, read once per session.
- `Tests/HackerOs.AppSdk.Tests/HackEnvironmentTests.cs`: SDK facade.

## Task list

- [x] Contracts `IProcessEnvironmentTable`, `ProcessEnvironmentSnapshot`, fields of `ProcessStartRequest`.
- [x] `InMemoryProcessEnvironmentTable` and wiring into `InMemoryProcessManager` (creation, end).
- [x] Login environment, `/etc/environment`, `/etc/hostname` and their bootstrap.
- [x] `argv`, directory and environment computed by `AppLifecycleOrchestrator`, `AppLaunchRequest.Environment`.
- [x] `IAppEnvironmentGateway`, `IAppExecutionContext.Environment`, factory gateway.
- [x] `HackEnvironment` and `HackExecutionContext.WorkingDirectory` linked to the process.
- [x] `/proc/<pid>/cmdline`, `environ`, `cwd`.
- [x] Terminal prompt from `/etc/hostname`.
- [x] Wiring of the Ecosystem host.
- [x] Tests and documentation (ADR 0053).
