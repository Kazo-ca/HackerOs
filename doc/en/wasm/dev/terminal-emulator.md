> [Version française](../../../fr/wasm/dev/terminal-emulator.md)

# Terminal emulator (xterm.js)

The contract on the side of apps and commands (`TerminalPanel`, `IHackTerminal`, password reading without echo, full-screen mode) is in [`terminal-api.md`](../framework/terminal-api.md).

## Purpose

`TerminalPanel` (`Shared/HackerOs.AppSdk.Terminal`) displays its shell in a real
character-grid terminal, xterm.js, the library of the legacy version
(ADR 0061). Commands can write ANSI colors, `nano` uses the alternate screen,
and the rendering looks like a Linux terminal.

The console (`Apps/System/HackerOs.Apps.Terminal`) uses `TerminalTabs`. The code
editor no longer uses `TerminalPanel` directly: it embeds the terminal that the
Terminal app registers for the `hackeros.terminal` contract (`TerminalEmbed`,
ADR 0063, [`embedded-components.md`](../framework/embedded-components.md)).

## Architecture

```text
clavier ─▶ xterm.js ──onData("\x1b[A")──▶ TerminalPanel.OnTerminalData
                                          └─ TerminalInputDecoder → TerminalKeyEvent
                                              ├─ commande plein écran active → TerminalFullScreenSession
                                              ├─ Ctrl+C pendant une commande → annulation, « ^C »
                                              └─ file de frappe → TerminalLineEditor / IHackShell
commande ─▶ TerminalOutputWriter ─▶ TerminalPanel.Write ("\n" → "\r\n") ─▶ xterm.js.write
```

| File | Role |
|---|---|
| `TerminalPanel.xterm.entry.js` | Bundle entry point: creates the `Terminal`, the `FitAddon`, the theme (`--hos-*` tokens), the key handler (copy, paste, tab shortcuts) and the size observer. |
| `build.mjs`, `package.json`, `package-lock.json` | Build `wwwroot/xterm/xterm.bundle.{js,css,LICENSE.txt}`. |
| `TerminalPanel.razor.js` | Component module: `mountTerminal(host, dotNet, options)` returns `write`, `focus`, `dimensions`, `dispose`. |
| `TerminalPanel.razor` | Line discipline: key routing, line execution, prompt, outputs, alternate screen. |
| `TerminalInputDecoder.cs` | Keyboard bytes → `TerminalKeyEvent` (arrows, `ESC[3~`, Ctrl+letter, Alt, multi-line paste). |
| `TerminalLineEditor.cs` | The line being edited and the sequences that redraw it, even wrapped over several rows. |
| `TerminalAnsi.cs` | Colored prompt, `ONLCR`, "cursor at start of line" tracking, rendering of a `TerminalScreenFrame`. |

### Theme

The background, text and cursor come from the design tokens read on the host
element (`--hos-bg-base`, `--hos-text-primary`, `--hos-accent`); the font comes
from `--hos-font-mono` (`0.875rem`, 14 px at 100 %). The 16-color ANSI palette is
the legacy one, with HackerOS's accent green. The frame style is in
`TerminalPanel.razor.css`; the grid itself is drawn by xterm.js.

Accessibility ([`accessibility.md`](accessibility.md), ADR 0105): `TerminalPanel` reads
`AppContext.Accessibility` on opening and follows `Changed`. The font size is reread from the
panel, whose `rem` size follows the text size; in high contrast, xterm.js receives a
palette in which every color reaches 7:1 on a black background, a yellow cursor and selection; in
reduced motion, the cursor does not blink. `applyAccessibility` (object returned by
`mountTerminal`) applies a change and refits the grid.

## Keyboard usage

| Key | Effect |
|---|---|
| ← → Home End, Ctrl+A/E/B/F | Move the cursor within the line. |
| Backspace, Delete, Ctrl+H | Erase one character. |
| Ctrl+W, Alt+Backspace | Erase the word before the cursor. |
| Ctrl+U / Ctrl+K | Erase to the start / to the end. |
| ↑ ↓, Ctrl+P/N | Shell history. |
| Tab | Completion by the shell; a second Tab lists the candidates under the line, then the prompt returns with the line ([`terminal-console.md`](terminal-console.md)). |
| Ctrl+C | With a selection: copy. Otherwise, during a command: cancels it (`^C`). At the prompt: abandons the line. |
| Ctrl+Shift+C / Ctrl+V / Ctrl+Shift+V | Copy / paste. A multi-line paste executes each line. |
| Ctrl+L | Clears the screen and keeps the current line. |
| Ctrl+D | Empty line: `exit`. Otherwise: erases the character under the cursor. |

Copy raises a real `copy` event (`document.execCommand("copy")`) instead of
writing `navigator.clipboard`: the OS clipboard bridge receives it as a native
copy (origin `GuestNative`) and sends it to the real machine only if
`/etc/hackeros/clipboard.config` allows it ([`clipboard.md`](clipboard.md),
ADR 0065).

