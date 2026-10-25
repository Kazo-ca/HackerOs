> [Version française](../../../fr/wasm/dev/shell-interpreter.md)

# The `hsh` shell interpreter

Phase 3 of the rebuild plan, work item `rebuild/3-shell-interpreter`
([ADR 0050](adr/0050-shell-grammar-and-process-environment.md), sections 1 to 4
and 7, refined by [ADR 0054](adr/0054-shell-exit-status-and-detached-execution.md)),
extended into a POSIX `sh` subset by [ADR 0064](adr/0064-posix-shell-scripts-jobs-and-app-commands.md)
(scripts, jobs, special built-ins, apps launched by name).

## Goal

A single shell, without an interface, testable without a browser, used by the
terminal, by scripts and by `ExecuteCommandIntent`. It understands lists (`;`,
`&&`, `||`, `&`), pipes, redirections (files, descriptors, here-documents),
compound commands, functions and all POSIX expansions, launches each command as
a child process of the shell and displays the output as it is produced.

```text
alice@hackeros:~$ for f in *.log; do n=$(grep -c ERROR "$f"); [ "$n" -gt 0 ] && echo "$f: $n"; done | sort -t: -k2 -n
alice@hackeros:~$ ./deploy.sh web db || echo "échec : $?" >&2
alice@hackeros:~$ text-editor notes.txt &
[1] 214
```

## Architecture

Everything is in `Platform/HackerOs.Platform.Core/Shell/`:

| Type | Role |
|---|---|
| `ShellParser` | Lexer (words, operators, comments, here-documents read at the newline, `$(…)`, `` `…` ``, `$((…))`) then recursive descent; returns a `ShellCommandList` or a `ShellSyntaxError` (code, message, position, length). Never throws an exception for input; `IsIncomplete` signals input that must be continued. |
| `ShellSyntax.cs` | The tree: `ShellCommandList` → `ShellListItem` (`&` or not) → `ShellAndOrList` (`&&`, `||`) → `ShellPipeline` (`!`) → `ShellCommand`: `ShellSimpleCommand` (assignments, words, redirections), `ShellGroupCommand` (`{ }`, `( )`), `ShellIfCommand`, `ShellLoopCommand`, `ShellForCommand`, `ShellCaseCommand`, `ShellFunctionDefinition`. A `ShellWord` keeps its pieces (`ShellLiteralPart`, `ShellVariablePart`, `ShellSpecialParameterPart`, `ShellParameterExpansionPart`, `ShellCommandSubstitutionPart`, `ShellArithmeticPart`, `ShellExitStatusPart`, `ShellTildePart`), each knowing whether it was quoted. |
| `ShellExpander` | POSIX expansion of a word: parameters, substitutions, arithmetic, splitting on `IFS`, paths (`*`, `?`, `[…]`), quote removal. |
| `ShellArithmetic` | `$((…))`: 64-bit integers, C operators and precedences, assignments, `++`/`--`, `?:`, `**`, octal, hexadecimal and `base#n` literals. |
| `ShellPattern` | Shell patterns (paths, `case`, `${x#motif}`) translated into regular expressions. |
| `ShellInterpreter` (file split into `.cs`, `.SimpleCommands.cs`, `.Redirections.cs`, `.Builtins.cs`, `.Scripts.cs`) | Executes the tree: lists, pipes, compound commands, redirections, command lookup (built-in, function, path, catalog, `sh`, app), scripts, jobs. Also provides `GetInstalledCommands()`, `GetLaunchableApplications()` and the elevated execution of `sudo`. |
| `ShellSession` | State of a shell: process, history, `LastExitCode`, non-exported variables, positional parameters, `$0`, functions, options (`set -eux`), jobs, `trap … EXIT`. The directory and the **exported** variables are **those of the process**, read through `IAppEnvironmentGateway`. `Fork()` gives the copy for a subshell. |
| `ShellJob`, `ShellJobTable` | Background jobs: command, process, state, status, cancellation. |
| `ShellPipe` | In-memory pipe of 64 Ki characters between two steps. |
| `ShellCommandResolver` | Lookup of a command (name, manifest alias or app identifier) and, since ADR 0064, of a windowed app by its short name (`ResolveApplication`). |
| `ShellExecution.cs` | `ShellExecutionOptions`, `ShellExecutionResult`, `ShellFailure`, `ShellElevation`. |
| `ShellSessionAccess` | `IShellSession` handed to commands that declare `shell.control` ([`shell-commands.md`](shell-commands.md)). |
| `ShellErrorText` | Short text of a file error (`No such file or directory`, `Permission denied`...), in the SDK. |

