> [English version](../../../../../en/wasm/dev/old/commands/terminal-commands.md)

# Core Terminal Command Applications (`Apps/Commands/`)

## Purpose

`Apps/Commands/` contains five independent first-party CLI command applications for HackerOS v3, implemented as modular `AppKind.Terminal` applications inheriting `TerminalAppBase`.

## Architecture & Commands

Each command is an independently versioned assembly under `Apps/Commands/Kazo.Os.Commands.{Name}/` owning its `app.manifest.json` and entry point:

1. **`pwd` (`org.hackeros.cmd.pwd`)** — [Kazo.Os.Commands.Pwd](file:///c:/Users/clefw/repos/source/HackerSimulator/wasm2/KazoOs/Apps/Commands/Kazo.Os.Commands.Pwd/)
   - Prints current working directory (`context.WorkingDirectory`) to `StandardOutput`. Exit code 0.
2. **`echo` (`org.hackeros.cmd.echo`)** — [Kazo.Os.Commands.Echo](file:///c:/Users/clefw/repos/source/HackerSimulator/wasm2/KazoOs/Apps/Commands/Kazo.Os.Commands.Echo/)
   - Joins arguments with spaces and outputs to `StandardOutput`. Exit code 0.
3. **`cd` (`org.hackeros.cmd.cd`)** — [Kazo.Os.Commands.Cd](file:///c:/Users/clefw/repos/source/HackerSimulator/wasm2/KazoOs/Apps/Commands/Kazo.Os.Commands.Cd/)
   - Navigates working directory (`~`, relative, absolute) and queries entry status via `context.App.FileSystem.StatAsync`. Outputs updated path on success or error diagnostics on `StandardError`. Exit code 0 or 1.
4. **`cat` (`org.hackeros.cmd.cat`)** — [Kazo.Os.Commands.Cat](file:///c:/Users/clefw/repos/source/HackerSimulator/wasm2/KazoOs/Apps/Commands/Kazo.Os.Commands.Cat/)
   - Streams file text content via `context.App.FileSystem.ReadAsync`. Outputs file contents to `StandardOutput` and permission/missing file errors to `StandardError`. Exit code 0 or 1.
5. **`ls` (`org.hackeros.cmd.ls`)** — [Kazo.Os.Commands.Ls](file:///c:/Users/clefw/repos/source/HackerSimulator/wasm2/KazoOs/Apps/Commands/Kazo.Os.Commands.Ls/)
   - Lists directory entries via `context.App.FileSystem.EnumerateAsync`. Supports `-a` (hidden entries) and `-l` (long format with permissions, size, and timestamp). Sorted deterministically. Exit code 0 or 1. Also registers `dir` static alias.

## Capabilities & Security

- Commands declare required capabilities in `app.manifest.json` (e.g. `filesystem.user-home.read`).
- Execution is capability-checked and sandboxed by `AppExecutionContext` gateways without mutating global static state.

## Task Checklist

- [x] `P2-CMD-001` Implement `Kazo.Os.Commands.Pwd` (`org.hackeros.cmd.pwd`).
- [x] `P2-CMD-002` Implement `Kazo.Os.Commands.Ls` (`org.hackeros.cmd.ls`) with `-a`/`-l` flags and sorting.
- [x] `P2-CMD-003` Implement `Kazo.Os.Commands.Cd` (`org.hackeros.cmd.cd`) with path navigation.
- [x] `P2-CMD-004` Implement `Kazo.Os.Commands.Cat` (`org.hackeros.cmd.cat`) for file streaming.
- [x] `P2-CMD-005` Implement `Kazo.Os.Commands.Echo` (`org.hackeros.cmd.echo`) for argument output.
- [x] `P2-CMD-006` Add unit test suite in `Tests/Kazo.Os.Commands.Tests/`.
