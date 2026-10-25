# HackerOS v3 Integration Audit — Remediation Handoff

**Status:** Remediation in progress  
**Audit date:** 2026-08-03  
**Last progress update:** 2026-08-04
**Target solution:** `wasm2/HackerOs/HackerOs.sln`  
**Source plan:** `wasm2/HackerOs/docs/integration-task-list.md`  
**Architecture reference:** `doc/wasm/dev/plans/wasm-v3-migration-analyse.md`

## Progress summary

The remediation passes restored a trustworthy Release baseline, completed the
window/scoped-asset wave, and completed the Nano/full-screen Terminal slice. They
also delivered partial accessibility, CI, and server-proxy remediation without
closing their remaining evidence gates.

### Verified complete in this pass

- [x] Standalone .NET 10 Release build: 0 warnings and 0 errors.
- [x] Standalone Release solution test: 622 passed, 0 failed, 0 skipped.
- [x] Trimmed `HackerOs.Ecosystem` Release publish succeeds.
- [x] Transitive vulnerability scan reports no vulnerable packages.
- [x] All window resize/focus scenarios pass, including three repeated runs of
  the three affected browser scenarios.
- [x] `WindowHost.razor` has no inline geometry style or validator exemption; an
  isolated invalid fixture proves the MSBuild validator rejects forbidden Razor
  assets.
- [x] Terminal and Nano manifests pass the trim-safe manifest validator.
- [x] Nano is functional through the renderer-independent full-screen contract,
  platform dispatcher, Blazor Terminal adapter, and browser key/render/cleanup
  path. VFS edit/save/Save As, denial, dirty exit, cancellation, resize, and
  alternate-screen restoration have executable coverage.
- [x] Production Razor source scan reports no forbidden constructs; the
  deliberately invalid fixture is excluded only from that supplemental scan and
  remains covered by its negative build test.

### Implemented but still gated

- CI now targets .NET 10 and `wasm2/HackerOs/HackerOs.sln`, but a successful
  GitHub-hosted run has not yet been presented.
- Code Editor now has a locally bundled Monaco Editor surface (replacing
  CodeMirror on 2026-08-21, plus an Open Folder tree view and an embedded
  terminal — see `docs/code-editor.md`), C#-owned tabs and recovery snapshots,
  bounded VFS load/Save/Save As, typed denial/conflict outcomes, deterministic
  disposal, a platform whole-window close guard, and VFS-backed recovery
  persistence. Twenty focused editor tests, 28 platform Blazor tests, Chromium
  interaction, and axe evidence pass. Real component reload proof plus
  published/offline full-app integration remain open.
- Axe 4.12.0 scans representative desktop/window/dialog, full-screen Terminal,
  local Monaco Code Editor, and Hack Paint canvas surfaces for serious and critical
  findings. Full app
  coverage and human assistive-technology evidence remain outstanding.
- Proxy requests now verify durable device ownership/revocation, validate every
  DNS answer, block special-use and IPv4-mapped addresses, and pin the selected
  address through `SocketsHttpHandler.ConnectCallback`. Server-side app grants,
  durable quotas, bandwidth policy, and the complete socket integration matrix
  remain outstanding.

### Still open

- Published-output PWA install/offline/update/corrupt-cache evidence.
- Build-known lazy loading and its published/offline matrix.
- Finish the Code Editor host/reload integration and complete RGBA-based Hack
  Paint.
- Complete accessibility automation and human keyboard/screen-reader evidence.
- Durable sync idempotency, restart recovery, ownership, cursor, tombstone, and
  chunk-resume matrices.
- `P2-GATE-005` explicit user approval after all repaired evidence is available.

## 1. Purpose

This document is an implementation handoff for correcting completion claims found
during an audit of the HackerOS v3 integration task list. The audit found that a
substantial part of the architecture and Phase 1/2 foundations exists, but several
checked tasks are contradicted by the current code, build, tests, or CI.

The remediation agent must treat the implementation and executable evidence as
authoritative. A design document, ADR, source file, or checked box is not by itself
proof that runtime behavior has been implemented or tested.