Terminal side (`Shared/HackerOs.AppSdk.Terminal/`):

| Type | Role |
|---|---|
| `TerminalPanel` | Line discipline and display in an xterm.js terminal ([`terminal-emulator.md`](terminal-emulator.md), ADR 0061). Starts its shell with `HackShell.StartAsync`; each line goes through `IHackShell.ExecuteAsync`. |
| `TerminalInputReader` | Standard input of the current line (ADR 0064 §6): tty in canonical mode, Ctrl+D = end of input, unread input handed back to the prompt. |
| `TerminalOutputWriter` | `TextWriter` that pushes each written chunk to the terminal. |

## Grammar

```text
programme   := liste
liste       := et_ou ( ( ";" | "&" | nouvelle-ligne ) et_ou )* [ ";" | "&" ]
et_ou       := pipeline ( ( "&&" | "||" ) pipeline )*
pipeline    := [ "!" ] commande ( "|" commande )*
commande    := simple | composée redirection* | fonction
simple      := ( affectation | mot | redirection )+
composée    := "{" liste "}" | "(" liste ")"
             | "if" liste "then" liste ( "elif" liste "then" liste )* [ "else" liste ] "fi"
             | ( "while" | "until" ) liste "do" liste "done"
             | "for" NOM [ "in" mot* ] ( ";" | nouvelle-ligne ) "do" liste "done"
             | "case" mot "in" ( [ "(" ] motif ( "|" motif )* ")" [ liste ] ";;" )* "esac"
fonction    := NOM "(" ")" composée | "function" NOM [ "(" ")" ] composée
redirection := [n] ( ">" | ">>" | ">|" | "<" | ">&" | "<&" ) mot
             | ( "&>" | "&>>" ) mot | [n] ( "<<" | "<<-" ) délimiteur
```

- Reserved words (`if`, `then`, `fi`, `do`, `done`, `case`, `esac`, `{`, `}`,
  `!`, `in`, `function`...) only when unquoted and in command position:
  `echo done` prints `done`.
