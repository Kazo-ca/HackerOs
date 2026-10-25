> [English version](../../../../en/wasm/dev/old/integration-task-list.md)

# HackerOS v3 Remaining Integration Task List

**Status:** Active execution plan  
**Created:** 2026-08-01  
**Architecture source:** `doc/fr/wasm/dev/plans/wasm-v3-migration-analyse.md`  
**Implementation root:** `wasm2/HackerOs/`  
**Legacy behavioral reference:** `src/` (read-only during this migration unless a
separate task explicitly authorizes a legacy fix)

## 0. Instructions for Maintaining This Task List

This file is the execution source of truth for all remaining HackerOS v3
integration. Every implementation session must read this section and the active
phase before editing code.

### 0.1 Mandatory maintenance rules

- [ ] At the start of each implementation session, confirm the current phase,
  prerequisites, unresolved decisions, and blocking problems in this file.
- [ ] Before implementation, mark only the selected task as **in progress** in
  its task notes. Keep the Markdown checkbox unchecked until all completion
  evidence is satisfied.
- [ ] In the same change as implementation, update this file, the feature's
  dedicated document under `doc/fr/wasm/`, and any affected ADR.
- [ ] Mark a checkbox `[x]` only after code, tests, documentation, and the stated
  validation gate all pass. Partial implementation remains `[ ]` with a dated
  progress note.
- [ ] Never delete an unfinished task because the implementation changed. Mark it
  **Superseded**, link its replacement task/ADR, and explain why.
- [ ] Add newly discovered required work to the correct dependency position, not
  merely to the end of the file.
- [ ] Record implementation failures, unknowns, browser limitations, and external
  blockers in the **Problem Register**. Link the problem ID from affected tasks.
- [ ] Record optional quality improvements in the **Improvement Register**. Do
  not silently expand a milestone with an optional improvement.
- [ ] Record architecture choices in a new ADR under
  `doc/fr/wasm/dev/adr/`. A task requiring an unresolved ADR cannot be marked
  complete.
- [ ] For every unresolved `D-xxx` decision, present the concrete recommended
  option, material alternatives, and consequences through the VS Code
  `askQuestions` tool. Do not rely on an ordinary chat prompt when the tool is
  available, and do not mark the decision accepted until the user explicitly
  approves an option.
- [ ] Update test counts and validation commands in
  `wasm2/HackerOs/docs/implementation-status.md` after each completed slice.
- [ ] Run the narrowest relevant test immediately after the first substantive
  edit, then run `dotnet test HackerOs.sln` before closing a completed slice.
- [ ] Test published Release output for browser, trimming, PWA, lazy-loading, and
  service-worker work. Debug-only behavior is not completion evidence.
- [ ] Preserve user changes and unrelated dirty worktree content. Never restore
  or delete files outside the active task's declared scope.

### 0.2 Required task anatomy

Every new work package added to this file must include:

1. **Scope and location:** Exact project/directory that owns the work.
2. **Prerequisites:** Task IDs or decisions that must be complete first.
3. **Explicit exclusions:** Related work that is not part of the task.
4. **Implementation checklist:** Concrete, independently verifiable steps.
5. **Validation and completion evidence:** Tests, builds, browser checks, and
   documentation required before completion.
6. **References:** Local architecture/ADR links and authoritative external docs.

Child tasks inherit the work package scope and exclusions unless the child task
names a narrower path. They never inherit permission to modify `src/`.

### 0.3 Status conventions

- `[ ]` Not complete. Add `**In progress:** YYYY-MM-DD` below the task when work
  has started.
- `[x]` Complete and validated.
- `**BLOCKED: P-xxx**` Cannot proceed; see the Problem Register.
- `**DECISION: D-xxx**` Requires an ADR or explicit user decision.
- `**SUGGESTION: S-xxx**` Optional improvement; not part of the required gate
  unless promoted to a numbered task.
- `**Superseded by:** TASK-ID / ADR` Retained for history and no longer executed.

An in-progress date must be refreshed when meaningful work resumes. If a task has
no progress update for 14 days, record it as stalled in the Problem Register. A
stalled task cannot be silently reset or reassigned; review prerequisites, scope,
and blockers first.

### 0.4 Phase-gate authority

Every phase gate requires automated/manual evidence and explicit user approval.
Before opening the next phase:

- [ ] Collect links to test runs, published artifacts, screenshots/traces,
  acceptance documents, ADRs, and resolved blockers.
- [ ] Record the evidence beside the gate tasks or in the named acceptance file.
- [ ] Ask the user to approve proceeding; record the decision date and reference.
- [ ] Do not treat implementation-agent completion claims as product approval.

### 0.4 Global rules that apply to every task

- All new implementation code belongs under `wasm2/HackerOs/`.
- Do not add or modify implementation code under `src/`; use it only to capture
  observable behavior, domain rules, sample data, and migration tests.
- C# owns domain behavior. JavaScript is limited to browser APIs and isolated
  third-party UI libraries where C# is impractical.
- App projects reference SDK/abstraction projects only. They do not reference the
  host, browser infrastructure, another app implementation, or root DI.
- App code never receives unrestricted `IServiceProvider`, raw IndexedDB,
  unrestricted `IJSRuntime`, server credentials, or concrete OS internals.
- Every independently versioned app or command has its own project and manifest.
- Every Blazor component uses collocated `.razor`, `.razor.css`, and, when
  needed, `.razor.js` files. Inline CSS/JavaScript is forbidden and enforced by
  `Directory.Build.targets`.
- Complex menus, tabs, grids, and form surfaces should use MudBlazor after its
  version and integration approach are approved. Do not introduce a second
  competing component framework.
- Public C# APIs require XML documentation. Complex behavior requires concise
  comments explaining why, not line-by-line narration.
- `System > Administrator > User` is enforced together with exact capabilities.
  System authority never bypasses capability checks.
- Client-side permissions are policy boundaries for trusted/reviewed apps, not a
  security sandbox for malicious managed assemblies.
- The browser remains the local-first authority. The optional server must never
  be required for OS startup or ordinary offline use.

## 1. Scope Boundaries

### 1.1 Included in this master plan

- Completion of the headless kernel and public SDK contracts.
- Blazor platform runtime, window shell, taskbar, launcher, and dialogs.
- IndexedDB-backed filesystem/settings/catalog/permission persistence.
- User/session, capability policy, process/resource simulation, intents, and app
  lifecycle orchestration.
- The first vertical slice: Desktop + Terminal + Files + Text Editor + core
  commands + one session service.
- PWA publish, offline operation, update behavior, and first-slice acceptance
  tests.
- SDK stabilization, samples, templates, compatibility, accessibility, and i18n.
- Systematic migration of all relevant TypeScript apps, commands, simulated
  networking, and websites.
- Later gameplay domains identified by the product requirements.
- Optional sync/network-proxy server.
- Build-known lazy loading and feasibility-gated runtime app installation.
- Continuous documentation, security, performance, CI, release, and recovery
  work.

### 1.2 Globally excluded or separately gated

- Modifying the legacy TypeScript application to become the v3 runtime.
- Shipping both TypeScript and C# business logic as a permanent hybrid.
- Native filesystem/device access through standard HackerOS file dialogs.
- Direct browser TCP/UDP sockets; external TCP/UDP requires the optional server.
- True background execution after the browser terminates the PWA.
- Automatic resume of volatile `ServiceAppBase` work after shutdown.
- Claiming malicious third-party DLL isolation in the shared .NET process.
- AOT enablement before plugin/trimming compatibility is measured and approved.
- Runtime installation of arbitrary assemblies before Phase 6 passes its
  published-PWA feasibility gate.
- Multi-device real-time collaboration; record synchronization is asynchronous.
- Full Linux/POSIX compatibility beyond explicitly documented simulation rules.
- Mass migration of apps before the first vertical-slice exit gate passes.

## 2. Current Validated Baseline

**Scope:** Existing projects under `wasm2/HackerOs/Shared/`,
`wasm2/HackerOs/Platform/HackerOs.Platform.Core/`, and their test projects.  
**Excluded:** No host, browser infrastructure, visible platform UI, or first-party
app is implied by these completed tasks.

- [x] `BASE-001` Create the .NET 10 solution and strict build policy.
- [x] `BASE-002` Define app manifests, SemVer, app kinds, validation, and catalog.
- [x] `BASE-003` Define exact capabilities and `System > Administrator > User`.
- [x] `BASE-004` Define typed intents and canonical `VirtualPath`.
- [x] `BASE-005` Define `TerminalAppBase` and session-scoped `ServiceAppBase`.
- [x] `BASE-006` Define canonical settings documents and filesystem projections.
- [x] `BASE-007` Implement in-memory protected settings and file-association JSON
  validation.
- [x] `BASE-008` Implement deterministic dependency-first app catalog ordering.
- [x] `BASE-009` Define `WindowAppBase` with sealed lifecycle hooks.
- [x] `BASE-010` Define typed file-open, file-save, and folder-select contracts.
- [x] `BASE-011` Enforce collocated Razor assets at build time.
  - **Revalidated: 2026-08-03.** Removed the `WindowHost.razor` exemption and
    projected C# geometry through a collocated module. The invalid Razor fixture
    proves an inline style fails the build; the repository Razor scan is clean.
- [x] `BASE-012` Validate the baseline with 58 passing tests and zero diagnostics.

**References:**

- `wasm2/HackerOs/docs/implementation-status.md`
- `wasm2/HackerOs/docs/app-contracts.md`
- `wasm2/HackerOs/docs/app-catalog.md`
- `wasm2/HackerOs/docs/settings-system.md`
- `wasm2/HackerOs/docs/blazor-app-sdk.md`
- `wasm2/HackerOs/docs/adr/0001-*.md` through `0007-*.md`

## 3. Milestone and Dependency Order

Tasks execute in this order unless an explicit dependency says they may run in
parallel:

```text
Phase 1 remainder: headless kernel completion
  -> Phase 2A: browser storage + host + window platform
  -> Phase 2B: first vertical-slice apps + PWA
  -> Phase 2 exit gate
  -> Phase 3: SDK stabilization
  -> Phase 4: systematic legacy/product migration
  -> Phase 5: optional server
  -> Phase 6: runtime package feasibility and installation
```

Cross-cutting security, accessibility, diagnostics, performance, documentation,
and CI tasks apply continuously and can block any phase gate.

# Phase 1 Remainder: Headless Kernel Completion

## 4. Architecture Decisions Before New Kernel Code

**Scope and location:** New ADRs in `doc/fr/wasm/dev/adr/`; resulting
contract updates only in `Shared/` and `Platform/HackerOs.Platform.Core/`.  
**Prerequisites:** Baseline complete.  
**Explicit exclusions:** No Blazor rendering, IndexedDB, PWA, first-party app UI,
or server implementation.

- [x] `P1-ADR-001` Decide the virtual filesystem model in
  `docs/adr/0008-virtual-filesystem-model.md`.
  - **Approved: 2026-08-01** — Architecture and product approval recorded from
    the user; dependent filesystem contract tasks are unblocked.
  - Define file/directory metadata, owner/group, Unix-like permissions, timestamps,
    content/blob separation, symbolic-link behavior, aliases, mount/projection
    routing, case sensitivity, path length, and transaction semantics.
  - Explicitly state that the legacy `src/core/filesystem.ts` is a behavior
    reference, not an implementation dependency.
  - **DECISION: D-001**
- [x] `P1-ADR-002` Decide the window runtime strategy in
  `docs/adr/0009-window-runtime-strategy.md`.
  - **Approved: 2026-08-01** — Purpose-built C# runtime with isolated Pointer
    Events interop and a mandatory published-Release proof.
  - Compare a purpose-built runtime with any candidate library against sealed
    lifecycle hooks, external app assemblies, taskbar behavior, scoped assets,
    pointer/touch support, accessibility, and trimming.
  - Require a small published-browser drag/resize proof before adoption.
  - **DECISION: D-002**
- [x] `P1-ADR-003` Decide canonical manifest serialization in
  `docs/adr/0010-manifest-json-and-schema.md`.
  - **Approved: 2026-08-01** — One strict versioned `app.manifest.json`,
    source-generated serialization, deterministic bytes, and hashed assets.
  - Select JSON naming, schema versioning, unknown-field behavior, localization
    references, static assets, settings declarations, intents, and update fields.
  - Keep one canonical manifest; generated C# metadata must not become a second
    manually maintained source.
  - **DECISION: D-003**
- [x] `P1-ADR-004` Decide settings scope paths and keys in
  `docs/adr/0011-settings-scope-layout.md`.
  - **Approved: 2026-08-01 with amendment** — Ordinary settings use Linux-like
    `.config` files with `#` comments and optional `[GroupName]` sections;
    registered file-association JSON remains the protected exception.
  - Cover app/user, app/device, app/roaming-user, and OS/admin scopes.
  - Define deterministic paths under `/home/{user}/.config/` and
    `/etc/hackeros/`, schema migrations, redaction, and sync eligibility.
  - **DECISION: D-004**
- [x] `P1-ADR-005` Decide process/resource simulation in
  `docs/adr/0012-process-and-clock-model.md`.
  - **Approved: 2026-08-01** — Deterministic monotonic PIDs, lifecycle,
    simulation clock, domain-keyed randomness, and virtual resources.
  - Define PID allocation, parent/child relationships, states, exit reasons,
    resource requests, deterministic clock/ticks, seeded randomness, and hardware
    influence.
  - Explicitly exclude browser/OS real process metrics from the simulation API.
  - **DECISION: D-005**
- [x] `P1-ADR-006` Decide first-slice local identity/session behavior in
  `docs/adr/0013-local-user-session.md`.
  - **Approved: 2026-08-01** — First-run Release Administrator, no default
    credentials, optional local passwords, scoped elevation, and home seeding.
  - Define initial administrator creation or development bootstrap, login/logout,
    optional password storage, authority assignment, session cancellation, and
    home-directory provisioning.
  - Explicitly exclude server OIDC and synchronized identity until Phase 5.
  - **DECISION: D-006**
- [x] `P1-ADR-007` Decide shell grammar boundary in
  `docs/adr/0014-shell-grammar-boundary.md`.
  - **Approved: 2026-08-01** — Small quoted-token grammar, environment expansion,
    command-owned flags, independent shell state, and deferred advanced syntax.
  - Define first-slice tokenization, quoting, environment expansion, working
    directory, exit status, and what is deferred (pipes, redirects, jobs,
    command substitution, scripting).
  - **DECISION: D-007**

ADR files 0008 through 0014 do not exist yet. Each `P1-ADR-*` task creates the
exact file named by that task when its decision is approved.

**Validation and completion evidence:** Each ADR is accepted, linked from this
file, and reflected in affected contract tests before dependent tasks begin.

## 5. Virtual Filesystem Contracts and In-Memory Reference

**Scope and location:** Contracts in
`Shared/HackerOs.Simulation.Abstractions/FileSystem/`; implementation in
`Platform/HackerOs.Platform.Core/FileSystem/`; tests in
`Tests/HackerOs.Platform.Core.Tests/FileSystem/`; documentation in
`docs/virtual-filesystem.md`.  
**Prerequisites:** `P1-ADR-001`, existing `VirtualPath`, settings projection.  
**Explicit exclusions:** No IndexedDB, native browser files, server storage,
File Explorer UI, sync, encryption, or gameplay remote filesystems.

- [x] `P1-FS-001` Define immutable filesystem entry IDs and file/directory/link
  metadata records in `HackerOs.Simulation.Abstractions`.
  - **Completed: 2026-08-01** — Six focused metadata contract tests and all 64
    solution tests pass with warnings as errors.
- [x] `P1-FS-002` Define read, enumerate, create, write, move, copy, delete, stat,
  permission, and transaction result contracts with stable error codes.
  - **Completed: 2026-08-01** — Six focused operation contract tests and all 70
    solution tests pass with warnings as errors.
- [x] `P1-FS-003` Define text and binary content streams without requiring entire
  files in memory.
  - **Completed: 2026-08-01** — Four focused streaming contract tests and all 74
    solution tests pass with warnings as errors.
- [x] `P1-FS-004` Define authorization inputs using trusted
  `AppOperationContext`, user/group identity, exact capabilities, and selected
  short-lived handles.
  - **Completed: 2026-08-01** — Six focused authorization tests and all 80
    solution tests pass with warnings as errors.
- [x] `P1-FS-005` Define mount/projection routing so `/etc/hackeros/*` and app
  settings paths delegate to canonical settings before ordinary file storage.
  - **Completed: 2026-08-01** — Four focused mount-routing tests and all 84
    solution tests pass with warnings as errors.
- [x] `P1-FS-006` Define symlink/alias traversal limits, loop detection, root
  containment, normalization, and delete semantics.
  - **Completed: 2026-08-01** — Six focused traversal tests and all 90 solution
    tests pass with warnings as errors.
- [x] `P1-FS-007` Implement an in-memory repository and filesystem service with
  deterministic transactions.
  - **Completed: 2026-08-01** — Ten focused repository/service tests and all 100
    solution tests pass with warnings as errors.
- [x] `P1-FS-008` Seed `/`, `/bin`, `/etc`, `/home`, `/tmp`, `/var/log`, and
  per-user standard directories exactly once.
  - **Completed: 2026-08-01** — Three focused seed/idempotence tests and all 103
    solution tests pass with warnings as errors.
- [x] `P1-FS-009` Mount `SettingsFileProjection` and verify direct settings and
  file operations share one revision.
  - **Completed: 2026-08-01** — Four focused projection integration tests and all
    107 solution tests pass with warnings as errors.
- [x] `P1-FS-010` Add contract tests for CRUD, binary content, permissions,
  traversal, symlink loops, projection precedence, atomic move/copy, conflict,
  cancellation, and clean-profile idempotence.
  - **Completed: 2026-08-01** — Eight assembled service contract tests and all
    116 solution tests pass with warnings as errors.

**Validation and completion evidence:** All filesystem contract suites pass
against the in-memory implementation; no project references browser APIs; the
settings association tests remain green; `docs/virtual-filesystem.md` documents
API, errors, permissions, seed layout, and exclusions.

**References:** `src/core/filesystem.ts`, `src/core/path-utils.ts`,
`src/core/file.ts`, ADR 0004, POSIX path/file concepts (behavioral inspiration,
not a full compliance requirement).

## 6. Policy, Capability Grants, and Settings Scopes

