> [Version française](../../../fr/wasm/framework/terminal-api.md)

# Terminal API for apps and commands

This document describes what an app or a command sees of the terminal: the `TerminalPanel` component, the `IHackTerminal` interface, reading a password without echo and full-screen mode. The internal workings (xterm.js, line discipline, rendering, shortcuts) are in [`dev/terminal-emulator.md`](../dev/terminal-emulator.md).

## `TerminalPanel` component

`TerminalPanel` implements `IHackTerminal`: a host can launch
commands in it (displayed at the prompt, after the keys already typed), change
folder with a visible `cd` and read the displayed text without control
sequences (`TerminalAnsi.StripControlSequences`).

`TerminalPanel.PushVirtualKey` (virtual keyboard) follows the same path as the
physical keyboard; with `UseVirtualKeyboard`, xterm.js's hidden input area
receives `inputmode="none"`.

## A command's standard input

While a line is running, keys go to the command's standard input, like a tty in canonical mode: `read`, `cat` and interactive `sh` read the lines sent with Enter, Ctrl+D on an empty line signals end of input, Ctrl+C interrupts. What the command has not read comes back at the next prompt, within the limit of 4096 keys. The details of the line discipline are in [`dev/terminal-emulator.md`](../dev/terminal-emulator.md).

## Reading a password without echo

A command can turn echo off, like the `ECHO` flag of `termios` (ADR
0120 §4): `TerminalInputReader` implements `ITerminalInputMode`, and
`TerminalExecutionContext.ReadSecretLineAsync(prompt)` sets `EchoEnabled` to
`false` for the duration of one line, like `getpass(3)`. The line discipline
(`HandleInputKey`) then displays neither the characters nor their erasure
(Backspace, Ctrl+U); Enter still moves to the next line. `passwd` reads
passwords this way.

## Full-screen commands

A full-screen command uses the SDK's `IFullScreenTerminalSession` contract: `EnterAlternateScreenAsync` switches to the alternate screen, `RenderAsync` receives a `TerminalScreenFrame` (lines, cursor, accessible label) on each frame, and `ReadKeyAsync` waits for the next key or the next resize. A row can carry color or reverse video sequences (SGR); it ends with `ESC[0m`. Rendering on the terminal side is in [`dev/terminal-emulator.md`](../dev/terminal-emulator.md#full-screen-commands).

## IHackTerminal

`IHackTerminal` (file `Shared/HackerOs.AppSdk/Components/IHackTerminal.cs`) has the members `RunCommandAsync(commandLine)`, `SetWorkingDirectoryAsync(path)`, `SendInputAsync(text)`, `ReadOutput(maxCharacters)` (65,536 characters by default), `ClearAsync()` and `FocusAsync()`.

Commands sent by a host (`IHackTerminal.RunCommandAsync`) are
never continued.
