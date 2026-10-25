> [Version française](../../../fr/wasm/dev/shell-commands.md)

# Shell commands and controlled shell access

Phase 3 of the rebuild plan, work item `rebuild/3-shell-commands`
([ADR 0051](adr/0051-unified-commands-project.md), sections 2 to 5). Last work
item of phase 3: it builds on the interpreter
([`shell-interpreter.md`](shell-interpreter.md), ADR 0050 and 0054) and on the
process environment ([`process-environment.md`](process-environment.md)).

> Since [ADR 0064](adr/0064-posix-shell-scripts-jobs-and-app-commands.md),
> a few POSIX **special built-ins** (`break`, `return`, `set`, `local`,
> `eval`, `source`, `wait`, `fg`, `trap`...) live in the interpreter
> ([`shell-interpreter.md`](shell-interpreter.md#special-built-ins)); everything
> else, including `read`, `jobs`, `test` and `which`, stays in the catalog. The
> added Linux commands are described in [`linux-commands.md`](linux-commands.md).

## Goal

The terminal no longer knows **any** command name. `cd`, `pwd`, `clear`,
`help`, `sudo`, and the new commands `export`, `unset` and `env` are catalog
commands like `ls` or `grep`. Those that must act on the shell itself (like
bash built-ins) do so through an SDK interface, `IShellSession`, reserved for
commands that declare the normal capability `shell.control`. A third-party
command can therefore provide its own improved `cd` without touching the
terminal.

```text
alice@hackeros:~$ cd projets && export CIBLE=10.0.0.5 && env | grep CIBLE
CIBLE=10.0.0.5
alice@hackeros:~/projets$ cd -
/home/alice
alice@hackeros:~$ sudo cat /etc/shadow | wc -l
```

## Architecture

| Element | Location | Role |
|---|---|---|
| `IShellSession`, `ShellCommandInfo`, `ShellJobInfo`, `ShellJobState` | `Shared/HackerOs.AppSdk/IShellSession.cs` | Access to the calling shell: directory, environment, history, `ChangeDirectoryAsync`, `SetVariable`, `UnsetVariable`, `GetCommands`, `RunElevatedAsync`; since ADR 0064 `GetVariable` (non-exported variables included), `AssignVariable` (`read`), `RunAsync` (a line in a subshell, `watch`), `Jobs` and `KillJob` (`jobs`, `kill %n`). Default members: an existing implementation still compiles. |
| `TerminalExecutionContext.Shell` | `Shared/HackerOs.AppSdk/` | The shell, or `null`. |
| `AppCapabilities.ShellControl` | `Shared/HackerOs.App.Abstractions/` | `shell.control`, classified **normal** (ADR 0049). |
| `ShellSessionAccess` | `Platform/HackerOs.Platform.Core/Shell/` | Internal implementation of `IShellSession`, created by the interpreter for one step. |
| `ShellSession.Fork()` | same | Copy of the shell for one step of a pipe (subshell). |
| `ShellInterpreter.GetInstalledCommands()` / `RunElevatedAsync` | same | List of installed commands; elevated execution for `sudo`. |
| `ShellExecutionOptions.CredentialPrompt` | same | Credentials window provided by the shell host (the terminal). |
| `AppLaunchRequest.Shell`, `CommandName` | `Platform/HackerOs.Platform.Core/Lifecycle/` | Shell passed to the command; call name that becomes `argv[0]`. |
| `TerminalControlSequenceDecoder` | `Shared/HackerOs.AppSdk.Terminal/` | Interprets the ECMA-48 sequences written by commands (screen clearing). |
| Commands | `Apps/Commands/HackerOs.Commands/{Cd,Pwd,Clear,Help,Sudo,Export,Unset,Env,Alias}/` | Catalog commands. |

### Who receives the shell

The interpreter creates a `ShellSessionAccess` for a step only if the shell is
**interactive** (the terminal) and the command's manifest declares
`shell.control`. The orchestrator then passes it to
`TerminalExecutionContext.Shell` only if the capability is **granted**.

- Command alone in its pipeline (including after `&&`): the real shell.
  `cd /tmp && ls` lists `/tmp`.
- Step of a pipe (`cd /tmp | cat`): a copy (`ShellSession.Fork()`), with its
  own directory and its own environment, like the subshell in which bash then
  runs its built-ins. The command succeeds, its changes are lost at the end of
  the step.
- `ExecuteCommandIntent` (non-interactive shell): `null`. `cd` prints
  `cd: no shell session` there and returns `1`.
- Script (ADR 0064): the script's shell, in its `hsh` process; a `cd` or a
  `read` in the script does not reach the terminal.

### `cd`: check under the command's identity

`ShellSession.ChangeDirectoryAsync` performs a real `chdir` of the checking
process, resolved from the shell's directory: the ambient context of the `cd`
command (its capabilities `filesystem.user-home.read` and
`filesystem.system.read`, and the folder's permissions), otherwise that of the
host. The resulting canonical path is adopted by the shell; the process that
checked returns to its directory. `PWD` and `OLDPWD` are updated right away, so
that the rest of an `&&` list sees the change.

### `sudo`

`sudo` has no authority of its own: it calls `IShellSession.RunElevatedAsync`.
The shell:

1. resolves the target command (`sudo: x: command not found`, code `1`);
2. refuses a command that declares `shell.control` (`sudo: cd: shell builtin`,
   code `1`), without asking for credentials, except `nice` (ADR 0136): `nice`
   receives shell access and the root ticket, which it passes to the command it
   launches;
3. calls `ShellExecutionOptions.CredentialPrompt` (the terminal opens an
   administrator's credentials window and obtains a `SudoRoot` ticket,
   ADR 0042); a refusal gives `sudo: authentication failed`, code `1`;
4. launches the target as a **child of the `sudo` process**, in the shell's
   directory, with the ticket, without shell access, its output connected to
   that of `sudo`. Its input is that of the `sudo` step (`echo x | sudo tee f`).

A host without `CredentialPrompt` makes `sudo` fail
(`sudo: a terminal is required to read the password`). The redirection stays
open by the non-elevated shell: `sudo echo x > /etc/f` fails, as on Linux.

### `clear` and the clearing sequences

`clear` writes `ESC[H ESC[2J ESC[3J` to its output. The terminal interprets
them: since ADR 0061, the xterm.js emulator receives the bytes as they are
([`terminal-emulator.md`](terminal-emulator.md)), so `ESC[2J` and `ESC[3J`
clear the screen and the scrollback, banner included, and the other sequences
(cursor, colors) apply as on Linux. A sequence split between two writes is
completed by the next one. `clear > f` writes the sequence into `f`. Ctrl+L
remains a keyboard shortcut of the terminal.

### Static aliases and `argv[0]`

A command's `argv[0]` is now the **name under which it was called**
(`AppLaunchRequest.CommandName`), including a manifest alias, as for a
multi-call binary. `alias` declares `addalias`, `rmalias` and `unalias`; when
called as `rmalias` or `unalias`, it behaves like `alias -r`. The interpreter
no longer rewrites these names. The expansion of the user's aliases
(`~/.hackeros/aliases`) stays in the shell.

## Commands

| Command | App identifier | Capabilities | Behavior |
|---|---|---|---|
| `cd [dir\|-]` | `org.hackeros.cmd.cd` | `shell.control`, `filesystem.user-home.read`, `filesystem.system.read` | No argument: `HOME`. `-`: `OLDPWD`, displayed. Errors `No such file or directory`, `Not a directory`, `Permission denied`, code `1`. |
| `pwd` | `org.hackeros.cmd.pwd` | none | Its own directory, inherited from the shell. |
| `clear` (`cls`) | `org.hackeros.commands.clear` | none | Writes `ESC[H ESC[2J ESC[3J`. |
| `help [cmd…]` (`man`) | `org.hackeros.commands.help` | `shell.control` | Lists the installed commands (`GetCommands`) or the usage and aliases of a command. Without a shell: code `1`. |
| `export [NOM=valeur…]` | `org.hackeros.commands.export` | `shell.control` | Modifies the shell environment. With no argument (or `-p`): lines `declare -x NOM="valeur"`. `export NOM` keeps the value or exports an empty value. Invalid name: ``export: `x': not a valid identifier``, code `1`. |
| `unset NOM…` | `org.hackeros.commands.unset` | `shell.control` | Removes variables from the shell; a missing variable is not an error. |
| `env` | `org.hackeros.commands.env` | none | Its own environment, one `NOM=valeur` line per variable, sorted. `env NOM=v commande` is not supported (code `125`). |
| `sudo cmd args…` | `org.hackeros.commands.sudo` | `shell.control` | See above. |
| `exit [n]` | `org.hackeros.commands.exit` | `shell.control` | `IShellSession.Exit`: the shell exits with `n` (modulo 256) or `$?`; the rest of the line is not executed; non-numeric argument: code `2`; too many arguments: code `1`, the shell stays ([`terminal-console.md`](terminal-console.md)). |
| `alias`, `addalias`, `rmalias`, `unalias` | `org.hackeros.commands.alias` | `filesystem.user-home.*` | `rmalias`/`unalias` = `alias -r`, chosen by `argv[0]`. |
| `read [-r] [-p p] [nom…]` | `org.hackeros.commands.read` | `shell.control` | ADR 0064: reads a line from standard input and assigns the fields through `AssignVariable` ([`linux-commands.md`](linux-commands.md)). |
| `jobs [-lprs]` | `org.hackeros.commands.jobs` | `shell.control` | ADR 0064: shell jobs (`IShellSession.Jobs`). |
| `kill [-SIG] pid\|%job` | `org.hackeros.commands.kill` | `process.manage`, `shell.control` | ADR 0064: `%n` goes through `IShellSession.KillJob`. ADR 0069: with no option (or `-15`, `-TERM`), `SIGTERM`: the target receives `StopRequested` (reason `Terminated`, deadline at 5 simulated seconds), then its token is cancelled, and it is killed if it has not finished; `-9`/`-KILL` kills immediately, without `StopRequested`. A killed command exits with `137`, a command that stops on its token with `130`. |
| `which nom…`, `watch cmd` | `org.hackeros.commands.which`, `…watch` | `shell.control` | ADR 0064: list of shell commands; `watch` reruns the line through `IShellSession.RunAsync`. |

Opening a file, a folder or a URI from the shell goes through `xdg-open`
(ADR 0096 §3, [`linux-commands.md`](linux-commands.md)): the command does not
need the shell (`shell.control`), it sends an `OpenFileIntent` or an
`OpenUriIntent` with `apps.launch` and returns control (freedesktop codes 0, 1,
3, 4). An `ExecuteCommandIntent` that runs `xdg-open` behaves the same way.
`launch <app-id> [args]` remains the way to open a specific app.
`xdg-mime default <app-id> <type>` chooses the app that opens a type for the
user (ADR 0121).

A valid variable name follows `[A-Za-z_][A-Za-z0-9_]*` (ADR 0050, section 3):
`IShellSession.SetVariable` and `UnsetVariable` throw `ArgumentException`
otherwise.

## Terminal

`TerminalPanel` keeps only line editing, display, completion and the
credentials windows. Tab completion offers the names and aliases of the
**installed** commands, like `help`; since `4-sdk-shell-host`, the shell
computes it (`IHackShell.CompleteAsync`, [`shell-host.md`](../framework/shell-host.md)), paths included since `4-terminal` ([`terminal-console.md`](terminal-console.md)). `TerminalBuiltins` and the interpreter's `IShellBuiltins` hook
are gone.

## Key decisions

- **No built-ins hook in the interpreter.** `IShellBuiltins` was only used for
  the terminal's provisional names; it is removed rather than kept empty, so
  that no host reintroduces hard-coded names.
- **`argv[0]` = call name.** This is what makes it possible to declare
  `unalias`/`rmalias` as plain manifest aliases, without rewriting in the shell
  (clarifies ADR 0050, section 5, which did not say which name).
- **`sudo` inherits the input of its step.** ADR 0051 gives no input to
  `RunElevatedAsync`; passing it makes `echo x | sudo tee f` possible, as on
  Linux, without changing the signature.
- **Command not found under `sudo`: code `1`**, like the real `sudo`, instead
  of `127` for the shell.
- **`cd` checks with a real `chdir`** of the command's process, then undoes it:
  the check goes through the public `Hack*` API, with the command's rights,
  without a new internal API.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellInterpreterTests.cs`: end to
  end with the real catalog commands (`cd` and `&&`, `cd -`, errors, copy in a
  pipe, `export`/`unset`/`env`, `help` that follows the installed commands,
  `clear` and its redirection, `sudo` child of `sudo` with ticket, refusal of
  `sudo cd` and of credentials, shell absent for `ExecuteCommandIntent` and for
  a command without `shell.control`, `unalias`).
- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalControlSequenceDecoderTests.cs`.
- `Tests/HackerOs.Commands.Tests/CoreCommandsTests.cs` and
  `Tests/HackerOs.Commands.Wave5.Tests/Wave5Tests.cs`: `cd`, `clear`, `help`
  with `FakeShellSession` (`Tests/HackerOs.Tests.Support/`).
- `Tests/HackerOs.Ecosystem.Tests`: 87 built-in apps, 70 commands (`stress` since `rebuild/5-worker-processes`, `ausearch` since `v3/8`, `xdg-open` since `v3/4`, `hackbackup` since `v3/7`, `localectl` since `v3/12`, `xdg-mime` since `v3/4b`, `passwd` since `v3/8`).

## Task list

- [x] `IShellSession`, `ShellCommandInfo` and `TerminalExecutionContext.Shell` in the SDK.
- [x] Capability `shell.control`, classified normal.
- [x] `ShellSessionAccess`, shell copy in a pipe, passed only if the capability is granted.
- [x] `cd` (`HOME`, `-`, Linux errors) through `IShellSession.ChangeDirectoryAsync`.
- [x] `pwd` and `env` on their own process.
- [x] `clear` through a clearing sequence, interpreted by the terminal.
- [x] `help` from the installed commands.
- [x] `export` and `unset`.
- [x] Catalog `sudo`, credentials kept by the shell (`RunElevatedAsync`).
- [x] Static aliases `rmalias`/`unalias` through `argv[0]`.
- [x] Removal of `TerminalBuiltins`, of `IShellBuiltins` and of the terminal's name filter; completion from the catalog.
- [x] Tests and documentation.
