# ADR 0030: FileSystem Domain Sync

## Status

Accepted on 2026-08-15.

## Context

`docs/server-implementation-pass.md` names "Pass N+2: Sync — FileSystem domain" as the next step after ADR 0029 (Settings sync, the first working sync client). This is the largest of the five sync domains: unlike a settings document, a file has content that can be megabytes in size, and the server already has a separate, purpose-built chunked transfer protocol for that (`Server/HackerOs.Server.Contracts/Sync/ContentTransferContracts.cs`, `IContentBlobService`) rather than routing bytes through `SyncRecordEnvelope.PayloadJson` the way Settings does.

Research before writing this ADR (a full read of the filesystem contracts, the content-transfer contracts, and the server's content-blob implementation) found:

- **`FileSystemEntryId` is already a `Guid`** (`Shared/HackerOs.Simulation.Abstractions/FileSystem/FileSystemEntryId.cs`). Unlike Settings, which needed a derived hash for `RecordId` (ADR 0029 Decision 3), a file's own entry ID can be used directly as the sync `RecordId` — no mapping table needed.
- **No enumeration method exists for "every file a user owns."** `EnumerateAsync` returns one directory's immediate children only; a sync adapter has to walk recursively itself. `/home/{userId}` is already the code-enforced (not just documented) per-user root — `FileSystemSeeder.SeedAsync` creates it and `AuthenticatedPrincipal.HomePath` points at it — so it's the natural, bounded scope: it excludes system directories, other users' files, and `/etc/hackeros/*` settings documents (already synced through a different provider by ADR 0029, so syncing them a second time through FileSystem sync would be redundant and could race).
- **Metadata and content are two separate protocols, tied only by `ContentHash`**, exactly as ADR 0025 anticipated: file metadata (path, permissions, owner/group, timestamps) syncs as an ordinary `SyncRecordEnvelope` through `/api/sync/push`/`pull` — the `filesystem` domain is already a recognized `SyncDomain` constant and accepted by `SyncService.IsKnownDomain`, unused until this pass. File *content* moves through the separate chunked, content-addressed, deduplicated protocol (`ContentTransferContracts.cs`, `IContentBlobService`), keyed on SHA-256 hash. No code anywhere, client or server, previously bridged the two.
- **Blocker found and fixed as the first step of this pass**: `ContentBlobService.GetChunkAsync` was a stub that always returned `Array.Empty<byte>()` — the doc comment said "the full implementation would look up `blob.StoragePath` and stream the chunk," but nothing did. Upload worked end-to-end; download of actual bytes did not. `InitiateDownloadAsync` also never created a session-tracking row server-side (unlike upload), so a real fix had to decide how a download request maps to a byte range without one. Since content is content-addressed and immutable, the simplest correct fix needed no session state at all: `GetChunkAsync` now takes `contentHash` + `chunkIndex` directly, computes the byte range deterministically (`chunkIndex * DefaultChunkSizeBytes`, the same 256 KiB used for upload), and streams it from `ContentBlobEntity.StoragePath`. The route changed from `GET /api/sync/content/download/{sessionId}/chunks/{chunkIndex}` to `GET /api/sync/content/download/{contentHash}/chunks/{chunkIndex}` to match — the session ID was never functionally used. Covered by the first test coverage this service has ever had (`Tests/HackerOs.Server.Tests/ContentBlobServiceTests.cs`): upload-then-download round trip, dedup short-circuit, out-of-range/unknown-hash rejection, and assembled-content-hash-mismatch rejection.

## Decision

### 1. Sync scope: recursive walk of `/home/{userId}` only

Not the whole filesystem. Excludes system directories and other users, and avoids double-syncing settings already handled by ADR 0029 through a different domain/provider. Implemented as new adapter code calling `EnumerateAsync` recursively from the home root — no existing enumeration helper covers this.

### 2. `RecordId` = the file's own `FileSystemEntryId.Value`, not derived

A real `Guid`, used as-is. A `CopyAsync`-created file gets a fresh `FileSystemEntryId` and is therefore treated as a brand-new sync record (a fresh `syncRecordState` row) even though its content chunks are shared by hash with the source — this is correct: the sync layer's job is to track *entries*, and a copy is a new entry by the filesystem's own model, independent of whether its bytes happen to be deduplicated on the server.

### 3. Metadata payload carries everything needed to reconstruct the entry

`PayloadJson` is a small JSON DTO (`FileSystemSyncPayload`): path relative to `/home/{userId}` (reconstructed to an absolute path on pull), kind (file/directory/symlink), owner/group IDs, permission mode, timestamps, and for files `ContentHash`+`Length`, for symlinks the link target. Directories sync as zero-content metadata-only records. Unlike Settings (where the document's own content is directly usable as `PayloadJson`), file bytes never travel through `PayloadJson` — only through the separate content-transfer protocol.

### 4. Content transfer glue

**Push**: after building a file's metadata envelope, check whether the server already has its `ContentHash` via `InitiateContentUploadRequest`. If `AlreadyExists: true`, skip byte transfer entirely (dedup, zero-cost). Otherwise drive chunked `multipart/form-data` PUTs from local content chunks — local `fsContent` chunking already matches the server's `DefaultChunkSizeBytes` (256 KiB), so chunks forward as-is with no rechunking.

**Pull**: after applying a file's metadata envelope, check whether the local `fsContent` store already has that `ContentHash` (common — most synced files won't have changed). If not, drive chunked GETs against the now-fixed download endpoint and write the received bytes into local `fsContent`, then finalize the entry.

### 5. Conflict resolution: never auto-apply the server's copy on a push conflict

Deliberately not reusing ADR 0029 Decision 6. Losing file content is more consequential than reverting a preference. On a push conflict for a file, the adapter skips applying anything and records the conflict (logged, plus a visible "N files have unresolved sync conflicts" indicator in Settings) rather than silently overwriting either copy. Manual/automatic merge UI is future work — this pass's bar is "never silently lose data," not "fully resolve conflicts."

### 6. Symlinks and directories sync as metadata only

No content-transfer step for either — directories have no content, and a symlink's target travels as part of its metadata payload, not as file content.

## Consequences

- FileSystem sync is real for the bounded `/home/{userId}` scope, reusing ADR 0029's `syncCursors`/`syncRecordState` stores, `ISyncClient`, and push/pull/cursor-paging pattern without new scaffolding.
- The server-side content-download path is no longer fundamentally broken — this fix also unblocks any other future feature (not just sync) that needs to read previously-uploaded blob content back from the server.
- Conflicts are never silently resolved for this domain; a device with unresolved file conflicts must surface that state to the user rather than picking a winner automatically. No merge UI exists yet — a conflicted file simply stops syncing until a future pass adds resolution.
- System directories, other users, and `/etc/hackeros/*` are out of scope for this pass by design; a later pass would need its own ADR to sync anything outside `/home/{userId}`.
- `docs/server-implementation-pass.md` Pass N+2 moves to Done.

## References

- ADR 0025: Record Synchronization Envelope, Conflict Model, and Cursor Strategy (defines the `filesystem` `SyncDomain` and the `Merge`/conflict vocabulary this pass uses)
- ADR 0029: Settings Domain Sync (First Client Sync Implementation) (the `syncCursors`/`syncRecordState` scaffolding and `ISyncClient` this pass reuses, and the conflict-policy precedent this pass deliberately departs from)
- `docs/server-implementation-pass.md`
- `docs/indexeddb-filesystem.md` (the entries+links vs. content transaction-boundary split this pass preserves rather than reinventing)
