> [Version française](../../../../fr/wasm/dev/old/implementation-status.md)

# v3 Implementation Status

## Purpose

Track implementation against `doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md` without
allowing later phases to obscure unfinished platform work.

The exhaustive remaining work, maintenance rules, decisions, problems, and phase
gates are maintained in `docs/integration-task-list.md`. This status file remains
the concise implemented-state summary.

## 2026-10-17 wasm v3 migration complete (`v3/16-final`)

This file is archived; the current state lives in
[`../wasm-v3-final-status.md`](../plans/wasm-v3-final-status.md). In short:

- The sixteen chantiers of [`../wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md)
  are merged into `v3/base`: window model, intents, services, SDK versions and
  NuGet packages, backup, sensitive settings, sync, server relay, Hack Paint and
  theme editor, localization (EN/FR), accessibility, trimmed publish, SDK 1.0
  freeze. The game left this repository (ADR 0092).
- Every deferred decision of the migration analysis has a written status
  (ADR 0094); runtime app installation is deferred to its own plan.
- Open gaps and unanswered questions are listed in the final status document,
  not here.

## 2026-08-19 shared themes and Start menu expansion

The former accent-only, Settings-window-owned DOM mutation has been replaced by
a startup-owned shared theme boundary:

- `HackerOs.Theming.Abstractions` defines seven desktop themes (HackerOS,
  Windows 98/XP/7/10, macOS, Ubuntu), two mobile themes (Android, iOS), and the
  bounded HackerOS accents.
- `HackerOs.Theming.Blazor` publishes one static token asset plus reusable
  `ThemeScope`/`ThemePreview` components. Windowing, taskbar, desktop/launcher,
  and mobile-shell components consume the same semantic contract.
- Appearance schema v2 persists independent desktop/mobile selections and
  migrates v1 accent/motion documents without loss. The Ecosystem host restores
  it after boot, before a desktop can be entered.
- The launcher now follows a Windows 7 two-column layout, fixes presentation
  category filtering, adds File Explorer/Settings quick links, and stores
  ordered quick-launch pins per opaque local-user ID.
- Arbitrary legacy `customCss` was intentionally not ported; themes remain
  reviewed static data under design-system decision D-013 and ADR 0041.

Implementation checklist:

- [x] Audit the read-only legacy implementation and current project boundaries.
- [x] Add the shared catalog, static theme RCL, schema migration, and pin store.
- [x] Complete shell/Settings wiring and token migration.
- [x] Complete focused tests, solution builds, and browser/static-asset checks.
- [x] Publish architecture, usage, extension, persistence, and launcher docs.

## 2026-08-03 integration-audit remediation

The audit baseline was reproduced before remediation. Server trimming/package
diagnostics, a stale Nano test, and two real-browser window tests prevented a
green Release solution. The first remediation wave is now verified:

- The optional ASP.NET Core/EF Core server has a documented server-only no-trim
  policy; WASM and shared shipping libraries retain trim analysis.
- Server dependencies are pinned to non-vulnerable compatible versions, restore
  is warning-free, and `dotnet list HackerOs.sln package --vulnerable
  --include-transitive --no-restore` reports no vulnerable packages.
- `WindowHost` no longer contains a Razor inline-style attribute or validator
  exception. An invalid fixture proves the build rejects inline Razor assets.
- Keyed window identity and deterministic cumulative pointer deltas repair all
  edge/corner resizing and pointer focus/z-order behavior.
- `dotnet build HackerOs.sln --configuration Release --no-restore` passes with
  0 warnings and 0 errors. This includes trim-safe manifest validation and the
  corrected Browser app lifecycle hooks.
- `dotnet test HackerOs.sln --configuration Release --no-build` passes 622
  tests with 0 failed and 0 skipped. The three window browser scenarios also
  pass three consecutive Release repetitions without product-level retries.
- The trimmed Release ecosystem publish succeeds, the Terminal and Nano
  manifests validate through the trim-safe CLI, the production Razor scan is
  clean, and the transitive vulnerability scan reports no vulnerable packages.

Published-PWA, lazy-loading, full accessibility, prototype-app, and expanded
server security/recovery claims remain reopened until their audit matrices are
implemented and pass. CI configuration was replaced but remains an external
evidence gate until GitHub executes it successfully.

## Current milestone

Phase 1 has started with a headless contracts slice. No Blazor host, browser
storage, window manager, or application UI has been created yet. The complete
Phase 1 in-memory virtual filesystem slice now passes its assembled contract
suite; browser persistence remains a later phase. The manifest/build-profile
validator slice is now also implemented and verified end to end: JSON Schema
conformance, semantic manifest validation, source-generated serializer/
validator coverage, build-profile reference/asset scoping, deterministic
explicit discovery-list assembly, dependency-graph validation,
boot-recovery policy enforcement, and explicit regression coverage for every
build-profile validation error code all pass in the headless solution test suite.
The Phase 1 exit gate (`P1-GATE-001` through `P1-GATE-004`) is now satisfied: a
Release build with the trim analyzer enabled on every shipping project reports
zero warnings, and a new headless kernel integration test
(`Tests/HackerOs.Platform.Core.Tests/HeadlessKernelIntegrationTests.cs`) proves
boot, session login, real assembly-based discovery, app launch/command
execution, capability-checked filesystem and settings gateway use, service
shutdown, app disable, and session logout compose correctly with no Blazor or
browser dependency. Fresh verification: `dotnet test HackerOs.sln --no-restore`
completed with 378 tests passed and 0 failed.

Phase 2A has started: `P2-IDB-001` through `P2-IDB-004` are complete. ADR 0015
decides the browser support baseline (Chromium 89+, Firefox 90+, Safari 15+,
evergreen only) and the IndexedDB adapter approach (hand-written minimal
collocated JS module(s) under
`Infrastructure/HackerOs.Infrastructure.Browser/wwwroot/`, batched
transaction-oriented primitives, no third-party IndexedDB NuGet package,
C#-owned schema/migration ordering). The new
`Infrastructure/HackerOs.Infrastructure.Browser` project (with its
`Tests/HackerOs.Infrastructure.Browser.Tests` project) now declares the
concrete `hackeros` database schema version 2: 12 object stores (`users`,
`groups`, `sessions`, `settings`, `fsEntries`, `fsLinks`, `fsContent`, `catalog`,
`grants`, `audit`, `diagnostics`, `syncMetadata`) and 10 named cross-store
transaction boundaries plus the independent `GroupWrite` boundary. Its static web asset now provides declarative database
upgrades and batched transactional operations without embedding domain rules in
JavaScript. The partial `P2-IDB-005` foundation adds an internal, lazily imported
C# interop adapter that accepts only named schema transaction boundaries, sends
ordered batches, propagates cancellation, and never exposes `IJSRuntime` to app
or shared projects. `P-007` is resolved through the ADR 0015 amendment:
user/group persistence contracts use cancellation-aware `ValueTask`, nullable
lookup results represent absence, and mutations acknowledge only committed
durability. `LocalSessionService` consumes the async boundary; memory
repositories retain synchronous test conveniences without sync-over-async.
The first concrete browser repository now persists and retrieves local groups;
the migration plan is now a contiguous C#-owned chain that aborts missing paths
without implicit deletion. Settings definitions now carry their canonical
structured repository key while retaining path-based projection APIs. The
IndexedDB settings service performs idempotent atomic initialization and
revision-checked replacement through generic `addIfAbsent` and `compareAndPut`
browser primitives, while C# remains the owner of authorization, validation,
revision semantics, and rebuildable file-association indexes. Other repositories
still need persistence implementations, including the last-administrator
invariant for user/group mutations. The first `P2-IDB-008` filesystem slice adds
an aborting multi-record revision precondition, stable persisted entry/link
records, an atomic create plan, a required `fsLinks.parentId` enumeration index,
and idempotent initialization of a reserved stable root entry. Its internal
reader now resolves link-based canonical paths, performs indexed ordinal child
enumeration with batched entry loads, and detects dangling-link corruption. A
public provider exposes stat/enumeration and atomic create; created entries use
the acting user as owner and inherit the parent directory group. Atomic
permission changes and stable-link move/rename are also implemented with
revision assertions and parent metadata updates. Recursive delete asserts the
complete observed subtree and removes all links/entries atomically. Recursive
copy assigns fresh IDs while retaining immutable deduplicated content hashes.
Content reads/writes now use bounded incremental SHA-256 hashing, deduplicated
256 KiB chunks, optimistic metadata publication, and integrity verification.
The shared maintenance API performs bounded 30-day orphan collection with an
atomic reference recheck and conservative v1 compatibility. Projection routing
and complete profile seeding reuse the existing Platform Core mount and seeder;
their concrete invocation remains owned by the future host composition. The
first `P2-IDB-009` slice adds async persistent diagnostics and audit contracts.
Browser repositories redact structured properties before interop, retain
diagnostics with atomic bounded eviction, preserve general audit append-only,
and order equal timestamps by auto-incremented primary key. Persistent grants
atomically couple grant state, canonical audit, and policy revision. Catalog
reconciliation replaces authoritative build snapshots while preserving local
enablement and retaining removed apps disabled outside runtime input.
Browser storage status now reports native usage/quota and durable-retention
state, applies the approved 10-percent-or-64-MiB low-space policy, and maps
IndexedDB quota exhaustion to a recoverable non-destructive exception.
Backup format v1 now exports all stores from one consistent transaction with a
SHA-256 integrity digest. Validated restore supports explicit atomic additive
merge or full replacement; differing merge collisions abort without mutation.
`D-010` is accepted in ADR 0018: IndexedDB recovery is non-destructive first,
with export before destructive actions, validated merge/replace restore, and
explicit confirmation for replacement or reset. The renderer-independent
recovery contract now expresses typed failure states, context-sensitive boot
blocking, safe actions, export availability, stable error codes, and correlation
IDs. Browser Infrastructure classifies storage exceptions without mutation;
replacement and reset require exact `REPLACE` or `RESET` confirmation.
`P2-IDB-013` adds a host-independent Blazor WASM harness and xUnit Playwright
driver. Installed headless Chrome now executes native IndexedDB group, settings,
and filesystem contracts, including optimistic conflicts and atomic metadata
mutations. This evidence also corrected inline-key `add`/`put` calls that passed
an invalid separate null key in real browsers. `P2-IDB-014` extends that matrix
to reload persistence, interrupted migration, transaction rollback, native quota
failure and C# recovery mapping, backup replace/idempotent merge, bounded orphan
cleanup, and two-tab revision conflict. Named DOM errors are normalized before
crossing JS interop so recovery classification no longer receives an undefined
message.
See `docs/browser-storage.md`, `docs/indexeddb-filesystem.md`, and
`docs/indexeddb-operational-records.md`. Focused recovery-contract verification
and all 439 solution tests, including the real-Chrome E2E and headless window
runtime tests, pass with 0
failed. The
browser infrastructure project build confirms that `indexedDb.js` and
`storageManager.js` are emitted through the Razor static-web-assets pipeline.

`D-013` is accepted in ADR 0016. MudBlazor 9.7.0 is approved only for complex
menus, grids, tabs, forms, dialogs, and selectors behind Platform-owned wrappers.
Native Blazor/scoped CSS remains authoritative for desktop, window chrome,
taskbar, launcher layout, and simple controls; public SDK/domain contracts remain
free of MudBlazor types. The retained wrapper publishes in trimmed Release with
no warning; direct MudBlazor runtime payload is 1,363,359 raw / 251,505 Brotli
bytes. Playwright proves desktop/mobile containment, complex interactions,
validation, accessible roles/labels, screenshot output, and clean console/
network behavior. Wrapper conventions are documented for `P2-UI-003`.

The Platform window runtime is complete through `P2-WIN-014`: immutable C#
state, geometry persistence, dynamic rendering, lifecycle setup, Pointer Events,
constraints, close coordination and owner modality are implemented. Chrome
proves all resize edges, touch/pen, keyboard controls, taskbar restore, viewport
changes, focus/z-order, close and clean module loading. File-dialog work has
advanced through the reusable browser, Save/Folder components, typed selected
resources, media-aware lazy enumeration, short-lived handle issuance and
lifecycle revocation. Active requests project into authoritative owner-modal
Windows with focus return and ordinary Escape/token/close cancellation. Chrome
now renders Open, Save, and Folder flows and proves filters, visible dotfiles,
multi-selection, overwrite, folder creation, filesystem denial, modality, focus
return, cancellation, and clean console/network behavior. `P2-DLG-001` through
`P2-DLG-010` are complete.

The standalone `OS/HackerOs.Ecosystem` .NET 10 Blazor WebAssembly PWA now
provides the Phase 2 host boundary. It is included in `HackerOs.sln`, contains no
template sample pages/assets, retains only boot-critical global styles, and
builds with repository trimming analysis and warnings-as-errors. Composition
references now point only to Platform Core/Blazor and Browser Infrastructure;
there are no concrete app references in the current empty build profile. DI
composition remains under `P2-HOST-003` and `P2-HOST-004`.

## Implemented architecture

```text
HackerOs.App.Abstractions
  <- HackerOs.AppSdk

HackerOs.App.Abstractions
  <- HackerOs.App.Abstractions.Tests

HackerOs.AppSdk
  <- HackerOs.AppSdk.Tests

HackerOs.App.Abstractions
  <- HackerOs.Simulation.Abstractions
  <- HackerOs.Platform.Core
  <- HackerOs.Platform.Core.Tests
```

The abstractions projects have no UI, browser, dependency injection, or concrete
storage dependency. The App SDK adds renderer-independent terminal execution and
active-session service execution. Platform Core now proves canonical settings,
authorization, revisions, validation, audit events, and filesystem projection in
memory before IndexedDB is introduced. The headless app catalog validates package
graphs and computes deterministic activation/deactivation order. The Blazor SDK
now defines the window app lifecycle and file dialog boundary without providing a
window manager or dialog renderer yet. Simulation abstractions now define stable
filesystem entry identity, normalized names, permissions, timestamps, and
file/directory/symbolic-link metadata plus validated operation requests, stable
errors, generic results, and transaction outcomes without introducing storage
dependencies. Platform Core implements deterministic in-memory files and
transactions, mounted authorization, bounded symbolic-link traversal,
clean-profile seeding, and canonical settings projections. App abstractions now
also define immutable exact capability grants with closed path/host/port
constraints.
Policy decisions now use a deny-by-default immutable result with stable granted,
missing, revoked, constrained, and authority-denied reasons plus policy/grant
revision evidence. An in-memory grant repository evaluates those decisions
against structured resource candidates, requires Administrator/System authority
to grant or revoke, detects broader re-grants as expansions, and records a
chronological audit log. Canonical settings now have a structured scope key
(app/user, app/device, app/roaming-user, OS/admin), a deterministic projection
path factory, a scope-authorization policy that never lets app kind alone
elevate scope, a typed schema/sensitivity model, and a Linux-like `.config`
parser/serializer feeding a schema-driven document validator. Capability grant
and other OS policy changes are themselves stored as an ordinary protected
canonical settings document under `/etc/hackeros/policy.config`.

## Key decisions

- Target .NET 10 and permit roll-forward within installed .NET 10 feature bands.
- Validate manifests before loading or executing application code.
- Assign Administrator/System authority through trusted OS policy, never through
  an app's self-authored manifest.
- Keep terminal commands independent of xterm.js and any concrete renderer.
- Treat service cancellation as session shutdown, with no resume-state contract.
- Defer `WindowAppBase` until the Blazor SDK project and window lifecycle contract
  are designed together.
- Match capabilities exactly and case-sensitively; policy constraints, not
  wildcard capability names, restrict resources.
- Store settings once and expose the same canonical records through virtual
  filesystem projections.
- Reject duplicate, missing, incompatible, or cyclic app dependencies before
  activation and order valid graphs deterministically by app ID.
- Seal the framework-owned Blazor lifecycle and expose app-specific hooks so a
  derived component cannot skip framework post-render setup.
- Reject inline CSS and JavaScript in every Razor project at build time.
- Use immutable filesystem entry IDs, ordinal case-sensitive paths, separate
  metadata/content, projection-first mounts, bounded symbolic-link traversal,
  and optimistic provider transactions as defined by ADR 0008.
- Use a purpose-built C# window runtime with Platform-owned chrome, isolated
  Pointer Events interop, and a mandatory published-Release proof per ADR 0009.
- Use one strict versioned `app.manifest.json` with source-generated canonical
  serialization, rejected unknown/duplicate fields, and hashed relative assets.
  The serializer is now implemented and pinned by a golden canonical fixture.
- Use structured settings keys with Linux-like `.config` projections supporting
  `#` comments and optional sections; retain protected association JSON.
- Use monotonic PIDs, platform-owned cancellation, deterministic simulation
  clocks/random streams, and virtual hardware resource profiles per ADR 0012.
- Require first-run Release Administrator creation with no default credentials;
  keep local elevation operation-scoped and provision homes through the VFS.
- Use a small deterministic shell grammar with quoted tokens, environment
  expansion, command-owned flags, and deferred pipes/redirection/jobs/scripts.
- Grant and revoke mutations require Administrator/System acting authority;
  they never succeed from ordinary User authority regardless of app kind.
- Persist policy and other protected OS configuration through the same
  canonical settings revision, validation, and audit path as every other
  document rather than a parallel storage mechanism.
- Gate settings scope usage on explicit manifest declaration plus, for roaming
  and OS/admin scopes, an explicit granted capability and sufficient authority.

## Task list

- [x] Create the .NET 10 solution under `wasm2/HackerOs/`.
- [x] Create app abstractions and headless App SDK projects.
- [x] Define app kinds and `System > Administrator > User` ordering.
- [x] Define the initial canonical app manifest.
- [x] Validate identity, semantic versions, SDK ranges, duplicates, and app-kind
  constraints.
- [x] Define renderer-independent `TerminalAppBase`.
- [x] Define active-session `ServiceAppBase` and stop reasons.
- [x] Add focused automated tests.
- [x] Decide the initial capability identifier catalog and exact matching semantics.
- [x] Define typed core intents and app intent requests.
- [x] Define canonical virtual paths and reject traversal above root.
- [x] Define settings/filesystem projection contracts and operation context.
- [x] Implement protected in-memory settings with capability plus authority checks.
- [x] Validate file-association JSON, atomic replacement, revision conflicts, and
  change audit events.
- [x] Implement the manifest catalog and deterministic dependency graph.
- [x] Implement Semantic Version 2.0.0 precedence for dependency ranges.
- [x] Create `HackerOs.AppSdk.Blazor` and define `WindowAppBase`.
- [x] Seal framework lifecycle methods and expose app lifecycle hooks.
- [x] Define typed file-open, file-save, and folder-selection contracts.
- [x] Enforce no inline CSS/JavaScript in Razor builds.
- [x] Accept ADR 0008 for the virtual filesystem model.
- [x] Define immutable filesystem IDs, names, permissions, timestamps, metadata,
  and directory links.
- [x] Define filesystem operation requests, stable errors, generic results, and
  transaction outcomes.
- [x] Define owned binary/text streaming content contracts.
- [x] Define trusted filesystem authorization and selected-resource handles.
- [x] Define provider contracts and longest-segment mount routing.
- [x] Define normalized, bounded, mount-aware symbolic-link traversal.
- [x] Implement the deterministic in-memory repository and mounted service.
- [x] Implement idempotent clean-profile system and per-user seeding.
- [x] Mount canonical settings documents with shared revisions.
- [x] Complete the assembled filesystem contract suite.
- [x] Accept ADR 0009 for the purpose-built window runtime strategy.
- [x] Accept ADR 0010 for canonical manifest JSON and schema evolution.
- [x] Accept ADR 0011 for settings scopes, keys, paths, and `.config` syntax.
- [x] Accept ADR 0012 for deterministic process, clock, and resource simulation.
- [x] Accept ADR 0013 for local users, sessions, credentials, and elevation.
- [x] Accept ADR 0014 for the first-slice shell grammar boundary.
- [x] Define immutable exact capability grants and structured path/host/port
  constraints.
- [x] Define deny-by-default capability evaluation with explicit stable reasons.
- [x] Define policy changes as a protected revisioned settings document
  requiring Administrator/System write authority.
- [x] Implement the in-memory capability grant repository with revocation,
  expansion detection, and an audit log.
- [x] Define app/user, app/device, roaming-user, and OS/admin settings document
  keys and a deterministic projection path factory.
- [x] Define settings-scope authorization so app kind alone never elevates
  scope and roaming/OS-admin require capability plus authority.
- [x] Implement schema-driven setting declarations, sensitivity classes, and the
  `.config` parser/serializer/validator.
- [x] Prove a system app operated by a User does not gain System authority
  without an explicit audited system context.
- [x] Expand the capability catalog with process, notification, window,
  clipboard, and service capabilities; reject window-only capabilities
  declared by non-window apps.
- [x] Seed clean-profile default capability grants from a manifest's declared
  capabilities and prove System authority never implies an undeclared one.
- [x] Define user/group/session identity records and implement `ISessionService`
  login/logout/shutdown with in-memory repositories and last-administrator
  protection (ADR 0013).
- [x] Define process identity/state/resource-profile contracts and implement an
  in-memory process manager with singleton lookup, bounded stop, kill/cascade,
  and bounded history (ADR 0012).
- [x] Define and implement a deterministic simulation clock, scheduler, and
  seeded random source used by process management and resource simulation.
- [x] Implement deterministic CPU/RAM/storage/network resource simulation with
  hardware-capacity clamping and per-process seeded jitter.
- [x] Define and implement a typed in-memory event bus with ordering, disposal,
  and fault isolation.
- [x] Define and implement bounded diagnostic/audit logging with sensitive-key
  redaction.
- [x] Add cross-cutting Section 7 tests proving session, process, event, and
  resource subsystems compose end to end with no sleeps.
- [x] Expand `IAppExecutionContext` with identity, cancellation, and seven
  narrow app-scoped gateways (filesystem, settings, events, notifications,
  logging, clock, process/job); implement a trusted
  `AppExecutionContextFactory` as the sole constructor; issue/revoke
  short-lived selected-resource handles with expiry and event-driven
  auto-revocation (see [`app-execution-context.md`](app-execution-context.md)).
- [x] Implement referenced-assembly app entry-point discovery, app descriptors,
  and lifecycle orchestration for Terminal/Service/Window apps including
  singleton focus and dependency-cascade enable/disable; implement
  capability-gated typed intent dispatch (launch, open/edit/reveal file,
  execute command, show settings) and canonical, protected file-association
  resolution (explicit target, configured default, sole candidate,
  chooser-required, no-handler) (see
  [`app-intents-and-associations.md`](app-intents-and-associations.md)).
- [x] Expand the canonical app manifest with Presentation, OS compatibility,
  Localizations, Settings schema, Intents, Assets, Update, and Service-only
  AutoStart fields per migration analysis section 7.2; publish a versioned
  Draft 2020-12 JSON Schema (`Schema/manifest.schema.v1.json`, embedded as a
  resource) enforcing unknown-field rejection and app-kind-specific structural
  rules, with one valid fixture per app kind and nine invalid fixtures under
  `Schema/Fixtures/` (see [`build-profile.md`](build-profile.md)).
- [x] Define the first build-profile contract for selected packages, default
  enables, grants, associations, locales, themes, and optional server features
  with serializer and validator coverage.
- [x] Add explicit invalid-fixture regression coverage for every build-profile
  validation error code (`P1-BLD-008`).
- [x] Enable the trim analyzer on every shipping project and resolve/justify
  every reflection warning at the discovery/instantiation boundary
  (`P1-GATE-004`).
- [x] Add a headless kernel integration test proving boot, session, discovery,
  app launch, command execution, capability-checked filesystem/settings
  gateway use, service shutdown, disable, and logout compose end to end
  (`P1-GATE-003`).
- [x] Implement the platform window runtime and modal file dialog renderer (`P2-WIN`, `P2-DLG`).
- [x] Scaffold and complete the PWA host composition root, boot coordinator, error boundary, recovery UI, shared RCL theme scope, and Release trim analyzer smoke tests (`P2-HOST-001` through `P2-HOST-010`).
- [x] Implement Desktop Shell, Taskbar, App Launcher, Notification Center, and Session Logout UX (`P2-SHELL-001` through `P2-SHELL-009`).
- [x] Establish Phase 2B App Project Standard (`P2-APPSTD-001` through `P2-APPSTD-005`) and implement the Terminal application (`org.hackeros.terminal`) under `Apps/System/HackerOs.Apps.Terminal/` (`P2-TERM-001` through `P2-TERM-009`).
- [x] Implement Core Terminal Command Apps (`pwd`, `ls`, `cd`, `cat`, `echo`) under `Apps/Commands/` (`P2-CMD-001` through `P2-CMD-006`).
- [x] Implement File Explorer Application (`org.hackeros.file-explorer`) under `Apps/System/HackerOs.Apps.FileExplorer/` (`P2-FEX-001` through `P2-FEX-009`).
- [x] Implement Text Editor Window App (`org.hackeros.text-editor`) under `Apps/System/HackerOs.Apps.TextEditor/` (`P2-TEXT-001` through `P2-TEXT-007`).
- [x] Implement First Session Service App (`org.hackeros.samples.service-app`) under `Apps/Samples/HackerOs.Samples.ServiceApp/` (`P2-SVC-001` through `P2-SVC-003`).
- [x] Implement PWA Packaging, Offline Operation, and Updates (`OS/HackerOs.Ecosystem/wwwroot/`) (`P2-PWA-001` through `P2-PWA-007`).
- [ ] Complete Phase 2 Acceptance and Exit Gate (`P2-ACC-SETUP-001` through `P2-GATE-005`). The evidence matrix exists, but published-PWA, accessibility, CI, and explicit approval gates remain reopened in `docs/phase-2-acceptance.md`.
- [x] Implement Public SDK 1.0 Candidate (`P3-SDK-001` through `P3-SDK-010`). Created sample Window app, Terminal app, Service app, ADR 0019 (`docs/adr/0019-sdk-versioning-and-compatibility.md`), Manifest Validator CLI (`Tools/HackerOs.Tools.ManifestValidator/`), and Developer Guide (`docs/sdk/developer-guide.md`).
- [x] Implement Accessibility, Localization, Theming, and Design System (`P3-UX-001` through `P3-UX-007`). The theming slice was expanded on 2026-08-19 into the shared nine-theme catalog documented by `docs/theming.md`; `docs/design-system.md`, `docs/localization.md`, and `docs/accessibility.md` remain the related contracts.
- [ ] Implement Build-Known Lazy Loading (`P3-LAZY-001` through `P3-LAZY-007`). Hack Paint is declared as the first lazy assembly and has a typed, coalescing browser loader; deterministic descriptor registration and published/offline evidence remain open.
- [x] Implement Migration Rules for Every Legacy Feature (`P4-RULE-001` through `P4-RULE-006`). Published `docs/migration/rules.md`. Phase 4 started.
- [x] Implement Wave 2 OS Fundamentals (`P4-W2-001` through `P4-W2-008`). Ported Settings app (`org.hackeros.settings`), System Monitor (`org.hackeros.system-monitor`), and Error Log Viewer (`org.hackeros.error-log-viewer`). Published `docs/migration/wave-2.md`.
- [ ] Complete Wave 3 Editing, Clipboard, and Drag/Drop (`P4-W3-001` through `P4-W3-007`). ADR 0020, the clipboard gateway, and drag payload exist. Nano is revalidated with its full-screen contract, VFS editor, Blazor Terminal adapter, lifecycle propagation, and browser key/render/cleanup evidence. Code Editor was reworked on 2026-08-21 from local CodeMirror 6 to local Monaco Editor (matching the legacy JS app), adding an Open Folder tree view (shared `FileView` control) and an embedded, tabbed terminal (new shared `HackerOs.AppSdk.Terminal` library, also now backing the standalone Terminal app — see ADR 0020's amendment). C# tab/document models, VFS-backed recovery, platform whole-window dirty-close protection, focused tests, Chromium interaction/disposal, and axe evidence remain in place under Monaco. A real rendered reload test plus published/offline integration remain open. Hack Paint remains a separate reopened Wave 5 item.
- [x] Implement Wave 4 Simulated Network, Browser, and Websites (`P4-W4-001` through `P4-W4-007`). Created ADR 0021 (`docs/adr/0021-simulated-network-and-browser-rendering.md`), implemented simulated network domain, DNS, website controllers (`HackerSearch`, `HackMail`, `CryptoBank`, `DarkNet Market`, `HackerZ Forum`), Browser app (`org.hackeros.browser`), terminal commands (`ping`, `nmap`, `curl`), and unit test suite verifying zero real network calls. Published `docs/migration/wave-4.md`.
- [ ] Complete Wave 5 Utility Apps and Commands (`P4-W5-APP-001` through `P4-W5-CMD-009`). Calculator, the bounded Settings theme picker, and terminal commands are implemented. Hack Paint is reopened: its core now has authoritative RGBA history, pixel drawing, crop, and rotation tests, but canvas rendering, image/VFS round trips, browser dialogs, and Playwright pixel evidence remain outstanding.
- [x] Create Gameplay V3 Analysis (`P4-W6-GATE-001`). Published `doc/en/wasm/gameplay-v3-analyse.md` defining gameplay domain architecture, contract generator, hardware simulation, exploit engine, player scripting sandbox, and encrypted save engine.
- [x] Obtain Gameplay Domain Approval (`P4-W6-GATE-002`). Created ADR 0023 (`docs/adr/0023-optional-game-domain-and-proxy-fallback.md`) establishing optional Game Domain build dependency, capability `gameplay.domain.access`, and server proxy fallback routing for network commands (`ping`, `curl`, `cat`).
- [x] Implement Wave 6 Gameplay Domains (`P4-W6-001` through `P4-W6-006`). Built `HackerOs.Game.Abstractions`, `HackerOs.Game.Core` (`InMemoryGameDomainGateway`, `NullGameDomainGateway`), contracts generator, hardware upgrade tree, economy payout engine, and automated unit test suite `HackerOs.Game.Tests`. Published `docs/migration/wave-6.md`.

## Validation

The 2026-08-19 integrated gate builds every project and all three host
compositions in Release with zero warnings/errors:

```powershell
dotnet build HackerOs.sln --configuration Release --no-restore
```

The shared-theme/Start-menu slice has 533 passing focused tests: Platform Core
(431), Platform Blazor and render-tree coverage (65), Ecosystem composition
(12), Settings persistence (5), icon SDK (16), and taskbar (4). The sequential
real-browser accessibility scenario additionally exercises the actual desktop,
the Windows 7-style launcher, File Explorer/Settings quick links, Settings,
Code Editor, Hack Paint, a live HackerOS → Windows 7 → HackerOS scope change,
and a 375×812 viewport; it reports no serious/critical axe finding.

Both the offline Ecosystem PWA and the package-only sample publish successfully.
Their output contains `_content/HackerOs.Theming.Blazor/themes.css`; the PWA
service-worker manifest includes that exact asset. The reusable desktop closure
also restores/builds/publishes from seven local `0.1.0-local` packages with no
remaining project dependency.

Warnings are treated as errors for every project under the solution directory,
and the trim analyzer runs on every shipping (non-test) project so trimming/AOT
reflection issues surface as ordinary build errors.

---

## Phase 5 — Optional Server (Wave 8) — ⚠️ AUDIT REMEDIATION IN PROGRESS

**Objective:** Provide an optional ASP.NET Core server for sync, identity,
and real-network proxy. The PWA continues to function fully offline when the
server is absent.

### P5-SRV — Server Foundation

| ID | Title | Status |
|---|---|---|
| P5-SRV-001 | API versioning and PWA compatibility window | ✅ Done |
| P5-SRV-002 | Device identity and registration (D-017) | ✅ Done |
| P5-SRV-003 | Server data policy, health, and export | ✅ Done |
| P5-SRV-004 | ASP.NET Core server composition (Program.cs + EF Core) | ✅ Done — documented configuration, schema bootstrap, health, authenticated ownership, and bounded backup/restore are integrated |

### P5-UI — Server-Hosted Blazor UI (ADR 0027)

| ID | Title | Status |
|---|---|---|
| P5-UI-001 | Render-mode-agnostic environment/lazy-load transport seam (`IEcosystemHostEnvironment`, `InProcessAssemblyTransport`) | ✅ Done |
| P5-UI-002 | Interactive Server component hosting in `HackerOs.Server` (`Components/App.razor`, `Program.cs` composition) | ✅ Done — single-tenant/single-active-circuit only |
| P5-UI-003 | Multi-tenant scoped-service conversion and EF-backed browser-storage replacement | ⬜ Not started — needs its own ADR |
| P5-UI-004 | Direct injection of `IAccountService`/`ISyncService`/`IProxyService` into UI code instead of HTTP | ⬜ Not started — needs its own ADR and shared client abstraction |

### P5-CONN — Client-Side Server Connection and Proxy Bridge (ADR 0028)

| ID | Title | Status |
|---|---|---|
| P5-CONN-001 | Per-device connection storage (`IServerConnectionRepository`, IndexedDB schema v3) | ✅ Done |
| P5-CONN-002 | Browser-independent HTTP clients (`IAccountClient`, `IProxyClient`, `IServerConnectionService` in `Platform.Core/ServerConnection/`) | ✅ Done |
| P5-CONN-003 | Settings UI panel to connect/disconnect | ✅ Done |
| P5-CONN-004 | Real-network proxy fallback wired into `ping` | ✅ Done |
| P5-CONN-005a | Real-network proxy fallback wired into `curl -I` | ✅ Done — matches `ping`'s `IProxyClient` pattern; unblocked by ADR 0034 (P5-CMD below) |
| P5-CONN-005b | Real-network single-port TCP probe fallback wired into `nmap` (ADR 0035) | ✅ Done — new `POST /api/proxy/tcp-probe`; single host:port only, never a range; see P5-PROXY-008 below |
| P5-CONN-005c | Real-network proxy fallback wired into full-body `curl` | ✅ Done — see P5-CONN-006 |
| P5-CONN-005d | `cat` URL-reading (simulated + real-network fallback) | ✅ Done — a scheme-prefixed argument (`http(s)://`) is read as a URL; bare filenames are unaffected. Shared `SimulatedPageTextFormatter` extracted from `CurlCommand` for both commands to render pages identically |
| P5-CONN-006 | Server-side proxy body-transfer (`ProxyHttpRequest.IncludeBody` / `ProxyHttpResponse.BodyBase64`) | ✅ Done — direct base64-in-response, not chunked; same `MaxResponseBytes` cap as every other proxy response; no new endpoint |

### P5-CMD — Terminal Command Catalog Wiring (ADR 0034)

Unplanned prerequisite discovered starting Pass N+1a: verifying `ping`'s
real-network fallback against a real app launch (not just direct unit-test
construction) surfaced that the entire `Apps/Commands/*` suite was invisible
to every host.

| ID | Description | Status |
| --- | --- | --- |
| P5-CMD-001 | Wire 24 of 28 `Apps/Commands/*` projects into `HackerOs.Ecosystem.csproj` (`ProjectReference`/`EmbeddedResource`/`BlazorWebAssemblyLazyLoad`), excluding `cd`/`pwd`/`clear`/`help` (terminal built-ins) | ✅ Done |
| P5-CMD-002 | `AppLifecycleOrchestrator` constructs terminal/service apps via `ActivatorUtilities.CreateInstance` so commands needing injected services (`ping`, `curl`, `nmap`) actually launch | ✅ Done |
| P5-CMD-003 | Register `ISimulatedNetworkService` with `SmokeTestNetworkSeed` (`example.hackeros`, `empty.hackeros`) — first production registration/seed of any kind | ✅ Done — deliberately minimal, not the ADR 0023 "Game domain" content pack |
| P5-CMD-004 | Fix incomplete capability declarations found during live verification: `mkdir`/`touch`/`rm`/`chmod` missing `filesystem.user-home.read`, `alias`'s JSON manifest missing both capabilities its C# manifest already declared | ✅ Done |
| P5-CMD-005 | `cat` cannot read a file `touch` just created (`IndexedDbFileSystemProvider.ReadAsync` treated a null `ContentHash` as failure instead of an empty file) | ✅ Done — fixed in `IndexedDbFileSystemProvider.ReadAsync`; see `docs/server-implementation-pass.md` |

### P5-SYNC-CLIENT — Settings, FileSystem, Grants, AppCatalog, and FileAssociations Domain Sync (ADR 0029-0031, ADR 0033)

All five domains named in the original roadmap now have a client-side adapter.

| ID | Title | Status |
|---|---|---|
| P5-SYNC-CLIENT-001 | Domain-agnostic sync scaffolding (`syncCursors`/`syncRecordState` stores, schema v4; `ISyncClient`) | ✅ Done |
| P5-SYNC-CLIENT-002 | `SyncEligible` opt-in flag; `AppearanceSettingsDocuments` opted in | ✅ Done |
| P5-SYNC-CLIENT-003 | `ISettingsSyncService` push/pull adapter with deterministic `RecordId` derivation | ✅ Done |
| P5-SYNC-CLIENT-004 | On-connect + manual "Sync now" trigger in Settings UI | ✅ Done |
| P5-SYNC-CLIENT-005 | `IFileAssociationsSyncService`/`IAppCatalogSyncService` domain adapters | ✅ Done (ADR 0033) |
| P5-SYNC-CLIENT-006 | Surfaced (non-automatic) conflict resolution UI for Settings | ⬜ Not started — ADR 0029 Decision 6 is an explicit simplification for this domain only |
| P5-SYNC-CLIENT-007 | `IContentTransferClient`/`HttpContentTransferClient` chunked content transfer (browser-independent) | ✅ Done (ADR 0030) |
| P5-SYNC-CLIENT-008 | `IFileSystemSyncService` push/pull adapter, recursive walk of `/home/{userId}`, `FileSystemEntryId`-derived `RecordId` | ✅ Done (ADR 0030) |
| P5-SYNC-CLIENT-009 | FileSystem conflict handling — never auto-apply either copy; unresolved-count indicator in Settings UI | ✅ Done for detection/surfacing — manual resolution UI is still future work |
| P5-SYNC-CLIENT-010 | FileSystem deletion propagation (tombstones) | ⬜ Not started — a file removed on one device is not removed on another via sync |
| P5-SYNC-CLIENT-011 | Pull-side local-content dedup check before downloading (skip re-download when `fsContent` already has the hash) | ⬜ Not started — pull always re-downloads; correctness-first simplification for this pass |
| P5-SYNC-CLIENT-012 | `IGrantsSyncService` pull-only adapter; `IPersistentCapabilityGrantRepository.ImportAsync` (upsert-by-server-ID) | ✅ Done (ADR 0031) |
| P5-SYNC-CLIENT-013 | Wire pulled/revoked grants into live `ICapabilityGrantRepository` enforcement | ⬜ Not started — pulled grants are durable but inert; enforcement still reads only the manifest-seeded in-memory repository, see ADR 0031 Decision 4 |
| P5-SYNC-CLIENT-014 | App enablement made real: durable persistence, boot-time live-enforcement hydration, "Installed Apps" Settings UI | ✅ Done (ADR 0032) |
| P5-SYNC-CLIENT-015 | `AppCatalogSyncService` push+pull; pulled enablement changes take effect immediately via `AppEnablementRegistry`, not just at next boot | ✅ Done (ADR 0033) |
| P5-SYNC-CLIENT-016 | AppCatalog `ClientWins` conflict policy (ADR 0025's stated preference for this domain) | ⬜ Not started — server-wins reused instead; see ADR 0033 and the open question in `docs/server-implementation-pass.md` |

### P5-SYNC — Record Synchronization

| ID | Title | Status |
|---|---|---|
| P5-SYNC-001 | Pull with opaque cursors and paging | ✅ Done |
| P5-SYNC-002 | Push with durable idempotency and content-hash verification | ⬜ Reopened: idempotency is not restart-safe |
| P5-SYNC-003 | Per-domain conflict rules (ADR 0025 / D-018) | ⬜ Reopened for ownership/conflict evidence |
| P5-SYNC-004 | Grant domain security block (ServerWins only) | ⬜ Reopened for complete authorization evidence |
| P5-SYNC-005 | Chunked resumable file content transfer with SHA-256 deduplication | ⬜ Reopened for restart/resume evidence — upload and content-addressed download both now work end-to-end (`GetChunkAsync` was a stub returning zero bytes until ADR 0030 fixed it, `Tests/HackerOs.Server.Tests/ContentBlobServiceTests.cs`); restart-mid-transfer resumption specifically still lacks test evidence |
| P5-SYNC-006 | Complete sync integration/security matrix | ⬜ Reopened |

### P5-PROXY — HTTP/TCP/UDP Proxy

| ID | Title | Status |
|---|---|---|
| P5-PROXY-001 | HTTP proxy contract and endpoint | ✅ Done |
| P5-PROXY-002 | Authenticated device/app policy and SSRF blocking | ⬜ Partial: account/device ownership and revocation enforced; app registration/grants absent |
| P5-PROXY-003 | Pinned address validation and port allow-list | ⬜ Partial: validated address is pinned; full rebinding/redirect integration matrix outstanding |
| P5-PROXY-004 | Redirect and resource limits | ⬜ Partial: redirect, payload, duration, concurrency and protocol limits exist; bandwidth policy absent |
| P5-PROXY-005 | Quotas, audit and explicit operator weakening | ⬜ Partial: concurrency and audit exist; durable quotas/configuration warnings absent |
| P5-PROXY-006 | Simulated-domain isolation | ⬜ Reopened for end-to-end evidence |
| P5-PROXY-007 | Complete proxy security suite | ⬜ Partial: focused server suite passes 40 tests; required integration cases remain |
| P5-PROXY-008 | Single-port TCP reachability probe (`POST /api/proxy/tcp-probe`, ADR 0035) | ✅ Done — reuses device-ownership/simulated-domain/blocked-address checks; no port allow-list (arbitrary port is the point); no range/multi-port shape in the contract; `IProxyTcpConnector` abstraction keeps it unit-testable |

### New ADRs
- **ADR 0024** — Server Identity and Device Registration (D-017)
- **ADR 0025** — Record Synchronization Envelope, Conflict Model, and Cursor Strategy (D-018)
- **ADR 0027** — Server-Hosted Blazor UI (Third Host, Single-Tenant Phase)
- **ADR 0028** — Client-Side Optional-Server Connection and Proxy Bridge
- **ADR 0029** — Settings Domain Sync (First Client Sync Implementation)
- **ADR 0030** — FileSystem Domain Sync
- **ADR 0031** — Grants Domain Sync (Pull-Only)
- **ADR 0032** — App Enablement Management
- **ADR 0033** — AppCatalog and FileAssociations Domain Sync
- **ADR 0034** — Wire the Terminal Command Catalog
- **ADR 0035** — Single-Port TCP Reachability Probe for nmap's Real-Network Fallback

### Solution structure
```
Server/
  HackerOs.Server.Contracts/       — shared contracts (versioning, identity, sync, proxy, admin)
  HackerOs.Server/                 — ASP.NET Core server (EF Core SQLite, bearer auth, minimal API)
    Data/                          — DbContext + entity models
    Services/                      — AuthServices, AccountService, SyncService, ProxyService, AuditService, ContentBlobService
    Endpoints/                     — VersionEndpoints, IdentityEndpoints, SyncEndpoints, ProxyEndpoints, AdminEndpoints
    Components/                    — Server-hosted Blazor UI (ADR 0027): App.razor, Interactive Server render mode
Tests/
  HackerOs.Server.Tests/           — focused unit tests for sync, proxy, versioning, and identity
```

The 2026-08-03 focused server run passes 39 tests. That total is evidence for the
current unit suite only and does not close the reopened Phase 5 integration gates.
See `docs/server-security.md` for the implemented boundary and remaining work.

### Test results (Phase 5)
- 39 focused server tests pass; the expanded integration matrix remains open.
- Verification command:
  ```powershell
  dotnet test Tests/HackerOs.Server.Tests/HackerOs.Server.Tests.csproj --configuration Release --no-restore
  ```
