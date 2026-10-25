# Wave 5: Remaining Utility Apps and Commands Migration Report

## Overview

This report documents the completion of **Wave 5** (Utility Applications, Filesystem Commands, Process Management Commands, Utilities, and Multi-Monitor Requirement Position).

---

## Migrated Component Matrix

| Task ID | Component Name | Legacy Source | C# Project / Location | Test Coverage | Status |
|---|---|---|---|---|---|
| `P4-W5-APP-001` | Calculator | `src/apps/calculator.ts` | `Apps/System/HackerOs.Apps.Calculator/` | `CalculatorEngine` arithmetic, memory & sqrt tests | **MIGRATED** |
| `P4-W5-APP-002` | Hack Paint | `src/apps/hack-paint.ts` | `Apps/System/HackerOs.Apps.HackPaint/` | 14 Wave 5 tests plus Chromium `Hack_paint_canvas_draws_undoes_redoes_crops_and_pans` | **REOPENED / IN PROGRESS** — canvas now renders the authoritative model with pointer capture; VFS/image round trips, dialogs, and full-app pixel E2E remain |
| `P4-W5-APP-003` | Theme picker / docs | `src/core/themes/` | `Apps/System/HackerOs.Apps.Settings/`, `HackerOs.Theming.*`, `docs/theming.md` | catalog/codec/Settings integration | **MIGRATED + EXPANDED** (reviewed built-ins only; legacy arbitrary `customCss` intentionally excluded) |
| `P4-W5-APP-004` | Multi-Monitor Position | `src/core/multi-monitor.ts` | `docs/adr/0022-multi-monitor-requirement-position.md` | ADR 0022 (DECISION: D-016 Explicit Exclusion) | **MIGRATED** |
| `P4-W5-CMD-001` | `mkdir`, `touch`, `rm`, `cp`, `mv` | `src/commands/` | `Apps/Commands/HackerOs.Commands.{Mkdir,Touch,Rm,Cp,Mv}/` | FileSystem gateway integration tests | **MIGRATED** |
| `P4-W5-CMD-002` | `chmod` | `src/commands/` | `Apps/Commands/HackerOs.Commands.Chmod/` | Permission bit mask parsing & update tests | **MIGRATED** |
| `P4-W5-CMD-003` | `find`, `grep`, `head`, `tail`, `sort`, `wc`, `diff` | `src/commands/` | `Apps/Commands/HackerOs.Commands.{Find,Grep,Head,Tail,Sort,Wc,Diff}/` | Text processing and regex matching unit tests | **MIGRATED** |
| `P4-W5-CMD-004` | `ps`, `kill` | `src/commands/` | `Apps/Commands/HackerOs.Commands.{Ps,Kill}/` | Process list & termination tests | **MIGRATED** |
| `P4-W5-CMD-005` | `launch` | `src/commands/` | `Apps/Commands/HackerOs.Commands.Launch/` | Application intent launcher tests | **MIGRATED** |
| `P4-W5-CMD-006` | `clear` | `src/commands/` | `Apps/Commands/HackerOs.Commands.Clear/` | ANSI clear-screen output test | **MIGRATED** |
| `P4-W5-CMD-007` | `help`, `man` | `src/commands/` | `Apps/Commands/HackerOs.Commands.Help/` | Help listing & manual page renderer test | **MIGRATED** |
| `P4-W5-CMD-008` | `alias`, `addalias`, `rmalias` | `src/commands/` | `Apps/Commands/HackerOs.Commands.Alias/` | Alias lookup & dynamic registration test | **MIGRATED** |
| `P4-W5-CMD-009` | Linux Commands Audit | `src/commands/linux/` | All 25 terminal command projects | 100% legacy Linux command coverage verified | **MIGRATED** |

---

## Key Design & Architecture Highlights

1. **Deterministic Engine Isolation:**
   - `CalculatorEngine` and `PaintCanvasState` isolate business logic into pure C# classes, enabling fast, headless unit tests independent of DOM or canvas element rendering.
2. **Modular Terminal Commands:**
   - Every command resides in its own project under `Apps/Commands/` with its own `app.manifest.json` and capability grants.
3. **ADR 0022 (D-016 Multi-Monitor):**
   - Popups (`window.open`/`BroadcastChannel`) are explicitly excluded. HackerOS v3 desktop management runs entirely within a single PWA viewport shell.
