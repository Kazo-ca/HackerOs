> [Version française](../../../../fr/wasm/dev/adr/0051-unified-commands-project.md)

# ADR 0051: Grouped commands, with no hard-coded exception

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 3, Shell, commands and trash
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 7.3,
  decision D14).
- **Replaces:** section 1 of ADR 0034 (exclusion of `cd`, `pwd`,
  `clear` and `help` from the catalog, because the terminal intercepted them).
  Sections 2 to 5 of ADR 0034 (construction by injection, simulated
  network, fixed capabilities) remain valid.
- **Complements:** ADR 0042: `sudo` becomes a catalog command; its
  rules (a single elevated command, an administrator's credentials,
  single-use ticket) do not change.
- **Builds on:** ADR 0047 (several `[AppManifest]` classes in a
  project produce `manifests/<appId>.json`), ADR 0048 (`IsSystemApp` on the
  `ProjectReference`), ADR 0050 (shell and process environment).

## Context

The commands are 28 `Apps/Commands/HackerOs.Commands.*` projects, each with
a command class and a manifest class. The host has 24 `ProjectReference`s
for them, the solution 28 projects, and four test projects reference
some of them. `TerminalPanel.razor` intercepts `clear`, `pwd`, `cd`, `help` and
`sudo` before any resolution: their `Cd`, `Pwd`, `Clear` and `Help` projects
exist but are not referenced by the host (ADR 0034, section 1), and both
completion and `help` list these names as hard-coded values.

## Decision

### 1. A single `HackerOs.Commands` project

- New project `Apps/Commands/HackerOs.Commands/HackerOs.Commands.csproj`.
  Each command occupies a folder (`Cat/CatApp.cs`, `Cat/CatCommand.cs`) and
  keeps its namespace (`HackerOs.Commands.Cat`), its classes and its
  `[AppManifest]` declaration.
- **App identifiers do not change** (`org.hackeros.commands.cat`...).
  The `apps.config` and `app-permissions.config` files and the aliases already
  written remain valid.
- The generator writes `manifests/<appId>.json` for each command (ADR 0047);
  the `manifest.json` files of the old projects disappear with them.
- The host has only **one** reference left:
  `<ProjectReference Include="..\..\Apps\Commands\HackerOs.Commands\HackerOs.Commands.csproj" IsSystemApp="true" />`.
  All built-in commands are therefore system apps, like the current 24.
  The assembly is loaded on demand as a single block.
- The 28 old projects are removed from the solution and from disk. Test
  projects reference the new project. The samples
  (`Apps/Samples/HackerOs.Samples.TerminalApp`) remain a separate project:
  it is the model for a third-party command.
- **Third-party command format unchanged:** one project, one app of type
  `Terminal` with its manifest class. A third-party project can also group
  several commands the same way.

### 2. No more exceptions in the terminal

`TerminalPanel` and `ShellInterpreter` know **no** command name.
Everything goes through catalog resolution: `cd`, `pwd`, `clear`,
`help`, `sudo` and the new commands `export`, `unset` and `env`. Completion
and `help` read the list of installed commands. Uninstalling
a system command remains impossible (ADR 0048), so `cd` cannot
disappear.

### 3. Controlled access to the calling shell

As on Linux, `cd` and `export` must act on the shell itself, not on
their own process. The SDK provides this access:

- `TerminalExecutionContext.Shell`, of type `IShellSession?`
  (`Shared/HackerOs.AppSdk/`).
- New capability **`shell.control`** (`AppCapabilities.ShellControl`),
  classified **normal** (ADR 0049): it only acts on the shell of the
  user who typed the command.
- `Shell` is non-null only if the command declares `shell.control` and
  is launched by an **interactive** shell.
- Alone in its pipeline (including after a `&&`), it receives the **real**
  shell: `cd /tmp && ls` lists `/tmp`.
- In a pipe (`cd /tmp | cat`), it receives a **copy** of the shell, like
  the subshell in which bash then runs its built-ins: the
  command succeeds, but its changes are lost at the end of the stage.

