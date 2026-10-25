# ADR 0050: Shell grammar and process environment

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 3, Shell, commands and trash
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 7.1 and
  7.2, decisions D12 and D13).
- **Replaces:** ADR 0014 (grammar of the first slice). The rules for
  splitting into words, quoting and escaping from ADR 0014 are
  taken over as is; the list of rejected operators and the read-only
  environment are replaced.
- **Complements:** ADR 0012 (process model) and ADR 0046 (`/proc`): a
  process gains arguments, a current directory and an environment,
  which `/proc/<pid>/cmdline`, `cwd` and `environ` finally expose.
- **Complements:** ADR 0043: the ambient context also carries the environment,
  read by the new `HackEnvironment` class.

## Context

The current shell is the `ExecuteInputAsync` method of
`Shared/HackerOs.AppSdk.Terminal/TerminalPanel.razor`. It mixes
grammar, resolution, `sudo`, aliases and display. Its parser
(`ShellParser`) knows neither variable expansion nor operators.
The line text is then rebuilt with spaces
(`$"{cmd} {string.Join(" ", args)}"`) and split again on spaces by
`AppIntentDispatcher.DispatchExecuteCommandAsync`: a quoted argument
that contains a space is therefore cut in two.

`AppLifecycleOrchestrator.RunTerminalAsync` gives each command an empty input,
an output buffered in a `StringWriter` rendered only at the end, and
an environment reduced to `HOME`. Yet `TerminalSession` holds an
environment (`USER`, `HOME`, `SHELL`, `TERM`, `PATH`) that never reaches
the commands. Processes have no arguments, no directory and no
environment: `ProcFileSystemProvider` returns the app identifier for
`cmdline`, an empty `environ` and the home folder for `cwd`.

## Decision

### 1. A headless interpreter, in Platform Core

The shell becomes a headless engine in
`Platform/HackerOs.Platform.Core/Shell/`:

- `ShellParser` produces a tree (section 2) or a structured error (code,
  position, length), never throwing an exception for an input.
- `ShellInterpreter` executes this tree: expansion, redirections, pipes,
  launching the stages through `AppLifecycleOrchestrator`, exit code.
- `ShellSession` is a shell's state: its process, the history, the
  last exit code. The current directory and the environment are
  **not** copied into the session: they are those of the shell's process
  (section 5).

`TerminalPanel` keeps only line editing, display, keyboard
history, completion and the credentials window. The old `ShellParser`
and `TerminalSession` of `HackerOs.AppSdk.Terminal` disappear.
`ExecuteCommandIntent` goes through the same interpreter, in non-interactive mode
(section 7): no more splitting on spaces.

### 2. Grammar

```text
list       := pipeline ( "&&" pipeline )*
pipeline   := command ( "|" command )*
command    := ( word | redirection )+
redirection:= ( ">" | ">>" ) word
```

- **Words, quotes and escapes:** unchanged from ADR 0014
  (literal single quotes, double quotes with expansion and
  escapes `\"`, `\\`, `\$`, `\n`, `\r`, `\t`, backslash outside
  quotes).
- **Recognized operators** outside quotes: `|`, `&&`, `>`, `>>`. They do not
  need spaces around them (`ls|wc`, `echo a>f` are valid).
