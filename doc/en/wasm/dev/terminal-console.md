> [Version française](../../../fr/wasm/dev/terminal-console.md)

# Console: path completion, `exit` and tabs

## Purpose

Work item `rebuild/4-terminal` of phase 4 (ADR 0058, section 4). The console
reaches parity with a Linux terminal:

- **Tab also completes paths**, and it is the shell that does it
  (`IHackShell.CompleteAsync`), not the terminal emulator;
- **`exit [n]`** is a catalog command that ends the shell;
- **several tabs**, each with its own `hsh` shell.

```text
Terminal (fenêtre, PID 12)
 ├─ hsh (PID 13)        ← onglet « projets »
 │   └─ ls (PID 20)
 └─ hsh (PID 14)        ← onglet « ~ »
```

Rendering (character grid, colors, line editing, alternate screen) is described
in [`terminal-emulator.md`](terminal-emulator.md) (ADR 0061).

## Tab completion

Computed by `ShellCompletion` (Platform Core) in the shell's execution context:
a relative path starts from the **shell's** directory, and reading the folder
goes through the public `HackDirectoryInfo` API with the shell's permissions,
like an `ls` it would launch.

| Word under the cursor | Candidates |
|---|---|
| First word of a command (start of line, after `\|` or `&&`), without `/` | Installed commands and static aliases, case-sensitive. |
| Any other word, or first word with a `/` (`./build.sh`) | Entries of the folder designated by the start of the word. |
| `~` alone | `~/`. |
| Word in quotes | Nothing (the quote would have to be closed). |

Path rules:

- the typed start (`~/`, `../`, `docs/`) is kept as is, only the name is
  completed; `~` is expanded with the shell's `HOME` for reading;
