# ADR 0018: IndexedDB Failure and Recovery Policy

## Status

Accepted on 2026-08-02 by explicit user approval (`D-010`).

## Context

HackerOS is local-first and IndexedDB is authoritative for ordinary offline use. Storage can become unavailable, exceed quota, contain invalid records, or fail during a schema migration. Recovery must preserve user data whenever possible and must never turn an operational failure into silent data loss.

This decision governs `P2-IDB-011`, `P2-IDB-012`, host recovery UI, and the failure scenarios exercised by `P2-IDB-014`. It does not define the backup file format or the visual implementation of the recovery surface.

## Decision

HackerOS uses a non-destructive-first recovery policy:

1. Storage failures never trigger automatic deletion, replacement, or reset of the IndexedDB database.
2. The system first attempts read-only diagnosis and non-destructive repair when the failure class permits it.
3. Before any destructive repair, replacement, or reset, HackerOS offers an export of all data that can still be read.
4. Restore validates format, schema compatibility, record identity, and domain invariants before changing current data.
5. Restore exposes explicit merge and replace modes. It never chooses replacement silently.
6. Replace and reset require explicit user confirmation that identifies the data affected.
7. A failed schema migration preserves the previously committed database version. HackerOS does not delete the database and retry automatically.
8. Quota exhaustion preserves committed data, reports the failed operation, and offers cleanup/export actions without silently evicting durable user content.
9. Recovery operations produce structured diagnostics suitable for support export, with sensitive values redacted.

## Recovery States

The host recovery contract distinguishes at least:

- storage unavailable or blocked;
- quota exhausted;
- migration failed with the previous version retained;
- database content invalid or partially unreadable;
- backup validation failed;
- recoverable operation conflict;
- destructive action awaiting confirmation.

Ordinary boot cannot report the OS as ready until the storage state is known. When safe read-only access remains possible, recovery UI should keep export available even if normal desktop startup is blocked.

## Consequences

- Recovery requires explicit user-facing choices and cannot be hidden inside repository retry logic.
- `deleteDatabase` remains restricted to confirmed reset, validated replace restore, and isolated browser tests.
- Backup/export must be implemented before destructive recovery can be considered complete.
- Browser tests must prove failed migrations and transactions retain the last committed state.
- Some corruption may be unrecoverable, but HackerOS still attempts export and presents the failure rather than erasing evidence.

## Rejected Alternatives

### Automatic reset after open or migration failure

Rejected because it converts an implementation or browser failure into irreversible user-data loss.

### Always replace during restore

Rejected because a valid backup can still be older or incomplete. Merge and replace have different consequences and require an explicit choice.

### Best-effort silent cleanup on quota exhaustion

Rejected for durable files, settings, grants, and catalog state. Domain-specific bounded diagnostic retention may evict records only under its documented retention policy.

## Validation

Completion evidence is provided by `P2-IDB-011` through `P2-IDB-014`: backup validation, merge/replace behavior, explicit confirmation, quota handling, failed-migration rollback, transaction rollback, reload persistence, and multi-tab conflicts in real browser automation.

## References

- `docs/integration-task-list.md` (`D-010`, `P2-IDB-011` through `P2-IDB-014`)
- `docs/browser-storage.md`
- `docs/indexeddb-migrations.md`
- ADR 0015: Browser Support Baseline and IndexedDB Adapter Approach
