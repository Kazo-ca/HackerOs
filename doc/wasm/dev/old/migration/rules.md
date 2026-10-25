# HackerOS Legacy Migration Rules & Standard Operating Procedure

## Overview

Phase 4 governs the systematic porting of legacy TypeScript/JavaScript features (`src/`) into the Blazor WASM C# architecture (`wasm2/HackerOs/`).
These rules apply to every feature, app, command, and domain service ported to the C# codebase.

---

## Migration Directives (`P4-RULE-001` through `P4-RULE-006`)

### 1. Observable Legacy Behavior Capture (`P4-RULE-001`)
Before writing code for any legacy feature:
- Inspect legacy source code in `src/`.
- Document observable inputs, outputs, UI states, keyboard shortcuts, error paths, and edge cases.
- Record sample data and state snapshots in `docs/migration/`.

### 2. Parity & Change Register (`P4-RULE-002`)
Each migration document must explicitly state:
- **Retained Behavior:** Capabilities preserved line-for-line in functionality.
- **Intentional Changes:** Modernized UI (MudBlazor / Gothic-Hacker theme), improved C# async patterns, or enhanced security authorization.
- **Dropped Workarounds:** Obsolete JS workarounds or unsecure direct DOM hacks.

### 3. Layer Assignment & Anti-Locator Rule (`P4-RULE-003`)
- Assign logic strictly to its proper layer: Domain (`Shared/`), Platform (`Platform/`), Infrastructure (`Infrastructure/`), or Application (`Apps/`).
- **PROHIBITED:** Global `OS` service locator or static global singletons. All dependencies must be injected via constructor or Blazor `[Inject]`.

### 4. Independent App & Command Isolation (`P4-RULE-004`)
- Every ported app and command MUST have its own project directory, `app.manifest.json`, unit test project (`Tests/`), and dedicated markdown documentation (`docs/apps/`).
- Bundling unrelated applications into a single project is strictly prohibited.

### 5. C# Domain First & Minimal JS Interop (`P4-RULE-005`)
- Prioritize C# for all business logic, data models, state management, and file processing.
- JS interop is restricted to minimal, well-documented wrapper scripts managed in `wwwroot/js/`.

### 6. Parity Verification & Backlog Retirement (`P4-RULE-006`)
- A migrated feature is marked completed in `integration-task-list.md` ONLY after:
  1. Unit tests pass with 0 errors (`dotnet test`).
  2. Release build succeeds (`dotnet build`).
  3. Feature documentation is updated.
