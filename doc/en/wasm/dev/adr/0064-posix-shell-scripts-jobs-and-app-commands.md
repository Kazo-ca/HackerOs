# ADR 0064: POSIX shell, scripts, jobs, apps launched by name and Linux commands

## Status

Accepted on 2026-10-13.

- **Context:** user request of 2026-10-13: integrate pipes and
  `>` in the terminal; add `sort`, `head`, `locate`, `zip`, `unzip`,
  `tail`, `cut`, `diff`, `tee`, `whoami`, `df`, `du`, `top`, `htop`,
  `systemctl`, `watch`, `jobs`, `kill`, `wget`, `curl`, `ip`, `netstat`, `ln`,
  `unalias`; run `sh` scripts "as close as possible to Linux shell
  scripts", with an error that gives the `chmod +x` command when the
  execute bit is missing; launch an app by its name without a namespace, with its
  arguments and seeing its standard output; make sure `nano` works
  with the xterm.js terminal (ADR 0061).
- **Supersedes** the grammar of ADR 0050 §2 (which refused `;`, `||`, `&`,
  `<`, `2>`, `$(`, parentheses and `NAME=value`) and the rule "no
  word splitting after expansion" of ADR 0050 §3 (taken from ADR 0014).
- **Amends** ADR 0051 §1 ("no built-ins"): a few special POSIX built-ins
  live in the shell (see §4). Everything else stays in the catalog.
- **Completes** ADR 0056 §7 (host shell): the terminal now provides a
  standard input to commands.

## Context

The `hsh` shell could do `|`, `>`, `>>` and `&&`, but no real Linux
script ran: no `;`, `||`, `if`, loops, functions,
`$(...)`, `$((...))`, positional parameters or input redirection.
Commands never received keyboard input (empty stdin), so
`read` or interactive `cat` were impossible. A windowed app had
no standard output. `chmod` only knew octal, whereas the
request cites `chmod +x`.

## Decision

### 1. A POSIX `sh` subset

`ShellParser` reads a line or a whole script (lexer then recursive
descent):

- lists: `;`, newline, `&` (background job); `&&` and `||` with
  the same precedence, left to right; `! pipeline` inverts the code;
- compound commands: `{ ...; }`, `( ... )` (subshell), `if/elif/else/fi`,
  `while`, `until`, `for NAME [in words]`, `case ... esac`; functions `name() { ... }`
  and `function name { ... }`;
- redirections: `[n]>`, `[n]>>`, `[n]<`, `[n]>&m`, `[n]<&m`, `&>`, `&>>`,
  here-documents `<<` and `<<-` (quoted delimiter: literal body),
  descriptors 0 to 2; `/dev/null`, `/dev/stdin`, `/dev/stdout`,
  `/dev/stderr` and `/dev/tty` are the shell's streams;
- expansions: `$NAME`, `${NAME}`, `${NAME:-x}`, `:=`, `:+`, `:?` (and without `:`),
  `${#NAME}`, `${NAME#pattern}`, `##`, `%`, `%%`, `$1`...`${10}`, `$#`, `$@`, `$*`,
  `$$`, `$!`, `$-`, `$0`, `$?`, `$(...)`, `` `...` ``, `$((...))` (C-style
  64-bit integer arithmetic), `~`;
- **field splitting** on `IFS` of unquoted results, then
  **pathname expansion** (`*`, `?`, `[...]`, with the shell's identity; a
  pattern with no match stays as is; leading `.` explicit);
- `NAME=value` alone creates a **non-exported shell variable**; in front of a
  command, it applies only to that command; `export` exports it;
- `#` comments, `\` + newline continuation.

The escapes of ADR 0014 inside double quotes (`\n`, `\t`) remain.
`<>`, `<<<`, `select`, `[[ ]]` are not supported
(`shell.syntax.unsupported` or syntax error). An incomplete input
(`if` without `fi`, trailing `|`, open quote) gives
`ShellParseResult.IsIncomplete`.

### 2. Scripts

- A name containing `/` is a path: the file must exist, not be a
  folder, be **executable** by the user (`x` bit of the owner, group
  or others depending on identity; root: any bit) and readable.
  Otherwise: `hsh: ./x.sh: Permission denied` (code 126) followed by
  `hsh: hint: './x.sh' is not executable. Make it executable with: chmod +x ./x.sh  (or run it with: sh ./x.sh)`.
- The `#!` line chooses the interpreter: `sh`, `bash`, `hsh`, `dash`, `zsh`...
  (directly or through `/usr/bin/env`) → `hsh`; another installed command
  receives `[arg] path args...`; otherwise `bad interpreter` (126). Without `#!`, the
  shell executes the file (like bash).
- A script runs in a **new `hsh` process**, child of the shell
  (`AppLifecycleOrchestrator.StartShellProcess`), with the capabilities and
  elevation of the parent shell, its own variables (the exported ones inherited),
  functions, options and positional parameters; `cd` and `exit` stay in it.
  A syntax error is reported before any launch
  (`script.sh: line N: ...`, code 2). `trap ... EXIT` runs on exit.
- `sh`, `bash` and `hsh` are the shell itself: `sh script [args]` (read-only
  access is enough), `sh -c 'command' [name args...]`, `sh -e/-x/-u`, and without a script
  the commands read from standard input (`echo ls | sh`).

