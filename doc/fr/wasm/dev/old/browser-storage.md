> [English version](../../../../en/wasm/dev/old/browser-storage.md)

# Browser Storage (IndexedDB)

## Purpose

Persist HackerOS state — users/sessions, canonical settings, the virtual
filesystem (metadata and binary content), capability grants, the app catalog,
and audit/diagnostic records — in the browser via IndexedDB, so the OS
survives a page reload without a server. This document tracks section 10
(`P2-IDB-*`) of `wasm2/HackerOs/docs/integration-task-list.md`.

## Status

`P2-IDB-001` through `P2-IDB-003` are complete. The browser support baseline and
IndexedDB adapter approach are decided in
[ADR 0015](../adr/0015-browser-storage-and-indexeddb-adapter.md) (`D-008`), and
the concrete database/object-store/index/transaction-boundary schema is
declared in
`Infrastructure/HackerOs.Infrastructure.Browser/Schema/HackerOsIndexedDbSchema.cs`.
The minimal JavaScript transaction module (`P2-IDB-004`) and the internal C#
interop adapter foundation for `P2-IDB-005` are implemented. Persistent C#
repositories have started with the local-group repository; migrations and the
remaining domain repositories remain `P2-IDB-006` through `P2-IDB-014`.

## Decisions in force (ADR 0015)

- **Supported browsers:** Chromium 89+, Firefox 90+, Safari 15+ (desktop and
  iOS), evergreen only. This is the oldest line with IndexedDB v2 and
  `navigator.storage.estimate()`/`persist()`.
- **Adapter approach:** hand-written, minimal, collocated static JS module(s)
  under `Infrastructure/HackerOs.Infrastructure.Browser/wwwroot/`, wrapping
  native IndexedDB with transaction-oriented, batched primitives. No
  third-party IndexedDB NuGet package. C# repositories in
  `HackerOs.Infrastructure.Browser` are the only callers of these modules and
  the only place storage-related `IJSRuntime` is injected.
- **Migration ownership:** schema version numbering and migration step
  ordering are owned by C# (`P2-IDB-006`), not embedded as JS
  `onupgradeneeded` business logic.
- **Known gap:** real-browser contract test automation (`P2-IDB-013`/
  `P2-IDB-014`) is Chromium-only via the existing Playwright tooling until
  Firefox/Safari automation is separately justified. Tracked as a known gap,
  not assumed equivalent.

## Database schema (`P2-IDB-002`, schema version 2)

Declared in `HackerOsIndexedDbSchema` (database name `hackeros`, schema
version `2`). Version 1 creates all 12 object stores below; version 2 adds the
`fsEntries.contentHash` index used for safe orphan-content reference checks.
database is first opened; changing any of them requires bumping
`CurrentVersion` and adding a migration step (`P2-IDB-006`).

| Store | Key path | Auto-increment | Indexes | Purpose |
| --- | --- | --- | --- | --- |
| `users` | `id` | no | `loginName` (unique) | One record per `LocalUser` account. |
| `groups` | `id` | no | `name` (unique) | One record per local group. |
| `sessions` | `id` | no | `userId` | One record per local session. |
| `settings` | `id` (composite `SettingsDocumentKey` string) | no | `scope`; `scope`+`appId` | One record per canonical settings document. |
| `fsEntries` | `id` | no | `ownerId` | Filesystem entry metadata (file/directory/symlink), no directory structure. |
| `fsLinks` | `parentId`+`name` (compound) | no | `entryId` | `FileSystemDirectoryEntry` parent→child-name links. |
| `fsContent` | `contentHash`+`chunkIndex` (compound) | no | `contentHash` | Deduplicated SHA-256-addressed content chunks defined by `P2-IDB-003` (`D-009`). |
| `catalog` | `appId` | no | `enabledFlag` (0/1, not boolean — IndexedDB index keys exclude booleans) | One record per installed app (manifest + enablement). |
| `grants` | `id` | no | `appId`+`userId`+`capability`; `userId` | One record per immutable `CapabilityGrant`. |
| `audit` | `id` | yes | `timestampUtcMs` (epoch ms number); `action` | Append-only `AuditEntry` records. |
| `diagnostics` | `id` | yes | `timestampUtcMs`; `severity` | Bounded `DiagnosticEntry` records; eviction is a repository concern. |
| `syncMetadata` | `key` | no | — | Small **local** bookkeeping values (policy revision, installation ID). Not multi-device sync. |