Chrome reserves Ctrl+W, Ctrl+N and Ctrl+T (close tab, new window, new tab)
as long as HackerOS is not installed as a PWA: these keys then do not reach the
terminal.

While a line runs, keys go to the **standard input** of its commands
(ADR 0064 §6, `TerminalInputReader`), like a tty in canonical mode: characters
are echoed, Backspace and Ctrl+U erase, Enter sends the line (`read`, `cat`,
interactive `sh` read it), Ctrl+D on an empty line is an end of input read
once, Ctrl+C interrupts. What the command has not read comes back at the next
prompt as type-ahead (4096 keys at most): `sleep 5` then `ls` + Enter typed
during the wait runs `ls` afterwards, as on Linux.

An unfinished command (`for f in *; do` + Enter, open quote, trailing `|`,
here-document) displays bash's continuation prompt `>` (`PS2`): the terminal
asks the shell through `IHackShell.NeedsMoreInput`, accumulates the lines, then
executes them together; Ctrl+C abandons. The history keeps a single line
(`TerminalHistory.ToSingleLine`: `for f in *; do echo $f; done`).

## Full-screen commands

The `IFullScreenTerminalSession` contract does not change. On entry, the panel
writes `ESC[?1049h`; each `TerminalScreenFrame` is drawn row by row (cut to the
width, without wrapping), then the cursor is placed or hidden. Since ADR 0064,
a row may contain SGR sequences (colors, reverse video: `htop`, `nano` bars):
`TerminalAnsi.FitToColumns` keeps them whole without counting them in the
width, and an `ESC[0m` ends the row;
on exit, `ESC[?1049l` returns the normal screen as it was. The grid size
(`UpdateTerminalViewport`) is given to the session from the start of each
command, and a resize sends `TerminalKey.Resize`.

## Decisions

- **xterm.js is only a screen and a keyboard.** Line editing, completion and
  history stay in C#: the shell and its contract (`IHackShell`) do not change,
  and the behavior is tested without a browser.
- **Versioned bundle**, like Monaco: no npm in the .NET build.
  `package.json` and `package-lock.json` are removed from the published content
  (otherwise they collide with those of CodeEditor in the Ecosystem host's
  publication).
- **Error output without color**, like a real terminal; only the terminal's own
  messages (shell startup failure, `Fault:`) are in red and the approval banners
  in yellow.
- **Colored prompt like bash on Debian**: `user@host` in bold green, the folder
  in bold blue.
- **`nano` follows GNU nano** (ADR 0064 §7): `^O` asks for the file name
  (pre-filled) and also serves as "save as", because xterm.js sends the same
  byte `0x0F` for Ctrl+O and Ctrl+Shift+O; `^S`, `^W`/`^F`, `^K`/`^U`, `^C`,
  `^G`, `^A`/`^E`, `^Y`/`^V`; title bar and two rows of shortcuts in reverse
  video; the cursor line scrolls horizontally (`<` and `>` at the edges); tabs
  on 8 columns. Ctrl+W remains reserved by Chrome outside a PWA: `^F` searches
  too.

## Updating xterm.js

```bash
cd wasm2/HackerOs/Shared/HackerOs.AppSdk.Terminal
# modifier les versions dans package.json
npm install && npm run build
```

Then commit `package-lock.json` and `wwwroot/xterm/`.

## Tests

- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalInputDecoderTests.cs`
- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalLineEditorTests.cs`
- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalAnsiTests.cs`
- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalInputReaderTests.cs` (standard input)
- `Tests/HackerOs.Commands.Nano.Tests/NanoCommandTests.cs` (pre-filled Write Out, cut/paste, search, horizontal scrolling)
- `VirtualScreen.cs` (in the same project): a small character-grid screen that
  applies the written sequences, to check where the text and cursor end up
  (wrapping, return to the start of the prompt block, frames).

Verified in Chromium on a Release publication: commands, wrapping of a long
line, editing in the middle, history, completion and list on the second Tab,
Ctrl+C, Ctrl+L, new tab and `exit`, `nano` (alternate screen then return),
integrated terminal of the code editor.

## Task list

- [x] Embed xterm.js 6.0 and the fit addon (npm/esbuild, versioned bundle, license, MSBuild target).
- [x] Mount the terminal from the component module, theme from the design tokens.
- [x] Decode keyboard bytes into `TerminalKeyEvent` (`TerminalInputDecoder`).
- [x] Readline-style line editing and redraw over several rows (`TerminalLineEditor`).
- [x] Command output to xterm.js with `ONLCR`, colored prompt.
- [x] Alternate screen for full-screen commands.
- [x] Copy, paste, tab shortcuts left to the interface.
- [x] Virtual keyboard through the same path.
- [x] Unit tests, verification in a browser.
- [x] Standard input of commands in canonical mode (`TerminalInputReader`, ADR 0064).
- [x] Colors in full-screen frames; `nano` with GNU nano's shortcuts (ADR 0064).
- [x] Echo off for passwords (`ITerminalInputMode`, `ReadSecretLineAsync`, ADR 0120).
- [x] ADR 0061, this page, index.
