---
description: "Continue the HackerOS v3 (Blazor WASM/C#) migration by implementing the next unblocked task(s) from the integration task list, with full test/doc/ADR/task-list maintenance."
name: "Continue Migration"
argument-hint: "Optional task ID (e.g. P1-BLD-001) or section number; omit to auto-select the next unblocked task"
agent: "agent"
---
Continue the HackerOS v3 migration described in
[integration-task-list.md](../../doc/wasm/dev/old/integration-task-list.md),
following the project rules in [AGENTS.md](../../AGENTS.md).

## Before writing any code

1. Read section `0. Instructions for Maintaining This Task List` in
   integration-task-list.md in full — its maintenance rules govern this entire
   session, not just the task you pick.
2. Check `/memories/repo/` for prior gotchas, verified build/test commands, and
   architecture facts before re-deriving anything.
3. Select the target task:
   - If `$ARGUMENTS` names a task ID (e.g. `P1-BLD-004`) or section, use it.
   - Otherwise scan phase-by-phase, top to bottom, for the first `[ ]` task
     whose prerequisites are all `[x]`. Skip (and report) any task marked
     `**BLOCKED: P-xxx**` or `**DECISION: D-xxx**` — those need the Problem
     Register resolved or an ADR/user decision first, not silent implementation.
   - Prefer finishing an already-started section (look for
     `**In progress:** YYYY-MM-DD` notes) over starting a new one.
4. Re-read that task's full work package: **Scope and location**,
   **Prerequisites**, **Explicit exclusions**, and **Validation and completion
   evidence**. Do not expand scope beyond what's listed.

## While implementing

- All new code goes under `wasm2/HackerOs/` only — never under `src/`.
- Follow AGENTS.md: prefer C# over JS interop, MudBlazor for complex UI,
  scoped `.razor.css` files (no inline styles/`<style>` blocks), "mimic the
  metal" realism for simulated OS behavior, XML doc comments on public APIs,
  and a dated Markdown checkbox task breakdown for any non-trivial task.
- For multi-step or complex tasks, post the checkbox breakdown up front and
  check items off as you complete them, per AGENTS.md section III.1.
- Add or extend tests in the matching `Tests/` project alongside the change,
  not as an afterthought.

## Before marking anything `[x]`

1. Run the narrowest relevant test filter first, then the full solution suite
   (warnings are treated as errors):
   ```powershell
   dotnet test HackerOs.sln --no-restore
   ```
2. Update, in the same change:
   - The task's checkbox in integration-task-list.md (only `[x]` if code,
     tests, docs, and the stated validation gate all pass — otherwise leave
     `[ ]` with a dated progress note).
   - The feature's dedicated doc under `doc/wasm/` (create one if
     none exists for this section).
   - `doc/wasm/dev/old/implementation-status.md` test counts and validation
     command.
   - A new ADR under `doc/wasm/dev/adr/` if an architecture decision was
     made (a task requiring an unresolved ADR cannot be marked complete).
   - `/memories/repo/` with any new gotcha, bug root-cause, or verified
     convention discovered this session.
3. Never delete or silently reset an unfinished task — mark it **Superseded**
   with a linked replacement, or leave it `[ ]` with a progress note.

## When done

Summarize what was completed, what remains `[ ]` or is blocked, and the exact
task ID to resume from next session.