## 2. Mandatory working rules

- Make all source changes under `wasm2/HackerOs/`.
- Do not modify legacy code under `src/`.
- Prefer C# and Blazor; JavaScript is limited to browser APIs and isolated browser
  integrations.
- Keep component styles in collocated `.razor.css` files. Do not add Razor `style`
  attributes, `<style>` blocks, `<script>` blocks, or inline JavaScript handlers.
- Preserve the `System > Administrator > User` authority model and deny-by-default
  capability evaluation.
- Do not mark a task complete until its full acceptance condition is backed by an
  executable test or other reproducible evidence appropriate to that task.
- Run the complete solution verification after each remediation wave. Do not hide
  analyzer, trimming, browser-console, network, or package-security failures.
- Update `integration-task-list.md`, `implementation-status.md`, and the relevant
  feature/acceptance document whenever a task is reopened or completed again.

## 3. Remediation task list

### Wave A — Restore a trustworthy build and test baseline

- [x] **A-001 — Make the complete Release solution build and test successfully.**
  - Reopen at least `P2-GATE-001` while this work is incomplete.
  - Fix the unused exception variable in
    `Server/HackerOs.Server/Endpoints/IdentityEndpoints.cs`.
  - Resolve the server trimming diagnostics instead of suppressing them globally.
    In particular, review EF Core construction, minimal API endpoint mapping,
    configuration binding, and reflection-based JSON serialization.
  - Fix `Tests/HackerOs.Commands.Nano.Tests/NanoCommandTests.cs` to construct
    `NanoCommand` with its manifest and pass a cancellation token to
    `ExecuteAsync`.
  - Resolve `Scalar.AspNetCore` version drift rather than allowing NuGet to choose
    a different version implicitly.
  - Update or remove vulnerable dependencies reported for `Microsoft.OpenApi`
    2.0.0 and `SQLitePCLRaw.lib.e_sqlite3` 2.1.11. Document any unavoidable
    temporary exception with owner and expiry.
  - **Acceptance:** `dotnet test HackerOs.sln --configuration Release` exits zero
    with warnings treated as errors and with no failed test project.
  - **Evidence 2026-08-03:** standalone Release build completed with 0 warnings
    and 0 errors; the subsequent `--no-build` run passed 622 tests with 0 failed
    and 0 skipped. Server trimming is explicitly disabled only for the EF Core
    executable; WASM/shared shipping projects retain trim analysis. Package scan
    reports no vulnerable dependencies.

- [ ] **A-002 — Make test evidence stable and diagnosable.**
  - Ensure E2E failures preserve useful logs, Playwright traces, screenshots, and
    browser console/network output.
  - Avoid a test configuration in which one project build failure obscures the
    results of the remaining projects.
  - Record the current total passed/failed/skipped count only after a fully green
    run.
  - **Acceptance:** a clean checkout can reproduce the same Release result using
    the documented command.
  - **Progress 2026-08-03 — PARTIAL:** test projects can be run independently;
    startup retry failures are no longer misclassified as post-load network
    failures; browser console/page/network diagnostics are collected. Complete
    trace/screenshot retention on every failure and clean-runner reproduction
    remain open.

### Wave B — Correct the window runtime and scoped-asset enforcement

- [x] **B-001 — Fix real-browser resize behavior.**
  - Reopen `P2-WIN-013`, `P2-ACC-004`, and dependent Phase 2 gates.
  - Diagnose the failing all-edge resize scenario in
    `Tests/HackerOs.E2E.Tests/IndexedDbBrowserContractTests.cs` where the expected
    Y coordinate is 85 but the rendered value remains 70.
  - Verify all eight edges/corners with mouse, touch, and pen pointer types.
  - Keep geometry authoritative in C# and make gesture deltas deterministic.
  - **Acceptance:** the all-edge real-browser test passes repeatedly without
    retries or timing-dependent assertions.
  - **Evidence 2026-08-03:** gestures use immutable start bounds and cumulative
    pointer deltas. All eight edges/corners pass for the exercised pointer
    scenarios, including three repeated Release runs.

