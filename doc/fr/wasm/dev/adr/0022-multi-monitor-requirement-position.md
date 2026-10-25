# ADR 0022 — Multi-Monitor Requirement Position

**Status:** Accepted  
**Date:** 2026-08-03  
**Deciders:** Product, Architecture  
**Supersedes:** —  
**Superseded by:** —  

---

## Context

Task `P4-W5-APP-004` requires a decision on legacy multi-monitor behavior (`src/core/multi-monitor.ts`),
which used browser popups (`window.open`) and `BroadcastChannel` to spawn external browser windows.

**Options considered:**

| Option | Description | Pros | Cons |
|---|---|---|---|
| A — Browser popups | Port `BroadcastChannel` and `window.open` popups to Blazor | Matches legacy code | Blocked by popup blockers, breaks PWA standalone installation, creates multi-tab state sync complexity |
| B — Explicit Exclusion (Single-viewport shell) | Explicitly exclude browser popups; HackerOS v3 owns a rich single-viewport desktop shell supporting virtual workspaces/desktops inside the PWA viewport | Reliable across all browsers/PWAs, no popup blocker issues, clean state encapsulation | External multi-window popups not supported |

**Decision: Option B — Explicit Exclusion.**

Legacy browser popup opening (`window.open`/`BroadcastChannel`) is explicitly excluded. HackerOS v3 desktop management operates entirely within a single PWA viewport shell. If multi-display workflows are needed in the future, virtual workspaces/desktops will be rendered within the shell canvas.

**Accepted:** 2026-08-03