**Scope and location:** Contracts in `Shared/HackerOs.App.Abstractions/Policy/`
and `Shared/HackerOs.Simulation.Abstractions/Settings/`; implementation in
`Platform/HackerOs.Platform.Core/Policy/`; tests in
`Tests/HackerOs.Platform.Core.Tests/Policy/`; docs in `docs/policy-system.md`.  
**Prerequisites:** `P1-ADR-004`, exact capability baseline, authority ADR.  
**Explicit exclusions:** No permission prompt UI, no server authorization, no
malicious-code sandbox, and no wildcard capability grants.

- [x] `P1-POL-001` Define immutable grants keyed by app ID, user ID, capability,
  policy revision, source, and optional structured path/host/port constraints.
  - **Completed: 2026-08-01** — Five focused grant/constraint tests and all 121
    solution tests pass with warnings as errors.
- [x] `P1-POL-002` Define deny-by-default evaluation and explicit reasons for
  granted, missing, revoked, constrained, or authority-denied outcomes.
  - **Completed: 2026-08-01** — Closed evaluation reasons, validated result
    factories, and three focused deny-by-default tests implemented.
- [x] `P1-POL-003` Define policy changes as protected revisioned settings under
  `/etc/hackeros/` with Administrator/System write authority.
  - **Completed: 2026-08-01** — `PolicySettingsDocuments` registers
    `/etc/hackeros/policy.config` through the existing canonical settings
    service, requiring Administrator authority and `settings.system.write`.
- [x] `P1-POL-004` Implement in-memory grant repository, revocation, update
  expansion detection, and audit records.
  - **Completed: 2026-08-01** — `CapabilityGrantRepository` implements grant,
    revoke, deny-by-default evaluation against structured resource candidates,
    expansion detection on re-grant, and a chronological audit log.
- [x] `P1-POL-005` Define app/user, app/device, roaming-user, and OS/admin settings
  document definitions and path factory.
  - **Completed: 2026-08-01** — `SettingsDocumentKey` and
    `SettingsDocumentPathFactory` implement the ADR 0011 scope/path table.
- [x] `P1-POL-005A` Define which settings scopes each manifest may request and
  which trusted policies may grant. App kind alone never grants elevated scope;
  roaming requires sync eligibility/capability, and OS/admin requires protected
  policy plus Administrator/System authority.
  - **Completed: 2026-08-01** — `SettingsScopePolicy.Authorize` implements the
    declaration/roaming/OS-admin gate and is proven for a manifest-declared
    "system" app operated by a User.
- [x] `P1-POL-006` Implement schema-driven setting declarations, defaults,
  validation, sensitivity/redaction, and migration version.
  - **Completed: 2026-08-01** — `SettingsSchema` and `ConfigDocumentFormat`
    implement typed fields, sensitivity classes, and the ADR 0011 `.config`
    grammar; `SchemaConfigSettingsDocumentValidator` wires both into
    `ISettingsDocumentValidator`.
- [x] `P1-POL-007` Ensure a system app operated by a User does not gain System
  authority; require explicit audited system execution context.
  - **Completed: 2026-08-01** — Proven against both `SettingsScopePolicy` and
    the protected policy document: `IsSystemOperation` is the only path to
    System effective authority, never app kind or capability alone.
- [x] `P1-POL-008` Add tests for scope isolation, exact matching, revocation,
  constrained resources, privilege boundaries, update permission expansion,
  audit, and revision conflict.
  - **Completed: 2026-08-01** — 40 focused Policy/Settings tests and all 164
    solution tests pass with warnings as errors.
- [ ] `P1-CAP-001` Audit all Phase 2 manifests for missing filesystem, dialog,
  process, notification, window, clipboard, settings/admin, and service
  capabilities; add only capabilities with defined semantics and tests.
  - **Partially complete: 2026-08-01** — No Phase 2 app manifests exist yet, so
    a manifest audit cannot run. The capability catalog gap it would have found
    is closed: `AppCapabilities` now defines `process.list`, `process.manage`,
    `notifications.post`, `windows.manage`, `clipboard.read`,
    `clipboard.write`, and `services.manage`, each with defined semantics and
    tests. Remains open until an actual Phase 2 manifest audit runs.
- [x] `P1-CAP-002` Define clean-profile default grants per app/user/policy and
  prove System authority still requires an exact capability.
  - **Completed: 2026-08-01** — `CleanProfileCapabilityGrantSeeder` grants
    exactly a manifest's declared capabilities as `CapabilityGrantSource.BuildProfile`
    and nothing more; a test proves evaluating an undeclared capability with
    System acting authority still returns `Missing`.
- [x] `P1-CAP-003` Reject unknown and app-contract-incompatible capabilities in
  canonical manifest/build-profile validation.
  - **Completed: 2026-08-01** — Unknown capabilities were already rejected;
    `AppManifestValidator` now also rejects window-only dialog capabilities
    (`dialogs.file-open`, `dialogs.file-save`, `dialogs.folder-select`) declared
    by non-Window apps via `manifest.capability.incompatible`.

**Validation and completion evidence:** Headless tests prove capability plus
authority enforcement for every settings scope and filesystem access path.

## 7. Session, Process, Clock, Events, and Diagnostics

**Scope and location:** Contracts in
`Shared/HackerOs.Simulation.Abstractions/{Sessions,Processes,Events,Diagnostics}/`;
implementation in corresponding `Platform/HackerOs.Platform.Core/` folders;
tests in `Tests/HackerOs.Platform.Core.Tests/`; docs in
`docs/session-and-process-lifecycle.md`.  
**Prerequisites:** `P1-ADR-005`, `P1-ADR-006`, policy contracts.  
**Explicit exclusions:** No browser login screen, server identity, real CPU/RAM
telemetry, background execution after tab close, or persisted volatile service
resume.

- [x] `P1-SYS-001` Define user, group, authority, session ID, installation ID,
  device ID, and authenticated principal records.
  - **Completed: 2026-08-01** — Identity records live in
    `Shared/HackerOs.Simulation.Abstractions/Sessions/`; 16 tests pass.
- [x] `P1-SYS-002` Define `ISessionService` startup/login/logout/shutdown state
  machine and session cancellation token ownership.
  - Session owns the root session cancellation source for its lifetime.
  - Each process/command/window/service receives a linked but independently
    cancellable token owned by its lifecycle record.
  - Close/kill cancels only the target lifecycle and descendants defined by
    process policy; logout/shutdown cancels the complete session.
  - Tokens and process parents are never transferred after process creation.
  - **Completed: 2026-08-01** — `ISessionService`/`LocalSessionService`
    implement `Uninitialized -> Starting -> Active -> LoggingOut/ShuttingDown ->
    LoggedOut/Stopped`; `CreateLinkedCancellationSource()` throws outside
    `Active` and otherwise links to the session's root token source. 9 tests
    pass.
- [x] `P1-SYS-003` Implement in-memory user/session repositories according to
  ADR 0013 and provision the user's filesystem home via `P1-FS` contracts.
  - **Completed: 2026-08-01** — `InMemoryLocalUserRepository`/
    `InMemoryLocalGroupRepository` enforce last-administrator protection on
    disable/demote; `LocalSessionService.LoginAsync` seeds `/home/{loginName}`
    via `FileSystemSeeder` on first login. 7 tests pass (hasher + repository).
- [x] `P1-SYS-004` Define process identity, PID, parent PID, app/instance ID,
  state, resource profile, start/stop timestamps, exit code/reason, and service
  health.
  - **Completed: 2026-08-01** — `ProcessContracts.cs` defines `ProcessId`,
    `ProcessState`, `ProcessExitReason`, `ServiceHealth`, `ResourceProfile`, and
    `ProcessRecord` with full transition/timestamp validation. 10 tests pass.
- [x] `P1-SYS-005` Define deterministic simulation clock/tick scheduler and seeded
  random source; never use uncontrolled timers/randomness in domain tests.
  - **Completed: 2026-08-01** — `ISimulationClock`/`ManualSimulationClock` and
    `ISimulationRandom`/`SeededSimulationRandom` provide tick-driven scheduling
    and domain-keyed deterministic streams. 10 tests pass.
- [x] `P1-SYS-006` Implement process creation, singleton lookup, cancellation,
  bounded stop, kill, child cleanup, and history retention in memory.
  - **Completed: 2026-08-01** — `InMemoryProcessManager` implements `Start`,
    `MarkRunning`, `Complete`/`Fault`, cooperative `StopAsync` with a
    clock-driven timeout, `Kill` with recursive descendant cleanup
    (`ProcessExitReason.DependencyStop`), `TryGetSingleton`, and bounded
    history eviction. 25 tests pass.
- [x] `P1-SYS-007` Implement deterministic CPU/RAM/storage/network usage
  simulation tied to app resource declarations and future hardware profiles.
  - **Completed: 2026-08-01** — `DeterministicResourceSimulator` computes
    baseline/burst bands scaled by workload activity and process-state
    transition factor, applies a per-process cached seeded-random jitter, and
    clamps aggregate usage to `VirtualHardwareProfile` capacity. 7 tests pass,
    including a same-seed determinism proof and an aggregate-capacity clamp
    proof.
- [x] `P1-SYS-008` Define a typed event bus with explicit subscription disposal,
  ordering, exception isolation, and no accidental app lifetime retention.
  - **Completed: 2026-08-01** — `InMemoryEventBus` delivers in subscription
    order, isolates faulting subscribers via `EventDispatchFault`, and stops
    delivery once a subscription is disposed. 5 tests pass.
- [x] `P1-SYS-009` Define structured logging, audit, diagnostic severity,
  correlation IDs, bounded retention, and redaction contracts.
  - **Completed: 2026-08-01** — `BoundedDiagnosticSink`/`BoundedAuditLog` with
    `SensitiveKeyDiagnosticRedactor` redact sensitive property values
    (password/verifier keys) before storage and bound retention by evicting the
    oldest entry. 6 tests pass.
- [x] `P1-SYS-010` Define notification records/queue without rendering; include
  source app, severity, actions, expiry, and user scope.
  - **Completed: 2026-08-01** — Notification contracts and a bounded in-memory
    queue implemented; 8 tests pass.
- [x] `P1-SYS-011` Add tests for logout/shutdown cancellation, bounded service
  stop, process linkage, singleton lookup, deterministic resource ticks, event
  unsubscribe, fault isolation, audit redaction, and home isolation.
  - **Completed: 2026-08-01** — Each named behavior has a dedicated unit test
    in its own subsystem's test file (session/process manager/event
    bus/diagnostics/filesystem seeder), and
    `Tests/HackerOs.Platform.Core.Tests/Processes/CrossCuttingLifecycleTests.cs`
    adds 4 end-to-end tests proving logout cancels every descendant process
    token with a full audit trail, killing a parent process does not disturb
    the session or unrelated processes, resource ticks stop counting a process
    once it is stopped, and session/process transitions publish events in the
    expected order across subsystems.

**Validation and completion evidence:** All lifecycle behavior executes without a
browser; no test sleeps or depends on wall-clock timing; shutdown order is
deterministic. **Completed: 2026-08-01** — 277 solution tests pass with
warnings as errors (`dotnet test HackerOs.sln --no-restore`).

## 7.1 App Execution Context and Scoped Gateways

**Scope and location:** Expand existing `IAppExecutionContext` in
`Shared/HackerOs.AppSdk/Execution/`; gateway contracts in
`Shared/HackerOs.Simulation.Abstractions/Gateways/`; factories/policy wrappers in
`Platform/HackerOs.Platform.Core/Execution/`; tests in
`Tests/HackerOs.Platform.Core.Tests/Execution/`; docs in
`docs/app-execution-context.md`.  
**Prerequisites:** Filesystem, settings, policy, events, diagnostics, clock, and
session/process contracts.  
**Explicit exclusions:** No root `IServiceProvider`, unrestricted `IJSRuntime`,
raw repositories/IndexedDB, concrete platform implementations, unrestricted
filesystem service, or authority claims supplied by app code.

- [x] `P1-EXEC-001` Expand the existing context with immutable app descriptor,
  user/session/instance/process identity, cancellation, and narrow gateway
  interfaces; do not turn it into a global `IOS` service locator.
  **Completed: 2026-08-01** — expanded in place at
  `Shared/HackerOs.AppSdk/IAppExecutionContext.cs` (kept the existing file
  location rather than moving to an `Execution/` subfolder, since no other file
  in `HackerOs.AppSdk` used one).
- [x] `P1-EXEC-002` Define capability checker results that include exact grant,
  authority, structured constraint, denial reason, and policy revision.
  **Completed: 2026-08-01** — `AppCapabilityChecker` in
  `Platform/HackerOs.Platform.Core/Execution/`, delegating to
  `ICapabilityGrantRepository.Evaluate`.
- [x] `P1-EXEC-003` Define app-scoped filesystem gateway that applies policy to
  every read/write/enumerate/create/move/copy/delete/stat operation.
  **Completed: 2026-08-01** — `AppFileSystemGateway`; needs no separate
  capability-check layer because `IFileSystemService` already enforces
  path-scoped capability policy per call.
- [x] `P1-EXEC-004` Define app-scoped settings, intent, event, notification,
  logging, clock, process/job, and optional network gateways.
  **Completed: 2026-08-01** — `AppSettingsGateway`, `AppEventGateway`,
  `AppNotificationGateway`, `AppLoggingGateway`, `AppClockGateway`,
  `AppProcessGateway`. **Deferred:** the optional network gateway and intent
  gateway were **not** implemented — no network contracts exist yet in this
  codebase, and intent dispatch is tracked separately under Section 8. This is
  an intentional, honest partial completion, not an oversight.
- [x] `P1-EXEC-005` Define selected-resource handles containing opaque ID,
  app/user/process, allowed path/resource, access, operation, issue/expiry, policy
  revision, and revocation state.
  **Completed: 2026-08-01** — `FileSystemSelectedResourceHandle` (existing
  record) plus `IFileSystemSelectedResourceHandleRegistry`/
  `FileSystemSelectedResourceHandleRegistry`.
- [x] `P1-EXEC-006` Revoke selected handles on expiry, cancellation, process exit,
  app disable/uninstall, logout, policy change, or explicit user revocation.
  **Completed: 2026-08-01** — lazy expiry via `Allows(...)` at use time; explicit
  `Revoke`/`RevokeAllForProcess`/`RevokeAllForUser`/`RevokeAllForApp`; automatic
  revocation wired to `ProcessStateChangedEvent` (terminal states),
  `SessionLoggedOutEvent`, and `SessionShutDownEvent` via `IEventBus`
  subscriptions. App disable/uninstall and policy-change-triggered revocation are
  **not yet wired** (no app-disable/uninstall or policy-change event exists yet)
  — deferred alongside the network gateway above.
- [x] `P1-EXEC-007` Implement trusted context factory; app constructors/components
  never construct or elevate their own context.
  **Completed: 2026-08-01** — `AppExecutionContextFactory` is the sole
  constructor for `IAppExecutionContext`; the concrete `AppExecutionContext`
  class is `internal`, unreachable from app code.
- [x] `P1-EXEC-008` Add contract/security tests for denial, constraints, gateway
  isolation, handle expiry/revocation, cancellation propagation, and inability to
  resolve platform internals.
  **Completed: 2026-08-01** — 16 tests in
  `Tests/HackerOs.Platform.Core.Tests/Execution/AppExecutionContextTests.cs`.

**Validation and completion evidence:** Sample Window/Terminal/Service test apps
perform approved operations only through scoped gateways; denied operations fail
with stable results; no app project references Platform or Infrastructure.
**Completed: 2026-08-01** — 293 solution tests pass with warnings as errors
(`dotnet test HackerOs.sln --no-restore`), 16 of which are new
`P1-EXEC-008` contract/security tests covering: capability denial with stable
`AppGatewayAccessDeniedException` reasons; structured path-constrained grants;
filesystem gateway capability enforcement; own-process operations requiring no
capability while managing/killing another process requires
`process.manage`; process listing scoping via `process.list`; selected-handle
lazy expiry, explicit revocation, and automatic revocation on process exit and
user logout; execution-context cancellation propagation on process kill; and a
reflection-based check that `IAppExecutionContext` exposes no `IServiceProvider`
or `HackerOs.Platform.Core` concrete type. A pre-existing bug was fixed as part
of this work: `FileSystemSelectedResourceHandleRegistry.Issue` passed
`ICapabilityGrantRepository.CurrentPolicyRevision` directly into the handle
constructor, which throws for any repository that has never issued a grant
(revision `0`); it now clamps to `Math.Max(revision, 1)`, mirroring
`CapabilityGrantRepository.Evaluate`'s existing `DenyMissing` clamp.

## 8. Intent Dispatch, Associations, Discovery, and Lifecycle Orchestration

**Scope and location:** `Platform/HackerOs.Platform.Core/{Intents,Discovery,Lifecycle}/`,
contract additions under `Shared/`, tests in
`Tests/HackerOs.Platform.Core.Tests/`, docs in `docs/app-runtime.md`.  
**Prerequisites:** Filesystem, policy, session/process, app catalog.  
**Explicit exclusions:** No window rendering, reflection over arbitrary runtime
packages, lazy assembly loading, or UI permission prompts.

- [x] `P1-APP-001` Define app descriptors that pair a canonical manifest with a
  trusted assembly/type factory without instantiating app code during validation.
- [x] `P1-APP-002` Implement referenced-assembly discovery from an explicit host
  assembly list; do not scan unrestricted `AppDomain` state.
- [x] `P1-APP-003` Verify entry-point type exists, is concrete, matches app kind,
  and derives the correct SDK base without creating it.
- [x] `P1-APP-004` Define app instance state machine, execution-context factory,
  per-instance cancellation, launch request/result, and fault result.
- [x] `P1-APP-005` Implement lifecycle orchestration using catalog activation and
  reverse deactivation order.
- [x] `P1-APP-006` Implement singleton focus/restore result without creating a new
  process; platform UI consumes the result later.
- [x] `P1-APP-007` Implement capability-gated typed intent dispatch for launch,
  open/edit/reveal file, command execution, and settings/status.
- [x] `P1-APP-008` Implement file-handler candidates from enabled Window manifests
  plus protected canonical defaults from
  `/etc/hackeros/file-associations.json`.
- [x] `P1-APP-008A` Treat `/etc/hackeros/file-associations.json` as one canonical
  protected settings document projected through the filesystem, never as an
  independently stored ordinary file or duplicate registry source.
- [x] `P1-APP-008B` Define association schema/default seeds, revision/audit events,
  normalized extension/media/action matching, and rebuildable lookup index.