### Transaction boundaries

Each named boundary lists the object stores committed together in one
IndexedDB transaction; a repository implementation must never span more
stores than its boundary declares:

- `UserAccountWrite` — `users` alone.
- `GroupWrite` — `groups` alone.
- `SessionLifecycle` — `sessions` alone.
- `SettingsDocumentWrite` — `settings` alone (one document + revision bump).
- `LegacySettingsRead` — `settings` alone, read-only `getAll` for the one-time settings-to-files migration (ADR 0044).
- `FileSystemMetadataMutation` — `fsEntries` + `fsLinks` together; content is
  excluded (see `FileSystemContentWrite`).
- `FileSystemContentWrite` — `fsContent` alone; ordering against the metadata
  transaction is a repository-level concern (`P2-IDB-008`).
- `CatalogEnablementChange` — `catalog` alone.
- `PolicyGrantMutation` — `grants` + `audit` + `syncMetadata` together
  (grant/revoke, canonical audit entry, and policy revision).
- `AuditAppend` — `audit` alone, for audit entries not covered by
  `PolicyGrantMutation`.
- `DiagnosticsAppend` — `diagnostics` alone.
- `LocalBookkeepingUpdate` — `syncMetadata` alone.
- `BackupRestore` — all 12 stores for a consistent read snapshot or one atomic
  validated restore.

## Architecture (planned, partially declared)

```text
HackerOs.Simulation.Abstractions / HackerOs.App.Abstractions   (repository contracts)
  <- HackerOs.Infrastructure.Browser                            (schema + future C# repositories)
       -> wwwroot/*.js                                          (future: raw IndexedDB primitives)
```

App code and Platform Core business logic depend only on the existing
repository interfaces; only `HackerOs.Infrastructure.Browser` references
IndexedDB or browser-storage `IJSRuntime`. The schema declaration itself has
no `IJSRuntime` or repository-interface dependency yet.

## JavaScript transaction adapter (`P2-IDB-004`)

The Razor SDK publishes `wwwroot/indexedDb.js` as
`_content/HackerOs.Infrastructure.Browser/indexedDb.js`. It exports three
functions, intended only for the C# infrastructure adapter introduced by
`P2-IDB-005`:

- `openDatabase(databaseName, version, migrationPlan)` opens or reuses a cached
  connection. During `onupgradeneeded`, it executes a declarative C#-supplied
  plan containing object-store and index create/delete operations. It closes
  stale cached versions and responds to `versionchange` so another tab can
  upgrade.
- `executeTransaction(databaseName, version, objectStoreNames, mode,
  operations)` executes an ordered operation batch in one `readonly` or
  `readwrite` transaction. It supports `get`, `getAll`, `getAllKeys`, `count`,
  `put`, `add`, `delete`, and `clear`, optionally through an index for reads.
  Results preserve operation order. An operation naming a store outside the
  declared transaction boundary aborts the batch.
- `deleteDatabase(databaseName)` closes cached connections before deletion. It
  is reserved for explicit recovery, validated restore, and browser tests; it
  is not an ordinary repository operation.

The module contains no domain rules, schema constants, retention policy, or
repository-specific serialization. One C# interop call carries a complete
batch, avoiding per-record JS interop. Real IndexedDB behavior, rollback, and
multi-tab conflicts remain completion evidence for `P2-IDB-013` and
`P2-IDB-014`.

## Internal C# adapter (`P2-IDB-005`, partial)

`Interop/IndexedDbInteropAdapter.cs` is internal to
`HackerOs.Infrastructure.Browser`; neither app projects nor shared abstractions
can receive `IJSRuntime`, `IJSObjectReference`, or this adapter. It:

- lazily imports the static module once per adapter scope;
- opens only the canonical database name/version with a C#-owned declarative
  migration plan;
- accepts a named `HackerOsIndexedDbSchema` transaction boundary rather than an
  arbitrary store set;
- rejects operations targeting stores outside that boundary before JS interop;
- sends an ordered operation batch in one call and returns positional
  `JsonElement` results;
- propagates cancellation and tolerates normal JS disconnection during scope
  disposal.

Unit tests use fake `IJSRuntime`/`IJSObjectReference` implementations, so this
boundary remains browser-free. `P2-IDB-013` is now covered by the independent
Blazor WASM and Playwright harness documented in
[`indexeddb-browser-contract-tests.md`](indexeddb-browser-contract-tests.md).
`P2-IDB-014` now adds reload, rollback, migration, quota, backup/restore,
cleanup, and multi-tab conflict evidence in installed Chromium.

