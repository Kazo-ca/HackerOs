> [Version française](../../../../fr/wasm/dev/old/README.md)

# HackerOS v3 Documentation Index

> **Archived.** These documents predate the rebuild plan. The current index is
> [`../README.md`](../../README.md); start with
> [`../project-overview.md`](../project-overview.md), and for app development
> with [`../sdk-developer-guide.md`](../../framework/sdk-developer-guide.md) and
> [`../sdk-reference.md`](../../framework/sdk-reference.md).

This is the entry point into `doc/en/wasm/`, organized by who is
reading it rather than by when it was written. If you add, rename, or remove a
doc, update this index in the same change — see the documentation-maintenance
rule in the repository root [`AGENTS.md`](../../../../../AGENTS.md).

Every doc listed here documents the browser-first WASM solution under
`wasm2/HackerOs/`. The legacy TypeScript app in `src/` is a separate,
read-only behavioral reference and is not covered by this index.

## Start here

Read these first regardless of what you're about to work on.

| Doc | What it's for |
| --- | --- |
| [`../README.md`](../../../../../wasm2/HackerOs/README.md) | Solution layout and build/test commands. |
| [`../../../AGENTS.md`](../../../../../AGENTS.md) | Binding rules: directory boundaries, collocated Razor assets, file naming, doc-maintenance. |
| [`hosting-model.md`](hosting-model.md) | The three host projects (Ecosystem PWA, `test/test` debug harness, optional Server) and how they relate. |
| [`implementation-status.md`](implementation-status.md) | The current, authoritative state of the migration — what's built, what's open, test counts. |
| [`webassembly-debugging.md`](webassembly-debugging.md) | How to run and debug the WASM host locally. |
| [`e2e-test-demo-mode.md`](e2e-test-demo-mode.md) | `--test-demo` switch of the `test/test` host: the button that seeds the deterministic files used by the E2E suites. |
| [`common-pitfalls.md`](common-pitfalls.md) | Recurring mistakes already made once in this codebase — static-asset routing, manifest/schema drift, IndexedDB versioning, E2E harness setup, ARIA states bound to `bool`, apps that stop loading after a rebuild. Read before touching any of those. |

## Building an app on HackerOS

For anyone (or any agent) writing an app/command that runs *on* HackerOS
against the public SDK, without needing to touch platform internals.