- [x] **B-002 — Fix focus and z-order during pointer interaction.**
  - Diagnose the scenario where the manipulated primary window remains at z=5
    while the secondary window remains at z=6.
  - Ensure pointer-down on window chrome focuses and raises the correct window
    before or atomically with move/resize processing.
  - Preserve owner-modal blocking and deterministic focus restoration.
  - **Acceptance:** the mouse/touch pointer test and headless z-order invariants
    pass repeatedly.
  - **Evidence 2026-08-03:** window component identity is keyed by window ID and
    pointer-down captures the target ID before the awaited atomic focus/raise and
    gesture operation. Repeated browser and headless tests pass.

- [x] **B-003 — Remove the `WindowHost.razor` inline-style exception.**
  - Reopen `BASE-011` and `P2-WIN-004` until the violation is removed.
  - `Platform/HackerOs.Platform.Blazor/Windows/WindowHost.razor` currently renders
    `style="@GeometryStyle"`.
  - `Directory.Build.targets` explicitly excludes `WindowHost.razor` from Razor
    asset validation. Remove this filename exception.
  - Project dynamic geometry without a Razor `style` attribute. A narrowly owned
    collocated JS module may update CSS custom properties on the isolated window
    host if required, while C# remains the authoritative state.
  - Extend validation tests so no named component can bypass the rule.
  - **Acceptance:** no `.razor` file contains a `style` attribute or embedded
    style/script element, and the build fails on a deliberately invalid fixture.
  - **Evidence 2026-08-03:** `GeometryStyle` and the filename exemption were
    removed. `WindowHost.razor.js` projects authoritative C# geometry into CSS
    custom properties consumed by scoped CSS. The negative fixture test passes.

### Wave C — Replace declarative PWA evidence with published-browser evidence

- [ ] **C-001 — Implement the complete published PWA test matrix.**
  - Reopen `P2-PWA-007`, `P2-ACC-015`, `P2-ACC-016`, `P2-ACC-017`, and
    `P2-GATE-004` until executable evidence exists.
  - Test the published Release output, not the development server or a source
    service-worker file.
  - Cover first online visit, installability, service-worker activation, server
    unavailable, browser offline reload, app launch, file/settings persistence,
    update waiting, safe activation, compatible data preservation, old-cache
    removal, release-asset consistency, and corrupt-cache recovery.
  - Ensure the tests prove that old and new release assets are never mixed.
  - Preserve browser console, network, cache, service-worker, and IndexedDB
    diagnostics on failure.
  - **Acceptance:** all PWA cases pass against a freshly published Release build
    in automated Chromium; the browser-support policy explicitly records any
    Firefox/Safari manual or automated coverage.

- [ ] **C-002 — Replace stale CI with .NET 10 solution CI.**
  - The repository workflow `.github/workflows/deploy-wasm.yml` currently installs
    .NET 9 and publishes `wasm/HackerSimulator.Wasm.sln`.
  - Add or update CI to restore, build, test, publish, and exercise
    `wasm2/HackerOs/HackerOs.sln` using .NET 10.
  - Include warnings-as-errors, Razor asset validation, Playwright browser setup,
    E2E execution, published PWA tests, and artifact retention.
  - Do not mark `P2-ACC-017` complete merely because unit tests run locally.
  - **Acceptance:** the repository's active CI workflow runs the intended solution
    and all required jobs pass on a clean runner.
  - **Progress 2026-08-03 — PARTIAL:** `.github/workflows/deploy-wasm.yml` now
    restores, builds, tests, scans, publishes, installs Playwright, and retains
    diagnostics for the .NET 10 `wasm2/HackerOs/HackerOs.sln`. It also performs
    the production Razor source scan before browser execution. Awaiting an actual
    successful GitHub runner execution; published-PWA coverage is still absent.