```csharp
public interface IShellSession
{
    string WorkingDirectory { get; }
    IReadOnlyDictionary<string, string> Environment { get; }
    IReadOnlyList<string> History { get; }

    // cd: checks the folder (existence, type, right to traverse) under the command's identity,
    // then updates the shell process's directory, PWD and OLDPWD.
    ValueTask ChangeDirectoryAsync(string path, CancellationToken cancellationToken);

    void SetVariable(string name, string value);   // invalid name: ArgumentException
    void UnsetVariable(string name);

    // help: installed commands (name, alias, usage, description).
    IReadOnlyList<ShellCommandInfo> GetCommands();

    // sudo: runs a command as root (section 4).
    ValueTask<int> RunElevatedAsync(IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken);
}
```

The change is applied right away to the shell's process (ADR 0050,
section 5), not at the end of the command, so that the rest of a `&&` list
sees it. A command declaring `shell.control` launched without a shell (by an
app through `ExecuteCommandIntent`) prints `<name>: no shell session` and returns `1`, unless
stated otherwise below.

### 4. Behavior of the affected commands

| Command | Capabilities | Behavior |
|---|---|---|
| `cd [dir\|-]` | `shell.control`, `filesystem.user-home.read`, `filesystem.system.read` | Without an argument: `HOME`. `-`: `OLDPWD`, printed. Linux errors (`No such file or directory`, `Not a directory`, `Permission denied`), code `1`. |
| `pwd` | n/a | Prints its **own** current directory, inherited from the shell: works without shell access, like `/bin/pwd`. |
| `clear` | n/a | Writes the sequence `ESC[H ESC[2J ESC[3J` to its output. The terminal interprets these sequences and clears the screen; `clear > f` writes the sequence to `f`, as on Linux. |
| `help [cmd]` | `shell.control` | Lists the installed commands or prints a command's usage. Without a shell: empty list and a message, code `1`. |
| `export NAME=value...` | `shell.control` | Modifies the shell's environment; commands launched afterwards inherit it. Without an argument: lists `declare -x NAME="value"`. |
| `unset NAME...` | `shell.control` | Removes variables from the shell. |
| `env` | n/a | Prints its own environment (inherited from the shell), one `NAME=value` line per variable. `env NAME=v command` is out of scope. |
| `sudo cmd args...` | `shell.control` | See below. |

**`sudo`.** The command calls `IShellSession.RunElevatedAsync`. The shell
keeps the credentials window and the ticket of ADR 0042: it asks for the
credentials of an administrator, creates the `SudoRoot` ticket, launches the target
command as a child with this ticket, and connects its output to that of `sudo`. The
target command **never** receives shell access: `sudo cd /root` prints
`sudo: cd: shell builtin` and returns `1`. An authentication failure prints
`sudo: authentication failed` and returns `1`. `sudo` in a pipe
works (`sudo cat /etc/shadow | wc`): the shell copy also knows how to
run `RunElevatedAsync`.

**Aliases.** `alias`, `unalias` and `rmalias` remain the `alias` command of the
catalog (static aliases `unalias`, `rmalias` declared in its manifest instead
of the current rewrite in the terminal); expansion remains in the
shell (ADR 0050, section 4).

### 5. One-off approval after a denial

The "Permission denied: approve this operation?" retry from ADR 0042
remains a feature of the interactive shell: it applies to a single command
(not in a pipe nor redirected, so as not to replay side effects), and
re-runs the same command with the `CapabilityApproval` ticket.

## Consequences

- A single `ProjectReference` in the host for all commands; a
  single assembly loaded on demand.
- The terminal no longer has a list of names: a third-party command can provide its
  own improved `cd` by declaring `shell.control`, without touching the terminal.
- `help`, completion and `ps` see the same commands as the shell.
- The test projects and the solution change references in one go;
  no app identifier changes.

## Implementation work items

See ADR 0052 for the full phase 3 table. Work items of this ADR:
`rebuild/3-commands-merge` (section 1, with no behavior change) then
`rebuild/3-shell-commands` (sections 2 to 5).

## References

- ADR 0034 (section 1 replaced), ADR 0042 (`sudo`, one-off approval),
  ADR 0047, ADR 0048, ADR 0049 (classification of `shell.control`), ADR 0050.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 7.3.
- `OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj`, `Apps/Commands/`,
  `Shared/HackerOs.AppSdk.Terminal/TerminalPanel.razor`.