| Doc | What it's for |
| --- | --- |
| [`sdk/developer-guide.md`](sdk/developer-guide.md) | Superseded by [`../sdk-developer-guide.md`](../../framework/sdk-developer-guide.md) (it still describes hand-written `app.manifest.json`, replaced by `[AppManifest]` classes in ADR 0047). |
| [`app-contracts.md`](app-contracts.md) | The stable, browser-independent contracts every app is built against. |
| [`blazor-app-sdk.md`](blazor-app-sdk.md) | `WindowAppBase`, file dialogs, and scoped Blazor asset rules for window apps. |
| [`app-execution-context.md`](app-execution-context.md) | The scoped gateways (filesystem, settings, events, notifications, logging, clock, process) an app instance receives. |
| [`app-intents-and-associations.md`](app-intents-and-associations.md) | Lifecycle orchestration, typed intent dispatch, and file-association resolution. |
| [`app-catalog.md`](app-catalog.md) | Manifest/dependency validation and deterministic activation order. |
| [`build-profile.md`](build-profile.md) | The `app.manifest.json` JSON Schema and build-profile contract. |
| [`file-dialogs.md`](file-dialogs.md) | App-scoped Open/Save/Folder dialogs over the virtual filesystem. |
| [`dialogs.md`](dialogs.md) | System-wide basic dialogs (MessageBox, TextInput) and file dialog contracts. |
| [`icon-library.md`](icon-library.md) | Drawing icons (Bootstrap, Font Awesome, Lucide, Simple Icons, Material) from any app or the shell via `IIconCatalog`/`HackerIcon`. |
| [`platform-ui-library.md`](platform-ui-library.md) | Where MudBlazor is (and isn't) allowed, and the wrapper conventions. |
| [`design-system.md`](design-system.md) | Visual tokens and design-system specification. |
| [`theming.md`](theming.md) | Shared theme catalog, runtime scope, semantic tokens, persistence, and the checklist for adding a theme. |
| [`localization.md`](localization.md) | Globalization/localization architecture jamais réalisée ; remplacée par l'ADR 0104 et [`../localization.md`](../../framework/localization.md). |
| [`accessibility.md`](accessibility.md) | WCAG 2.2 AA checklist and accessibility evidence. |
| [`Human-test-needed-p2-GATE-003.md`](Human-test-needed-p2-GATE-003.md) | Manual accessibility and screenshot checks still required for gate `P2-GATE-003`. |
| [`samples/service-app.md`](samples/service-app.md) | Worked example of a non-visual background service app. |

## Working on the HackerOS platform core

For contributors (or agents) implementing platform/infrastructure behavior
itself — filesystem, settings, security, window runtime, storage.

| Doc | What it's for |
| --- | --- |
| [`virtual-filesystem.md`](virtual-filesystem.md) | The Linux-like, browser-independent filesystem contract. |
| [`settings-system.md`](settings-system.md) | Canonical settings documents and their `.config`-style projections. |
| [`policy-system.md`](policy-system.md) | Deny-by-default capability grants and structured resource constraints. |
| [`session-and-process-lifecycle.md`](session-and-process-lifecycle.md) | Deterministic sessions, processes, cancellation, clock, and resource simulation. |
| [`browser-storage.md`](browser-storage.md) | IndexedDB persistence overview for users, settings, filesystem, and more. |
| [`indexeddb-migrations.md`](indexeddb-migrations.md) | The versioned, C#-owned IndexedDB schema migration chain. |
| [`indexeddb-filesystem.md`](indexeddb-filesystem.md) | Filesystem persistence specifics over IndexedDB. |
| [`indexeddb-settings-persistence.md`](indexeddb-settings-persistence.md) | Settings persistence specifics over IndexedDB. |
| [`indexeddb-operational-records.md`](indexeddb-operational-records.md) | Diagnostics and audit record persistence. |
| [`indexeddb-backup-restore.md`](indexeddb-backup-restore.md) | Snapshot export and validated restore of browser storage. |
| [`indexeddb-recovery-contract.md`](indexeddb-recovery-contract.md) | Renderer-independent failure/recovery states for browser storage. |
| [`indexeddb-browser-contract-tests.md`](indexeddb-browser-contract-tests.md) | Real-Chromium proof for the IndexedDB contracts above. |
| [`window-runtime.md`](window-runtime.md) | The C# owner of window identity, geometry, and lifecycle. |
| [`desktop-shell.md`](desktop-shell.md) | Desktop workspace, taskbar, launcher, and notifications. |
| [`start-menu.md`](start-menu.md) | Windows 7-inspired Start menu, quick links, and ordered per-user application pins. |
| [`Global-FileView-And-MessagingSystem.md`](Global-FileView-And-MessagingSystem.md) | Design entry point for the reusable `FileView` control and the topic-based messaging/filesystem-watch extension to `IEventBus`; see the linked `FileViewControl.md`, `MessagingSystem.md`, and `integrationPlan.md`. |
| [`ecosystem-host.md`](ecosystem-host.md) | The standalone `HackerOs.Ecosystem` PWA composition root. |
| [`lazy-loading.md`](lazy-loading.md) | Build-known lazy assembly loading architecture. |
| [`startup-performance.md`](startup-performance.md) | The WASM startup path and its performance budget. |
| [`web-crypto-password-hasher.md`](web-crypto-password-hasher.md) | Hardware-accelerated PBKDF2 hashing via the Web Crypto API. |
| [`login-progress-screen.md`](login-progress-screen.md) | Session-startup progress feedback UI. |
| [`terminal-full-screen.md`](terminal-full-screen.md) | The full-screen/alternate-screen contract used by Nano and similar commands. |
| [`code-editor.md`](code-editor.md) | Multi-tab source editing over the virtual filesystem. |
| [`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md) | Proposal (French, not implemented): hybrid client/server C# compilation and execution for user-authored ecosystem apps. |
| [`web-project-type-plan.md`](web-project-type-plan.md) | Proposal (French, not implemented): a "Web" project type (hosted ASP.NET/Blazor, WebAssembly or Server). |
| [`system-monitor.md`](system-monitor.md) | Process/CPU/memory monitoring architecture. |
| [`mobile-interface-platform-plan.md`](mobile-interface-platform-plan.md) | Mobile interface platform plan (French). |
| [`window-taskbar-export-plan.md`](window-taskbar-export-plan.md) | Window/taskbar system extraction plan (French). |
| [`ecosystem-maturity-gap-analysis.md`](ecosystem-maturity-gap-analysis.md) | Analysis (French, exploratory): what's missing to call HackerOS a real WebAssembly ecosystem, beyond the tracked phase plan. |

## Built-in apps and commands (reference)

Documentation of the first-party apps/commands shipped today, useful as
worked examples of the two sections above.

| Doc | What it's for |
| --- | --- |
| [`apps/terminal.md`](apps/terminal.md) | The Terminal window app. |
| [`apps/file-explorer.md`](apps/file-explorer.md) | The File Explorer window app. |
| [`apps/text-editor.md`](apps/text-editor.md) | The Text Editor window app. |
| [`apps/diagnostic-app.md`](apps/diagnostic-app.md) | The IndexedDB diagnostic/inspector app. |
| [`apps/icon-viewer.md`](apps/icon-viewer.md) | The Icon Viewer app: browse/search/copy every bundled icon. |
| [`commands/terminal-commands.md`](commands/terminal-commands.md) | The core `pwd`/`ls`/`cd`/`cat`/`echo` command apps. |

## Optional server

For work on the backend process described in
[`hosting-model.md`](hosting-model.md#3-serverhackerosserver--the-optional-backend-and-a-third-ui-host).

| Doc | What it's for |
| --- | --- |
| [`server-security.md`](server-security.md) | The server's implemented security boundary and open evidence gaps. |
| [`server-backup-restore.md`](server-backup-restore.md) | SQLite snapshot backup/restore for the optional server. |
| [`server-implementation-pass.md`](server-implementation-pass.md) | Standing roadmap of every remaining client-server integration pass (sync per domain, direct injection); read before starting new server-integration work. |

## Architecture Decision Records

Accepted decisions, in order. Each stays as originally accepted; a later
change in direction gets a **new** ADR that references the one it supersedes
rather than an edit to the old one (see the doc-maintenance rule in
[`AGENTS.md`](../../../../../AGENTS.md)).

| ADR | Decision |
| --- | --- |
| [0001](../adr/0001-target-dotnet-10.md) | Target .NET 10 |
| [0002](../adr/0002-authority-comes-from-policy.md) | Authority comes from trusted policy, never a manifest's self-claim |
| [0003](../adr/0003-exact-capability-matching.md) | Capabilities match exactly, case-sensitively |
| [0004](../adr/0004-settings-files-are-projections.md) | Settings files are canonical projections, not a second source of truth |
| [0005](../adr/0005-deterministic-app-dependency-order.md) | Deterministic app dependency/activation order |
| [0006](../adr/0006-seal-window-component-lifecycle.md) | Seal the window component lifecycle |
| [0007](../adr/0007-enforce-collocated-razor-assets.md) | Enforce collocated Razor assets; ban inline CSS/JS |
| [0008](../adr/0008-virtual-filesystem-model.md) | Virtual filesystem model |
| [0009](../adr/0009-window-runtime-strategy.md) | Purpose-built window runtime |
| [0010](../adr/0010-manifest-json-and-schema.md) | Canonical manifest JSON and schema evolution |
| [0011](../adr/0011-settings-scope-layout.md) | Settings scope keys and projection paths |
| [0012](../adr/0012-process-and-clock-model.md) | Deterministic process, clock, and resource model |
| [0013](../adr/0013-local-user-session.md) | Local user and session model |
| [0014](../adr/0014-shell-grammar-boundary.md) | First-slice shell grammar boundary |
| [0015](../adr/0015-browser-storage-and-indexeddb-adapter.md) | Browser support baseline and IndexedDB adapter approach |
| [0016](../adr/0016-platform-ui-library.md) | Platform UI library boundary (MudBlazor) |
| [0017](../adr/0017-pwa-cache-and-offline-strategy.md) | PWA caching, offline strategy, and version migration |
| [0018](../adr/0018-indexeddb-failure-and-recovery-policy.md) | IndexedDB failure and recovery policy |
| [0019](../adr/0019-sdk-versioning-and-compatibility.md) | App SDK versioning, compatibility, and deprecation policy |
| [0020](../adr/0020-editor-framework-and-script-sandbox.md) | Code editor framework and script sandbox policy |
| [0021](../adr/0021-simulated-network-and-browser-rendering.md) | Simulated network contracts and browser rendering model |
| [0022](../adr/0022-multi-monitor-requirement-position.md) | Multi-monitor requirement position |
| [0023](../adr/0023-optional-game-domain-and-proxy-fallback.md) | Optional Game Domain integration and network proxy fallback (remplacé par l'[ADR 0092](../adr/0092-game-in-a-separate-repository.md) : le jeu vit dans un autre dépôt) |
| [0024](../adr/0024-server-identity-and-device-registration.md) | Server identity and device registration |
| [0025](../adr/0025-record-synchronization-envelope-and-conflict-model.md) | Record synchronization envelope, conflict model, and cursor strategy |
| [0026](../adr/0026-icon-library-support.md) | Shared icon library support (Bootstrap, Font Awesome, Lucide, Simple Icons) |
| [0027](../adr/0027-server-hosted-blazor-ui.md) | Server-hosted Blazor UI (third host, single-tenant phase) — remplacé par l'[ADR 0130](../adr/0130-server-serves-webassembly-os-and-api.md) |
| [0028](../adr/0028-client-side-server-connection.md) | Client-side optional-server connection and proxy bridge |
| [0029](../adr/0029-settings-sync.md) | Settings domain sync (first client sync implementation) |
| [0030](../adr/0030-filesystem-sync.md) | FileSystem domain sync |
| [0031](../adr/0031-grants-sync.md) | Grants domain sync (pull-only) |
| [0032](../adr/0032-app-enablement-management.md) | App enablement management |
| [0033](../adr/0033-appcatalog-and-fileassociations-sync.md) | AppCatalog and FileAssociations domain sync |
| [0034](../adr/0034-wire-terminal-command-catalog.md) | Wire the terminal command catalog |
| [0035](../adr/0035-nmap-tcp-probe-fallback.md) | Single-port TCP reachability probe for nmap's real-network fallback |
| [0036](../adr/0036-direct-service-injection-for-server-host.md) | Direct service injection for the server-hosted UI — remplacé par l'[ADR 0130](../adr/0130-server-serves-webassembly-os-and-api.md) |
| [0037](../adr/0037-reusable-file-view-control.md) | `FileView` as the canonical file-listing control |
| [0038](../adr/0038-emitter-authorized-topic-messaging.md) | Emitter-authorized topic messaging |
| [0039](../adr/0039-directory-open-file-association.md) | `inode/directory` media-type convention for directory file association |
| [0040](../adr/0040-declared-topic-permissions.md) | Declared topic permissions and optional per-channel access (tri-state `SharedChannelPolicy`) |
| [0041](../adr/0041-shared-theme-catalog-and-scoped-tokens.md) | Shared framework-neutral theme catalog and inherited Razor-library token scope |

## Migration history and project status

Background and record-keeping for the legacy-to-WASM migration itself, not a
live backlog. Per `AGENTS.md`, `implementation-status.md` (linked above under
Start Here) is the authoritative current state; everything in this section is
historical context for how the project got there, and the actual path taken
has diverged from the original plan in places.

| Doc | What it's for |
| --- | --- |
| [`integration-task-list.md`](integration-task-list.md) | The original exhaustive execution plan, kept as background/history — see the caveat above. |
| [`progress-and-plan-2026-08-17.md`](progress-and-plan-2026-08-17.md) | Progress snapshot and prioritized action plan as of 2026-08-17 (French). |
| [`integration-audit-remediation.md`](integration-audit-remediation.md) | Remediation handoff from the 2026-08-03 integration audit. |
| [`phase-2-acceptance.md`](phase-2-acceptance.md) | Phase 2 acceptance and exit-gate evidence. |
| [`migration/rules.md`](migration/rules.md) | The standard operating procedure used for porting each legacy feature. |
| [`migration/wave-2.md`](migration/wave-2.md) | Wave 2 — OS fundamentals migration report. |
| [`migration/wave-3.md`](migration/wave-3.md) | Wave 3 — editing, clipboard, and drag/drop migration report. |
| [`migration/wave-4.md`](migration/wave-4.md) | Wave 4 — simulated network, browser, and websites migration report. |
| [`migration/wave-5.md`](migration/wave-5.md) | Wave 5 — remaining utility apps and commands migration report. |
| [`pwa-release.md`](pwa-release.md) | PWA release and offline strategy write-up. |
