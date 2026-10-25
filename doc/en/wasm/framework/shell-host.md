> [Version française](../../../fr/wasm/framework/shell-host.md)

# Shell host: `HackShell` and the `shell.host` capability

## Purpose

A terminal emulator (the Terminal window, the code editor's terminal panel,
or a third-party terminal) hosts an `hsh` shell through the public SDK,
without touching `ShellInterpreter`, `ISessionService` or
`IElevationService` (ADR 0055). This is section 7 of ADR 0056, delivered by the
work item `rebuild/4-sdk-shell-host`.

As on Linux, `gnome-terminal` launches `bash` then passes it the lines:

```text
Terminal (fenêtre, PID 12)
 └─ hsh (PID 13)            ← HackShell.StartAsync, un par panneau ou onglet
     ├─ ls (PID 14)          ← chaque commande, enfant du shell
     └─ grep (PID 15)
```

## Usage

```csharp
await using IHackShell shell = await HackShell.StartAsync(new HackShellOptions
{
    WorkingDirectory = "~/projets",       // facultatif ; absent ou illisible : répertoire de l'appelant
    CredentialPrompt = myTerminalPrompt,  // facultatif ; sans lui, sudo échoue
});

int status = await shell.ExecuteAsync("ls ~ | grep notes", stdin: null, stdout, stderr, ct);
HackShellCompletion completion = await shell.CompleteAsync("ec", cursor: 2);
string line = completion.ApplyTo("ec", out int cursor);   // "echo "
```

A Razor component that receives its app's context as a parameter uses
`HackShell.StartAsync(AppContext, options, ct)`.

### `IHackShell`

| Member | Role |
|---|---|
| `ProcessId` | PID of the `hsh` process (`$$`). |
| `WorkingDirectory` | Current directory of the shell (changed by `cd`). |
| `ExitStatus` | `$?` of the last pipeline. |
| `Prompt` | `user@machine:directory$` prompt (`#` for root), without the trailing space. |
| `HasExited` | The process has ended (shell released, or killed with its host). |
| `History`, `AddHistory`, `NavigateHistory` | Shell history (also exposed to commands through `IShellSession.History`); ↑ and ↓ like `readline`. |
| `GetInstalledCommands()` | Installed commands, sorted (what `help` lists). |
| `ExecuteAsync(line, stdin, stdout, stderr, ct)` | Runs a line; output arrives as it is produced. |
| `ExecuteAsync(line, HackShellExecution, ct)` | Same with the alternate screen (`FullScreen`) for `nano`. |
| `CompleteAsync(line, cursor, ct)` | Tab completion (see below). |
| `NeedsMoreInput(text)` | ADR 0064: is the command unfinished (`if` without `fi`, open quote, trailing `\|`...)? The terminal then displays the `>` prompt ([`terminal-emulator.md`](../dev/terminal-emulator.md)). |
| `ExecuteAsync(…, HackShellExecution)`: `StandardInput` | ADR 0064: the standard input that the terminal gives to the line (`TerminalInputReader`). |
| `DisposeAsync()` | Terminal closing: kills commands still running, then ends `hsh` with `$?`. The `exit [n]` command does the same from the shell (`HasExited` becomes true, [`terminal-console.md`](../dev/terminal-console.md)). |

Only one line runs at a time; a second one waits for the end of the
first, like a shell reading its terminal. Ctrl+C cancels the token passed to
`ExecuteAsync` (code `130`). After `DisposeAsync`, `ExecuteAsync` throws
`ObjectDisposedException`.

`stdin` feeds the first non-redirected command of each pipeline
(`ShellExecutionOptions.StandardInput`). A command that has read it is not
rerun by the one-time approval of ADR 0042: its input is consumed.

### Completion

`CompleteAsync` does completion on the shell side, like `bash` (ADR 0058,
section 4):

- first word of a command (start of line, after `|`, `&&` or `;`): names of
  installed commands and their static aliases, case-sensitive;