- [ ] **C-003 — Repair the Phase 2 evidence matrix.**
  - Replace implementation files and ADRs used as test evidence in
    `docs/phase-2-acceptance.md` with exact test names and reproducible commands.
  - Remove or correct references to test classes that do not exist.
  - Record failures honestly; do not label a criterion `PASSED` until its linked
    evidence is green.
  - Treat user-approval gates separately from technical tests. Link genuine
    approval evidence or leave those gates awaiting approval.
  - **Acceptance:** every one of the 17 criteria links to an existing, relevant,
    passing test or to clearly identified manual approval evidence.
  - **Progress 2026-08-03 — PARTIAL:** unsupported claims were reopened and the
    repaired window/build entries now name exact tests and commands. PWA,
    accessibility, published-browser, and approval entries remain unchecked.

### Wave D — Implement or reopen Phase 3 claims

- [ ] **D-001 — Implement real build-known lazy assembly loading.**
  - Reopen `P3-LAZY-001` through `P3-LAZY-007`.
  - The first optional assembly is now declared and loadable; the remaining
    catalog-to-lifecycle registration and published-browser proof are still open.
  - Classify eager and lazy assemblies in the actual host build.
  - Load optional app assemblies on first launch, then perform discovery safely.
  - Add recoverable UI for unavailable assets and preserve deterministic
    lifecycle behavior under concurrent launch requests.
  - Prove trimming, reload, offline cached loading, missing asset recovery, and
    exactly-once load behavior for every lazy sample.
  - **Acceptance:** published output demonstrates that lazy assemblies are absent
    from initial eager loading and are fetched/loaded once on demand, including
    offline-from-cache behavior.
  - **Progress 2026-08-04 — PARTIAL:** `BuildKnownAssemblyLoaderRegistry`
    permits only declared assembly names, coalesces concurrent requests, and
    returns typed missing/cancelled/failed outcomes. The WASM host declares
    Hack Paint with `BlazorWebAssemblyLazyLoad`, registers a transport backed by
    `LazyAssemblyLoader`, and its trimmed Release publish places Hack Paint in
    `lazyAssembly` rather than the eager resource list. Thirty-three focused platform
    tests pass, including caller cancellation that does not cancel the shared
    load. The host now supplies an immutable Hack Paint catalog and a descriptor
    registry to the lifecycle; first launch loads, validates, and registers the
    descriptor once. Published-browser fetch/reload, offline cache, and
    corrupt/missing-asset recovery remain open.

- [ ] **D-002 — Add real automated accessibility verification.**
  - Reopen at least `P3-UX-005`; reevaluate `P3-UX-004` and `P3-UX-006`.
  - `docs/accessibility.md` claims an axe Playwright integration that is absent.
  - Add axe-core or an equivalent automated engine to the browser test suite.
  - Test the desktop shell, launcher, taskbar, window chrome, dialogs, Terminal,
    File Explorer, Text Editor, Settings, and other shipped window apps.
  - Add keyboard-only scenarios, focus order/trapping, Escape behavior, zoom,
    mobile viewport, long text, reduced motion, and the documented RTL decision.
  - Fix violations rather than blanket-excluding rules.
  - **Acceptance:** automated accessibility reports contain no unexplained serious
    or critical violations, and the manual keyboard checklist records reproducible
    evidence.
  - **Progress 2026-08-03 — PARTIAL:** `Deque.AxeCore.Playwright` 4.12.0 is
    installed and representative idle/window/dialog scans pass with no serious or
    critical findings. Full shipped-app coverage and human evidence remain open.

### Wave E — Finish applications currently represented by prototypes

