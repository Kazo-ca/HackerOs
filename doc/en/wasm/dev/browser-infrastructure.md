> [Version française](../../../fr/wasm/dev/browser-infrastructure.md)

# Browser infrastructure: `HackerOs.Infrastructure.Browser`

## Purpose

`Infrastructure/HackerOs.Infrastructure.Browser` is the only layer that
touches the browser: IndexedDB persistence (ADR 0015), recovery after
a storage failure (ADR 0018), host clipboard, encryption through
WebCrypto, cross-origin isolation, Web Workers. The rest of the OS never calls
`IJSRuntime` directly: it goes through the contracts of
`Shared/HackerOs.Simulation.Abstractions` that this layer implements.

The platform (`Platform/HackerOs.Platform.Core`) does not depend on this project
([`platform-core.md`](platform-core.md)). It is `AddHackerOsEcosystem`
(`OS/HackerOs.Ecosystem.Host`) that registers the browser
implementations, in the host's composition.

## What is where

| Folder | Content |
|---|---|
| `Schema/` | `HackerOsIndexedDbSchema` (database name, version, stores and indexes), schema model, content storage policy (`FileContentStoragePolicy`). |
| `FileSystem/` | File system provider (`IndexedDbFileSystemProvider`), reader and writer, mutation planner, seeding, maintenance, chunked content. |
| `Sessions/`, `Policy/`, `Catalog/`, `Settings/`, `ServerConnection/`, `Sync/`, `WebData/` | One IndexedDB repository per domain (users, groups, grants, app catalog, legacy settings, server connection, sync cursors and states, ASP.Hack site databases). |
| `Diagnostics/` | Persisted diagnostic and audit log, storage inspector. |
| `Backup/` | Storage backup and restore (`IndexedDbBackupService`). |
| `Recovery/` | `BrowserStorageRecoveryClassifier`: translates a browser error into a recovery state (ADR 0018, `StorageRecoveryState`). |
| `Storage/` | `BrowserStorageManager`: quota estimation, persistence request, low-space thresholds. |
| `Interop/` | `IndexedDbInteropAdapter` (JavaScript calls), `WebCryptoPasswordHasher`, host clipboard channel. |
| `Isolation/` | `BrowserCrossOriginIsolation`: COOP/COEP state (ADR 0131, 0146). |
| `Workers/` | `BrowserWebWorkerRuntime`: Web Workers runtime on the browser side ([`worker-processes.md`](worker-processes.md)). |
| `Globalization/` | Language satellites loaded by the browser. |
| `wwwroot/` | JavaScript scripts served under `_content/HackerOs.Infrastructure.Browser/`: `indexedDb.js`, `storageManager.js`, `webSiteDatabases.js`, `cryptoHasher.js`, `hostClipboard.js`, `crossOriginIsolation.js`, `sharedRing.js` (shared ring of the workers' pipes), `workerHost.js` and `workerBootstrap.js`. |

## IndexedDB database

- Name: `hackeros`. Current version: **4** (`HackerOsIndexedDbSchema.CurrentVersion`).
  Changing the version is the only way to add, remove or modify a
  store or an index; each upgrade has its migration step.
- Main stores: `users`, `groups`, `sessions`, `settings`, `fsEntries`
  (metadata: files, folders, links), `fsLinks`, `fsContent` (chunks),
  `catalog`, `grants`, `audit`, `diagnostics`, `syncMetadata`, `serverConnection`,
  `syncCursors`, `syncRecordState`, and one database per ASP.Hack site (`hackweb.<appId>`).
- File content: split into chunks addressed by SHA-256, of at most 256 KiB
  by default (`FileContentStoragePolicy.DefaultMaxChunkSizeBytes`); an identical chunk
  is stored only once.

## Recovery

`BrowserStorageRecoveryClassifier` sorts a failure into one of the states of
`StorageRecoveryState`: storage unavailable, quota exhausted, migration
failed, invalid content. The context (`StorageRecoveryContext`) distinguishes
startup (boot) from an ordinary operation. A destructive reset
always goes through an explicit confirmation (`AwaitDestructiveConfirmation`).
The rule: never turn an operational failure into silent data loss
(ADR 0018). The PWA cache recovery procedure is in
[`pwa-cache-recovery.md`](pwa-cache-recovery.md); the machine backup
in [`system-backup.md`](system-backup.md).

## Points to know

- Metadata mutations are ordered batches of IndexedDB operations,
  built by `IndexedDbFileSystemMutationPlanner` to be atomic. Content
  chunks are addressed by hash; those that are no longer referenced
  are collected by a bounded pass, after a retention period
  (`OrphanRetention`), and never without revalidating their references.
- A file's path is resolved in one request (`resolvePath`, ADR 0090,
  [`filesystem-path-lookup.md`](filesystem-path-lookup.md)).
- The `wwwroot/*.js` scripts are contract-tested by
  `Tests/HackerOs.Infrastructure.Browser.Tests` (Node tests for `sharedRing.js`)
  and `Tests/HackerOs.E2E.Tests` (`IndexedDbBrowserContractTests`, in a real browser).
- The old IndexedDB migration notes (versions, resumption, recovery
  contract) are archived in [`old/`](old/); they describe the earlier
  state and are not authoritative for the current schema.

## Links

- Decisions: [ADR 0015](adr/0015-browser-storage-and-indexeddb-adapter.md), [ADR 0018](adr/0018-indexeddb-failure-and-recovery-policy.md), [ADR 0099](adr/0099-os-backup-and-low-storage.md), [ADR 0131](adr/0131-cross-origin-isolation.md).
- File system: [`virtual-proc-and-dev.md`](virtual-proc-and-dev.md), [`../framework/hack-file-api.md`](../framework/hack-file-api.md).
- Host and build: [`building-and-testing.md`](building-and-testing.md).