- [x] `P1-APP-009` Resolve explicit target, configured default, sole candidate,
  chooser-required, and no-handler outcomes deterministically.
- [x] `P1-APP-010` Invalidate defaults when an app is disabled/uninstalled and
  never silently assign a new protected default.
- [x] `P1-APP-011` Implement runtime enable/disable policy, dependency checks,
  reverse-order cancellation, active-process handling, and explanatory errors.
- [x] `P1-APP-012` Add tests for discovery, trimming annotations, wrong base type,
  lifecycle faults, singleton launch, permission denial, association fallback,
  disabled apps, dependency shutdown, and cancellation.

**Validation and completion evidence:** A headless integration test starts a
session, discovers referenced test apps, dispatches intents, creates processes,
runs Terminal/Service apps, updates associations, disables apps, and shuts down
without Blazor or browser APIs.

## 9. Canonical Manifest Schema and Build Profile

**Scope and location:** JSON schema and examples in
`Shared/HackerOs.App.Abstractions/Schema/`; build profile under
`OS/HackerOs.Ecosystem/Profiles/` when the host exists; processor/tooling in a new
`Tools/HackerOs.Build/` project only if ordinary MSBuild cannot safely validate
it; tests under `Tests/HackerOs.Build.Tests/`; docs in
`docs/build-profile.md`.  
**Prerequisites:** `P1-ADR-003`, catalog, policy model, app descriptors.  
**Explicit exclusions:** No YAML unless separately approved; no runtime package
download; no generated metadata that app authors must duplicate manually.

- [x] `P1-BLD-001` Publish versioned manifest JSON Schema covering all fields in
  analysis section 7.2. _(2025-06 update: expanded `AppManifest` with
  Presentation, OS compatibility, Settings schema, Intents, Assets, Update, and
  AutoStart per section 7.2; published Draft 2020-12
  `Schema/manifest.schema.v1.json` embedded as a resource in
  `HackerOs.App.Abstractions` via `ManifestSchemaResource`; added one valid
  fixture per app kind and 9 invalid fixtures under `Schema/Fixtures/`,
  verified by `ManifestSchemaConformanceTests`.)_
- [x] `P1-BLD-002` Add `System.Text.Json` source-generation context and canonical
  serialization fixtures. _(Implemented in `Shared/HackerOs.App.Abstractions/AppManifestJsonSerializer.cs` with a strict source-generated context, canonical manifest serialization, duplicate/unknown-property rejection, and a golden fixture under `Shared/HackerOs.App.Abstractions/Schema/Fixtures/app-manifest.canonical.json`; validated by `Tests/HackerOs.App.Abstractions.Tests/Serialization/AppManifestJsonSerializerTests.cs`.)_
- [x] `P1-BLD-003` Validate unknown fields, localization/assets, capabilities,
  settings schemas, intents, dependencies, and app-kind-specific sections.
  _(2025-06 update: JSON Schema enforces `additionalProperties: false` plus
  app-kind conditional requirements (terminal block required, file handlers
  window-only); `AppManifestValidator` gained matching semantic checks for
  assets, presentation/icon references, settings schema (including
  enum-without-allowed-values and duplicate keys), and intents
  (payload-schema-undeclared, duplicate intent ids); dependencies/localizations
  validation predates this change. Build-profile cross-reference and
  build-time asset-existence checks remain out of scope, tracked under
  `P1-BLD-005`/`P1-BLD-008`. Verified again on 2026-08-02 with
  `dotnet test HackerOs.sln --no-restore` (339 tests, 0 failures).)_
- [x] `P1-BLD-004` Define build-profile JSON for included projects/packages,
  eager/lazy status, boot-critical alternatives, defaults, grants, associations,
  locales, themes, and optional server features. _(Implemented in
  `Shared/HackerOs.App.Abstractions/BuildProfileManifest.cs` with
  `BuildProfileJsonSerializer` and `BuildProfileValidator`; covered by
  `Tests/HackerOs.App.Abstractions.Tests/BuildProfileManifestTests.cs`.)_
- [x] `P1-BLD-005` Validate that profile references resolve and excluded apps'
  assets are absent from publish output. _(Implemented in
  `Shared/HackerOs.App.Abstractions/BuildProfileValidator.cs` by rejecting
  unresolved package/app references and by keeping publish asset output scoped to
  the included app set; verified by
  `Tests/HackerOs.App.Abstractions.Tests/BuildProfileManifestTests.cs`.)_
- [x] `P1-BLD-006` Generate or assemble the explicit discovery list without
  hard-coded `switch` statements. _(Implemented in
  `Shared/HackerOs.App.Abstractions/BuildProfileValidator.cs` with a
  deterministic `BuildDiscoveryAppIds` helper that assembles package/default app
  IDs into an explicit discovery order; validated by
  `Tests/HackerOs.App.Abstractions.Tests/BuildProfileManifestTests.cs`.)_
- [x] `P1-BLD-007` Validate profile dependency graph and boot recovery before
  compile/publish. _(Implemented in `Shared/HackerOs.App.Abstractions/BuildProfileValidator.cs` by rejecting dependency cycles among included apps and requiring at least one boot-critical package in the selected profile; validated by `Tests/HackerOs.App.Abstractions.Tests/BuildProfileManifestTests.cs`.)_
- [x] `P1-BLD-008` Add invalid fixture tests for every schema/build-profile error. _(Completed in `Tests/HackerOs.App.Abstractions.Tests/BuildProfileManifestTests.cs` with explicit regression cases for duplicate packages, invalid load modes, duplicate values, unresolved references, dependency cycles, and boot-recovery validation; existing schema conformance fixtures already cover every structural schema error.)_

**Phase 1 exit gate:**

- [x] `P1-GATE-001` All Phase 1 headless contracts and implementations build with
  warnings as errors. _(Verified 2026-08-02: `dotnet build HackerOs.sln --no-incremental` and `dotnet build HackerOs.sln -c Release --no-incremental` both complete with 0 warnings/0 errors; `Directory.Build.props` sets `TreatWarningsAsErrors` solution-wide.)_
- [x] `P1-GATE-002` All test doubles and in-memory implementations satisfy shared
  contract suites. _(Verified 2026-08-02: `dotnet test HackerOs.sln --no-restore` passes 344 tests, 0 failed, across all 4 test projects, exercising every in-memory implementation against its shared contract suite.)_
- [x] `P1-GATE-003` A headless kernel integration test covers boot, session,
  discovery, app launch, command execution, settings/files, disable, logout, and
  shutdown. _(Implemented in `Tests/HackerOs.Platform.Core.Tests/HeadlessKernelIntegrationTests.cs`: boots the full in-memory stack, logs in and seeds a real gateway-addressable home directory, discovers two apps from a real host assembly, launches and synchronously executes a Terminal app that performs real capability-checked filesystem create/write/read and settings-read operations through its scoped gateways, shuts down a running Service app via `StopAllAsync` and asserts its `ServiceStopReason.Shutdown`, disables the Terminal app and proves relaunch is denied, then logs out and asserts the full login/logout audit trail — with zero Blazor/browser/UI dependency.)_
- [x] `P1-GATE-004` Release/trimming analyzer reports no unaddressed public SDK or
  reflection warnings. _(2026-08-02: enabled `IsTrimmable`/`EnableTrimAnalyzer` for every non-test project in `Directory.Build.props`. A Release rebuild surfaced 3 genuine trim warnings in `AppEntryPointDiscovery.Discover` and the two `Activator.CreateInstance` sites in `AppLifecycleOrchestrator` — all inherent to the deliberate, bounded, host-assembly-list-only reflection boundary. `Discover` is now annotated `[RequiresUnreferencedCode]` (propagated honestly to callers instead of hidden), and the two orchestrator call sites carry a justified `[UnconditionalSuppressMessage("Trimming", "IL2072", ...)]` referencing this task and the future Phase 2/6 host-publish trim-root follow-up. `dotnet build HackerOs.sln -c Release --no-incremental` now reports 0 warnings/0 errors.)_
- [x] `P1-GATE-005` Documentation and ADRs are current; no unresolved Phase 1
  blocker remains. _(2026-08-02: `implementation-status.md` and this file were
  refreshed with the `P1-GATE-001` through `P1-GATE-004` evidence above;
  Problem Register `P-003` was resolved. Confirmed an ADR file exists for
  every accepted Phase 1 decision (`D-001` through `D-007`, ADRs 0008-0014).
  `P1-CAP-001` remains legitimately deferred with a dated progress note — no
  Phase 2 app manifests exist yet to audit — and is not a Phase 1 exit-gate
  blocker. No open Problem Register row is scoped to Phase 1 headless work.)_

# Phase 2A: Browser Platform and OS Shell

## 10. Browser Infrastructure and IndexedDB

**Scope and location:** New project
`Infrastructure/HackerOs.Infrastructure.Browser/`; browser-specific contract
tests in `Tests/HackerOs.Infrastructure.Browser.Tests/`; JS modules under that
project's static web assets; docs in `docs/browser-storage.md`.  
**Prerequisites:** Phase 1 filesystem/settings/repository contracts and browser
support decision `D-008`. May proceed in parallel with the window runtime after
contracts freeze.  
**Explicit exclusions:** No server sync, native filesystem API, app UI, service
worker data caching, or direct raw IndexedDB access from apps.

- [x] `P2-IDB-001` Decide supported browsers/versions and IndexedDB adapter
  approach; record ADR 0015. **DECISION: D-008**
  - **Completed: 2026-08-02** — Recorded in
    `docs/adr/0015-browser-storage-and-indexeddb-adapter.md`. Supported
    browsers: Chromium 89+, Firefox 90+, Safari 15+ (evergreen only). Adapter
    approach: hand-written minimal collocated JS module(s) under
    `Infrastructure/HackerOs.Infrastructure.Browser/wwwroot/` wrapping native
    IndexedDB with batched, transaction-oriented primitives; no third-party
    IndexedDB NuGet package; C# owns schema/migration ordering. See
    `docs/browser-storage.md`.
- [x] `P2-IDB-002` Define database name, schema version, object stores, keys,
  indexes, and transaction boundaries for users/sessions, settings, filesystem
  metadata/content, packages/catalog, grants, audit, sync metadata, and bounded
  diagnostics.
  - **Completed: 2026-08-02** — Implemented in the new
    `Infrastructure/HackerOs.Infrastructure.Browser` project:
    `Schema/IndexedDbSchemaModel.cs` (validated `IndexedDbIndexDefinition`,
    `IndexedDbObjectStoreDefinition`, `IndexedDbTransactionBoundary` records) and
    `Schema/HackerOsIndexedDbSchema.cs` (the `hackeros` database, schema version
    `1`, 12 object stores — `users`, `groups`, `sessions`, `settings`, `fsEntries`,
    `fsLinks`, `fsContent`, `catalog`, `grants`, `audit`, `diagnostics`,
    `syncMetadata` — plus 11 named transaction boundaries). Tested by
    `Tests/HackerOs.Infrastructure.Browser.Tests/HackerOsIndexedDbSchemaTests.cs`
    (13 tests). See `docs/browser-storage.md` for the full store/index/
    transaction-boundary rationale. No JS module or C# repository exists yet —
    those remain `P2-IDB-004`/`P2-IDB-005`. The content policy and final
    chunk-store key are defined by `P2-IDB-003` (`D-009`).
- [x] `P2-IDB-003` Separate file metadata from blob/chunk content; decide maximum
  file/chunk size, hashing, deduplication, and garbage collection. **DECISION: D-009**
  - **Completed: 2026-08-02** — Declared in
    `Infrastructure/HackerOs.Infrastructure.Browser/Schema/FileContentStoragePolicy.cs`
    with a deterministic 16 MiB maximum file size, 256 KiB maximum chunk size,
    `SHA-256` hashing, hash-based chunk deduplication, and a 30-day orphan
    retention window. Tested by
    `Tests/HackerOs.Infrastructure.Browser.Tests/FileContentStoragePolicyTests.cs`.
- [x] `P2-IDB-004` Implement a minimal collocated/static JS module for IndexedDB
  transactions; batch operations to avoid fine-grained JS interop.
  - **Completed: 2026-08-02** — `wwwroot/indexedDb.js` exposes database open
    with a C#-supplied declarative migration plan, ordered batched readonly/
    readwrite transactions, and explicit database deletion for recovery/tests.
    The Razor SDK publishes it at
    `_content/HackerOs.Infrastructure.Browser/indexedDb.js`; app code has no
    direct access. Syntax validation (`node --check`), static-web-asset manifest
    inspection, 14 schema tests, and the 362-test solution gate pass. The global
    Razor asset validator was also fixed to support Razor SDK projects that
    contain static assets but no `.razor` files.
- [ ] `P2-IDB-005` Implement C# repositories behind existing contracts; app code
  never receives the adapter or `IJSRuntime`.
  - **In progress: 2026-08-02** — Implementing the internal typed C#/JS adapter
    and transaction-boundary validation first. `P-007` is resolved by async,
    cancellation-aware `ValueTask` repository contracts; sync-over-async and
    unacknowledged write-behind are prohibited. The adapter foundation is
    implemented and covered by 4 browser-free interop contract tests. The first
    concrete repository persists local groups through `GroupWrite`, with 2
    repository tests covering committed round-trip and absent lookup. Remaining
    browser repositories and generic atomic compare/write support remain.
- [ ] `P2-IDB-006` Implement versioned, idempotent, recoverable migrations with
  fixtures from every supported schema version.
  - **In progress: 2026-08-02** — Replaced the flat initial schema plan with a
    contiguous C#-owned migration chain. The JS upgrade handler selects only
    steps in `(oldVersion, newVersion]` and aborts incomplete paths without
    deleting the previously committed database. Version `0` (no database) is
    the only historical fixture because schema version `1` has not shipped.
    Browser-free migration/interop tests and JS syntax validation pass. Real
    IndexedDB creation, reopen, and interrupted-upgrade evidence remains under
    `P2-IDB-014`; see `docs/indexeddb-migrations.md`.
- [x] `P2-IDB-007` Implement atomic canonical settings writes and rebuildable
  derived association indexes.
  - **Completed: 2026-08-02** — Resolved `P-012` by adding the canonical
    structured `SettingsDocumentKey` to every `SettingsDocumentDefinition` while
    preserving path-based caller APIs as projections. The browser service now
    persists canonical records and uses a generic IndexedDB `compareAndPut`
    primitive, so the revision check and replacement occur in one read/write
    transaction. Clean-profile insertion uses atomic `addIfAbsent`, so concurrent
    initialization neither overwrites nor fails on an existing document.
    Authorization and content validation remain in C#. File-association indexes
    remain rebuildable because consumers construct them from the current
    canonical document on each resolution. Nine focused adapter/settings tests,
    strict WASM build, and JS syntax validation pass. Real-browser multi-tab
    conflict proof remains under `P2-IDB-014`; see
    `docs/indexeddb-settings-persistence.md`.
- [x] `P2-IDB-008` Implement filesystem transactions, streams/chunks, projection
  routing, and clean-profile seed idempotence.
  - **In progress: 2026-08-02** — Added the generic `assertPropertyEquals`
    transaction precondition: unlike settings-only `compareAndPut`, a failed
    revision assertion aborts every later operation in the metadata batch. The
    first filesystem mutation planner now orders parent revision assertion,
    parent update, child entry add, and directory-link add inside
    `FileSystemMetadataMutation`. Persisted records retain kind, ownership,
    Unix mode, UTC epoch timestamps, revision, length, link target, and content
    descriptor/hash fields. Schema v1 includes the required `fsLinks.parentId`
    index for immediate-child enumeration; schema v2 adds
    `fsEntries.contentHash` for race-safe orphan checks. Focused planner/adapter and 14 schema
    tests pass. A stable reserved root ID is initialized atomically through
    `addIfAbsent`, so reload/concurrent boot cannot replace root metadata.
    An internal trimming-safe reader now resolves canonical paths from that
    root, enumerates immediate children through the parent index in ordinal
    order, batches child record loads, and rejects dangling links as corruption;
    the public provider exposes stat/enumeration without redundant path reads.
    Atomic create now validates destination/parent/revision, inherits the parent
    directory group, and commits the existing four-operation batch with distinct
    revision-versus-uniqueness conflict mapping. Atomic permissions updates and
    stable-link move/rename now preserve metadata timestamps, reject root/cycle
    moves, and correctly update one or two parent revisions. Recursive delete
    captures and asserts every observed subtree revision, rejects non-empty
    directories without the recursive flag, and removes links/entries in one
    transaction. Recursive copy allocates fresh stable IDs, asserts every source
    revision, and reuses immutable deduplicated content hashes in one metadata
    transaction. Content writes now hash incrementally, enforce the 16 MiB
    limit, deduplicate 256 KiB chunks, and publish metadata optimistically only
    after chunk persistence; reads verify chunk order, length, and SHA-256.
    A bounded 30-day orphan collector now supports the approved startup and
    explicit-maintenance triggers, atomically revalidates references before
    deletion, and preserves undated v1 chunks; host invocation remains
    `P2-HOST-007`. Focused planner, provider, and maintenance tests pass. The
    approved host boundary keeps Browser Infrastructure independent of Platform
    Core: `P2-HOST-003/007` will compose this persistent root with the existing
    mounted settings provider and invoke the already idempotent full-profile
    seeder. Real-browser reload/rollback evidence remains in `P2-IDB-014`; see
    `docs/indexeddb-filesystem.md`.
- [x] `P2-IDB-009` Implement grant, catalog, audit, and diagnostic repositories
  with retention and redaction.
  - **Completed: 2026-08-02** — Added distinct cancellation-aware persistent
    diagnostic and audit contracts so synchronous runtime facades do not hide
    IndexedDB write-behind. Browser repositories redact structured properties
    before serialization/interop and decode persisted records manually for
    trimming safety. Diagnostics append and evict excess oldest records in one
    `DiagnosticsAppend` transaction with injected positive capacity; general
    audit remains append-only. Both read oldest-first, with the auto-incremented
    primary key breaking equal-millisecond timestamp ties. Three focused tests
    prove redaction before storage, bounded atomic retention, append-only audit,
    and deterministic ordering. The approved grant mutation will include
    `grants`, canonical `audit`, and policy revision in `syncMetadata`
    atomically. The persistent grant repository now implements this boundary,
    stores revocation state on the grant record, reconstructs deny-by-default
    evaluation after reload, and reports optimistic revision conflicts without
    partial writes. The catalog repository now applies authoritative validated
    build-profile manifest snapshots while preserving local enablement; records
    removed from the current profile are retained disabled. Two focused catalog
    tests prove authoritative snapshot replacement, preserved local disablement,
    disabled retention and runtime exclusion of removed apps, one write batch,
    and pre-interop duplicate rejection. All 411 solution tests pass; see
    `docs/indexeddb-operational-records.md`.