- **Operators still rejected** outside quotes, with the error
  `shell.syntax.unsupported`: `;`, `||`, a lone `&`, `<`, `2>`, `&>`, `` ` ``,
  `$(`, `(`, `)`. They remain usable inside quotes or escaped. They
  can be added by a later ADR without breaking this grammar.
- **Syntax errors:** operator at the start or end (`| wc`, `ls &&`),
  two operators in a row, redirection without a target, empty command in a
  pipe. Exit code `2`, no command launched.
- **Assignment:** a first word of the form `NAME=value` is rejected with
  the error `shell.syntax.assignment` ("use export NAME=value"). The form
  `NAME=value command` is out of scope.
- **Not supported (unchanged):** globbing, brace expansion, command
  substitution, subshells, functions, scripts, background
  jobs.

### 3. Expansion

It takes place after splitting, word by word, and never re-reads the result
as syntax.

- `$NAME` and `${NAME}` outside single quotes, names
  `[A-Za-z_][A-Za-z0-9_]*`. A missing variable yields an empty string.
- `$?`: exit code of the last list executed by this shell.
- `~` and `~/...` at the start of an unquoted word: value of `HOME`.
  `~other` is not supported and remains literal.
- **No word splitting after expansion** (deliberate deviation from POSIX,
  taken from ADR 0014): `$X` remains a single argument even if it contains
  spaces. This is safer for paths that contain spaces.
- A redirection target is expanded the same way.

### 4. Execution

**Pipes.** The stages of a pipe run **at the same time**, as on Linux.
They are connected by a `ShellPipe`: an in-memory buffer of 64 KiB (the
default size of a Linux pipe). A writer waits when the buffer is
full; a reader waits when it is empty; the writer ending closes the
pipe and the reader receives end of stream. The browser has only one thread of
execution: synchronous reads (`Read`, `ReadLine`, `ReadToEnd`) on an
empty pipe that is still open throw `InvalidOperationException` with a message that
asks for the asynchronous form. All current commands already read
asynchronously. When a reader ends before its writer, the next
write throws `HackIOException` ("Broken pipe"), which the command can
ignore; the stage then ends with code `141` if it lets the
exception through, like `SIGPIPE`.

**Streams.** The input of the first stage is empty (immediate end of stream,
like `/dev/null`): keyboard input to a running command is not
planned in this phase, `nano` keeps its full screen. The output of the
last stage and the error output of **all** stages go to the
terminal **as they are produced**, no longer only at the end.
`AppLaunchRequest` gains optional `StandardInput`,
`StandardOutput` and `StandardError` streams; when provided,
`RunTerminalAsync` uses them in place of its buffers, and
`AppLaunchResult.StandardOutput`/`StandardError` stay empty. Without streams
provided, the current buffered behavior is kept for other
callers.

**Redirections.** The shell, not the command, opens the target, before
launching the stage, with the identity and capabilities of the app that hosts the
shell (the Terminal), and under file permissions (D16). `>` creates or empties the
file, `>>` creates or appends. Several redirections in the same
command are opened in order (each creates or empties its file) and the
last one receives the output, as on Linux. A failing redirection
prints `hsh: <target>: <reason>`, does not launch the stage and gives code `1`.
Intended consequence, faithful to Linux: `sudo echo x > /etc/f` fails, because the
redirection is not elevated. A redirected stage that is not the last
of a pipe sends its output to the file, and the next stage reads an empty
input.

**`&&`.** The next pipeline runs only if the exit code of the
previous one is `0`. A pipeline's code is that of its last stage (no
`pipefail`). The line's code is that of the last pipeline executed.

**Exit codes** (taken from ADR 0014): `127` command not found, `2`
syntax error, `130` interruption, `1` unhandled fault, `141` broken pipe.

**Interruption.** Ctrl+C cancels all stages of the current pipeline and
stops the list.

**Processes.** Each stage is a child process of the shell's process
(`AppLaunchRequest.ParentPid`). The shell's process is that of the app that
hosts it: the Terminal window for the terminal, the calling app for
`ExecuteCommandIntent`. `ps` therefore shows the tree `terminal` → `grep`, `wc`.

**Aliases.** Alias expansion remains a shell step (as on Linux)
and applies to the first word of each command, before resolution. Their
file does not change.

### 5. Per-process environment

A process carries three new pieces of data, fixed at its creation:

- its **arguments** (`argv`, command name included);
- its **current directory**, canonical absolute path;
- its **environment**, ordinal dictionary name → value.

They live in a separate table, `IProcessEnvironmentTable`
(`Shared/HackerOs.Simulation.Abstractions/Processes/`), indexed by PID and
emptied when the process ends. `ProcessRecord` remains an immutable
snapshot and does not change.

`ProcessStartRequest` and `AppLaunchRequest` gain `Environment` (optional)
and reuse `Arguments` and `WorkingDirectory`. At creation:

1. **with a parent:** copy of the parent's directory and environment at
   creation time, then application of the values provided by the
   request;
2. **without a parent** (launch from the desktop, service): login environment
   (below) and home folder.

A later change to the parent never touches a child already created, and
vice versa. A process can only change **its own** directory and
**its own** environment (section 6). Changing those of its parent shell
goes through the controlled access of ADR 0051.

**Login environment**, built when the session opens:

| Variable | Value |
|---|---|
| `HOME` | home folder |
| `USER`, `LOGNAME` | login name |
| `SHELL` | `/bin/hsh` |
| `PATH` | `/bin:/usr/bin` (informational: resolution goes through the catalog) |
| `PWD` | current directory, kept up to date by the shell |
| `LANG` | `en_US.UTF-8` |

Then the `NAME=value` lines of **`/etc/environment`** (text file, permissions
`0644`, owner root, editable by an administrator, like
`pam_env`), which add or replace variables, except `HOME`, `USER` and
`LOGNAME`. Empty lines and `#` lines ignored, invalid line ignored with a
warning in the log (D17). The shell adds `TERM=xterm-256color`
and `OLDPWD` to its own environment. A persistent user environment
(`~/.profile`) is out of scope, for lack of scripts.

**`/proc`** (complements ADR 0046): `/proc/<pid>/cmdline` gives the arguments
separated by `\0` (the app identifier when there are none), `environ` the
`NAME=value` pairs separated by `\0` (permissions `0400` unchanged), `cwd` is a
link to the real current directory.

### 6. `HackEnvironment` in the SDK

`Shared/HackerOs.AppSdk/HackEnvironment.cs`, static, modeled on
`System.Environment`, and read from the ambient context of ADR 0043:

```csharp
public static class HackEnvironment
{
    public static string CurrentDirectory { get; set; }        // set: existing path, otherwise HackDirectoryNotFoundException
    public static string? GetEnvironmentVariable(string name);
    public static IReadOnlyDictionary<string, string> GetEnvironmentVariables();
    public static void SetEnvironmentVariable(string name, string? value); // null removes
    public static string[] GetCommandLineArgs();
    public static int ProcessId { get; }
    public static string UserName { get; }
    public static string MachineName { get; }                  // first line of /etc/hostname
    public static string NewLine => "\n";
}
```

- All these operations target the current process only; no
  capability is needed, as on Linux.
- `CurrentDirectory` is the directory used by `HackPath` for relative
  paths: `HackExecutionContext.WorkingDirectory` now reads the
  process's directory.
- `/etc/hostname` (`0644`, root) is created by the file system bootstrap
  with `hackeros` if it is missing; the terminal prompt uses it
  instead of the hard-coded value.
- `TerminalExecutionContext.Environment` and `WorkingDirectory` remain, for
  compatibility, and give the process state at the start of the
  command.

### 7. Non-interactive shell

`ExecuteCommandIntent.CommandLine` is executed by `ShellInterpreter` with the
same grammar, in a shell without an interactive session: no history,
no shell access for commands (ADR 0051), output buffered in
`AppIntentDispatchResult`. The starting directory is
`ExecuteCommandIntent.WorkingDirectory`, otherwise that of the calling process.
The caller's `apps.launch` authorization is checked once for the line.

## Consequences

- A single implementation of the shell, testable without a browser, for the
  terminal and for apps.
- Quoted arguments reach commands intact.
- `ps`, `/proc` and commands see realistic processes: arguments,
  directory, environment, parent-child tree.
- Commands that read their input must remain asynchronous; this is
  already the case.
- Terminal output is displayed as it is produced, which changes the rendering of
  `ping` and `curl`.
- `;`, `||`, `<` and globbing remain to be done; they are clearly rejected.

## Implementation work items

See ADR 0052 for the full phase 3 table. Work items of this ADR:
`rebuild/3-process-environment` (sections 5 and 6, `/proc`) then
`rebuild/3-shell-interpreter` (sections 1 to 4 and 7).

## References

- ADR 0012 (processes), ADR 0014 (replaced), ADR 0042 (`sudo`), ADR 0043
  (ambient context), ADR 0046 (`/proc`), ADR 0051 (commands).
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 7.1 and 7.2.
- `Shared/HackerOs.AppSdk.Terminal/TerminalPanel.razor`, `ShellParser.cs`,
  `TerminalSession.cs`; `Platform/HackerOs.Platform.Core/Intents/AppIntentDispatcher.cs`;
  `Platform/HackerOs.Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs`;
  `Platform/HackerOs.Platform.Core/FileSystem/Virtual/ProcFileSystemProvider.cs`.