- [ ] **E-001 — Complete the Code Editor port.**
  - Reopen `P4-W3-002` and dependent validation task `P4-W3-007`.
  - **Original audit finding:** the component had one textarea, no tabs, no real
    syntax editor, and no VFS read/write despite its manifest description.
  - Implement file content loading through the app-scoped filesystem gateway.
  - Implement atomic saving, Save As, dirty-state protection, multiple tabs,
    syntax-mode behavior, large-file limits/recovery, and deterministic disposal
    of any editor/worker resources.
  - Keep code editing separate from script execution authority.
  - Add app-local component/integration tests for actual content round trips,
    tab state, denied capabilities, worker cleanup, reload recovery, and
    accessibility.
  - **Acceptance:** opening a virtual file displays its content, saving changes the
    VFS content, tabs behave independently, and tests verify the complete claimed
    behavior.
  - **Progress 2026-08-03 — SUBSTANTIAL PARTIAL:** the textarea prototype was
    replaced by exact-version, locally bundled CodeMirror 6. C# now owns bounded
    independent documents/tabs, syntax modes, dirty baselines, recovery
    snapshots, selected-resource-aware VFS load, optimistic atomic Save/Save As,
    typed denial/conflict/size failures, and deterministic editor disposal.
    Twenty focused tests, Chromium
    `Code_editor_local_bundle_edits_switches_mode_and_disposes_cleanly`, and the
    representative axe scan pass. The platform's dynamic renderer registers the
    editor close guard with `WindowCloseCoordinator`, so dirty windows now require
    an explicit discard decision. Recovery snapshots persist through the
    app-scoped VFS and malformed/denied recovery is typed. The item remains
    unchecked pending real component reload proof and published/offline full-app
    VFS coverage. See `docs/code-editor.md`.
  - **Progress 2026-08-21/22 — REWORKED:** CodeMirror 6 was replaced by Monaco
    Editor (own esbuild pipeline, per-worker bundles, no CDN dependency) to
    match the legacy JS app's editor. An Open Folder tree view (shared
    `FileView` control) and an embedded tabbed terminal (new shared
    `HackerOs.AppSdk.Terminal` library, also now backing the standalone
    Terminal app) were added. The Chromium contract test was updated for
    Monaco's DOM (`.monaco-editor`/`.native-edit-context`) and a recovery-path
    bug (using the opaque user ID instead of the home path, so saves silently
    failed) was fixed. The item is still unchecked for the same reasons as
    above. See `docs/code-editor.md`.

- [x] **E-002 — Complete or explicitly defer Nano.**
  - Reopen `P4-W3-006`.
  - The current command explicitly says interactive editing is deferred and only
    prints a simulated header.
  - Either implement the approved terminal full-screen interaction contract and a
    functional editor with read/save/cancel behavior, or formally mark Nano as
    deferred and leave the integration task unchecked.
  - Do not describe a header-printing placeholder as a port of the legacy editor.
  - **Acceptance:** functional editing tests pass, or the task list and migration
    documentation consistently identify Nano as deferred.
  - **Evidence 2026-08-03:** the public full-screen contract, functional
    VFS-backed editor, platform dispatch propagation, per-window Blazor Terminal
    adapter, exact browser key/frame renderer, resize reporting, cancellation,
    and guaranteed alternate-screen cleanup are implemented. Five Nano tests,
    six Terminal tests, two dispatcher/lifecycle tests, the Chromium
    `Terminal_full_screen_adapter_edits_and_restores_the_regular_screen` test,
    and the representative axe scan pass. Production optional-app delivery is
    still governed by the separate reopened lazy-loading gate.

- [ ] **E-003 — Complete Hack Paint.**
  - Reopen `P4-W5-APP-002`.
  - Implement virtual image-file open/save, import/export dialogs, content
    encoding/decoding, undo/redo of actual rendered content, crop/pan/rotate
    behavior, pointer/touch input, safe canvas lifecycle, and cleanup.
  - The current implementation appends SVG strokes while pushing a blank pixel
    buffer into history; replace this split state with one authoritative document
    model.
  - Add deterministic state tests and Playwright pixel/screenshot assertions.
  - **Acceptance:** imported or created images round-trip through the VFS, undo and
    redo restore visible pixels, and browser tests validate drawing and export.
  - **Progress 2026-08-03 — IN PROGRESS:** `PaintCanvasState` now owns defensive
    RGBA snapshots and applies brush pixels, crop, rotation, undo, and redo to
    that one document model. `HackPaintWindow` renders that model into a
    collocated canvas module and uses pointer capture, removing the old SVG/blank
    history split. Thirteen focused Wave 5 tests pass, including pixel history
    and crop/rotation regressions. Crop mode now applies its pointer selection to
    that document. Pan is renderer-only view state and has a regression proving
    it cannot mutate pixels or history. Image codecs, VFS/browser dialogs,
    `IndexedDbBrowserContractTests.Hack_paint_canvas_draws_undoes_redoes_crops_and_pans`
    passes in Chromium, proving rendered pixels through draw/history/crop/pan.
    Image codecs, VFS/browser dialogs, touch/full-app pixel evidence, and export
    remain open, so this
    task is deliberately unchecked.