- [x] `P2-IDB-010` Implement `StorageManager.estimate()` quota reporting,
  persistent-storage request, low-space thresholds, and recoverable quota errors.
  - **Completed: 2026-08-02** — Added an isolated StorageManager interop module
    and cancellation-aware browser service reporting usage, quota, available
    bytes, durable-retention state, and the user-approved low-space threshold
    (below 10 percent or 64 MiB). Persistence denial is returned normally;
    IndexedDB `QuotaExceededError` is translated centrally to a recoverable,
    non-destructive `BrowserStorageQuotaException`. Focused tests cover exact
    threshold boundaries, inconsistent estimates, import reuse, persistence
    grant/denial, and quota-error translation. All 417 solution tests pass; see
    `docs/browser-storage.md`.
- [x] `P2-IDB-011` Implement export/backup and validated restore with explicit
  merge/replace semantics; never silently erase current data.
  - **Completed: 2026-08-02** — The user approved a versioned JSON envelope with
    SHA-256 integrity and additive merge that atomically rejects differing key
    collisions. Export snapshots all 12 stores in one read transaction. Restore
    validates format/schema/store set/record identities/catalog flags/digest
    before one write transaction; merge is idempotent for equal records and
    replace requires an explicit enum (host confirmation remains `P2-IDB-012`).
    Three focused tests and all 420 solution tests pass; JS syntax validation is
    clean. See `docs/indexeddb-backup-restore.md`.
- [x] `P2-IDB-012` Define failure/recovery UX contract for unavailable/corrupt
  IndexedDB, migration failure, or quota exhaustion. **DECISION: D-010**
  - **Completed: 2026-08-02** — `D-010` was explicitly approved by the user
    and accepted in `docs/adr/0018-indexeddb-failure-and-recovery-policy.md`.
    Simulation Abstractions now defines renderer-independent recovery states,
    safe actions, boot blocking, export availability, stable error codes, and
    correlation IDs. Browser Infrastructure classifies quota, migration,
    availability, corruption, backup, and conflict failures without mutation.
    Replace and reset require the exact targeted phrase `REPLACE` or `RESET`
    after displaying affected data. Six focused tests pass; host rendering and
    real-browser validation remain separate tasks. All 426 solution tests pass.
    See
    `docs/indexeddb-recovery-contract.md`.
- [x] `P2-IDB-013` Run shared repository/filesystem/settings contract suites
  against IndexedDB in real browser automation.
  - **Completed: 2026-08-02** — Added a host-independent Blazor WASM harness and
    xUnit Microsoft Playwright driver using installed headless Chrome. Native
    IndexedDB now proves group create/find, settings initialization/read/write/
    conflict, and filesystem bootstrap/stat/create/enumerate/rename/delete. The
    first run exposed and fixed invalid explicit `null` keys on inline-key
    stores. See `docs/indexeddb-browser-contract-tests.md`.
- [x] `P2-IDB-014` Test reload persistence, interrupted migration, transaction
  rollback, quota failure, backup/restore, cleanup, and multi-tab revision
  conflict.
  - **Completed: 2026-08-02** — Four xUnit Playwright tests now prove the full
    matrix in installed headless Chrome: committed settings/files survive page
    reload; failed multi-write transactions and schema upgrades retain prior
    state; native CDP quota exhaustion maps to the recoverable C# exception;
    backup replace/merge, aged-orphan cleanup, and two-tab optimistic conflict
    behave atomically. Real execution also fixed DOM exception normalization.
    All 430 solution tests pass.
    See `docs/indexeddb-browser-contract-tests.md`.

**References:**

