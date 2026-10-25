# Wave 3: Editing, Clipboard, and Drag/Drop Migration Report

## Overview

This report documents the migration of code editing, typed clipboard handling, drag/drop payloads, and terminal editing (`nano`) into Blazor WebAssembly C# (`wasm2/HackerOs/`).

---

## Migrated Component Matrix

| ID | Feature Name | Legacy Source | C# Project / Location | Automated Test Evidence | Status |
|---|---|---|---|---|---|
| `P4-W3-001` | Editor Framework Selection | `src/apps/code-editor.ts` | `docs/adr/0020-editor-framework-and-script-sandbox.md` | ADR 0020 (DECISION: D-014) | **DECIDED** |
| `P4-W3-002` | Code Editor App | `src/apps/code-editor.ts` | `Apps/System/HackerOs.Apps.CodeEditor/` | 20 focused editor tests; 28 platform close-guard tests; Chromium Monaco edit/mode/disposal test; axe scan | **SUBSTANTIAL PARTIAL / REOPENED** — reworked from CodeMirror to Monaco with an Open Folder tree view and embedded terminal on 2026-08-21 (see `docs/code-editor.md`); whole-window close and VFS recovery are implemented; real component reload and published/offline integration remain |
| `P4-W3-003` | Script Execution Sandbox Policy | `src/apps/code-editor.ts` | `docs/adr/0020-editor-framework-and-script-sandbox.md` | ADR 0020 (`user-scripts.execute`) | **MIGRATED** |
| `P4-W3-004` | Typed Clipboard Gateway | `src/core/os.ts` | `Shared/HackerOs.AppSdk/Clipboard/` | `AppClipboardGateway` | **MIGRATED** |
| `P4-W3-005` | Typed Drag & Drop Payloads | `src/apps/file-explorer.ts` | `Shared/HackerOs.AppSdk/DragDrop/` | `VirtualFileDragPayload` | **MIGRATED** |
| `P4-W3-006` | Nano Terminal Editor | `src/commands/app/nano-editor.ts` | `Apps/Commands/HackerOs.Commands.Nano/`, `Shared/HackerOs.AppSdk.Terminal/` (extracted 2026-08-21 from `Apps/System/HackerOs.Apps.Terminal/`) | `NanoCommandTests`; `TerminalFullScreenSessionTests`; dispatcher and Chromium adapter tests | **MIGRATED / REVALIDATED** |
| `P4-W3-007` | Wave 3 Verification | Entire solution | `dotnet test HackerOs.sln` | Solution and browser suites | **REOPENED** |