### Wave F — Complete and harden the optional server

- [x] **F-001 — Restore server build and deployment correctness.**
  - `P5-SRV-004` is rechecked and complete.
  - Resolve trimming and serialization diagnostics with supported patterns or
    document an explicit server trimming decision distinct from the WASM client.
  - Verify authentication, authorization, configuration, health, persistence,
    migration, and backup behavior in integration tests.
  - **Acceptance:** server and server tests build with warnings as errors, start
    from documented configuration, and pass integration tests.
  - **Completed 2026-08-04:** the server builds cleanly, uses the
    approved server-only no-trim policy, and the focused server suite passes 40
    tests. A framework-dependent, non-trimmed Release publish now succeeds to
    `artifacts/server-publish`; it starts, migrates its SQLite schema, and returns
    a healthy `/health` response. Explicit `[FromBody]` binding repairs the
    previous startup crash on `DELETE /api/account`.
    `ServerStartupIntegrationTests.Startup_MigratesSqliteDatabase_AndReportsHealthy`
    now starts the composed host using the documented
    `HACKEROS_ConnectionStrings__HackerOsDb` override, migrates that isolated
    SQLite database, rejects anonymous account access, and checks `/health`.
    It also creates a bounded snapshot, verifies a subsequent write is removed
    by restore, and rejects traversal. It also proves both anonymous rejection
    and a server-issued authenticated account-data request. The stubbed
    export/deletion lifecycle remains separate `P5-SRV-003` work.

- [ ] **F-002 — Enforce authenticated app/user/device proxy policy.**
  - Reopen `P5-PROXY-002` and dependent proxy tasks.
  - Do not trust caller-supplied `accountId`, `deviceId`, or `AppId` values.
  - Derive authenticated identity from server authentication and validate device,
    app registration, operation capability, destination policy, and quotas on the
    server before sending a request.
  - Ensure audit records use trusted identities.
  - **Acceptance:** spoofed account/device/app claims are rejected by endpoint and
    service-level security tests.
  - **Progress 2026-08-03 — PARTIAL:** account and device IDs come from claims;
    the durable device row must match the account and must not be revoked before
    transport. A durable server app-registration/capability-grant model does not
    yet exist, so `AppId` is not yet trusted and this task remains open.

- [ ] **F-003 — Implement robust SSRF and DNS-rebinding protection.**
  - Reopen `P5-PROXY-003`, `P5-PROXY-004`, and `P5-PROXY-007`.
  - Current preflight DNS validation is followed by an independent `HttpClient`
    resolution and therefore does not pin the validated address.
  - Bind the outbound connection to validated DNS results or use another design
    that prevents time-of-check/time-of-use rebinding.
  - Revalidate every redirect and address family, including IPv4-mapped IPv6 and
    special-use ranges. Enforce protocol, port, request body, response body,
    duration, bandwidth, concurrency, and redirect limits.
  - Add tests for DNS rebinding, redirect chains, malformed protocols, auth,
    quotas, cancellation, timeout, audit, IPv6, and operator weakening.
  - **Acceptance:** the complete security matrix is covered by passing tests and
    no test performs uncontrolled real-network access.
  - **Progress 2026-08-03 — PARTIAL:** DNS is injectable in tests; all returned
    addresses are validated; special-use and IPv4-mapped IPv6 addresses are
    blocked; and the chosen address is pinned for the socket connection while the
    original host remains available for Host/TLS SNI. Redirect, bandwidth,
    timeout, cancellation, operator-weakening, and controlled socket integration
    matrices remain incomplete.