- Quotes and escapes from ADR 0014: `'…'` literal; `"…"` keeps spaces, expands
  `$` and `` ` ``, and understands `\"`, `\\`, `\$`, `` \` ``, `\n`, `\r`,
  `\t`; outside quotes, `\` makes the next character literal; `\` + newline
  joins two lines. `""` gives an empty argument.
- `#` at the start of a word begins a comment.
- `NOM=valeur` at the start: assignment (see [Variables](#variables)).
- Here-documents: the body is read after the command line up to the delimiter;
  `<<-` strips tabs; a quoted delimiter (`<<'EOF'`) makes the body literal,
  otherwise `$` and `` ` `` are expanded in it.
- Rejected: `<>`, `<<<` (`shell.syntax.unsupported`). Errors:
  `shell.syntax.unexpected-token`, `shell.syntax.unexpected-end` (incomplete
  input), `shell.syntax.missing-redirect-target`,
  `shell.syntax.unterminated-quote`, `shell.syntax.unterminated-substitution`,
  `shell.syntax.unterminated-here-document`, `shell.syntax.trailing-escape`,
  `shell.syntax.bad-substitution`, `shell.syntax.bad-function-name`.

## Expansion

Just before launching each simple command, in POSIX order:

1. **Tilde**: `~` and `~/…` at the start of an unquoted word (or assignment
   value): `HOME`.
2. **Parameters**: `$NOM`, `${NOM}`; `$1`...`$9`, `${10}`; `$#`, `$@`, `$*`,
   `$$` (shell PID), `$!` (last job), `$-` (options), `$0` (name of the shell
   or script), `$?`; `${NOM:-défaut}`, `${NOM:=défaut}`,
   `${NOM:+autre}`, `${NOM:?message}` (and without `:`: only if unset),
   `${#NOM}`, `${NOM#motif}`, `${NOM##motif}`, `${NOM%motif}`,
   `${NOM%%motif}`. With `set -u`, an unset parameter is an error.
3. **Command substitution**: `$(…)` and `` `…` `` run in a copy of the shell;
   their output, without the trailing newlines, is inserted; `$?` becomes
   their status.
4. **Arithmetic**: `$((…))`, after expansion of its `$`.
5. **Field splitting** of the **unquoted** results on `IFS` (space, tab,
   newline by default); `"$@"` gives one field per parameter.
6. **Paths**: a field that contains unquoted `*`, `?` or `[…]` is replaced by
   the matching paths, sorted, read with the shell's identity (`set -f`
   disables this); with no match it stays as is; a name starting with `.` is
   only matched by a pattern starting with `.`.
7. Quote removal.

**Aliases**: for a typed line (not in a script), if the first word is a simple
unquoted word that is not a function, the shell looks for `nom=valeur` in
`~/.hackeros/aliases`; the value must be a simple command.

### Variables

A bare `NOM=valeur` creates a **non-exported shell variable** (or modifies an
existing exported variable); commands do not inherit it. `export NOM` exports
it. `NOM=valeur commande` applies only to that command. `local` (in a
function) saves and restores a variable.

## Execution

- **Lists**: `a; b` in order; `a && b` if `a` is 0; `a || b` otherwise;
  `! p` inverts the code; `set -e` stops the shell on an untested failure.
- **Pipes**: all steps start at the same time in copies of the shell
  (subshells), connected by `ShellPipe`s. When a step ends, its input closes:
  the next writer receives `HackIOException("Broken pipe")`, and a command that
  lets it through exits with `141`.
- **Streams**: input of the first step = the shell's standard input (the
  terminal's, ADR 0064 §6); output of the last to the terminal; errors of all
  steps to the terminal. Only a step that writes to the terminal itself
  receives the alternate screen (`nano`, `top`).
- **Redirections**: opened from left to right with the identity of the shell
  host; `>` truncates on open, `>>` creates or appends, `<` reads, `n>&m`
  copies the descriptor as it is at that point (`>f 2>&1` ≠ `2>&1 >f`), `&>f` =
  `>f 2>&1`, here-document = input; `/dev/null`, `/dev/stdin`, `/dev/stdout`,
  `/dev/stderr`, `/dev/tty` are the shell's streams. Failure:
  `hsh: <cible>: <raison>`, command not launched, code `1`. Files are
  published at the end of the command (`HackFileStream`).
- **Command lookup**: special built-in, then function, then path (name
  containing `/`), then catalog command (`[` = `test`), then `sh`, `bash`,
  `hsh`, then windowed app by its short name, otherwise `127`.
- **Codes**: `127` not found (or uninstalled), `126` not executable, `2`
  syntax, `130` interruption, `1` fault or redirection, `141` broken pipe,
  `143` job terminated by `kill`.
- **Interruption**: Ctrl+C cancels the token: all steps receive the
  cancellation, the list stops; a foreground windowed app is closed. Loops
  yield to the browser every 64 iterations.
- **Processes**: each command is launched with `ParentPid` = the shell's
  process and `WorkingDirectory` = the shell's directory. `ps` therefore shows
  `terminal → hsh → grep, wc`.
- **One-time approval** (ADR 0042): if a single command typed in the terminal
  fails on a permission denial, `PermissionApproval` can supply a token; the
  command is rerun once with it.
- `sudo echo x > /etc/f` fails: the redirection is opened by the non-elevated
  shell, as on Linux.

### Special built-ins

`:`, `break [n]`, `continue [n]`, `return [n]`, `shift [n]`,
`set [-eufx] [-o nom] [-- args]` (with no argument: all variables),
`local nom[=valeur]`, `eval args`, `. fichier` / `source fichier [args]`,
`wait [%n|pid]`, `fg [%n]`, `bg`, `exec commande`, `trap action EXIT`,
`command [-v|-V] nom`, `type nom`, `ulimit [-SH] [-a|-f|-m|-n|-t|-u] [limite]`
(limits of the shell's process, inherited by the commands launched afterwards;
ADR 0125, [`process-limits.md`](process-limits.md)). Everything else comes from the catalog
([`shell-commands.md`](shell-commands.md), [`linux-commands.md`](linux-commands.md)).

### Functions

`nom() { …; }`: called like a command, with its own positional parameters,
`local` and `return`; at most 200 levels of nesting (functions, scripts,
`eval`, substitutions).

### Scripts

- `./script.sh` (any name with `/`): the file must exist, not be a folder, be
  **executable** (owner's `x` bit for the owner, the group's or others'
  otherwise; root: any bit) and readable. Otherwise:

  ```text
  hsh: ./deploy.sh: Permission denied
  hsh: hint: './deploy.sh' is not executable. Make it executable with: chmod +x ./deploy.sh  (or run it with: sh ./deploy.sh)
  ```

  (code 126). A binary file gives `cannot execute binary file`.
- `#!`: `sh`, `bash`, `hsh`, `dash`, `ash`, `zsh`, `ksh` (directly or through
  `/usr/bin/env`) → `hsh`, with the line's options (`#!/bin/sh -e`); another
  installed command receives `[arg] script args…`; otherwise `bad interpreter`.
  Without `#!`, `hsh` executes the file.
- The script runs in a **new `hsh` process**, a child of the shell
  (`AppLifecycleOrchestrator.StartShellProcess`), which inherits the
  capabilities, the elevation, the directory and the exported variables; `$0`
  is the typed path, `$1`... the arguments. Its `cd`, variables, functions and
  `exit` do not affect the calling shell; its `shell.control` commands (`cd`,
  `read`) act on the script's shell. The process ends with the script's status,
  after `trap … EXIT`.
- A syntax error is reported before launching anything:
  `deploy.sh: line 4: syntax error …` (code 2).
- `sh script [args]` (read access is enough), `sh -c 'cmd' [nom args…]`,
  `sh -e/-x/-u`; without a script, `sh` reads its commands from standard input
  (`echo 'ls' | sh`, or typed until Ctrl+D).

### Jobs

`commande &` runs in a copy of the shell, empty input, with its own
cancellation: Ctrl+C at the prompt does not reach it, closing the terminal
does (SIGHUP). In interactive mode, the shell prints `[1] 214`, then before the
next prompt `[1]+  Done                    sleep 5`. `$!` is the PID of the
last process launched by the last job. `jobs`, `kill %1`, `wait`, `fg`.

### Apps launched by name

When no command has the typed name, the shell looks for a **windowed** app
whose identifier's last segment (without the namespace) is that name:
`text-editor notes.txt`, `browser`, `terminal`, `calculator`. The app is
launched as a child of the shell, in its folder, with its arguments
(`WindowAppBase.LaunchArguments`) and the shell's streams: what it writes
through `HackConsole.WriteLine` appears in the terminal. Like a graphical
program launched from bash, **the shell waits for the window to close**;
`text-editor notes.txt &` returns the prompt immediately; Ctrl+C closes the
window (code 130). A single-instance app that is already open is brought to the
foreground (code 0). Completion offers these names.

## Terminal

Since the `4-sdk-shell-host` work item (ADR 0056 §7), the shell is its own
`hsh` process, a child of the Terminal window (or of the app that embeds
`TerminalPanel`): each panel has its own. `InitialCwd` is the shell's starting
directory ([`shell-host.md`](../framework/shell-host.md)). The prompt comes
from the process: `utilisateur@machine:répertoire$`, `~` for the home folder,
`#` for root.

The terminal knows no command name: `cd`, `clear`, `help`, `sudo` are catalog
commands, and completion offers the installed commands, the built-ins, `sh`
and the apps.

## `ExecuteCommandIntent`

`AppIntentDispatcher` executes `CommandLine` with the same interpreter, in
non-interactive mode, output buffered in `AppIntentDispatchResult`. The
caller's `apps.launch` is checked once. With `callerContext`, the shell is the
caller's process; without, it is detached (ADR 0054) and can neither open files
nor launch scripts. Status: `NotFound`, `Disabled`, `ElevationDenied` or
`EntryPointFault` depending on the failure of the last command, otherwise
`Dispatched` with the exit code (a syntax error gives `Dispatched`, code `2`
and `ErrorCode` = the error's code).

## Key decisions

- The shell has no directory or exported variables of its own: those of the
  process are authoritative, which keeps `/proc/<pid>/cwd`, `ps` and the
  commands consistent. Non-exported variables, on the other hand, exist only in
  the shell.
- POSIX field splitting and paths (ADR 0064): the ADR 0014 deviation (no
  splitting) is dropped, because Linux scripts depend on it.
- A script is a separate `hsh` process, not a function of the calling shell:
  its `cd` and `exit` stay with it.
- Special built-ins are reduced to the strict minimum (ADR 0051 amended).
- Message language (ADR 0104): like bash with gettext, diagnostics
  (`commande introuvable`, syntax and expansion errors, `type`, jobs, `sudo`)
  follow `LC_ALL` > `LC_MESSAGES` > `LANG` of the session, reread on each line
  (`ShellText`, keys `Shell.*`, `Syntax.*`, `Arithmetic.*`, `Errno.*` of the
  Platform Core `Strings`). The `hsh: ` / `sudo: ` prefixes, names and paths
  are not translated. `ShellSyntaxError.Message` and exception messages stay in
  English (logs, callers); `TextKey` / `TextArguments` carry the text to
  translate.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellParserTests.cs`: quotes,
  operators, lists, descriptor redirections, here-documents, compound
  commands, functions, incomplete input, errors, word pieces.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellPipeTests.cs`.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellInterpreterTests.cs` and
  `ShellInterpreterTests.Scripting.cs`: end to end with orchestrator, processes
  and real files (pipes, `;`, `||`, assignments, substitutions, arithmetic,
  `${x%motif}`, `if`/`for`/`while`/`until`/`case`, `break 2`, functions and
  `local`, `2>&1`, here-documents, `/dev/null`, paths, scripts with and without
  `+x`, `#!`, `set -e`, script syntax error, `sh -c`, `| bash`, `.`, jobs,
  apps by name and `HackConsole`, Ctrl+C on an app).
- `ShellInterpreterTests.Commands.cs`: the commands of
  [`linux-commands.md`](linux-commands.md) in the real shell.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellCommandResolverTests.cs`.

## Task list

- [x] Tree parser with structured errors (`ShellParser`, `ShellSyntax`).
- [x] Expansion of `$VAR`, `${VAR}`, `$?`, `~` (without splitting until ADR 0064).
- [x] `ShellPipe` 64 Ki, broken pipe → 141.
- [x] Redirections `>` and `>>` opened by the shell with the host's identity.
- [x] `&&`, exit codes, interruption.
- [x] `ShellInterpreter` and `ShellSession` in Platform Core.
- [x] Streams on `AppLaunchRequest`, output as it is produced in the terminal.
- [x] Commands as children of the shell's process, in its directory.
- [x] `ExecuteCommandIntent` through the interpreter.
- [x] Catalog `cd`, `export`, `sudo`... and `IShellSession`.
- [x] ADR 0064: `;`, `||`, `&`, `!`, `<`, `n>&m`, `&>`, here-documents.
- [x] ADR 0064: `if`, `while`, `until`, `for`, `case`, `{ }`, `( )`, functions.
- [x] ADR 0064: positional and special parameters, `${…}` with operators, `$(…)`, `` `…` ``, `$((…))`.
- [x] ADR 0064: splitting on `IFS`, path expansion, non-exported variables, prefix assignments.
- [x] ADR 0064: special built-ins (`break`, `return`, `set`, `local`, `eval`, `source`, `wait`, `fg`, `trap`...).
- [x] ADR 0064: scripts (`+x` and `chmod +x` message, `#!`, `hsh` process), `sh`/`bash`.
- [x] ADR 0064: jobs (`&`, `$!`, `jobs`, `kill %n`, `wait`, `fg`).
- [x] ADR 0064: windowed apps by their short name, `HackConsole`, waiting for the window to close.
- [x] ADR 0064: terminal standard input (`TerminalInputReader`).
- [x] ADR 0064: continuation prompt `>` for an unfinished command (`IHackShell.NeedsMoreInput`).
