# ADR 0025: Record Synchronization Envelope, Conflict Model, and Cursor Strategy

**Status:** Accepted  
**Date:** 2026-08-03  
**Supersedes:** None  
**Superseded by:** None  
**Related decisions:** D-018 (Per-domain sync conflict algorithms)

## Context

The optional sync engine must:
1. Transfer versioned records between devices without silent overwrites.
2. Surface all conflicts explicitly; the client must resolve them intentionally.
3. Protect security-sensitive data (grants, policy) from client-driven downgrade.
4. Support incremental pull with resumable cursors.
5. Handle idempotent push retries safely.

## Decision

### Record Envelope
Every synchronized piece of data is wrapped in `SyncRecordEnvelope`:
- `RecordId` — stable UUID assigned at creation; never changes across devices.
- `Domain` — discriminator identifying the data type (`settings`, `filesystem`, `grants`, `app_catalog`, `file_associations`).
- `SchemaVersion` — allows rolling schema upgrades per domain.
- `Revision` — monotonically increasing per-record counter; the server detects concurrency when the submitted revision ≤ the current server revision.
- `ModifiedUtc` — originating device timestamp (informational; server uses its own `ServerReceivedUtc` for ordering).
- `ContentHash` — SHA-256 hex of the serialized payload; verified on push.
- `IsTombstone` — logical deletion; payload may be null for tombstones.

### Domain-Specific Conflict Rules
| Domain | Conflict strategy |
|---|---|
| `settings` | Explicit conflict → client must choose ClientWins, ServerWins, or Merge |
| `filesystem` | Same as settings; Merge allowed |
| `grants` | **Server-authoritative**: ClientWins and Merge are blocked; only ServerWins accepted |
| `app_catalog` | ClientWins preferred (enablement flags are device-local opinion) |
| `file_associations` | Same as settings |

**Grant tombstones are always blocked.** Clients cannot delete grant records through sync; revocation must go through the authorized grant API.

### Pull Cursor Strategy
- Cursors are **opaque Base64-encoded server sequence numbers**.
- Clients treat them as position bookmarks and never parse them.
- `null` cursor → full refresh from the beginning.
- The server returns `HasMore = true` when the batch is truncated; clients page until `HasMore = false`.

### Idempotency
- Push requests carry a client-generated `IdempotencyKey` (UUID).
- The server caches the response for 5 minutes; duplicate pushes return `AlreadyProcessed`.

### File Content Transfer
- File metadata (path, permissions, timestamps) syncs as a regular `SyncRecord` in the `filesystem` domain.
- File content transfers separately through a chunked, resumable upload/download protocol keyed on SHA-256 content hash.
- Identical content across devices shares one server-side blob (deduplication by hash).

## Consequences

- **Positive:** All conflicts are explicit and intentional; no silent overwrite.
- **Positive:** Grants can never be weakened through sync.
- **Positive:** Resumable pull and content transfer survive network interruptions.
- **Negative:** Clients must implement conflict resolution UI for settings and filesystem conflicts.
- **Mitigation:** The browser client will provide a simple diff-based resolution dialog.

## Rejected Alternatives

- **Last-write-wins:** Silent data loss; unacceptable for any security-sensitive domain.
- **CRDT per field:** Too complex for the first version; deferred to a future ADR if needed.
- **Server-authoritative for all domains:** Too restrictive for user preference data (settings) that is legitimately different per device.