- [ ] **F-004 — Complete the sync acceptance matrix.**
  - Reopen `P5-SYNC-006` and reevaluate `P5-SYNC-003` through `P5-SYNC-005`.
  - Add tests for offline edits and reconnect, multiple devices, duplicate
    delivery across restart, bounded/resumable batches, tombstones, schema
    upgrades, server loss, quota, deletion, file chunks/hashes/resume, and
    security-sensitive grant/policy conflicts.
  - An in-memory five-minute idempotency dictionary is not restart-safe; implement
    durable idempotency if the task requires recovery across process restart.
  - Verify account ownership in record lookup and conflict resolution paths.
  - **Acceptance:** all scenarios named in `P5-SYNC-006` have exact executable
    evidence and preserve local-first behavior when the server is unavailable.

## 4. Task-list reconciliation

Before implementing later phases, audit and update the checked state of at least
the following tasks:

- `BASE-011`
- `P2-WIN-004`, `P2-WIN-013`
- `P2-PWA-007`
- `P2-ACC-004`, `P2-ACC-015`, `P2-ACC-016`, `P2-ACC-017`
- `P2-GATE-001`, `P2-GATE-002`, `P2-GATE-003`, `P2-GATE-004`
- `P3-UX-004`, `P3-UX-005`, `P3-UX-006`
- `P3-LAZY-001` through `P3-LAZY-007`
- `P4-W3-002`, `P4-W3-006`, `P4-W3-007`
- `P4-W5-APP-002`
- `P5-SYNC-003` through `P5-SYNC-006`
- `P5-PROXY-002` through `P5-PROXY-007`

Also review every checked task that uses words such as **test**, **validate**,
**offline**, **accessible**, **complete**, **atomic**, **secure**, or **approved**.
Confirm that the referenced evidence exists and proves the entire statement.

Approval-only gates such as `P2-GATE-005` cannot be inferred from source code or a
self-declared status line. Link genuine user approval evidence or leave the gate
unchecked.

## 5. Required verification commands

Run commands from `wasm2/HackerOs/` unless noted otherwise.

```powershell
dotnet restore HackerOs.sln
dotnet build HackerOs.sln --configuration Release --no-restore
dotnet test HackerOs.sln --configuration Release --no-build
dotnet publish OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj --configuration Release --no-restore
```

Run the repository's updated Playwright/PWA workflow against published output.
The verification must include:

- clean browser console and expected network behavior;
- real IndexedDB persistence and reload;
- all window pointer gestures;
- dialogs and selected-resource security;
- axe or equivalent accessibility checks;
- offline service-worker boot and update lifecycle;
- lazy assembly fetch, cache, reload, and failure recovery;
- optional-server absence; and
- server sync/proxy security suites.

Use a Razor source scan as an additional check, not as a replacement for the
MSBuild validation target:

```powershell
rg -n '<style|<script|\sstyle\s*=|(?<!@)\bon[a-z][a-z0-9_-]*\s*=' . -g '*.razor'
```

## 6. Completion definition for this remediation

This remediation is complete only when:

- [x] The complete .NET 10 Release solution builds and tests with warnings as
  errors.
- [x] All real-browser window tests pass reliably.
- [x] No Razor component bypasses scoped-asset validation.
- [ ] Published PWA offline and update behavior is proven in a real browser.
- [x] Active CI targets `wasm2/HackerOs/HackerOs.sln` on .NET 10. A green hosted
  run is still required by C-002.
- [x] Lazy loading is implemented as documented or its tasks are left unchecked.
- [ ] Accessibility claims are backed by automated and manual evidence.
- [x] Code Editor, Nano, and Hack Paint are either fully implemented to their task
  descriptions or explicitly reopened/deferred.
- [ ] Server sync and proxy behavior meets the complete security and recovery
  matrices.
