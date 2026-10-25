# IndexedDB Filesystem

## Purpose

Persist the canonical virtual filesystem in IndexedDB while preserving stable
entry identities, optimistic revisions, atomic metadata changes, and streamed
deduplicated content.

## Architecture

Metadata is split across `fsEntries` and `fsLinks`. A link uses the compound
`(parentId, name)` key and points to one stable entry ID. The `parentId` index
supports immediate-directory enumeration; `entryId` supports reverse lookup.

`IndexedDbFileSystemMutationPlanner` builds ordered operations for the named
`FileSystemMetadataMutation` boundary. JavaScript exposes only generic browser
primitives. C# remains responsible for paths, permissions, revisions, records,
and transaction plans.

Content belongs to the independent `FileSystemContentWrite` boundary. A future
provider write will stage deduplicated chunks first and then conditionally
commit metadata. Failed metadata commits may leave bounded orphan chunks for
the documented cleanup policy; committed metadata must never reference missing
content.

## Atomic Creation

Creation executes one read/write transaction in this order:

1. Assert the observed parent revision.
2. Replace the parent with incremented revision and timestamps.
3. Add the child entry record.
4. Add the parent/name link.

Any failed assertion or uniqueness constraint aborts the complete transaction.

## Persisted Entry Fields

Records retain entry ID, kind, owner, group, Unix permission mode, UTC epoch
timestamps, revision, logical length, symbolic-link target, content hash, and
content descriptor fields. Paths are reconstructed from directory links and are
not duplicated as entry identity.

The root directory uses one reserved stable entry ID and is inserted with
`addIfAbsent`. Existing root metadata wins during reload or concurrent boot.

## Metadata Reads

`IndexedDbFileSystemReader` resolves canonical paths from the stable root by
following compound `(parentId, name)` links. Since ADR 0090 the whole walk runs
inside one read-only transaction (the `resolvePath` operation of
`indexedDb.js`, see [`../filesystem-path-lookup.md`](../filesystem-path-lookup.md)). Missing links return absence;
links that reference missing entries are reported as persisted-data corruption.
Immediate-child enumeration uses the `parentId` index, loads child entries in
one ordered transaction batch, and returns names in ordinal order. Manual
`JsonElement` codecs keep this path compatible with WASM trimming.

`IndexedDbFileSystemProvider` exposes these reads through the shared provider
contract. Missing paths, cancellation, malformed persisted data, and operations
not yet migrated are translated to stable provider-neutral result codes.

New entries inherit the parent directory group. This preserves deterministic
Unix-like directory ownership without selecting an arbitrary group from the
actor's unordered memberships. The acting user remains the entry owner.

`CreateAsync` validates destination absence, parent kind, and expected parent
revision before executing the four-operation metadata batch. A coded aborting
precondition distinguishes revision conflicts from concurrent unique-key
conflicts while preserving transaction rollback.

`SetPermissionsAsync` atomically asserts the entry revision, replaces the Unix
mode, advances only the metadata-change timestamp, and increments the entry
revision. `MoveAsync` rewires the stable directory link, so a directory subtree
moves without rewriting descendant records. Same-parent renames update that
parent once; cross-parent moves assert and update both parents. Root moves and
moves into the source subtree are rejected before the transaction.

`DeleteAsync` protects the root and rejects non-recursive deletion of non-empty
directories. Recursive deletion traverses stable links without re-resolving
paths, captures every observed entry revision, and then atomically updates the
external parent while deleting descendant links and entries deepest-first. Any
concurrent subtree mutation changes an asserted directory revision and aborts
the complete transaction.

`CopyAsync` captures and asserts every source revision, assigns a fresh stable ID
and revision 1 to each copied entry, and creates the complete link tree in the
same metadata transaction as the destination-parent update. Immutable
`contentHash` references are retained, so copied files reuse deduplicated chunks
without copying blob data or coupling metadata atomicity to content transfer.

File writes consume the source stream once with incremental SHA-256 hashing and
256 KiB chunks, enforcing the 16 MiB browser limit before publication. Chunks
are inserted idempotently by compound `(contentHash, chunkIndex)` key, then a
separate optimistic metadata transaction publishes the hash, length, descriptor,
timestamps, and next revision. A failed metadata precondition can leave only an
unreferenced immutable chunk set, never partial visible file content. Reads load
chunks through the hash index, validate contiguous indexes, length, and SHA-256,
then return an owned readable stream matching the captured entry revision.

Orphan cleanup uses the approved shared trigger model: the host calls
`IndexedDbFileSystemMaintenance.InitializeAsync` during deterministic startup,
and recovery/quota UI calls `CleanupAsync` explicitly. Each pass examines at
most 64 retained hashes by default. Chunks become eligible after 30 days, but
deletion occurs in a transaction spanning `fsEntries` and `fsContent` that
rechecks the `contentHash` index before deleting every chunk for that hash.
Legacy chunks without `createdUtcMs` are retained rather than guessed old. The
API is implemented now; startup composition remains part of `P2-HOST-007`.

## Host Composition Boundary

Browser infrastructure exposes the persistent root provider and does not
depend on Platform Core. The composition root in `P2-HOST-003` will register it
as the root of `FileSystemMountRouter`, mount `SettingsFileSystemProvider` at
the canonical settings paths, and construct `FileSystemService`. It will then
run the existing `FileSystemSeeder`, whose create-if-missing behavior preserves
IDs and revisions across repeated clean-profile initialization.

This keeps projection semantics and Linux-like profile layout out of the
IndexedDB provider. `P2-HOST-007` owns deterministic invocation of root
bootstrap, profile seed, and startup orphan cleanup. Real-browser persistence
and rollback evidence remains tracked by `P2-IDB-014`.

## Task List

- [x] Add aborting revision preconditions for multi-record transactions.
- [x] Define stable entry and link records.
- [x] Plan atomic child creation and parent revision update.
- [x] Index links by parent for immediate-child enumeration.
- [x] Seed the stable root idempotently.
- [x] Implement internal path resolution and immediate-child enumeration.
- [x] Expose stat and enumeration through the browser provider.
- [x] Implement atomic create through the browser provider.
- [x] Implement atomic move/rename and permissions through the browser provider.
- [x] Implement atomic non-recursive and recursive delete.
- [x] Implement atomic recursive copy with deduplicated content references.
- [x] Implement bounded streamed hashing, deduplicated chunk writes, and verified reads.
- [x] Implement bounded 30-day orphan tracking and race-safe cleanup API.
- [x] Preserve projection routing through the existing mounted provider model.
- [x] Reuse the existing idempotent complete-profile seeder at host composition.
- [ ] Invoke bootstrap, profile seed, and startup cleanup in `P2-HOST-007`.
- [ ] Run real-browser reload/rollback tests in `P2-IDB-014`.