- other words (and a first word containing `/`): paths, relative to the
  shell's directory, `~` expanded, folders ending with `/`, hidden ones after a
  typed `.`, special characters escaped with `\`; an unreadable folder
  offers nothing (work item `4-terminal`, see
  [`terminal-console.md`](../dev/terminal-console.md)).

`HackShellCompletion` gives `WordStart`, `WordLength`, `Candidates` (sorted,
a folder ends with `/`) and `CommonPrefix`. `ApplyTo` applies the Tab rule:
a single candidate is inserted followed by a space (none after a `/`),
several insert their common prefix. `TerminalPanel` displays the list on the
second Tab on the same line, with `DisplayCandidates` (last segment of
each path).

### `HackCredentialPrompt`

The questions the shell asks its terminal:

| Method | Used for | Default |
|---|---|---|
| `ReadCredentialAsync(HackCredentialRequest)` | `sudo`, one-time approval (ADR 0042), an administrator's credentials for dangerous capabilities | abstract |
| `ConfirmAsync(HackConfirmationRequest)` | Yes / No / Cancel of text-mode approval (ADR 0049 §5) | `Cancel` |
| `WriteLineAsync(text, HackPromptLineKind)` | warning banner, "Sorry, try again." | ignored |

The terminal **reads**; the OS **verifies**. `ShellHostPrompts` (Platform Core)
passes the credentials to `IElevationService` (`SudoRoot` or
`CapabilityApproval` token) or to `DangerousCapabilityApprovalRequest.ApproveAsync`,
which writes `/etc/hackeros/app-permissions.config` and `/var/log/auth.log`.
`HackCredential.ToString()` masks the password.

`TerminalCredentialPrompt` (`HackerOs.AppSdk.Terminal`) is the
`TerminalPanel` implementation: the platform's credentials box and Yes / No / Cancel box
(`IDialogService`), lines written in the terminal.

## `shell.host` capability

| Capability | Classification | Risk displayed |
|---|---|---|
| `shell.host` | **dangerous** | "Run commands on your behalf, with the rights of those commands." |

A command launched by the shell runs with **its** capabilities and without a
window: an app that only had `notifications.post` could otherwise read
all of `~` through `cat`. Without `shell.host`, `HackShell.StartAsync` throws
`HackUnauthorizedAccessException` (`RequiredCapability = "shell.host"`) and
no process is created.

Terminal and CodeEditor declare it; both are system apps
(`IsSystemApp="true"` on their `ProjectReference` in
`HackerOs.Ecosystem.csproj`, ADR 0048), so they receive it without administrator
approval.

## Architecture

| Type | Project | Role |
|---|---|---|
| `HackShell` | `Shared/HackerOs.AppSdk/Shell` | Facade; translates the capability refusal into `HackUnauthorizedAccessException`. |
| `IHackShell`, `HackShellOptions`, `HackShellExecution`, `HackShellCompletion` | same | Public contracts. |
| `HackCredentialPrompt` and its types | same | Questions to the terminal. |
| `IAppShellHostGateway` | same | `IAppExecutionContext.ShellHost` gateway (default: "not supported"). |
| `AppShellHostGateway` | `Platform/HackerOs.Platform.Core/Execution` | Checks `shell.host`, then calls the service. |
| `ShellHostService` | `Platform/HackerOs.Platform.Core/Shell` | Creates the `hsh` process, its context and its `ShellSession`; implements `IHackShell`. |
| `ShellHostPrompts` | same | Adapts `HackCredentialPrompt` to the `ShellInterpreter` prompts and to `IDangerousCapabilityApprovalPrompt`. |
| `ShellCompletion` | same | Command name completion. |
| `TerminalPanel`, `TerminalCredentialPrompt` | `Shared/HackerOs.AppSdk.Terminal` | Terminal emulator on the SDK alone. |

### Starting a shell

1. `AppShellHostGateway` checks `shell.host` through the app's
   capability checker.
2. `ShellHostService` starts an `org.hackeros.hsh` process (`argv[0]` =
   `hsh`, name `hsh` in `/proc/<pid>/status`), child of the app's process.
   The process manager copies the parent's directory and environment to it
   (ADR 0050).
3. The context factory creates the shell's context: manifest
   `ShellHostService.ShellManifest` (outside the catalog), **capabilities of the host
   app** (a redirection opens with the terminal's rights, as a
   `fork` keeps the parent's rights), same root elevation if the host was
   launched by `sudo`.
4. A `ShellSession` is created on this context (`TERM`, `PWD`), then the shell
   moves into `WorkingDirectory` with its own rights; on failure
   it stays in the inherited directory.

Each line runs under the shell's ambient context: `sudo` and the
commands have the shell as parent. The line's token is bound to that of the
`hsh` process: killing the shell (or its terminal) stops the line.

### Commands in Web Workers (ADR 0070)

The shell itself (`hsh`, its interpreter, its pipes and its redirections)
stays on the main thread. Each external command whose manifest says
`pool` or `dedicated` is launched there by `AppLifecycleOrchestrator` in a Web
Worker, with remote gateways: its standard streams go through messages
and its calls come back to the OS. All the steps of a pipeline are placed
on the same worker (`ExecutionPlacement.BeginGroup`). Built-in commands
(`cd`, `export`, `jobs`, `kill`, `read`...) and those that drive the shell or
the screen (`sudo`, `nano`, `top`, `watch`...) stay on the main thread: `kill`
answers even when a worker is blocked. A killed command (code 137, reason
`Killed` or `Timeout`) causes "Killed" to be written on the error output, like
`bash`. See [`worker-processes.md`](../dev/worker-processes.md).

### End of a shell

`DisposeAsync` first kills the commands still running (reason
`DependencyStop`, like a `SIGHUP`), then ends `hsh` with `$?` as exit
code. `AppLifecycleOrchestrator` now accepts that a command has been
killed while it was running: its end record is kept instead of
throwing `KeyNotFoundException`.

## Decisions

- **The shell is its own process**, child of the host app (ADR 0056 §7).
  Before this work item, the shell shared the terminal's process (ADR 0050
  §1): two tabs of the code editor shared their directory. Each
  `TerminalPanel` now has its own. `ExecuteCommandIntent` keeps the detached
  shell or the caller's shell from ADR 0050 §7: it has no terminal.
- **History and prompt in `IHackShell`**, in addition to the members listed by
  the ADR: the history is the shell's (`IShellSession.History` gives it
  to commands), and the prompt depends on the shell's directory.
- **`HackCredentialPrompt` is an abstract class** with two optional
  methods: a minimal terminal only implements credential input.
- **`sudo` makes only one attempt** and writes "Sorry, try again." before
  "sudo: authentication failed", as before this work item.
- **Case-sensitive completion**, like `bash` (the old `TerminalPanel`
  ignored case and took the first candidate).

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellInterpreterTests.HostedShell.cs`:
  refusal without `shell.host`, child process and parent of commands, separate
  directories and `$?`, `WorkingDirectory` and fallback, shell end that kills
  commands, `sudo` through `HackCredentialPrompt`, standard input and history,
  completion (command positions, aliases, case).