`Sessions/IndexedDbLocalGroupRepository.cs` is the first concrete persistent
repository. It serializes canonical group IDs/names, commits creation through
`GroupWrite`, maps IndexedDB uniqueness failures to a domain-facing operation
failure, and returns `null` for absent IDs. Its tests exercise the repository
through a fake JS module rather than bypassing the interop boundary.

`P-007` is resolved: persistence repositories use cancellation-aware `ValueTask`
contracts. In-memory implementations may complete synchronously, while browser
implementations acknowledge mutations only after IndexedDB transaction commit.
Lookups return nullable records for absence. Sync-over-async and unacknowledged
write-behind are prohibited.

## Storage quota and retention (`P2-IDB-010`)

`Storage/BrowserStorageManager.cs` imports the isolated `storageManager.js`
module and exposes cancellation-aware status and persistence-request methods.
Status reports usage, quota, non-negative available bytes, whether durable
retention is already granted, and a deterministic low-space flag. The approved
low-space policy triggers when available space is below either 10 percent of
the granted quota or 64 MiB; equality does not trigger it.

The browser may deny `navigator.storage.persist()` without treating that denial
as an exceptional failure, so `RequestPersistenceAsync` returns the granted
boolean. Unsupported StorageManager APIs remain an explicit browser
compatibility failure rather than fabricated estimates.

IndexedDB transaction failures whose DOM exception is `QuotaExceededError` are
translated centrally to `BrowserStorageQuotaException`. The exception states
that the transaction did not commit and preserves the original `JSException`.
It never deletes data, retries by silently evicting durable records, or invokes
filesystem cleanup; recovery UI may offer the existing explicit bounded cleanup
and future export actions under ADR 0018.

## Decisions in force (`P2-IDB-003`, `D-009`)

The browser-storage content policy is now declared in
`Infrastructure/HackerOs.Infrastructure.Browser/Schema/FileContentStoragePolicy.cs`.
It chooses a conservative, browser-safe policy for the provisional `fsContent`
store:

- Maximum file size: 16 MiB.
- Maximum chunk size: 256 KiB.
- Hash algorithm: SHA-256.
- Chunk deduplication: enabled by hash.
- Orphaned content retention: 30 days before cleanup consideration.
- Cleanup trigger: one bounded pass during deterministic host startup plus the
  same API exposed to explicit recovery/quota maintenance. Deletion atomically
  rechecks `fsEntries.contentHash`; undated v1 chunks are retained.

The policy is intentionally simple and deterministic so the later repository and
migration work can implement it without guessing about chunk boundaries or
content identity semantics.

## Next tasks

Complete `P2-IDB-005` with persistent C# repositories behind the approved
asynchronous boundary. The versioned migration chain for `P2-IDB-006` is
implemented through schema version 2; real-browser rollback evidence remains.
`P-012` is resolved: settings definitions now carry their canonical structured
key while path-based APIs remain projections. `P2-IDB-007` can therefore
implement atomic compare/write persistence without inferring ownership from a
path.

`D-010` is accepted in ADR 0018. Recovery is non-destructive first: no storage
failure or migration error may automatically delete the database. Export is
offered before destructive repair, restore is validated with explicit merge or
replace semantics, and replacement/reset requires user confirmation. The
renderer-independent states, actions, boot-blocking rules, stable error codes,
and exact `REPLACE`/`RESET` confirmation are implemented and documented in
[`indexeddb-recovery-contract.md`](indexeddb-recovery-contract.md). Host UI
rendering and real-browser recovery evidence remain separate tasks.

## References

- [ADR 0015](../adr/0015-browser-storage-and-indexeddb-adapter.md)
- [ADR 0018](../adr/0018-indexeddb-failure-and-recovery-policy.md)
- [IndexedDB migrations](indexeddb-migrations.md)
- [IndexedDB backup and restore](indexeddb-backup-restore.md)
- `Infrastructure/HackerOs.Infrastructure.Browser/Schema/HackerOsIndexedDbSchema.cs`
- `Tests/HackerOs.Infrastructure.Browser.Tests/HackerOsIndexedDbSchemaTests.cs`
- `wasm2/HackerOs/docs/integration-task-list.md` section 10
