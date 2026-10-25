# ADR 0029: Settings Domain Sync (First Client Sync Implementation)

## Status

Accepted on 2026-08-14.

## Context

`docs/server-implementation-pass.md` (introduced by ADR 0028) names "Pass N+1: Sync — Settings domain" as the next server-integration step: push/pull local settings documents to the optional server so an offline session that reconnects, or the same profile used from a second computer, converges. The server side already exists and works (`ISyncService`/`SyncRecordEntity`, ADR 0025), but no client anywhere in the repo has ever called `/api/sync/*` — this is the first sync implementation for any domain.

Reading the actual settings persistence contracts and the server's `SyncService` implementation (including its test suite, the only worked examples of well-formed pull/push sequences) surfaced the concrete mechanics this ADR needs to settle:

- `ISettingsDocumentService` has no enumeration method — reads/writes are by known `VirtualPath` only (`Shared/HackerOs.Simulation.Abstractions/SettingsContracts.cs`). The only source of "what documents exist" is the `IEnumerable<SettingsDocumentDefinition>` catalog `AddHackerOsEcosystem` already injects.
- A settings document's local `Revision` (`IndexedDbSettingsDocumentService.WriteAsync`'s optimistic-concurrency CAS, starting at 1) and a `SyncRecordEnvelope`'s `Revision` (the server's per-record monotonic counter — `SyncService.PushAsync` rejects a push whose `Revision` does not strictly exceed the last accepted value, confirmed by `SyncServiceTests.Push_ConcurrentEdit_ReturnsConflict`) are unrelated counters serving different purposes and must not be conflated.
- **`docs/settings-system.md` states "Only roaming scope is sync-eligible by default; device scope never roams," but this is unenforced prose** — `SettingsDocumentDefinition` has no field implementing it, and none of the three documents registered today (`PolicySettingsDocuments`, `FileAssociationSettingsDocuments`, `AppearanceSettingsDocuments`) are actually `AppRoamingUser` scope; all three are `OsAdmin`. Taken literally, zero documents would be sync-eligible.
- `PayloadJson` (the opaque string every `SyncRecordEnvelope` carries) has no defined shape for the Settings domain anywhere in code or docs — the server never interprets it, by design, so a client is free to choose the shape, but something has to choose it.

## Decision

### 1. Sync eligibility: explicit opt-in, not scope-inferred

Add `bool SyncEligible = false` to `SettingsDocumentDefinition`. A document syncs if and only if this flag is `true`. This replaces `docs/settings-system.md`'s unenforced scope-based claim with an actual mechanism, corrected in the same change. Scope remains meaningful for authorization (who can read/write a document at all) but no longer implies or blocks sync eligibility on its own.

### 2. First document: `AppearanceSettingsDocuments` only

Opts in for this pass (`SyncEligible: true`) — user-visible accent/animation preference, `User`/`User` authority, no security sensitivity. `PolicySettingsDocuments` and `FileAssociationSettingsDocuments` stay opted out: they're Administrator-authority documents, higher-stakes, and can opt in once this first pass is proven correct in production use.

### 3. `RecordId`: deterministic, never stored

A stable `Guid` derived by hashing each document's existing composite `SettingsDocumentKey` string (the same string already used as the IndexedDB primary key — see `IndexedDbSettingsDocumentService.FormatKey`) — first 16 bytes of its SHA-256 digest, interpreted as a `Guid`. Every device computes the identical `RecordId` for the identical document key with no separate mapping table to keep consistent across devices. Pull-side matching is simply "does this envelope's `RecordId` equal one I can compute right now from my own injected catalog."

### 4. `PayloadJson`: raw document content, unwrapped

No extra JSON envelope around it — a settings document's `Content` is already `.config` or JSON text, and the server never interprets `PayloadJson` regardless of domain. Wrapping it in another JSON layer would only add encoding overhead for no benefit.

### 5. Sync engine tracks its own state, generically, reusable by future passes

Two new IndexedDB stores (schema version 4), deliberately domain-agnostic so Pass N+2/N+3/N+4 (FileSystem, Grants, AppCatalog+FileAssociations) reuse them unchanged:
- `syncCursors`, keyed by `domain` — one opaque pull cursor per `SyncDomain` string.
- `syncRecordState`, keyed by `domain|recordId` — last-synced sync-`Revision` and `ContentHash` per record, independent of the document's own local optimistic-concurrency revision.

`ISyncCursorRepository`/`ISyncRecordStateRepository` (browser-independent interfaces, IndexedDB-backed implementations) are the only new persistence contracts; a future sync pass for another domain writes a new domain-specific adapter against the same two repositories and the same `ISyncClient`, not new storage.

### 6. Conflict resolution default: automatic pull-and-apply-server

Not surfaced to the user in this pass. Settings sync is low-stakes (a preference, not irreplaceable user data), and ADR 0025's `Merge` resolution needs document-specific merge logic that doesn't exist yet for any document. On a push conflict, the engine pulls the server's current version and applies it locally instead of the client's attempted write. This is an explicit simplification, recorded rather than silently chosen — manual conflict surfacing is future work once a document exists where silently preferring the server's copy would be the wrong default.

### 7. Trigger: on-connect once, plus manual "Sync now"

Push then pull runs once immediately after `IServerConnectionService.ConnectWithNewAccountAsync`/`ConnectWithExistingAccountAsync` succeeds, and again on demand from a "Sync now" button in the same Settings connection panel ADR 0028 added. No periodic background timer in this pass — keeps scope bounded; trivial to add once the manual/on-connect path is proven correct.

## Consequences

- Settings sync is real and provable end-to-end for one document, without touching the higher-stakes Policy/FileAssociations documents yet.
- The two new IndexedDB stores and `ISyncClient` are generic — the next four sync passes (tracked in `docs/server-implementation-pass.md`) reuse this scaffolding rather than rebuilding it.
- Conflicts are resolved silently in the server's favor; a future pass introducing sync for a document where that's unacceptable (e.g. Grants, already server-authoritative per ADR 0025 so this is moot there, or a hypothetical user-authored document) needs its own ADR for surfaced conflict resolution before opting in.
- No periodic sync exists yet — a device that stays connected but never revisits Settings or reconnects won't pick up remote changes automatically. Named as a known gap, not silently assumed solved.
- `docs/settings-system.md`'s scope-based sync-eligibility claim is corrected to describe the actual `SyncEligible` field.

## References

- ADR 0025: Record Synchronization Envelope, Conflict Model, and Cursor Strategy (the contract this pass implements a client against)
- ADR 0028: Client-Side Optional-Server Connection and Proxy Bridge (the connection foundation this pass builds on)
- `docs/settings-system.md`
- `docs/server-implementation-pass.md`
