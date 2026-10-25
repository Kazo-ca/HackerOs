# HackerOS Terminal Application (`org.hackeros.terminal`)

## Purpose

`Apps/System/HackerOs.Apps.Terminal/` provides the interactive command-line interface and terminal emulator for HackerOS v3, implemented as a first-party `AppKind.Window` application. Since the CodeEditor rework (2026-08-21), the actual session/shell/rendering logic lives in the shared `Shared/HackerOs.AppSdk.Terminal/` library so the Code Editor's embedded terminal can reuse it; this app is now a thin host around that library.

## Architecture

- **`TerminalWindow.razor`** (`Apps/System/HackerOs.Apps.Terminal/`): Thin window host inheriting `WindowAppBase`. Its entire body is `<TerminalPanel AppContext="AppContext" CallerAppId="org.hackeros.terminal" />` — all terminal behavior below lives in the shared library, not this app.
- **`TerminalPanel.razor/.css/.js`** (`Shared/HackerOs.AppSdk.Terminal/`): The actual terminal UI — prompt (`user@hackeros:path$`), output buffer with stdout/stderr/system formatting, input line, history/alias/autocomplete, and full-screen/alt-screen rendering via `TerminalScreen`. Takes `AppContext`, a `CallerAppId` (so capability checks and intent dispatch are attributed to whichever app embeds it), an `InitialUsername`, and an optional `InitialCwd`. The Code Editor embeds its own `TerminalPanel` instances (`CallerAppId="org.hackeros.code-editor"`) scoped to its currently open folder — see [`code-editor.md`](../code-editor.md).
- **`TerminalSession.cs`** (`Shared/HackerOs.AppSdk.Terminal/`): Encapsulates active session state: username, current working directory (`Cwd`), environment variables (`PATH`, `USER`, `HOME`, `TERM`, `SHELL`), command history navigation, and last exit status.
- **`ShellParser.cs`** (`Shared/HackerOs.AppSdk.Terminal/`): Tokenizes and parses command lines according to ADR 0014 syntax rules (quotes, escaping, whitespace) and returns structured syntax diagnostics.
- **`TerminalCommandResolver.cs`** (`Shared/HackerOs.AppSdk.Terminal/`): Resolves command names and static catalog aliases against `AppKind.Terminal` manifests registered in `AppCatalog`.
- **`TerminalFullScreenSession.cs`** (`Shared/HackerOs.AppSdk.Terminal/`): Implements `IFullScreenTerminalSession` for alt-screen apps (e.g. `nano`) launched from within a `TerminalPanel`.
- **`app.manifest.json`** (`Apps/System/HackerOs.Apps.Terminal/`): Manifest declaring reverse-domain ID `org.hackeros.terminal`, required capabilities (`apps.launch`, `filesystem.read`, `filesystem.write`, `process.read`, `process.write`, `notifications.post`, `settings.read`, `settings.write`), and single-instance user policy.

## Interactive Controls & Shortcuts

- **Command Execution**: Enter submits input; built-in commands include `clear`, `cd`, `pwd`, `help`.
- **Catalog Execution**: Dispatches `ExecuteCommandIntent` via `AppIntentDispatcher` for external commands registered in `AppCatalog`.
- **History Navigation**: Up and Down arrow keys cycle through previous command lines.
- **Buffer Management**: `Ctrl+L` clears the terminal output buffer.
- **Command Interruption**: `Ctrl+C` sends cancellation to running command execution tasks.
- **Tab Completion**: `Tab` key auto-completes command names against built-in and catalog commands.

## Key Decisions

- **WindowAppBase Inheritance**: Implemented as a full `AppKind.Window` app, utilizing platform windowing and execution context rather than raw DOM elements.
- **Command Resolution**: Resolves commands dynamically against `AppCatalog` manifests, ensuring capability checks and process boundaries are respected.

## Task Checklist

- [x] `P2-APPSTD-001` - `P2-APPSTD-005` Define first-slice app project standard.
- [x] `P2-TERM-001` Create complete Window manifest for `org.hackeros.terminal`.
- [x] `P2-TERM-002` Define terminal session state (user, CWD, environment, history, exit status).
- [x] `P2-TERM-003` Implement `ShellParser` tokenizer/parser to ADR 0014 syntax rules.
- [x] `P2-TERM-004` & `P2-TERM-004A` Implement command resolution for catalog commands and static aliases.
- [x] `P2-TERM-005` Connect `TerminalExecutionContext` streams and cancellation.
- [x] `P2-TERM-006` - `P2-TERM-008` Implement prompt, line editing, history, completion, clear buffer, and error resilience.
- [x] `P2-TERM-009` Add unit tests in `Tests/HackerOs.Apps.Terminal.Tests/`.