### 3. Jobs

`list &` runs in a copy of the shell, empty input, with its own
cancellation (Ctrl+C does not reach it, closing the terminal does). The interactive
shell displays `[n] pid`, then `[n]+  Done  command` on the next line.
`$!`, `wait [%n|pid]`, `fg [%n]` (built-ins), `jobs` and `kill %n` (catalog,
through `IShellSession.Jobs` and `IShellSession.KillJob`). The processes of a job
are known through `AppLaunchRequest.ProcessStarted`.

### 4. Special built-ins

Only the commands that change the control or state of the shell in a way
that no separate process can reproduce are in the interpreter: `:`,
`break`, `continue`, `return`, `shift`, `set` (`-e -u -x -f`, `-o`, `--`),
`local`, `eval`, `.`/`source`, `wait`, `fg`, `bg`, `exec`, `trap`,
`command`, `type`. `help` and completion list them with `sh`. `cd`,
`export`, `read`, `test`, `jobs`... stay catalog commands (ADR
0051): `read` modifies the shell through `IShellSession.AssignVariable`. `[` is the
shell's link to `test` (a manifest command name cannot contain
a bracket); `argv[0]` is `[`, which requires the final `]`.

### 5. Apps launched by their short name

When no command has the typed name, the shell looks for a **windowed**
app whose identifier's last segment is that name
(`text-editor` for `org.hackeros.text-editor`, `terminal`, `browser`...) or
the whole identifier. It is launched as a child of the shell, in its folder,
with the shell's arguments and streams; the shell **waits for the window
to close** (like bash with a graphical program; `&` returns the prompt), Ctrl+C
closes it (130). A single-instance app that is already open is only brought to the
foreground (code 0). Completion offers these names.

An app's standard output is the new facade `HackConsole`
(`Out`, `Error`, `In`, `WriteLine`...) over `IAppExecutionContext.Console`
(`IAppConsoleGateway`): the streams of the shell that launched it, protected against a
closed terminal or a broken pipe; `/dev/null` for an app launched from the desktop.
A terminal command sees the same streams as its `TerminalExecutionContext`.

### 6. Terminal standard input

While a line is executing, the terminal provides a standard input
(`TerminalInputReader`) handled like a tty in canonical mode: echo, Backspace,
Ctrl+U, Enter sends the line, Ctrl+D on an empty line = end of input
(once), Ctrl+C interrupts. What has not been read returns to the prompt
(type-ahead), as on Linux. Only a command writing to the terminal
itself receives the alternate screen (`nano`, `top`, `htop`, `watch`): in a
pipe or redirected, it produces its text. An unfinished command typed at the
terminal (`IHackShell.NeedsMoreInput`) displays the continuation prompt `>`
(`PS2`) and the lines are executed together; the history keeps them as one
line.

### 7. Commands and `nano`

Commands added to the `HackerOs.Commands` project (one folder per command):
`locate`, `zip`, `unzip`, `cut`, `tee`, `whoami`, `df`, `du`, `top`, `htop`,
`systemctl` (alias `service`), `watch`, `jobs`, `wget`, `ip` (alias
`ifconfig`), `netstat` (alias `ss`), `ln`, `test`, `true`, `false`, `read`,
`sleep`, `printf`, `seq`, `basename`, `dirname`, `which`, `uniq`. Reworked
commands: `chmod` (symbolic modes, `-R`), `sort` (`-k -t -h -V -f -c -o`),
`head`/`tail` (`-N`, `-c`, `+N`, `tail -f`), `kill` (`%job`, signal names,
`-l`), `curl` (`-o`, `-O`, `-H`, `-A`, `-s`). The SDK gains
`HackFile.CreateSymbolicLinkAsync` (`ln -s`); the file system has no
hard links, `ln` without `-s` answers `Operation not supported`.

`nano` adopts the shortcuts and layout of GNU nano: `^O` asks for the
name (pre-filled; it is also "save as", because a real terminal does not
distinguish Ctrl+Shift+O), `^S`, `^W`/`^F`, `^K`/`^U`, `^C`, `^G`, `^Y`/`^V`,
title bar and two lines of shortcuts, horizontal scrolling, tab stops
at 8 columns. Full-screen rendering accepts SGR sequences (colors) without
counting them in the width.

## Consequences

- Common Linux scripts run as is; the end-to-end tests
  of `ShellInterpreterTests` verify it with the real orchestrator.
- Field splitting changes the semantics of an unquoted `$X` that contains
  spaces: it must be written `"$X"`, as on Linux.
- An app launched from the terminal blocks the prompt until it is closed.
- 28 more commands: the host catalog has 74 apps, of which 62
  are commands.

## Rejected alternatives

- **Keeping the refusals of ADR 0050** and only adding `;`: real scripts
  use `if`, `for`, `$(...)` and `$((...))`.
- **Everything as built-ins like bash**: contrary to ADR 0051; only the indispensable
  built-ins were added.
- **Running scripts in the shell's process**: `cd` or `exit` in a
  script would change the terminal, unlike Linux.
- **Opening an app without waiting for it to close**: more convenient but different
  from Linux; `&` gives this behavior.
