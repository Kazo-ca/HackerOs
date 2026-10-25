# ADR 0061: xterm.js as the terminal emulator

## Status

Accepted on 2026-10-11.

- **Plan context:** after phase 4 (rebuild finished), request from
  Benoit on 2026-10-11: a more realistic terminal rendering, with the same
  library as the legacy version (`src/apps/terminal.ts`, xterm.js 5.3).
- **Supersedes no ADR.** ADR 0050 (output displayed while the
  command runs), 0051 section 4 (the terminal interprets the
  `clear` sequences) and 0058 section 4 (completion by the shell, tabs)
  remain true; only the means of achieving them changes.

## Context

Until now `TerminalPanel` displayed the output in HTML `<div>` elements, one per
line, and the command line in an `<input>`. Consequences:

- ANSI colors were stripped (`TerminalControlSequenceDecoder` kept
  only the screen clearing);
- no real cursor, no character grid: a line that was too long
  wrapped like HTML text, selection picked up the prompts;
- `nano` went through a second HTML rendering (`TerminalScreen`), one `<span>` per
  character.

The code editor already uses the same library as the legacy (Monaco, ADR
0020), embedded by a versioned npm/esbuild bundle. The terminal can follow
the same model.

## Decision

### 1. xterm.js, embedded in the terminal library

`Shared/HackerOs.AppSdk.Terminal` embeds `@xterm/xterm` 6.0.0 and
`@xterm/addon-fit` 0.11.0, versions pinned by `package-lock.json`.
`build.mjs` (esbuild) produces `wwwroot/xterm/xterm.bundle.js`,
`xterm.bundle.css` and `xterm.bundle.LICENSE.txt`, versioned in the repository:
the .NET build does not run npm. An MSBuild target fails the build if
the bundle, the lockfile or the license is missing (as for Monaco).

The bundle is imported only by `TerminalPanel.razor.js`, the component's own
module; no host references a script (host rule of
`AGENTS.md`). The apps (Terminal, CodeEditor, third-party apps) do not change:
they use `TerminalPanel` or `TerminalTabs` as before, without
`IJSRuntime` (ADR 0055).

### 2. xterm.js plays the role of the hardware, line discipline stays in C#

xterm.js is used like a physical terminal: it displays the bytes written to
it (ECMA-48 sequences included) and returns the keyboard bytes
(`onData`). Everything else is in C#, in the library:

| Role on Linux | Here |
|---|---|
| Terminal screen and keyboard | xterm.js (`TerminalPanel.xterm.entry.js`) |
| Key decoding (`0x7f`, `ESC [ A`...) | `TerminalInputDecoder` |
| Line discipline (`ONLCR`, `^C`, typing queue) | `TerminalPanel` |
| readline (editing, history, completion) | `TerminalLineEditor` + `IHackShell` |
| Full-screen program (smcup, cursor addressing, rmcup) | `TerminalAnsi.RenderFrame` |

Command outputs go to xterm.js with no transformation other than
`\n` → `\r\n` (the `ONLCR` flag of a tty). Colors, `clear`
(`ESC[H ESC[2J ESC[3J`) and any other sequence are therefore interpreted by
the emulator, as on Linux.

### 3. Unchanged contract for full-screen commands

`IFullScreenTerminalSession` and `TerminalScreenFrame` do not change. The
panel translates each frame into sequences: `ESC[?1049h` on entry, one
line written in place per row, cursor positioned, `ESC[?1049l` on
exit (the normal screen and the cursor come back as before the command).
`TerminalScreen` stays in the library (BrowserHarness scenarios) but
`TerminalPanel` no longer uses it.

### 4. Shortcuts left to the browser and the interface

The xterm.js key handler lets through: the tab shortcuts of `TerminalTabs`
(Ctrl+Shift+T/W, Ctrl+PageUp/PageDown), Ctrl+V and
Ctrl+Shift+V (paste through the browser's `paste` event), Ctrl+C when
text is selected and Ctrl+Shift+C (copy). Without a selection, Ctrl+C sends
`^C` to the terminal.

## Consequences

- ANSI colors, blinking cursor, selection and copy, line wrapping at
  the grid width, 5000 lines of scrollback.
- The bundle adds about 340 KB (uncompressed) to the terminal
  library. It is loaded only when a terminal is opened.
- Unit tests cover decoding, editing and rendering (with a
  small virtual test screen); the real rendering is verified in a browser.
- `TerminalControlSequenceDecoder` stays a public API of the library
  but `TerminalPanel` no longer uses it.
- The screen reader no longer receives a `role="log"` region: xterm.js has a
  screen reader mode (`screenReaderMode`) that is not enabled. To be
  taken up with the accessibility work item.
- Out of scope: standard input for commands (typing during
  a command waits for the next prompt), wide characters (CJK,
  emoji on two cells).

See [`terminal-emulator.md`](../terminal-emulator.md).