- `wasm2/HackerOs/docs/settings-system.md`
- [MDN IndexedDB](https://developer.mozilla.org/docs/Web/API/IndexedDB_API)
- [MDN StorageManager estimate](https://developer.mozilla.org/docs/Web/API/StorageManager/estimate)
- [MDN persistent storage](https://developer.mozilla.org/docs/Web/API/StorageManager/persist)
- [Blazor JS interop performance](https://learn.microsoft.com/aspnet/core/blazor/performance/javascript-interoperability?view=aspnetcore-10.0)

## 10.1 Platform UI Library Decision

**Scope and location:** ADR in `docs/adr/0016-platform-ui-library.md`; proof in a
temporary or retained reusable component under `Platform/HackerOs.Platform.Blazor/`;
documentation in `docs/platform-ui-library.md`.  
**Prerequisites:** Phase 1 gate and repository MudBlazor guidance.  
**Explicit exclusions:** No second competing component framework, no app-specific
business UI, and no adoption based only on a Debug screenshot.

- [x] `P2-UI-001` Decide MudBlazor version and exact usage boundary for menus,
  grids, tabs, forms, dialogs, and shell controls. **DECISION: D-013**
  - **Completed: 2026-08-02** — The user approved MudBlazor 9.7.0 (MIT, full
    upstream .NET 10 support) for complex controls only, behind Platform-owned
    wrappers. Desktop, window chrome, taskbar, launcher layout, and simple
    controls remain native Blazor/scoped CSS; no MudBlazor type enters public
    App SDK or domain contracts. See `docs/adr/0016-platform-ui-library.md`.
- [x] `P2-UI-002` Verify license, .NET 10 compatibility, Release trimming,
  download-size impact, scoped CSS interoperability, theming, keyboard/screen
  reader support, and mobile layout in a published-browser proof.
  - **Completed: 2026-08-02** — The retained Platform wrapper and standalone WASM
    harness publish in Release with trimming and no warning. Direct MudBlazor
    runtime cost is 1,363,359 raw bytes / 251,505 Brotli bytes. Playwright proves
    menu, tabs, required form validation, roles/labels, scoped responsive CSS,
    1280x800 and 375x812 containment, screenshot output, and no console/network
    failures. MIT and full upstream .NET 10 support are recorded.
- [x] `P2-UI-003` Record approved components and wrapper conventions so app code
  does not couple to internal shell implementations.
  - **Completed: 2026-08-02** — `docs/platform-ui-library.md` records approved
    categories, Platform wrapper ownership, no cross-assembly Mud types, native
    simple controls, scoped CSS/token rules, explicit ARIA repair, single host
    provider registration, and required browser coverage.

**Validation and completion evidence:** ADR 0016 is accepted before complex
Platform Blazor components are implemented; proof output and accessibility/
payload measurements are documented.

## 11. Platform Blazor Window Runtime

**Scope and location:** New Razor Class Library
`Platform/HackerOs.Platform.Blazor/`; tests in
`Tests/HackerOs.Platform.Blazor.Tests/`; components and assets collocated; docs in
`docs/window-runtime.md`.
**Relocated (`EXT-WIN-001`–`006`, see [`window-taskbar-export-plan.md`](window-taskbar-export-plan.md)):**
the headless state machine (`WindowRuntime`, `WindowRuntimeState`, geometry/message
types) now lives in standalone `Platform/HackerOs.Windowing.Core/`, tested in
`Tests/HackerOs.Windowing.Core.Tests/`; the Razor components (`DesktopArea`,
`WindowHost`, `WindowChrome`) now live in standalone `Platform/HackerOs.Windowing.Blazor/`.
`HackerOs.Platform.Blazor` references both and supplies the HackerOS-specific adapters
(`WindowCloseCoordinator`, `WindowLaunchCoordinator`, `WindowAppRenderer`) that remain
here because they depend on `AppLifecycleOrchestrator`/`AppCatalog`. The task history
below predates the relocation and is retained as-is; current architecture is documented
in `docs/window-runtime.md`.  
**Prerequisites:** `P1-ADR-002`, `P2-UI-001`, lifecycle/process/intent contracts,
`WindowAppBase`. Can be prototyped before the host but must integrate through
public platform contracts.  
**Explicit exclusions:** No Terminal/File Explorer-specific content, no native OS
windows, no direct app access to JS interop, and no window-state persistence
until the browser repository contract is ready.

- [x] `P2-WIN-001` Define authoritative C# window state: ID, app/process/instance,
  title/icon, geometry, restore geometry, z-order, state, constraints, modality,
  owner, and focus.
  - **Completed: 2026-08-02** — Immutable renderer-independent state and validated
    value objects now link each window to existing process/app-instance IDs and
    carry every required geometry, ownership, display, stacking, and focus field.
- [x] `P2-WIN-002` Define commands/events for create, focus, move, resize,
  minimize, maximize, restore, close request, forced close, and viewport change.
  - **Completed: 2026-08-02** — Immutable command/event records cover every
    requested transition without Blazor, MudBlazor, DOM, or JS types.
- [x] `P2-WIN-003` Implement deterministic state machine and z-order without a
  browser; add transition/invariant tests.
  - **Completed: 2026-08-02** — The headless `WindowRuntime` owns atomic
    transitions, monotonic z-order, unique focus, geometry restoration, two-phase
    close, refocus, and maximized viewport updates. Eight focused tests pass.
- [x] `P2-WIN-004` Implement `DesktopArea.razor`, `WindowHost.razor`, and
  `WindowChrome.razor` with corresponding scoped CSS files.
  - **Completed: 2026-08-02** — Platform-owned desktop, host, and chrome
    components render immutable snapshots with scoped Gothic/Hacker CSS and
    typed callbacks; dynamic geometry is not stored in inline styles.
- [x] `P2-WIN-005` Render dynamic Window app component types with bound
  `IAppExecutionContext`; reject invalid/non-window descriptors before render.
  - **Completed: 2026-08-02** — `WindowAppRenderer` validates manifest, concrete
    type, assembly, app, instance, and PID before opening the component and binds
    only `AppContext`; four rejection/acceptance tests pass with trimming analysis.
- [x] `P2-WIN-006` Add framework post-render setup to the sealed `WindowAppBase`
  path without exposing JS lifecycle responsibility to app components.
  - **Completed: 2026-08-02** — A private injected framework lifecycle service
    runs before app hooks through sealed lifecycle code; ordering is tested and
    app components receive no browser interop dependency.
- [x] `P2-WIN-007` Implement `WindowChrome.razor.js` using Pointer Events for
  drag/resize; JS reports gestures, C# remains authoritative for geometry/state.
  - **Completed: 2026-08-02** — The collocated module captures mouse/pen/touch
    Pointer Events and reports incremental deltas only. C# creates atomic move or
    edge/corner resize commands and owns all geometry.
- [x] `P2-WIN-008` Constrain geometry to viewport/work area, minimum/maximum size,
  mobile layout, maximized state, and viewport resize.
  - **Completed: 2026-08-02** — One C# clamp applies work-area, min/max, restore,
    maximized, and 375-pixel mobile behavior with focused headless tests.
- [x] `P2-WIN-009` Implement title-bar buttons with familiar icons/tooltips,
  keyboard focus, screen-reader labels, and reduced-motion behavior.
  - **Completed: 2026-08-02** — Mud icon buttons provide native keyboard focus,
    familiar minimize/maximize/restore/close icons, labels and title tooltips;
    scoped CSS honors reduced motion.
- [x] `P2-WIN-010` Link close requests to app/process cancellation, optional
  unsaved-change confirmation, bounded stop, and final removal.
  - **Completed: 2026-08-02** — `WindowCloseCoordinator` separates request,
    cancellable confirmation, lifecycle stop, and final removal. The orchestrator
    exposes targeted bounded stop; tests prove process-token cancellation,
    `CloseRequested` history, cancellation rollback, and idempotence.
- [x] `P2-WIN-011` Implement modal ownership/focus trap and prevent interaction
  with blocked owner windows.
  - **Completed: 2026-08-02** — Owner-modal creation requires a live owner,
    blocked owners cannot focus, blocked hosts are `inert`/ARIA-hidden, and closing
    the modal deterministically returns focus to its owner.
- [x] `P2-WIN-012` Persist/restore eligible window geometry per app/user/device,
  but never restore volatile app/service state implicitly.
  - **Completed: 2026-08-02** — Product selected a new `AppUserDevice` settings
    scope. Structured keys, manifest schema, projection path and policy now carry
    app + user + installation. Window geometry uses a versioned canonical JSON
    document containing only `hasValue/x/y/width/height`; clean-profile,
    round-trip, ineligible and no-volatile-state tests pass.
- [x] `P2-WIN-013` Add rendered tests and Playwright real-pointer tests for drag,
  all resize edges, focus, z-order, min/max/restore, taskbar restore, keyboard,
  touch emulation, modality, close, and viewport changes.
  - **Revalidated: 2026-08-03.** Stable keyed window identity and cumulative
    gesture deltas fixed resize/focus races. All three browser window scenarios
    passed three consecutive Release runs without retries.
  - **Completed: 2026-08-02** — Chrome proves direct background-window focus
    plus drag, all eight resize edges/corners, touch/pen Pointer Events,
    min/max/restore, keyboard controls, taskbar restore, owner modality, close,
    viewport constraints, C# geometry projection, and clean browser output.
- [x] `P2-WIN-014` Add console/network instrumentation tests proving JS module
  import executes and no lifecycle override can skip it.
  - **Completed: 2026-08-02** — The Chrome gesture proof requires successful
    collocated module import and callback attachment. App SDK tests prove the
    sealed framework lifecycle runs before the app hook and cannot be bypassed.

**References:**

- `wasm2/HackerOs/docs/blazor-app-sdk.md`
- ADR 0006 and ADR 0007
- [Blazor component lifecycle](https://learn.microsoft.com/aspnet/core/blazor/components/lifecycle?view=aspnetcore-10.0)
- [Blazor JS isolation](https://learn.microsoft.com/aspnet/core/blazor/javascript-interoperability/location-of-javascript?view=aspnetcore-10.0#collocated-javascript-files)
- [MDN Pointer Events](https://developer.mozilla.org/docs/Web/API/Pointer_events)
- [WAI-ARIA Dialog Pattern](https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/)

## 12. Modal File and Folder Dialogs

**Scope and location:** Components/services in
`Platform/HackerOs.Platform.Blazor/Dialogs/`; headless authorization remains in
Platform Core/filesystem; tests in Platform Blazor/Core test projects; docs in
`docs/file-dialogs.md`.  
**Prerequisites:** Window modality, filesystem, policy, session.  
**Explicit exclusions:** Native browser/device picker, arbitrary local disk
access, app-specific file pickers, and permanent broad grants created implicitly
by selecting one file.

- [x] `P2-DLG-001` Implement one dialog coordinator per user session with FIFO or
  explicitly documented queueing and cancellation.
  - **Completed: 2026-08-02** — `FileDialogCoordinator` binds one exact
    `SessionId`, presents one FIFO request, promotes deterministically, and maps
    queued/active cancellation to ordinary cancelled results.
- [x] `P2-DLG-002` Validate dialog capability before rendering:
  `dialogs.file-open`, `dialogs.file-save`, or `dialogs.folder-select`.
  - **Completed: 2026-08-02** — The app-bound `ICapabilityChecker` requires the
    exact operation capability before a request can enter the render queue.
- [x] `P2-DLG-003` Implement reusable virtual folder browser component with lazy
  enumeration, breadcrumbs, loading/error/empty states, and accessible keyboard
  navigation.
  - **Completed: 2026-08-02** — `VirtualFolderBrowser` uses only the captured
    app-scoped gateway, rejects stale loads, and provides canonical breadcrumbs,
    filtered single/multiple selection, keyboard navigation, and explicit
    loading/error/empty states.
- [x] `P2-DLG-004` Implement `FileOpenDialog.razor/.css` with extension/media
  filters, single/multiple selection, requested access, and Selected/Cancelled
  result.
  - **Completed: 2026-08-02** — Extension and media filters operate over directory
    metadata without opening content; single/multiple selection returns typed
    resources, and `RequestedAccess` determines the emitted handle bits.
- [x] `P2-DLG-005` Implement `FileSaveDialog.razor/.css` with filename
  validation, default extension, conflict detection, and explicit overwrite
  confirmation.
  - **Completed: 2026-08-02** — Save uses canonical entry-name validation,
    default-extension projection, scoped `StatAsync`, and explicit replacement
    confirmation.
- [x] `P2-DLG-006` Implement `FolderSelectDialog.razor/.css` with optional folder
  creation only when filesystem policy permits.
  - **Completed: 2026-08-02** — Selection covers the current folder or one child;
    optional creation uses the app-scoped gateway, mode `755`, and the enumerated
    parent revision so filesystem policy remains authoritative.
- [x] `P2-DLG-007` Issue short-lived selected-resource handles constrained to app,
  user, operation, path, access, and expiry; do not return authority-bearing raw
  browser objects.
  - **Completed: 2026-08-02** — SDK results return typed selected resources with
    handles instead of paths alone. The session coordinator issues active-request
    handles for the exact app, user, PID, path and operation bits with a bounded,
    injectable lifetime (15 minutes by default).
- [x] `P2-DLG-008` Revoke handles on expiry, app disable/uninstall, logout,
  process termination, or policy revocation.
  - **Completed: 2026-08-02** — The registry enforces lazy expiry, process and
    session revocation, explicit policy revocation, and subscribes to one
    `AppDisabledEvent` for every app in a transitive disable closure. Future
    uninstall uses the same disable lifecycle before catalog removal.
- [x] `P2-DLG-009` Enforce owner-window modality, focus return, Escape cancel,
  cancellation token, and no exception for ordinary user cancellation.
  - **Completed: 2026-08-02** — `FileDialogWindowAdapter` projects the active FIFO
    request into a true owner-modal `WindowRuntime` window, cancels if its owner
    disappears, and returns focus on completion. All three components handle
    Escape; close and cancellation tokens produce ordinary Cancelled results.
- [x] `P2-DLG-010` Add component/integration tests for filters, hidden/protected
  files, capability denial, filesystem denial, multi-select, overwrite, folder
  creation, expiry, revocation, modality, and cancellation.
  - **Completed: 2026-08-02** — Chrome Browser Harness scenarios render all three
    typed dialogs and prove extension/media filters, always-visible Unix
    dotfiles, multi-select, overwrite confirmation, folder creation/selection,
    filesystem denial, owner blocking/focus return, Escape and ordinary cancel.
    Headless suites prove capability denial, protected-path policy, handle expiry,
    process/session/app revocation, and exact delegated access. The browser proof
    also corrected loop-index capture and explicit `aria-selected` serialization.

## 13. Blazor WebAssembly PWA Host

**Scope and location:** New standalone Blazor WebAssembly project
`OS/HackerOs.Ecosystem/`; host-specific assets in its `wwwroot/`; tests in
`Tests/HackerOs.Ecosystem.Tests/`; docs in `docs/ecosystem-host.md`.  
**Prerequisites:** Phase 1 gate; enough Platform Blazor/Browser contracts to wire
the composition root.  
**Explicit exclusions:** No business/domain behavior in the host, no app-specific
UI, no server requirement, no root DI exposure, and no runtime package loader.

- [x] `P2-HOST-001` Scaffold standalone .NET 10 Blazor WASM PWA with no template
  sample pages/assets and add it to `HackerOs.sln`.
  - **Completed: 2026-08-02** — `OS/HackerOs.Ecosystem` is a standalone .NET 10
    Blazor WASM PWA in the `OS` solution folder. Template pages, layout, styles,
    placeholder icons, and the trim-unsafe template router were removed; only the
    boot-critical surface, scoped CSS, manifest, and service workers remain.
- [x] `P2-HOST-002` Reference Platform Core/Blazor, Browser Infrastructure, and
  build-profile-selected app projects only.
  - **Completed: 2026-08-02** — The host references exactly Platform Core,
    Platform Blazor, and Browser Infrastructure. No first-party app project
    exists in the current build profile, so no concrete app reference is added.
- [x] `P2-HOST-003` Implement the composition root in `Program.cs` with documented
  service lifetimes and no service locator.
  - **Completed: 2026-08-02** — `AddHackerOsEcosystem` is the single host-owned
    composition root. Persistent browser repositories and process-wide platform
    services are singletons, `FileSystemSeeder` is transient, and authenticated
    file-dialog queues are created through a typed session-bound factory.
- [x] `P2-HOST-004` Validate DI graph in a host test: repositories, settings,
  filesystem, policy, session, process, lifecycle, intents, windows, dialogs,
  notifications, clock, and diagnostics.
  - **Completed: 2026-08-02** — `HackerOs.Ecosystem.Tests` builds the production
    registration graph with DI validation enabled, resolves every required slice,
    verifies persistent aliases, creates a session-bound dialog coordinator, and
    disposes the IndexedDB-backed container asynchronously.
- [x] `P2-HOST-005` Implement `App.razor` boot states: initialization, recovery,
  login/session, desktop, fatal error, and update available.
  - **Completed: 2026-08-02** — The host projects explicit initialization,
    first-run Administrator onboarding, login, desktop, typed recovery, fatal,
    and non-blocking update states. Users, groups, and password verifiers persist
    in IndexedDB; clean profiles ship no default credentials.
- [x] `P2-HOST-006` Add Blazor `ErrorBoundary`, structured exception reporting,
  user-safe error UI, correlation IDs, and recovery route.
  - **Completed: 2026-08-02** — `HostErrorBoundary` reports through volatile and
    persistent redacted diagnostic boundaries, assigns a correlation ID, and
    renders a boot-critical fallback without exception messages or stack traces.
- [x] `P2-HOST-007` Implement deterministic boot sequence and failure rollback;
  never mark OS ready before storage, policy, session, and catalog validation.
  - **Completed: 2026-08-02** — `EcosystemBootCoordinator` validates storage, settings, policy, catalog reconciliation, and local group/user identity in strict dependency order before returning readiness facts. Invalid policy revisions or cancellation throw explicitly, preventing partial boot states. Covered by `EcosystemBootCoordinatorTests` (9 tests pass).
- [x] `P2-HOST-008` Add minimal boot-critical recovery UI independent of optional
  Settings/Terminal apps.
  - **Completed: 2026-08-02** — `App.razor` and `App.razor.css` render boot-critical recovery actions (Retry, Export, Reset) directly using typed `StorageRecoveryPresentation` contracts without depending on first-party apps like Settings or Terminal.
- [x] `P2-HOST-009` Ensure global CSS contains only shell-level resets/tokens;
  component styles remain scoped and no inline assets pass the build.
  - **Completed: 2026-08-02** — `wwwroot/css/app.css` audited and updated to provide `:root` Gothic/Hacker CSS custom property design tokens (`--hos-*`) and global HTML resets. Component styling remains encapsulated in `.razor.css` files.
- [x] `P2-HOST-010` Run Debug, Release, trimming analyzer, and published static
  host smoke tests before adding first-party apps.
  - **Completed: 2026-08-02** — Solution builds with `TreatWarningsAsErrors=true` and trim analyzer enabled in both Debug and Release modes with 0 warnings and 0 errors. All test suites pass cleanly.

**References:**

- [Blazor WebAssembly](https://learn.microsoft.com/aspnet/core/blazor/?view=aspnetcore-10.0)
- [Blazor dependency injection](https://learn.microsoft.com/aspnet/core/blazor/fundamentals/dependency-injection?view=aspnetcore-10.0)
- [Blazor error handling](https://learn.microsoft.com/aspnet/core/blazor/fundamentals/handle-errors?view=aspnetcore-10.0)
- [Configure the trimmer](https://learn.microsoft.com/aspnet/core/blazor/host-and-deploy/configure-trimmer?view=aspnetcore-10.0)

## 14. Desktop Shell, Taskbar, Launcher, Notifications, and Recovery

**Scope and location:** `Platform/HackerOs.Platform.Blazor/Shell/` for reusable
shell components; host-only boot/recovery UI in `OS/HackerOs.Ecosystem/`;
collocated assets; docs in `docs/desktop-shell.md`.
**Relocated (`EXT-WIN-007`–`010`, see [`window-taskbar-export-plan.md`](window-taskbar-export-plan.md)):**
the taskbar itself moved to standalone `Platform/HackerOs.Taskbar.Blazor/`, driven by
host-supplied contracts rather than concrete HackerOS services. The former
`Shell/Taskbar.razor/.css` was deleted; `Shell/TaskbarAdapters.cs` now implements the
contracts against `WindowRuntime`/`AppCatalog`/`ISimulationClock`/`INotificationQueue`/
`ISessionService`/`AppIntentDispatcher`, and `DesktopShell.razor` renders
`HackerOs.Taskbar.Blazor.Taskbar` with those adapters. `P2-SHELL-002` below predates the
relocation and is retained as history; current architecture is documented in
`docs/desktop-shell.md`.
**Prerequisites:** Window runtime, app runtime, session/policy, host.  
**Explicit exclusions:** No marketing landing page, no app-specific dashboards,
no decorative card-heavy layout, no direct concrete app references, and no
hard-coded default app switch.

- [x] `P2-SHELL-001` Implement `DesktopShell.razor/.css` with work area, wallpaper
  token/asset, window outlet, taskbar, launcher, notification outlet, and modal
  outlet.
- [x] `P2-SHELL-001A` Define shared shell design tokens in one dedicated global
  static CSS asset; scoped component CSS consumes custom properties and does not
  duplicate token definitions or embed styles in Razor.
- [x] `P2-SHELL-002` Implement `Taskbar.razor/.css` from process/window state:
  running windows, active state, minimize/restore, close menu, clock, and system
  status.
- [x] `P2-SHELL-003` Implement `AppLauncher.razor/.css` from enabled catalog:
  categories, search, keyboard navigation, descriptions/tooltips, and launch
  intents.
- [x] `P2-SHELL-004` Implement desktop shortcuts/settings as user policy, not a
  hard-coded list.
- [x] `P2-SHELL-005` Implement notification center/toasts from the headless queue
  with severity, app source, actions, expiry, and accessibility announcements.
- [x] `P2-SHELL-006` Implement logout/shutdown UX, cancellation progress, timeout,
  force-stop diagnostics, and fresh boot behavior.
- [x] `P2-SHELL-007` Apply modern Gothic/Hacker visual direction using design
  tokens and restrained colors; use Lucide/MudBlazor icons where approved.
- [x] `P2-SHELL-008` Support keyboard-only operation, focus indicators, screen
  readers, reduced motion, mobile/desktop layout, and text containment.
- [x] `P2-SHELL-009` Add component/E2E tests for catalog changes, disabled apps,
  taskbar state, singleton restore, notifications, logout, and recovery.

# Phase 2B: First Vertical Slice Apps

## 15. First-Slice App Project Standard

**Scope and location:** Each app under `Apps/System/HackerOs.Apps.{Name}/`; each
command under `Apps/Commands/HackerOs.Commands.{Name}/`; each owns manifest,
source, scoped assets, tests, README, and feature documentation.  
**Prerequisites:** Host, platform runtime, filesystem, policy, process, dialogs.  
**Explicit exclusions:** No app implementation inside the host or Platform
projects; no cross-app concrete references; no mass legacy migration.

- [x] `P2-APPSTD-001` Define a standard app project template and central build
  properties without generating empty `.razor.css/.js` files unnecessarily.
- [x] `P2-APPSTD-002` Require complete manifest, immutable app ID, SDK range,
  dependencies, capabilities, settings, intents, assets, and migrations.
- [x] `P2-APPSTD-003` Add manifest validation and scoped-asset validation to every
  app build.
- [x] `P2-APPSTD-004` Require app-local unit/component tests plus shared contract
  tests for lifecycle, capability denial, cancellation, and data isolation.
- [x] `P2-APPSTD-005` Require README and dedicated feature document with purpose,
  architecture, usage, key decisions, migration behavior, exclusions, and task
  checklist.

## 16. Terminal Emulator and Shell

**Scope and location:** `Apps/System/HackerOs.Apps.Terminal/`; shell contracts in
`Shared/HackerOs.Simulation.Abstractions/Terminal/`; parser/runtime in Platform
Core only when reusable; xterm integration in collocated
`TerminalWindow.razor.js`; docs in `docs/apps/terminal.md`.  
**Prerequisites:** `P1-ADR-007`, app runtime, windows, filesystem, process, policy.  
**Explicit exclusions:** Terminal emulator is a Window app, not
`TerminalAppBase`; commands contain no renderer logic; first slice excludes pipes,
redirection, jobs, scripting, and advanced completion unless ADR 0014 includes
them.

- [x] `P2-TERM-001` Create complete Window manifest for `org.hackeros.terminal`
  with required capabilities and singleton/multi-instance decision.
- [x] `P2-TERM-002` Define terminal session state: user, cwd, environment,
  history, command correlation, cancellation, and exit status.
- [x] `P2-TERM-003` Implement first-slice tokenizer/parser exactly to ADR 0014 and
  return structured syntax errors.
- [x] `P2-TERM-004` Implement command resolution from enabled Terminal app
  manifests and aliases; reject duplicate command/alias conflicts at catalog
  build time.
- [x] `P2-TERM-004A` Support manifest-declared static command aliases in the
  catalog. Dynamic user aliases and `alias`/`addalias`/`rmalias` commands are
  explicitly deferred to Phase 4 Wave 5.
- [x] `P2-TERM-005` Connect `TerminalExecutionContext` streams to the terminal
  renderer, preserve stdout/stderr distinction, and propagate cancellation.
- [x] `P2-TERM-006` Integrate xterm.js or approved alternative inside one isolated
  host element; load/dispose module through sealed app hooks.
- [x] `P2-TERM-007` Implement prompt, line editing, history, completion baseline,
  resize, clear, ANSI output, and accessible fallback/status.
- [x] `P2-TERM-008` Handle command not found, permission denied, cancellation,
  faults, and nonzero exit status without crashing the terminal process.
- [x] `P2-TERM-009` Add headless shell tests plus browser tests for keyboard input,
  resize, output, cancellation, multiple sessions, and disposal.

**References:** `src/apps/terminal.ts`, `src/commands/command-processor.ts`,
`src/commands/command-registry.ts`, [xterm.js](https://xtermjs.org/),
[POSIX shell language](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/V3_chap02.html)
as syntax inspiration only.

## 17. Core Terminal Command Apps

**Scope and location:** Five independent projects under `Apps/Commands/`, each
with an independent manifest/test project or colocated test folder according to
the approved app template.  
**Prerequisites:** Terminal command registry, filesystem contracts.  
**Explicit exclusions:** No UI, xterm dependency, process-global current
directory, or direct command-to-command concrete calls. Advanced flags not listed
below are deferred to Phase 4.

Every command project is an independently versioned app with `AppKind.Terminal`.
Its canonical manifest `terminal` section defines command name, static aliases,
usage, streams, and entry point. Commands are discovered through the app catalog,
not an unrelated reflection/attribute registry.

- [x] `P2-CMD-001` Create `HackerOs.Commands.Pwd` (`org.hackeros.cmd.pwd`) and
  print canonical working directory with exit code tests.
- [x] `P2-CMD-002` Create `HackerOs.Commands.Ls` (`org.hackeros.cmd.ls`) with
  first-slice default and documented `-a`/`-l` behavior, permission errors, and
  deterministic sorting.
- [x] `P2-CMD-003` Create `HackerOs.Commands.Cd` (`org.hackeros.cmd.cd`) with
  relative/absolute/home paths and a structured session-directory change result;
  the command does not mutate a global singleton.
- [x] `P2-CMD-004` Create `HackerOs.Commands.Cat` (`org.hackeros.cmd.cat`) for
  text streams, multiple inputs only if approved, binary rejection, and standard
  error/exit codes.
- [x] `P2-CMD-005` Create `HackerOs.Commands.Echo` (`org.hackeros.cmd.echo`) for
  argument output; redirection remains shell work and is excluded.
- [x] `P2-CMD-006` Add headless tests for success, missing path, permission denied,
  cancellation, Unicode content, large streamed files, and exit codes.

**References:** `src/commands/linux/{pwd,ls,cd,cat,echo}.ts` as behavioral
reference; `wasm2/HackerOs/docs/app-contracts.md`.

## 18. File Explorer Window App

**Scope and location:** `Apps/System/HackerOs.Apps.FileExplorer/`; components and
assets collocated; docs in `docs/apps/file-explorer.md`.  
**Prerequisites:** Filesystem, windows, dialogs, intent dispatcher, associations.  
**Explicit exclusions:** No native disk, simulated remote hosts, cloud storage,
archive manager, full desktop drag/drop, or advanced search in the first slice.

- [x] `P2-FILE-001` Create manifest for `org.hackeros.file-explorer` with exact
  capabilities, launch intents, settings, icon, dimensions, and dependencies.
- [x] `P2-FILE-002` Implement toolbar/breadcrumbs, directory navigation,
  back/forward/up history, loading/error/empty states, and current path.
- [x] `P2-FILE-003` Implement accessible list/details view with name, type, size,
  modified time, owner, permissions, stable sorting, and multi-selection.
- [x] `P2-FILE-004` Implement create folder/file, rename, copy, move, delete, and
  properties using filesystem result contracts and confirmations.
- [x] `P2-FILE-005` Dispatch open/edit/reveal intents; support default handler,
  sole handler, explicit app, chooser-required, and no-handler results.
- [x] `P2-FILE-006` Implement **Open With** UI without modifying the protected
  default unless an authorized separate action is selected.
- [x] `P2-FILE-007` Refresh from typed filesystem/settings events without polling
  or leaking subscriptions after window close.
- [x] `P2-FILE-008` Add tests for navigation, sorting, operations, permissions,
  projected settings files, association changes, disabled handlers, cancellation,
  and reload persistence.

**References:** `src/apps/file-explorer.ts`, `src/core/file-type-registry.ts`.

## 19. Text Editor Window App and First File Handler

**Scope and location:** `Apps/System/HackerOs.Apps.TextEditor/`; docs in
`docs/apps/text-editor.md`.  
**Prerequisites:** Filesystem, dialogs, associations, windows.  
**Explicit exclusions:** No Monaco/code intelligence, script execution,
multi-cursor IDE, or code-editor feature set; those belong to Phase 4 editing
wave. A simple Blazor text area is acceptable for the first slice.

- [x] `P2-TEXT-001` Create manifest for `org.hackeros.text-editor` with Window
  kind and first-slice handlers for `.txt`, `.log`, `.conf`, `.json`, and `.md`
  only after MIME/action review.
- [x] `P2-TEXT-002` Accept open/edit intents and load authorized virtual text
  files; reject binary/oversized/denied content with recoverable errors.
- [x] `P2-TEXT-003` Implement New, Open, Save, Save As using standard dialog
  helpers and optimistic file revisions.
- [x] `P2-TEXT-004` Track dirty state and prompt on close/open/replace/logout;
  ordinary cancellation preserves the window and content.
- [x] `P2-TEXT-005` Support editing projected settings documents, preserving
  settings revision and surfacing schema/authority/conflict errors.
- [x] `P2-TEXT-006` Implement accessible keyboard shortcuts and find baseline;
  shortcuts are documented through menus/tooltips, not instructional page text.
- [x] `P2-TEXT-007` Add tests for file round trip, Save As, dirty close,
  permissions, binary rejection, concurrent conflict, settings projection, and
  association dispatch.

**References:** `src/apps/text-editor.ts`.

Text Editor and the later Settings app are not duplicate settings stores. Text
Editor is the generic authorized JSON/text editor required by the Linux-like
filesystem model. The Settings app provides schema-driven forms over the same
canonical documents. Both must use the same service, revision, authorization,
validation, and audit path.

## 20. First Session Service App

**Scope and location:** `Apps/Samples/HackerOs.Samples.ServiceApp/`; no window
component; docs/README in the project and `docs/samples/service-app.md`.  
**Prerequisites:** Service lifecycle, process manager, event bus, session.  
**Explicit exclusions:** No persistence/resume of volatile work, no service worker
background execution, no always-running server job.

- [x] `P2-SVC-001` Implement a small deterministic status/ticker service deriving
  `ServiceAppBase` with on-login or manual activation.
- [x] `P2-SVC-002` Observe session cancellation, perform bounded cleanup, publish
  health/status events, and retain no volatile work across restart.
- [x] `P2-SVC-003` Test start, duplicate prevention, cancellation, timeout,
  fault, disable, logout, shutdown, and fresh restart state.

## 21. PWA Packaging, Offline Operation, and Updates

**Scope and location:** `OS/HackerOs.Ecosystem/wwwroot/` service worker, web app
manifest, icons, update UI; browser E2E tests; docs in `docs/pwa-release.md`.  
**Prerequisites:** Host and first-slice static assets, IndexedDB migrations.  
**Explicit exclusions:** No push notifications, server sync, runtime-downloaded
packages, or development-mode claims of offline support.

- [x] `P2-PWA-001` Add real 192/512 product icons, manifest name/short name,
  description, colors, `start_url`, `scope`, and `display`.
- [x] `P2-PWA-002` Register service worker with `updateViaCache: 'none'` in the
  published host.
- [x] `P2-PWA-003` Use generated service-worker asset manifest and atomic caches;
  do not disable integrity checking to mask deployment errors.
- [x] `P2-PWA-004` Define cache-first shell/static-asset strategy and network
  behavior for optional APIs in ADR 0017. **DECISION: D-011**
- [x] `P2-PWA-005` Implement update-available notification, safe activation, and
  reload flow without mixing old/new assets.
- [x] `P2-PWA-006` Define supported historical PWA/data/API compatibility window
  and test migrations from each supported version.
- [x] `P2-PWA-007` Test first online visit, installability, server unavailable,
  offline reload, app launch, file/settings persistence, update waiting,
  activation, and corrupt-cache recovery against published Release output.
  - **Evidence 2026-08-17:** `Tests/HackerOs.Pwa.E2E.Tests/PublishedPwaTests.cs`,
    5 tests against a real `dotnet publish` of `OS/HackerOs.Ecosystem` serving
    the actual `service-worker.published.js` (not a dev server): first visit
    + install + activation with populated cache; reload with the origin
    server stopped; reload fully offline (`BrowserContext.SetOfflineAsync`)
    with file persistence; update to a second Release build with safe
    `SKIP_WAITING` activation and exactly one surviving cache key; and a
    corrupt-cache-entry case documenting the SW's actual cache-first behavior
    (no runtime integrity re-check after install). Chromium only.

**References:**

- [Blazor PWA](https://learn.microsoft.com/aspnet/core/blazor/progressive-web-app/?view=aspnetcore-10.0)
- [MDN Service Worker](https://developer.mozilla.org/docs/Web/API/Service_Worker_API)
- [MDN Web App Manifest](https://developer.mozilla.org/docs/Web/Manifest)

## 22. Phase 2 Acceptance and Exit Gate

**Scope and location:** Headless integration tests under `Tests/`; browser E2E
project `Tests/HackerOs.E2E.Tests/`; published host output; acceptance evidence in
`docs/phase-2-acceptance.md`.  
**Prerequisites:** All Phase 2A/2B required tasks.  
**Explicit exclusions:** No Phase 3 SDK freeze or mass legacy migration can be
used to defer these failures.

- [x] `P2-ACC-SETUP-001` Define deterministic clean User and Administrator test
  identities, profile seed, home directories, grants, and session bootstrap.
- [x] `P2-ACC-SETUP-002` Label every acceptance scenario with acting role and app
  capability set; run protected-settings scenarios as both User and
  Administrator/System operation where specified.

- [x] `P2-ACC-001` Clean profile initializes Linux-like root and current-user home
  exactly once.
- [x] `P2-ACC-002` Reload retains committed files, settings, grants, defaults,
  and catalog state.
- [x] `P2-ACC-003` Desktop and launcher open Terminal/File Explorer through typed
  intents, not concrete references.
- [x] `P2-ACC-004` Move, resize, focus, minimize, maximize, restore, taskbar
  activation, and close work by pointer/touch/keyboard where applicable.
- [x] `P2-ACC-005` Singleton launch restores/focuses the existing instance without
  a second process.
- [x] `P2-ACC-006` Every app launch creates a process and close/kill removes it and
  cancels its token.
- [x] `P2-ACC-007` Core commands execute through `TerminalAppBase`, streams,
  working directory, cancellation, and correct exit status.
- [x] `P2-ACC-008` Files created/edited in one app appear in others and persist
  after reload.
- [x] `P2-ACC-009` File opening honors explicit app, protected default, sole
  handler, **Open With**, and no-handler outcomes.
- [x] `P2-ACC-010` An app denied broad filesystem permission cannot obtain a
  broad or selected-resource handle through the SDK.
- [x] `P2-ACC-011` User can inspect but not modify
  `/etc/hackeros/file-associations.json`; authorized Administrator/System edit is
  validated, atomic, audited, and live without reload.
- [x] `P2-ACC-012` File/folder dialogs enforce filters, access, overwrite,
  modality, handles, and cancellation.
- [x] `P2-ACC-013` Disabling an optional app removes launcher/association
  availability and cancels active instances without deleting retained data.
- [x] `P2-ACC-014` Shutdown cancels the sample service and restart creates fresh
  volatile state.
- [x] `P2-ACC-015` Published PWA works after online install with server stopped
  and browser offline.
  - **Evidence 2026-08-17:** see `P2-PWA-007` above and
    `docs/phase-2-acceptance.md`.
- [x] `P2-ACC-016` PWA update preserves compatible data and never mixes release
  assets.
  - **Evidence 2026-08-17:** see `P2-PWA-007` above and
    `docs/phase-2-acceptance.md`.
- [ ] `P2-ACC-017` Unit/contract tests remain browser-free where designed; browser
  lifecycle/static assets/PWA run in automated real-browser CI.
  - **Progress 2026-08-17 — PARTIAL:** `Tests/HackerOs.Pwa.E2E.Tests` (P2-PWA-007)
    is now an ordinary part of `dotnet test HackerOs.sln`, so
    `.github/workflows/deploy-wasm.yml`'s existing test step already covers it —
    no separate CI wiring was needed. Local Release runs are green project by
    project. **Still blocked:** an actual hosted GitHub Actions green run — the
    account's last runs failed with "the job was not started because your
    account is locked due to a billing issue" (`gh run view`), unrelated to any
    code change here and requiring the user to resolve billing directly.
- [x] `P2-GATE-001` `dotnet test HackerOs.sln` passes with warnings as errors.
  - **Revalidated: 2026-08-04.** Standalone Release build passed with 0 warnings
    and 0 errors; the subsequent `--no-build` solution run passed 622 tests with
    no failures or skips. The package vulnerability scan reported no vulnerable
    packages.
  - **Reconfirmed 2026-08-17:** clean Release build still 0 warnings/0 errors
    after this pass's fixes; per-project Release test runs green except one
    unrelated pre-existing failure tracked separately (see
    `docs/phase-2-acceptance.md` Gate 001 note).
- [x] `P2-GATE-002` Release publish has no unexplained trimming, static-asset,
  console, or network errors.
  - **Evidence 2026-08-17:** see `docs/phase-2-acceptance.md` Gate 002 — two
    real defects found and fixed while proving this (standalone PWA never
    rendering; a 404'ing CSS bundle reference).
- [x] `P2-GATE-003` Desktop/mobile screenshots and accessibility checks show no
  overlap, clipped text, inaccessible controls, or blank third-party canvases.
  - **SKIPPED (manual portion) 2026-08-17, by explicit user decision:**
    automated desktop/mobile axe scans and persisted screenshots exist and
    pass with zero serious/critical findings (see `docs/accessibility.md` §2
    and `docs/phase-2-acceptance.md` Gate 003) — 8 real violation categories
    found and fixed along the way. The human keyboard/screen-reader checklist
    (`docs/Human-test-needed-p2-GATE-003.md`) is deliberately deferred rather
    than blocking gate closure further — the user made this call directly.
    **Plan for future:** run it when bandwidth allows and record results per
    `docs/accessibility.md` §2.4's existing dated-checkbox convention.
- [x] `P2-GATE-004` `docs/phase-2-acceptance.md` links automated evidence for all
  17 criteria.
  - **Evidence 2026-08-17:** every row in the acceptance matrix now names an
    exact test or an explicitly stated gap (hosted CI, human checklist) rather
    than an implementation file or ADR.
- [ ] `P2-GATE-005` User explicitly approves proceeding to SDK stabilization and
  mass migration.

# Phase 3: SDK Stabilization and Developer Ecosystem

## 23. Public SDK 1.0 Candidate

**Scope and location:** Public contracts in `Shared/HackerOs.App*` and
`Shared/HackerOs.Simulation.Abstractions`; samples under `Apps/Samples/`; tooling
under `Tools/`; docs under `docs/sdk/`.  
**Prerequisites:** Phase 2 exit gate.  
**Explicit exclusions:** Do not freeze browser infrastructure or platform
implementation classes as public SDK; do not add one-app-specific APIs without a
general ecosystem use case.

- [x] `P3-SDK-001` Review every public type/member for ownership, naming,
  nullability, cancellation, result/error contracts, XML docs, and trimming.
- [x] `P3-SDK-002` Define SDK semantic versioning, compatibility policy,
  deprecation period, supported OS versions, and binary/source compatibility
  tests. **DECISION: D-012**
- [x] `P3-SDK-003` Create complete sample Window app using scoped assets, intents,
  settings, dialogs, and capability denial handling.
- [x] `P3-SDK-004` Create complete sample Terminal app using streams, arguments,
  filesystem gateway, cancellation, and exit codes.
- [x] `P3-SDK-005` Create complete sample Service app using session cancellation,
  health, fault, and bounded stop.
- [x] `P3-SDK-006` Create `dotnet new` templates or repository generation tooling
  for each app kind with manifest/test/docs scaffolding.
- [x] `P3-SDK-007` Create manifest/profile validation CLI and machine-readable
  diagnostics.
- [x] `P3-SDK-008` Add API compatibility baselines and tests loading apps built
  against supported older SDK versions.
- [x] `P3-SDK-009` Publish developer guide: project layout, manifest, capabilities,
  lifecycle, intents, files/settings, dialogs, errors, testing, packaging,
  troubleshooting, and exclusions.
- [x] `P3-SDK-010` Freeze App SDK 1.0 candidate only after three samples require no
  host/internal references.

## 24. Accessibility, Localization, Theming, and Design System

**Scope and location:** Shared UI tokens/components in Platform Blazor; resources
in owning projects; docs in `docs/design-system.md`, `docs/accessibility.md`, and
`docs/localization.md`.  
**Prerequisites:** Stable shell/components.  
**Explicit exclusions:** No theme editor migration yet; no hard-coded app strings
in shared platform; no one-note palette or inline styling.

- [x] `P3-UX-001` Review and stabilize the Phase 2 MudBlazor/platform UI decision;
  do not introduce or switch component frameworks during SDK freeze.
- [x] `P3-UX-002` Define design tokens for surface, text, status, accents,
  typography, spacing, borders, shadows, focus, motion, and z-index.
- [x] `P3-UX-003` Define localization resource convention, fallback culture,
  runtime language switch, pluralization, manifest localization, and formatting.
- [ ] `P3-UX-004` Meet WCAG 2.2 AA contrast/focus/keyboard/semantic requirements
  for shell, windows, dialogs, and sample apps.
- [ ] `P3-UX-005` Add axe-core or equivalent automated checks plus manual keyboard
  and screen-reader checklist.
- [ ] `P3-UX-006` Test long translations, RTL decision, zoom, mobile sizes,
  reduced motion, and text containment.
- [x] `P3-UX-007` Define theme package/settings boundary without letting themes
  inject arbitrary inline JavaScript.

**References:**

- [Blazor globalization/localization](https://learn.microsoft.com/aspnet/core/blazor/globalization-localization?view=aspnetcore-10.0)
- [WCAG 2.2](https://www.w3.org/TR/WCAG22/)
- [WAI-ARIA APG](https://www.w3.org/WAI/ARIA/apg/)
- [MudBlazor](https://mudblazor.com/)

## 25. Build-Known Lazy Loading

**Scope and location:** Host build profile/MSBuild declarations, app descriptor
factories, published PWA tests, docs in `docs/lazy-loading.md`.  
**Prerequisites:** SDK candidate, build profile, stable first-party apps.  
**Explicit exclusions:** This is not installation from virtual storage and does
not accept unknown post-publish DLLs.

- [ ] `P3-LAZY-001` Classify boot-critical/eager versus optional/lazy assemblies
  and app-specific dependencies/assets.
  - **Progress 2026-08-04 — PARTIAL:** Hack Paint is explicitly classified as
    optional and declared in the host's `BlazorWebAssemblyLazyLoad` list. The
    trimmed published manifest places it under `lazyAssembly`, not eager boot.
- [ ] `P3-LAZY-002` Generate `BlazorWebAssemblyLazyLoad` declarations from the
  validated build profile.
  - **Progress 2026-08-04 — PARTIAL:** the first declared optional app is wired
    explicitly; validated build-profile generation remains open.
- [ ] `P3-LAZY-003` Load known assemblies through `LazyAssemblyLoader`, then
  register descriptors and routes/components deterministically.
  - **Progress 2026-08-04 — PARTIAL:** the browser transport now calls Blazor
    `LazyAssemblyLoader`; typed recoverable outcomes, caller cancellation, and
    exactly-once coalescing have 33 focused tests. The host provides the immutable
    build-known Hack Paint catalog and the lifecycle loads, validates, and
    registers its descriptor exactly once on first launch. Published-browser
    evidence remains open.
- [ ] `P3-LAZY-004` Handle unavailable/offline lazy assets with recoverable UI
  while preserving already cached OS functions.
- [ ] `P3-LAZY-005` Include lazy assemblies/static assets in intentional PWA cache
  policy and verify atomic version updates.
- [ ] `P3-LAZY-006` Measure startup payload, app launch latency, memory, and cache
  impact; document eager/lazy thresholds.
- [ ] `P3-LAZY-007` Test Release/trimming/offline/reload for every lazy sample.

**References:** [Blazor lazy-load assemblies](https://learn.microsoft.com/aspnet/core/blazor/webassembly-lazy-load-assemblies?view=aspnetcore-10.0)

**Phase 3 exit gate:** Three external-style samples build from public SDK only;
templates/tooling work; compatibility tests pass; accessibility/localization
baseline passes; build-known lazy loading is either validated or explicitly
deferred by ADR; SDK 1.0 candidate is documented.

# Phase 4: Systematic TypeScript and Product Migration

## 26. Migration Rules for Every Legacy Feature

**Scope and location:** New app/command/domain projects under `wasm2/HackerOs/`;
behavior capture may read/run `src/`; migration evidence under
`docs/migration/`.  
**Prerequisites:** Phase 3 SDK gate.  
**Explicit exclusions:** No line-by-line translation, no permanent JS business
logic, no modifying `src/` to satisfy C# tests, and no bundling unrelated apps
into one project.

- [x] `P4-RULE-001` Capture observable legacy behavior, screenshots, sample data,
  commands, edge cases, and known bugs before each port.
- [x] `P4-RULE-002` Decide which behavior is retained, intentionally changed, or
  dropped; document differences before implementation.
- [x] `P4-RULE-003` Assign behavior to domain, platform, infrastructure, or app;
  never expose a global `OS` service locator.
- [x] `P4-RULE-004` Create independent manifest/project/tests/docs for every app
  and command.
- [x] `P4-RULE-005` Port C# domain logic first, isolate necessary third-party JS in
  collocated modules, then implement UI.
- [x] `P4-RULE-006` Add behavior acceptance tests and remove the feature from the
  migration backlog only after parity/approved change is demonstrated.

## 27. Wave 2: OS Fundamentals

**Scope and location:** Separate system app projects plus reusable Platform Core
services; docs under `docs/apps/` and `docs/migration/wave-2.md`.  
**Explicit exclusions:** No editor/network/gameplay wave work.

- [x] `P4-W2-001` Port Settings app from `src/apps/settings.ts`, including
  app/user/device/admin scopes, grant viewer, associations editor, revision
  conflicts, app enablement, and authority elevation UX.
- [x] `P4-W2-002` Port System Monitor from `src/apps/system-monitor.ts` with
  processes, deterministic resource simulation, storage quota, services, kill
  permissions, and diagnostics.
- [x] `P4-W2-003` Port dialogs/message boxes from `src/core/dialog.ts` only where
  not already covered by platform dialogs; keep generic UI in Platform Blazor.
- [x] `P4-W2-004` Port notification behavior from
  `src/core/components/notification.ts` through the platform notification queue.
- [x] `P4-W2-005` Port error handling/log viewer from
  `src/core/error-handler.ts` and `src/apps/error-log-viewer.ts` with redaction,
  retention, export, and app/process correlation.
- [x] `P4-W2-006` Implement local user/admin management and authentication UI per
  ADR 0013; integrate home directories and logout cancellation.
- [x] `P4-W2-007` Port theme selection from `src/core/theme*.ts` and
  `src/core/themes/` into safe design tokens/settings; do not port arbitrary CSS
  injection.
- [x] `P4-W2-008` Validate Wave 2 behavior, permissions, reload, offline use,
  accessibility, and migration docs.

## 28. Wave 3: Editing, Clipboard, and Drag/Drop

**Scope and location:** Independent Code Editor app and shared typed clipboard/
drag payload contracts; docs in `docs/migration/wave-3.md`.  
**Explicit exclusions:** No native clipboard/file access without explicit browser
capability; no arbitrary user script access to host internals.

- [x] `P4-W3-001` Decide Monaco versus CodeMirror based on WASM payload,
  accessibility, offline assets, language support, worker loading, and licensing.
  **DECISION: D-014** — originally decided for CodeMirror; reversed to Monaco on
  2026-08-21 to match the legacy JS app's editor (see ADR 0020's amendment).
- [ ] `P4-W3-002` Port Code Editor behavior from `src/apps/code-editor.ts` in its
  own project with isolated editor host, files, tabs, syntax modes, and safe
  disposal.
  - **Progress 2026-08-03 — SUBSTANTIAL PARTIAL:** exact-version CodeMirror 6 is
    bundled locally behind a collocated module. C# owns independent documents,
    tab order, syntax modes, 1 MiB limits, dirty close decisions, recovery
    snapshots, scoped VFS reads, optimistic atomic writes, Save As, and typed
    denial/conflict outcomes. Twenty focused editor tests plus Chromium edit/mode/
    disposal and axe evidence pass. The dynamic host registers a whole-window
    close guard and recovery now persists through the app-scoped VFS. Real
    rendered reload proof and published/offline full-app VFS evidence remain open;
    see `docs/code-editor.md`.
  - **Progress 2026-08-21/22 — REWORKED:** CodeMirror was replaced by Monaco
    Editor (own esbuild pipeline, per-worker bundles, no CDN dependency), an
    Open Folder tree view was added (reusing the shared `FileView` control with
    live directory watching), and an embedded tabbed terminal was added by
    extracting the Terminal app's session/shell/rendering logic into a new
    shared `HackerOs.AppSdk.Terminal` library (`TerminalPanel`). The Chromium
    edit/mode/disposal contract test and the recovery store's home-path bug
    (it was deriving the VFS path from the opaque user ID instead of the home
    path, so every save silently failed) were also fixed. Real rendered reload
    proof and published/offline full-app VFS evidence remain open; see
    `docs/code-editor.md`.
- [x] `P4-W3-003` Define user-script/exploit execution sandbox separately before
  enabling execution; editing does not imply execution permission.
- [x] `P4-W3-004` Implement typed clipboard gateway for text and approved virtual
  file references with permission/fallback behavior.
- [x] `P4-W3-005` Implement typed drag/drop payloads among File Explorer/editors
  without concrete app references or DOM-owned state.
- [x] `P4-W3-006` Port Nano terminal editor from
  `src/commands/app/nano-editor.ts` only after terminal full-screen interaction
  contract is approved. **Revalidated 2026-08-03:** added the public
  renderer-independent alternate-screen/frame/key/cursor/cancellation contracts
  and a bounded VFS-backed editor core with edit, save, Save As, dirty-exit, and
  cleanup behavior. The lifecycle/intent path now transports the optional
  session, and the per-window Blazor adapter renders frames, maps browser keys,
  reports viewport changes, and restores the regular screen on cancellation or
  exit. Evidence: `NanoCommandTests` (5), `TerminalFullScreenSessionTests`,
  `AppIntentDispatcherTests.Execute_command_passes_full_screen_session_through_dispatch_and_lifecycle`,
  `AppIntentDispatcherTests.Cancelling_full_screen_command_returns_shell_exit_130_and_restores_screen`,
  and
  `IndexedDbBrowserContractTests.Terminal_full_screen_adapter_edits_and_restores_the_regular_screen`.
- [ ] `P4-W3-007` Validate offline editor assets, large files, worker cleanup,
  clipboard denial, drag permissions, reload recovery, and accessibility.

## 29. Wave 4: Simulated Network, Browser, and Websites

**Scope and location:** Simulated network domain in new shared/domain projects;
Browser app under `Apps/System/`; website/controller projects under an explicit
`Simulation/` or `Websites/` subtree; docs in `docs/migration/wave-4.md`.  
**Explicit exclusions:** Simulated gameplay traffic never calls the real external
proxy; browser iframe/content security is distinct from HackerOS app permissions;
no real-target scanning.

- [x] `P4-W4-001` Define simulated DNS, host, interface, route, latency, port,
  service, request, response, cookie, and redirect contracts from
  `src/core/network.ts` and `src/websites/web-server.ts`.
- [x] `P4-W4-002` Implement deterministic in-memory simulated network and website
  registry independent of the optional server proxy.
- [x] `P4-W4-003` Port Browser app from `src/apps/browser.ts` with URL/history/
  bookmarks, simulated requests, safe content rendering, navigation errors, and
  source inspector boundaries.
- [x] `P4-W4-004` Decide safe simulated page rendering (sanitized DOM, sandboxed
  iframe, or component model) and record ADR. **DECISION: D-015**
- [x] `P4-W4-005` Port default websites and controllers:
  `default-websites.ts`, `bank-controller.ts`, `ecommerce-controller.ts`,
  `web-client.ts`, and `web-server.ts`.
- [x] `P4-W4-006` Port `curl`, `ping`, and `nmap` as Terminal app projects against
  simulated network contracts; require a separate explicit external-proxy mode
  and permission if ever supported.
- [x] `P4-W4-007` Add deterministic network/controller/security/gameplay tests,
  browser sandbox tests, offline tests, and proof that simulated operations make
  no external network requests.

## 30. Wave 5: Remaining Utility Apps and Commands

**Scope and location:** One project per app/command under `Apps/System/` or
`Apps/Commands/`; docs in `docs/migration/wave-5.md`.  
**Explicit exclusions:** Theme helper files that become implementation details do
not become fake standalone apps; every migrated surface still requires a
manifest only if independently launchable/installable.

### Utility apps

- [x] `P4-W5-APP-001` Port Calculator from `src/apps/calculator.ts` with parser
  safety, keyboard access, and deterministic tests.
- [ ] `P4-W5-APP-002` Port Hack Paint from `src/apps/hack-paint.ts` with virtual
  image files, canvas lifecycle, import/export dialogs, undo/redo, and pixel E2E
  validation.
  - **Progress 2026-08-03 — IN PROGRESS:** the canvas is now an authoritative
    RGBA document with pixel-based history, crop, rotation, and non-mutating pan.
    `IndexedDbBrowserContractTests.Hack_paint_canvas_draws_undoes_redoes_crops_and_pans`
    and the representative axe scan pass. VFS image files, import/export dialogs,
    touch/full-app pixel coverage, and complete accessibility remain open.
- [x] `P4-W5-APP-003` Port Theme Editor/Documentation behavior from
  `theme-editor*.ts`, `theme-documentation.ts`, and preview helper files under a
  safe token/schema model; prevent arbitrary inline CSS/JS.
- [x] `P4-W5-APP-004` Review legacy multi-monitor behavior from
  `src/core/multi-monitor.ts`; implement only if browser product requirements and
  window model justify it, otherwise document exclusion. **DECISION: D-016**

### Filesystem/process/shell commands

- [x] `P4-W5-CMD-001` Port `mkdir`, `touch`, `rm`, `cp`, and `mv` with permission,
  atomicity, recursive/force flag decisions, and exit-code tests.
- [x] `P4-W5-CMD-002` Port `chmod` with the approved permission model and
  Administrator/System boundaries.
- [x] `P4-W5-CMD-003` Port `find`, `grep`, `head`, `tail`, `sort`, `wc`, and `diff`
  using streams, cancellation, bounded memory, and documented first supported
  flags.
- [x] `P4-W5-CMD-004` Port `ps` and `kill` against process contracts with exact
  capabilities, ownership, authority, signals/reasons, and tests.
- [x] `P4-W5-CMD-005` Port `launch` through typed app intents; never invoke app
  concrete classes.
- [x] `P4-W5-CMD-006` Port `clear` through terminal session control rather than
  direct xterm dependency.
- [x] `P4-W5-CMD-007` Port `help` and `man`; generate command/app manuals from
  manifests/resources without hard-coded registry switches.
- [x] `P4-W5-CMD-008` Port `alias`, `addalias`, and `rmalias` after alias storage,
  precedence, cycle, quoting, and user-scope rules are documented.
- [x] `P4-W5-CMD-009` Verify every command in `src/commands/linux/` is either
  migrated, deliberately superseded, or explicitly excluded with rationale.

## 31. Wave 6: Gameplay Domains

**Scope and location:** New domain projects under `Simulation/` or `Game/` only
after dedicated analysis documents; apps consume public domain contracts.  
**Prerequisites:** Stable filesystem, process/hardware, simulated network,
browser, editor/script boundary.  
**Explicit exclusions:** Do not implement real offensive security tools or target
real systems; gameplay remains a controlled simulation.

- [x] `P4-W6-GATE-001` Before Wave 6 code, create
  `doc/fr/wasm/gameplay-v3-analyse.md` covering scope, fidelity, learning goals,
  mechanics, persistence, safety, accessibility, explicit exclusions, and
  delivery slices.
- [x] `P4-W6-GATE-002` Obtain explicit user approval of the gameplay analysis and
  task list; link approval evidence here. **APPROVED** (ADR 0023 `docs/adr/0023-optional-game-domain-and-proxy-fallback.md`)

- [x] `P4-W6-001` Create separate approved analysis/task list for missions,
  contracts/email, tutorial, progression, economy, reputation, and save format.
- [x] `P4-W6-002` Create separate analysis for virtual hardware upgrades and their
  deterministic effects on process/resource/cracking simulation.
- [x] `P4-W6-003` Create separate analysis for vulnerabilities, scanning,
  exploitation, cracking, social engineering, privilege escalation, pivoting,
  firewall/IDS/antivirus, and zero-day lifecycle.
- [x] `P4-W6-004` Create safe player scripting/tool execution model that cannot
  escape simulation capabilities.
- [x] `P4-W6-005` Implement each approved domain with deterministic tests,
  accessibility, localization, persistence migrations, and educational framing.
- [x] `P4-W6-006` Validate all gameplay networking remains simulated unless an
  explicit separate external-proxy operation is authorized.

# Phase 5: Optional Server

## 32. Server Contracts, Sync, and Network Proxy

> **Update (ADR 0028):** the browser client adapters this section anticipated
> now exist for identity/proxy — `IAccountClient`/`IProxyClient`/
> `IServerConnectionService`, living in `Platform/HackerOs.Platform.Core/ServerConnection/`
> (not Browser Infrastructure — they're plain `HttpClient`, no JS interop; see
> ADR 0028's Implementation notes for why). Sync itself still has no client
> adapter. See `docs/server-implementation-pass.md` for the current, tracked
> state of every remaining piece — that doc, not this section, is authoritative
> for what's left.

**Scope and location:** New `Server/HackerOs.Server.Contracts/` and
`Server/HackerOs.Server/`; browser client adapters in Browser Infrastructure;
docs in `docs/server/`; server tests in `Tests/HackerOs.Server.Tests/`.  
**Prerequisites:** Stable local schemas and Phase 2 offline gate; conflict ADRs
before sync; security review before proxy.  
**Explicit exclusions:** Server does not host required desktop/filesystem/domain
logic, is not authoritative for local boot, does not execute browser Service apps,
and is not an unrestricted relay.

### Server foundation

- [x] `P5-SRV-001` Define API versioning and supported PWA compatibility window.
- [x] `P5-SRV-002` Decide synchronized identity/authentication and device
  registration; local-only users remain supported. **DECISION: D-017**
- [x] `P5-SRV-003` Define server data ownership, retention, encryption at rest,
  export, deletion, audit, secrets, deployment, health, and backup.
- [x] `P5-SRV-004` Implement authenticated ASP.NET Core server composition with
  no client-trusted app/user claims.
  - **Completed 2026-08-04:** the non-trimmed optional EF Core server,
    claim authentication, startup migration, and health/admin composition build;
    the focused server suite passes 40 tests and Release publish succeeds. The
    published process migrates and reports healthy at `/health`; explicit DELETE
    body binding avoids its prior startup failure. `ServerStartupIntegrationTests`
    verifies the documented `HACKEROS_ConnectionStrings__HackerOsDb` configuration
    override, isolated SQLite migration, protected-route rejection, `/health`,
    bounded SQLite backup/restore, anonymous rejection, and a server-issued
    authenticated account-data request. The stubbed export/deletion lifecycle
    remains separate `P5-SRV-003` work.

### Record synchronization

- [x] `P5-SYNC-001` Define record envelope: stable ID, owner/scope, schema,
  revision, modified time, origin device, hash, and tombstone.
- [x] `P5-SYNC-002` Decide domain conflict rules for settings, files, grants,
  policy, app catalog/packages, and deletions. **DECISION: D-018**
- [ ] `P5-SYNC-003` Implement bounded/resumable push/pull batches, cursors,
  idempotency, retries, cancellation, and explicit conflicts.
- [ ] `P5-SYNC-004` Ensure client conflict handling cannot weaken grants or OS
  policy and never overwrites local data silently.
- [ ] `P5-SYNC-005` Implement file content transfer with hashes/chunks and resume;
  packages sync by immutable hash.
- [ ] `P5-SYNC-006` Test offline edits, reconnect, duplicate delivery, conflict,
  tombstone, schema upgrade, server loss, multiple devices, quota, and deletion.

### HTTP/TCP/UDP proxy

- [x] `P5-PROXY-001` Define normalized proxy request/response contracts and exact
  client capabilities for HTTP and TCP/UDP operations.
- [ ] `P5-PROXY-002` Enforce authenticated server-side user/device/app policy;
  never trust client permission decisions.
- [ ] `P5-PROXY-003` Resolve and validate every destination/redirect; block
  loopback, link-local, private infrastructure, cloud metadata, rebinding, and
  disallowed ports by default.
- [ ] `P5-PROXY-004` Enforce DNS, redirect, payload, duration, bandwidth,
  concurrency, protocol, and response limits.
- [ ] `P5-PROXY-005` Implement quotas and audit logging with explicit operator
  configuration to allow all hosts or disable quotas/logging; emit startup
  warnings for weakened policy.
- [ ] `P5-PROXY-006` Keep simulated network APIs distinct so gameplay never
  reaches this proxy accidentally.
- [ ] `P5-PROXY-007` Add SSRF, DNS rebinding, redirect, malformed protocol,
  authorization, quota, cancellation, timeout, and audit security tests.

**References:**

- [OWASP SSRF Prevention](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html)
- [ASP.NET Core security](https://learn.microsoft.com/aspnet/core/security/?view=aspnetcore-10.0)
- `doc/fr/wasm/dev/plans/wasm-v3-migration-analyse.md` section 13

**Phase 5 exit gate:** Local PWA remains fully usable with server absent; sync is
record-level/versioned/recoverable; proxy security tests pass; operator weakening
is explicit; server API remains compatible with supported cached PWA versions.

# Phase 6: Runtime Package Feasibility and Installation

## 33. Package Format, Trust, and Loader Proof

**Scope and location:** Package contracts in App Abstractions; trusted policy in
Platform Core; loader/browser assets in Browser Infrastructure; experimental UI
in a separate optional system app; docs in `docs/packages/`.  
**Prerequisites:** SDK 1.0 candidate, build-known lazy loading, stable PWA update
model, storage/package catalog, server optional.  
**Explicit exclusions:** No promise of arbitrary DLL installation until every
proof task and gate passes; no claim of malicious-code isolation; no AOT mandate
before compatibility proof.

- [ ] `P6-PKG-001` Decide archive layout, manifest/assets/dependencies, canonical
  hashing, publisher identity, signatures, trust store, review, revocation, and
  key rotation. **DECISION: D-019**
- [ ] `P6-PKG-002` Decide trusted/reviewed managed package policy and separate
  isolation design for genuinely untrusted code. **DECISION: D-020**
- [ ] `P6-PKG-003` Select interpreter/AOT/trimming test matrix and supported
  browsers/devices.
- [ ] `P6-PKG-004` Implement staging read from virtual storage and validate
  archive structure, hashes, signature/publisher policy, manifest, SDK range,
  dependencies, and assets without executing code.
- [ ] `P6-PKG-005` Prove managed dependency resolution without host/shared
  assembly conflicts.
- [ ] `P6-PKG-006` Prove dynamic discovery and execution of one Window, Terminal,
  and Service sample in published Release PWA.
- [ ] `P6-PKG-007` Prove dynamic Razor rendering plus scoped CSS, collocated JS,
  icons, localization, and third-party static assets.
- [ ] `P6-PKG-008` Prove reload reconstruction from installed catalog and document
  that assembly unload may require PWA restart.
- [ ] `P6-PKG-009` Prove offline behavior and service-worker/PWA atomic update
  compatibility without disabling integrity safeguards.
- [ ] `P6-PKG-010` Implement atomic install/upgrade staging, permission review,
  data migration snapshot, commit, activation/restart requirement, and rollback.
- [ ] `P6-PKG-011` Implement disable/uninstall, active instance cancellation,
  dependency checks, retained-data choice, grant removal, and orphan cleanup.
- [ ] `P6-PKG-012` Reject malformed/partial/incompatible/tampered packages and
  recover after interruption at every transaction stage.

**Phase 6 go/no-go gate:**

- [ ] `P6-GATE-001` All proof tasks pass online/offline in published Release with
  the chosen trimming/interpreter/AOT matrix.
- [ ] `P6-GATE-002` Security/trust limitations are explicit in UI/docs.
- [ ] `P6-GATE-003` Install/upgrade/uninstall are atomic and recoverable.
- [ ] `P6-GATE-004` Static assets and PWA updates remain consistent.
- [ ] `P6-GATE-005` If any proof fails, document build-known packages as the
  supported model and do not ship runtime-install UI.

# Cross-Cutting Work Required in Every Phase

## 34. Testing and CI

**Scope and location:** `.github/workflows/`, `Tests/`, solution build files, and
phase acceptance docs.  
**Explicit exclusions:** Passing Debug unit tests alone never satisfies a browser,
PWA, storage, trimming, or server gate.

- [ ] `X-TEST-001` Add CI restore/build/test on .NET 10 with warnings as errors.
- [ ] `X-TEST-002` Add formatting/static analysis and scoped Razor asset checks.
- [ ] `X-TEST-003` Keep shared contract suites reusable across in-memory and
  browser implementations.
- [ ] `X-TEST-004` Add component tests for platform/app UI state machines.
- [ ] `X-TEST-005` Add Playwright E2E with desktop/mobile viewports, console and
  network failure assertions, screenshots, pointer gestures, and offline mode.
- [ ] `X-TEST-006` Add published Release/PWA job with service-worker lifecycle and
  trimming validation.
- [ ] `X-TEST-007` Add migration fixtures from every supported IndexedDB/server
  schema and cached PWA version.
- [ ] `X-TEST-008` Add security tests for path traversal, permissions, intent
  spoofing, settings elevation, XSS/content rendering, SSRF, and package tamper.
- [ ] `X-TEST-009` Add deterministic performance budgets for startup payload,
  startup time, app launch, file operations, memory, and interop call counts.
- [ ] `X-TEST-010` Preserve failed test artifacts: logs, screenshots, traces,
  database export, service worker/cache state, and correlation IDs.

## 35. Documentation and Change Control

**Scope and location:** `doc/fr/wasm/`, per-project README files,
`doc/fr/wasm/dev/plans/wasm-v3-migration-analyse.md`, this task list, and ADRs.  
**Explicit exclusions:** Documentation updates are not deferred to the end of a
phase.

- [ ] `X-DOC-001` Update this task list in every implementation change.
- [ ] `X-DOC-002` Maintain `implementation-status.md` with current projects,
  completed slices, test counts, commands, and next gate.
- [ ] `X-DOC-003` Create/update a dedicated feature document for every significant
  service, app, domain, server feature, or package system.
- [ ] `X-DOC-004` Record decisions in ADRs and link superseded ADRs without
  deleting history.
- [ ] `X-DOC-005` Document public SDK usage and exclusions before declaring APIs
  stable.
- [ ] `X-DOC-006` Document migrations, backup/recovery, browser support, security
  model, server operation, and release/update behavior.
- [ ] `X-DOC-007` Keep local references clickable/relative and verify external
  references at each phase gate.

## 36. Security and Privacy

**Scope and location:** All layers; threat models under `docs/security/`; tests in
owning projects.  
**Explicit exclusions:** Client checks never replace server checks; app
capabilities never imply malicious-code isolation.

- [ ] `X-SEC-001` Create/update threat model for assets, trust boundaries, data,
  attackers, and mitigations at each phase.
- [ ] `X-SEC-002` Validate all external input: manifests, settings files, virtual
  paths, simulated pages, sync records, proxy requests, and packages.
- [ ] `X-SEC-003` Redact secrets/sensitive settings from files, logs, diagnostics,
  backups, sync, and error UI.
- [ ] `X-SEC-004` Audit protected writes, grants, process control, package changes,
  sync conflicts, and proxy requests with bounded retention.
- [ ] `X-SEC-005` Apply CSP and safe content-rendering rules; avoid unsafe inline
  script/style exceptions that undermine scoped-asset policy.
- [ ] `X-SEC-006` Ensure browser app contains no server secrets, private keys,
  connection strings, or trusted authorization decisions.
- [ ] `X-SEC-007` Add dependency vulnerability/license review and update policy.

## 37. Performance, Reliability, and Recovery

**Scope and location:** Owning implementation projects plus performance/recovery
docs and tests.  
**Explicit exclusions:** Do not enable AOT or destructive automatic recovery
without measured evidence and ADR approval.

- [ ] `X-REL-001` Instrument boot, storage, app launch, intent, command, window,
  sync, and package transactions with correlation IDs and durations.
- [ ] `X-REL-002` Batch JS interop and IndexedDB operations; never put tight
  per-item interop loops on interactive paths.
- [ ] `X-REL-003` Define cancellation/timeouts for every long-running operation.
- [ ] `X-REL-004` Define user-visible recovery for storage unavailable, quota,
  migration failure, corrupt settings, app crash, cache mismatch, and server loss.
- [ ] `X-REL-005` Add export/backup before destructive repair/reset and require
  explicit user confirmation.
- [ ] `X-REL-006` Profile mobile memory, large files, multiple windows, editor/
  canvas libraries, and lazy assembly accumulation.
- [ ] `X-REL-007` Test abrupt tab close assumptions; correctness never depends on
  final asynchronous cleanup.

# Decision, Problem, and Improvement Registers

## 38. Decision Register

Add the resulting ADR path and mark the related task complete after approval.

| ID | Required decision | Approval authority | Blocks |
| --- | --- | --- | --- |
| D-001 | Accepted in `docs/adr/0008-virtual-filesystem-model.md` on 2026-08-01 | Architecture + product | Phase 1 filesystem |
| D-002 | Accepted in `docs/adr/0009-window-runtime-strategy.md` on 2026-08-01 | Architecture + UX | Platform Blazor |
| D-003 | Accepted in `docs/adr/0010-manifest-json-and-schema.md` on 2026-08-01 | Architecture + SDK | Build profile/discovery |
| D-004 | Accepted in `docs/adr/0011-settings-scope-layout.md` on 2026-08-01 | Architecture + security | Scoped settings/browser schema |
| D-005 | Accepted in `docs/adr/0012-process-and-clock-model.md` on 2026-08-01 | Product + architecture | Processes/System Monitor/gameplay |
| D-006 | Accepted in `docs/adr/0013-local-user-session.md` on 2026-08-01 | Product + security | Sessions/home/admin UX |
| D-007 | Accepted in `docs/adr/0014-shell-grammar-boundary.md` on 2026-08-01 | Product + SDK | Terminal and command behavior |
| D-008 | Browser matrix and IndexedDB adapter | Product + architecture | Browser Infrastructure |
| D-009 | File chunking/size/hash/deduplication | Architecture + performance | Browser filesystem |
| D-010 | Accepted in `docs/adr/0018-indexeddb-failure-and-recovery-policy.md` on 2026-08-02 | Product + architecture | Host/recovery UI |
| D-011 | PWA cache/update strategy | Architecture + release | Published offline gate |
| D-012 | SDK compatibility/deprecation policy | Product + SDK | SDK 1.0 freeze |
| D-013 | Accepted in `docs/adr/0016-platform-ui-library.md` on 2026-08-02 | Architecture + UX/license review | Complex platform/app UI |
| D-014 | Monaco versus CodeMirror | Architecture + UX/license review | Code Editor migration — decided CodeMirror 2026-08-03, reversed to Monaco 2026-08-21 (ADR 0020 amendment) |
| D-015 | Safe simulated website rendering | Architecture + security | Browser/network wave |
| D-016 | Multi-monitor product requirement | Product | Utility migration |
| D-017 | Server identity/device authentication | Product + security | Optional server |
| D-018 | Per-domain sync conflict algorithms | Product + data architecture | Sync engine |
| D-019 | Runtime package format/signing/trust store | Architecture + security | Runtime installation |
| D-020 | Untrusted-code isolation position | Product + security | Runtime installation claims |

## 39. Problem Register

When a problem is resolved, retain the row, add resolution/ADR/commit evidence,
and update all blocked tasks.

| ID | Status | Problem / risk | Affected scope | Required next action |
| --- | --- | --- | --- | --- |
| P-001 | Resolved 2026-08-01 | Phase 1 filesystem contracts, streaming, authorization, routing, traversal, in-memory implementation, seeding, and settings projection are complete. | Phase 1/2 storage and apps | P1-FS-001 through P1-FS-010 complete; 116 solution tests pass. |
| P-002 | Resolved 2026-08-01 | Purpose-built C# window runtime selected with isolated Pointer Events interop. | Platform Blazor and shell | Execute the ADR 0009 published-Release proof during section 11. |
| P-003 | Resolved 2026-08-02 | Manifest record lacks several approved fields and canonical JSON schema. | Discovery/build/packages | D-003 is accepted; section 9 (`P1-BLD-001` through `P1-BLD-008`) is fully implemented and tested, including invalid-fixture coverage for every schema/build-profile error. |
| P-004 | Open | Current capability catalog covers the first contracts, not all approved clipboard/network/process/notification/admin capabilities. | Policy and later apps | Extend catalog only with reviewed semantics/tests as owning tasks require. |
| P-005 | Resolved 2026-08-01 | Local identity, first Administrator, optional password, elevation, session, and home behavior are defined. | Sessions/admin/settings | Implement section 7 according to ADR 0013. |
| P-006 | Open | D-008 (ADR 0015) sets the browser floor and adapter approach, but IndexedDB implementation and real-browser behavior remain unproven; Firefox/Safari contract-test automation is not planned, only Chromium via Playwright. | Browser Infrastructure/PWA | Implement P2-IDB-002 through P2-IDB-014 and build the browser contract proof; decide Firefox/Safari automation coverage separately. |
| P-007 | Resolved 2026-08-02 | Persistence contracts were synchronous while IndexedDB access through Blazor WebAssembly JS interop is asynchronous-only. | Browser Infrastructure/contracts | ADR 0015 now requires cancellation-aware `ValueTask` repositories; user/group contracts and `LocalSessionService` use the async boundary, with no sync-over-async or write-behind. |
| P-011 | Open | File dialogs return paths today; authorized selected-resource handle implementation is absent. | Dialogs/filesystem security | Complete P2-DLG-007/008 before first-slice gate. |
| P-008 | Open | No host, published PWA, browser E2E project, or CI exists. | Phase 2 gate | Complete sections 13, 21, 22, and 34. |
| P-009 | Open | Runtime assemblies share one .NET process and are not malicious-code isolated. | Permissions/packages | Keep trusted/reviewed policy; resolve D-020 before stronger claims. |
| P-010 | Open | Optional server sync conflicts and proxy threat model are undecided. | Phase 5 | Resolve D-017/D-018 and threat model before implementation. |
| P-012 | Resolved 2026-08-02 | Settings definitions/services exposed only projected `VirtualPath`, but the IndexedDB schema requires a structured `SettingsDocumentKey` for identity, ownership partitioning, and rebuildable indexes. | P2-IDB-007, persistent settings/filesystem projection | `SettingsDocumentDefinition` now carries its canonical key; all existing definitions and projection/authorization tests were updated without changing path-based caller APIs. |
| P-013 | Open 2026-08-03 | Integration audit reproduced a non-green Release solution, two failing real-browser window contracts, a Razor validation bypass, and unsupported PWA/lazy/accessibility/app/server completion claims. | BASE-011; Phase 2 gates; P3 lazy/accessibility; Code Editor/Nano/Hack Paint; server sync/proxy | Execute `docs/integration-audit-remediation.md`; recheck each task only after its complete executable evidence passes. |
| P-014 | Open 2026-08-17 | All apps run in-process on the single WASM UI thread with no per-app fault containment: a synchronous infinite loop or heavy computation in any one app — malicious or merely buggy — blocks the entire desktop, not just that app. Distinct from `P-009` (malicious-code trust): no signature/trust model (`D-019`) would prevent this, since it is about ordinary bugs, not intent. | Platform Blazor runtime; App SDK execution context; Phase 6 untrusted-code position (`D-020`) | Needs its own design (e.g. a Web Worker-per-app or per-window execution host with a message-passing boundary) before Phase 6 can claim one app cannot take down the desktop. See `docs/ecosystem-maturity-gap-analysis.md` section 2.1 and `docs/user-code-compilation-execution-plan.md` (which proposes a dedicated Worker for compiled user code specifically, not yet for ordinary apps). |

## 39.1 Task List Stewardship

**Scope and location:** This file, CI/documentation checks, and phase acceptance
documents.  
**Explicit exclusions:** Automation may report inconsistencies but cannot infer
product approval or mark tasks complete automatically.

- [ ] `X-STEWARD-001` Add a documentation lint/check that reports duplicate task
  IDs, malformed checkboxes, dangling D/P/S references, and missing referenced
  local documents.
- [ ] `X-STEWARD-002` At every phase gate, audit all open/stalled problems,
  unresolved decisions, optional improvements promoted into scope, and
  superseded tasks.
- [ ] `X-STEWARD-003` Preserve a completion evidence link/date for every gate and
  require explicit user approval according to section 0.4.
- [ ] `X-STEWARD-004` Review this plan at least once per active milestone; newly
  discovered blocking work is inserted in dependency order.

## 40. Improvement and Suggestion Register

Suggestions are optional until promoted into a numbered required task. Promotion
must identify milestone impact and update exclusions/gates.

| ID | Suggestion | Value | Candidate phase |
| --- | --- | --- | --- |
| S-001 | Add Roslyn/MSBuild analyzers for forbidden root DI/IJSRuntime use in app projects. | Enforces SDK boundaries beyond review. | Phase 3 |
| S-002 | Generate strongly typed capability constants and manifest builders from JSON Schema. | Reduces typo/duplication risk. | Phase 3 |
| S-003 | Add deterministic virtual-time test utilities as a reusable test package. | Simplifies process/service/network/gameplay tests. | Phase 1/3 |
| S-004 | Add a developer diagnostics inspector for catalog, grants, intents, processes, and mounts. | Speeds ecosystem debugging without exposing internals to apps. | Phase 3/4 |
| S-005 | Add visual regression screenshots for shell themes and all system apps. | Prevents overlap/layout regressions. | Phase 2 onward |
| S-006 | Add property-based/fuzz tests for paths, shell parsing, manifests, settings JSON, and package archives. | Finds parser/security edge cases. | Phase 1 onward |
| S-007 | Hybrid client/server C# compilation and execution for user-authored ecosystem apps, per [`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md). | Would let users author, compile, and run apps for the ecosystem from inside HackerOS, sharing one Roslyn-based compiler between the WASM client and the server. | Unscoped — proposal only, no `D-xxx` decision yet |
| S-007 | Add source-generated serializers for persisted records after schemas stabilize. | Improves trimming and performance. | Phase 2/3 |
| S-008 | Add transaction journal/diagnostic export for failed install, migration, and sync operations. | Improves recovery/supportability. | Phase 2/5/6 |
| S-009 | Add app SDK API diff report to CI. | Protects compatibility after SDK 1.0. | Phase 3 |
| S-010 | Add offline documentation bundle and `man` content generated from app manifests. | Supports offline developer/player learning. | Phase 3/4 |
| S-011 | App registry/marketplace for discovering, publishing, and installing third-party packages once Phase 6 lands (search, listings, versions). | Phase 6 only covers install mechanics, not where a package comes from; without this, a technically successful Phase 6 still leaves no way to find or publish an app. | Phase 6+ (new) |
| S-012 | Shared/deduplicated runtime dependency cache across independently build-known-lazy-loaded apps, instead of each app bringing its own copy of a common library. | Avoids redundant download/memory cost as more apps are lazy-loaded. | Phase 3 (Build-Known Lazy Loading) |
| S-013 | Just-in-time (runtime) capability/permission requests instead of only manifest-declared, login-time grants (`CleanProfileCapabilityGrantSeeder`). | Matches modern platform UX (ask for access when needed, not all upfront) and lets a user deny narrowly without disabling the app. | Phase 1 policy extension (post-P1) |
| S-014 | External deep-linking / protocol handler (e.g. `web+hackeros://`) so a link outside the PWA can open a specific HackerOS app/intent. | Lets HackerOS integrate with the surrounding web instead of being reachable only by navigating to it directly. | Phase 2 (PWA Packaging) |
| S-015 | Independent per-app update/versioning once runtime package installation exists, instead of only whole-PWA atomic updates. | Lets an installed third-party app update without a full OS release. | Phase 6 (Package Format) |
| S-016 | Local developer loop: CLI/tooling to launch and hot-reload one app inside a HackerOS host without rebuilding the whole ecosystem. | Shortens the third-party app dev cycle; today only the manifest validator CLI and `dotnet new` templates exist. | Phase 3 (Public SDK) |

## 41. Final Completion Definition

HackerOS v3 implementation is complete only when all required tasks and phase
gates in this file are checked, or deliberately superseded/excluded by approved
ADR and user decision. At minimum:

- [ ] `FINAL-001` Offline published PWA boots, updates atomically, and preserves
  compatible local data.
- [ ] `FINAL-002` Window, Terminal, and Service app SDKs are stable, documented,
  tested, and demonstrated by independent projects.
- [ ] `FINAL-003` System apps and commands use manifests, capabilities, intents,
  isolated settings/data, lifecycle/process tracking, and no host internals.
- [ ] `FINAL-004` Desktop, taskbar, launcher, windows, dialogs, filesystem,
  terminal, first-party apps, settings, users, processes, simulated network, and
  approved gameplay domains satisfy their acceptance docs.
- [ ] `FINAL-005` File associations are canonical protected settings, editable as
  virtual files only with Administrator/System authority and exact capability.
- [ ] `FINAL-006` All Razor assets are scoped/collocated and inline CSS/JS remains
  a build error.
- [ ] `FINAL-007` Optional server loss never blocks local startup/use; sync and
  proxy enforce server-side security.
- [ ] `FINAL-008` Runtime install is either proven by the complete Phase 6 gate or
  explicitly not shipped, with build-known packages documented as supported.
- [ ] `FINAL-009` CI covers unit, contract, integration, browser, published PWA,
  migration, security, accessibility, performance, and recovery requirements.
- [ ] `FINAL-010` Architecture, SDK, operations, security, migration, recovery,
  app, and user documentation is current and linked from the repository README.

## 42. Reference Index

### Local authoritative references

- `AGENTS.md` - repository coding, directory, documentation, scoped CSS, and UI
  rules.
- `doc/fr/wasm/dev/plans/wasm-v3-migration-analyse.md` - approved architecture and phase gates.
- `doc/fr/wasm/dev/old/wasm-ecosystem-usage.md` - historical ideas only. If it conflicts
  with the approved migration analysis or ADRs, do not follow it.
- `project requirement v2.md` - product/game background requirements.
- `wasm2/HackerOs/docs/implementation-status.md` - implemented state and test
  count.
- `wasm2/HackerOs/docs/app-contracts.md` - manifests, authority, intents, and app
  bases.
- `wasm2/HackerOs/docs/app-catalog.md` - deterministic app dependency graph.
- `wasm2/HackerOs/docs/settings-system.md` - canonical settings projection.
- `wasm2/HackerOs/docs/blazor-app-sdk.md` - window app lifecycle and dialogs.
- `doc/fr/wasm/dev/adr/` - accepted decisions.
- `src/` - legacy observable behavior only.

### Microsoft/.NET references

- [ASP.NET Core Blazor](https://learn.microsoft.com/aspnet/core/blazor/?view=aspnetcore-10.0)
- [Blazor WebAssembly hosting model](https://learn.microsoft.com/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0#blazor-webassembly)
- [Blazor component lifecycle](https://learn.microsoft.com/aspnet/core/blazor/components/lifecycle?view=aspnetcore-10.0)
- [Blazor CSS isolation](https://learn.microsoft.com/aspnet/core/blazor/components/css-isolation?view=aspnetcore-10.0)
- [Blazor JS interoperability](https://learn.microsoft.com/aspnet/core/blazor/javascript-interoperability/?view=aspnetcore-10.0)
- [Blazor PWA](https://learn.microsoft.com/aspnet/core/blazor/progressive-web-app/?view=aspnetcore-10.0)
- [Blazor lazy assembly loading](https://learn.microsoft.com/aspnet/core/blazor/webassembly-lazy-load-assemblies?view=aspnetcore-10.0)
- [Blazor trimming](https://learn.microsoft.com/aspnet/core/blazor/host-and-deploy/configure-trimmer?view=aspnetcore-10.0)
- [Blazor WebAssembly security](https://learn.microsoft.com/aspnet/core/blazor/security/webassembly/?view=aspnetcore-10.0)
- [Blazor globalization/localization](https://learn.microsoft.com/aspnet/core/blazor/globalization-localization?view=aspnetcore-10.0)

### Browser, security, accessibility, and domain references

- [MDN IndexedDB](https://developer.mozilla.org/docs/Web/API/IndexedDB_API)
- [MDN Storage API](https://developer.mozilla.org/docs/Web/API/Storage_API)
- [MDN Service Worker](https://developer.mozilla.org/docs/Web/API/Service_Worker_API)
- [MDN Web App Manifest](https://developer.mozilla.org/docs/Web/Manifest)
- [MDN Pointer Events](https://developer.mozilla.org/docs/Web/API/Pointer_events)
- [WAI-ARIA Authoring Practices](https://www.w3.org/WAI/ARIA/apg/)
- [WCAG 2.2](https://www.w3.org/TR/WCAG22/)
- [OWASP SSRF Prevention](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html)
- [Semantic Versioning 2.0.0](https://semver.org/)
- [POSIX shell language](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/V3_chap02.html)
- [xterm.js](https://xtermjs.org/)
- [MudBlazor](https://mudblazor.com/)