- a folder ends with `/` (no space is added after it);
- hidden entries are offered only if the typed name starts with `.`;
- characters that the parser would split or expand (space, `|`, `&`, `;`, `<`,
  `>`, `$`, quotes, `*`, `?`...) are escaped by `\`: `cat My` gives
  `cat My\ Notes.txt `; an already escaped word (`My\ N`) is recognized as a
  single word;
- a redirection ends a word: `echo x >no` completes `notes.txt`;
- a missing or unreadable folder (permissions, capability) offers nothing,
  without an error.

`HackShellCompletion.DisplayCandidates` gives what the terminal lists on the
second Tab: the last segment, without its escapes (`My Notes.txt`, `sub/`),
like `bash`.

## `exit [n]`

| Call | Effect |
|---|---|
| `exit` | The shell exits with `$?`. |
| `exit n` | Exits with `n` modulo 256 (`exit 300` → `44`, `exit -1` → `255`). |
| `exit abc` | `exit: abc: numeric argument required`, exits with `2`. |
| `exit 1 2` | `exit: too many arguments`, code `1`, the shell stays. |
| `exit 4 \| cat` | In a pipe, the request reaches a copy of the shell and is lost, like `bash`'s subshell. |

The command (`org.hackeros.commands.exit`, capability `shell.control`) calls
`IShellSession.Exit(int?)`. `ShellSession.RequestExit` notes the status; the
interpreter does not execute the rest of the line (`exit 0 && echo late` prints
nothing), then the shell host (`ShellHostService`) ends the `hsh` process with
that status. `IHackShell.HasExited` becomes true and `ExecuteAsync` then throws
`ObjectDisposedException`.

**Ctrl+D** on an empty line, with no command running, does the same thing: the
terminal prints `exit` and ends the shell with `$?`.

`TerminalPanel.OnExitRequested` is called when the shell has exited. Without a
subscriber, the panel stays open and prints `[hsh exited with status n]`. A
host that embeds the terminal by contract (ADR 0063) also receives the
`IHackTerminal.Exited` event: the code editor closes the integrated terminal's
tab, and hides the dock for the last one.

## Tabs

`TerminalTabs` (`Shared/HackerOs.AppSdk.Terminal`) is a reusable component:
each tab is a `TerminalPanel`, hence a distinct `hsh` with its directory,
environment, history and `$?`. The Terminal window is now only a host of
`TerminalTabs`.

| Action | Effect |
|---|---|
| `+`, Ctrl+Shift+T | New tab after the active tab, in the active tab's directory. |
| `×`, Ctrl+Shift+W, `exit`, Ctrl+D | Closes the tab; its shell and commands stop (panel disposal). The tab to the right (otherwise to the left) becomes active. |
| Ctrl+PgUp, Ctrl+PgDn | Previous, next tab, looping. |
| Last tab closed | `OnLastTabClosed`: the Terminal window stops its own process (`Processes.StopAsync` on its PID), which closes the window. |

Title of a tab (`TerminalPanel.Title`, `OnTitleChanged`): name of the running
command, otherwise last segment of the prompt's directory (`~`, `projets`,
`/`).

The shortcuts are intercepted by `TerminalTabs.razor.js`, which cancels the
browser's action. A browser may keep some shortcuts for its own tabs (Chrome
keeps Ctrl+Shift+T and Ctrl+Shift+W outside an installed app); the `+` and `×`
buttons always work.

Tab does not move the browser's focus: xterm.js receives it and sends it to the
terminal (ADR 0061). xterm.js's key handler lets the tab shortcuts above bubble
up to `TerminalTabs`.

### API

| Member | Role |
|---|---|
| `TerminalTabs.AppContext`, `CallerAppId`, `InitialCwd` | As for `TerminalPanel`, for the first tab. |
| `TerminalTabs.OnLastTabClosed` | The last tab has closed. |
| `TerminalTabs.OpenTabAsync`, `CloseActiveTabAsync`, `SwitchTabAsync(±1)` | The shortcut actions. |
| `TerminalTabSet`, `TerminalTabState` | Tab ordering rules, testable without rendering. |
| `TerminalPanel.Title`, `OnTitleChanged`, `WorkingDirectory`, `FocusAsync` | What a tab host needs. |

## Decisions

- **`exit` is a catalog command**, like `cd` (ADR 0051), and not a hard-coded
  name in the terminal: it goes through `IShellSession.Exit`, a member with a
  default implementation (`NotSupportedException`) so as not to break other
  implementations.
- **Candidates = full words** (`~/docs/notes.txt`) so that `ApplyTo` and
  `CommonPrefix` stay simple; the short display is `DisplayCandidates`.
- **No completion inside quotes** and no `~user`: absent from the legacy
  version, they would complicate word splitting.
- **The window closes by stopping its process**: the SDK has no "close my
  window" API, and the desktop already closes the window of a stopped process.
- **CodeEditor keeps its own tabs** (ADR 0058); since ADR 0063, each embeds the
  Terminal app's terminal by contract, and the editor reacts to `exit` through
  `IHackTerminal.Exited`.
- **`;` is not recognized by the parser**: completion treats it as a separator
  to be ready, but the examples use `&&`.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellInterpreterTests.HostedShell.cs`:
  paths (relative, `~`, `../`, absolute, escapes, hidden, redirections, `./`,
  missing folder, case), shell directory, unreadable folder,
  `DisplayCandidates`, `exit` (statuses, modulo, non-numeric, end of process,
  rest of the line, pipe, too many arguments, `$?`).
- `Tests/HackerOs.Commands.Tests/CoreCommandsTests.cs`: `ExitCommand`.
- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalTabSetTests.cs`: order,
  closing, tab switching.
- `Tests/HackerOs.Ecosystem.Tests/BuildKnownLazyAppsTests.cs`: `exit` in the
  catalog (75 apps, 63 commands since `stress`, ADR 0070).
- Verification in the browser (Release publication): `cd pro` + Tab →
  `cd projets/`, second Tab lists `alpha/  notes.txt`, focus stays on the line,
  a new tab opens in `~/projets`, `exit` closes the tab then the window, no
  error.

## Task list for work item `rebuild/4-terminal`

- [x] Path completion in `ShellCompletion`, with the shell's permissions and directory.
- [x] `HackShellCompletion.DisplayCandidates`, short list on the second Tab.
- [x] `IShellSession.Exit`, `ShellSession.RequestExit`, stop of the line, end of the `hsh` process.
- [x] `exit [n]` command in `HackerOs.Commands`.
- [x] `TerminalTabs`, `TerminalTabSet`, shortcuts, tab titles; Terminal window on `TerminalTabs`, closed with the last tab.
- [x] Ctrl+D ends the shell; Tab no longer moves focus.
- [x] CodeEditor closes the tab whose shell exits.
- [x] Tests, this document, `shell-host.md`, `shell-commands.md`, `app-sdk.md`, plan and index.