- `Tests/HackerOs.App.Abstractions.Tests/AppCapabilitiesTests.cs`: `shell.host`
  dangerous.

## Task list for work item `rebuild/4-sdk-shell-host`

- [x] `shell.host` capability (dangerous) in `AppCapabilities`.
- [x] `HackShell`, `IHackShell`, `HackShellOptions`, `HackShellExecution`, `HackShellCompletion`, `HackCredentialPrompt`, `IAppShellHostGateway` in the SDK.
- [x] `ShellHostService`, `ShellHostPrompts`, `ShellCompletion`, `AppShellHostGateway`; gateway wired by `AppExecutionContextFactory`; registration in the Ecosystem.
- [x] `ShellExecutionOptions.StandardInput`.
- [x] `TerminalPanel` on the SDK alone, `TerminalCredentialPrompt` in place of `TerminalDangerousCapabilityApprovalPrompt`, `Platform.Core` reference removed, exemption removed.
- [x] Terminal and CodeEditor declare `shell.host`; CodeEditor is a system app.
- [x] Orchestrator: a command killed during its execution no longer throws an exception.
- [x] Tests, this document, `app-sdk.md`, `capabilities.md`, `shell-interpreter.md`, `shell-commands.md`, `dangerous-capability-approval.md`, plan and index.