- [ ] `integration-task-list.md`, `implementation-status.md`, acceptance reports,
  feature documents, and ADR status are mutually consistent.
- [ ] No task is rechecked solely because code or documentation exists; its full
  acceptance evidence must pass.

## 7. Audit evidence summary

The 2026-08-03 audit observed:

- 305 checked tasks in `integration-task-list.md`.
- `dotnet test HackerOs.sln --configuration Release --no-restore` exited nonzero.
- Server build/analyzer failures and stale Nano tests prevented a green solution.
- Two Playwright window tests failed: all-edge resize and pointer focus/z-order.
- `WindowHost.razor` used inline geometry styling and was explicitly exempted from
  the Razor validator.
- PWA acceptance used a service-worker source file and an ADR as proof of runtime
  tests.
- The active deployment workflow targeted .NET 9 and the older `wasm/` solution.
- Lazy-loading APIs/configuration described by the documentation were absent.
- The documented axe integration was absent.
- Code Editor, Nano, and Hack Paint did not implement their complete checked task
  descriptions.
- Proxy and sync test suites covered only subsets of their checked security and
  recovery matrices.

These observations are the starting point, not a substitute for rerunning the
verification after repository changes.

## 8. Latest executable evidence

Evidence collected on 2026-08-04 from `wasm2/HackerOs/`:

```text
dotnet restore HackerOs.sln
  PASS

dotnet build HackerOs.sln --configuration Release --no-restore
  PASS — 0 warnings, 0 errors

dotnet test HackerOs.sln --configuration Release --no-build
  PASS — 622 passed, 0 failed, 0 skipped

dotnet publish OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj --configuration Release --no-restore
  PASS — trimmed Release output produced

dotnet publish Server/HackerOs.Server/HackerOs.Server.csproj --configuration Release --no-restore --output artifacts/server-publish
  PASS — non-trimmed optional-server publish produced; local startup migration and GET /health returned 200 healthy

dotnet list HackerOs.sln package --vulnerable --include-transitive --no-restore
  PASS — no vulnerable packages reported

dotnet test Tests/HackerOs.Server.Tests/HackerOs.Server.Tests.csproj --configuration Release --no-restore
  PASS — 40 passed, 0 failed, 0 skipped (includes startup migration and health integration test)

dotnet test Tests/HackerOs.Commands.Nano.Tests/HackerOs.Commands.Nano.Tests.csproj --configuration Release --no-restore
  PASS — 5 passed, 0 failed, 0 skipped

dotnet test Tests/HackerOs.Apps.Terminal.Tests/HackerOs.Apps.Terminal.Tests.csproj --configuration Release --no-restore
  PASS — 6 passed, 0 failed, 0 skipped

dotnet test Tests/HackerOs.Apps.CodeEditor.Tests/HackerOs.Apps.CodeEditor.Tests.csproj --configuration Release --no-restore
  PASS — 20 passed, 0 failed, 0 skipped

dotnet test Tests/HackerOs.Platform.Blazor.Tests/HackerOs.Platform.Blazor.Tests.csproj --configuration Release --no-restore
  PASS — 28 passed, 0 failed, 0 skipped
```

The supplemental production Razor scan passes when the intentionally invalid
`Tests/RazorAssetValidation.Invalid/**` fixture is excluded. That fixture is not
an exemption from enforcement: `RazorAssetValidationTests` separately proves its
build fails with the expected validator diagnostic.

The browser evidence includes
`IndexedDbBrowserContractTests.Terminal_full_screen_adapter_edits_and_restores_the_regular_screen`.
The dispatcher evidence includes
`AppIntentDispatcherTests.Execute_command_passes_full_screen_session_through_dispatch_and_lifecycle`
and
`AppIntentDispatcherTests.Cancelling_full_screen_command_returns_shell_exit_130_and_restores_screen`.
Code Editor browser evidence (Monaco since 2026-08-21) is
`IndexedDbBrowserContractTests.Code_editor_local_bundle_edits_switches_mode_and_disposes_cleanly`.
