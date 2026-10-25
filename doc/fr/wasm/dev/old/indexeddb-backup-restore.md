# IndexedDB Backup and Restore

## Purpose

`IndexedDbBackupService` exports a consistent snapshot of all HackerOS browser
stores and restores a validated backup without silently deleting current data.
It implements `P2-IDB-011` under the non-destructive recovery policy in ADR
0018.

## Format Version 1

The backup is one portable JSON object containing:

- `formatVersion`: backup contract version, currently `1`;
- `databaseName`: canonical `hackeros` database identity;
- `databaseSchemaVersion`: exact IndexedDB schema version;
- `createdAtUtc`: ISO 8601 UTC export time;
- `stores`: exactly one record array for every declared object store;
- `sha256`: lowercase SHA-256 of the deterministic payload without this field.

Export reads every store through one read-only `BackupRestore` transaction, so
records cannot come from different commit points. Binary record properties use
the existing Blazor JSON interop representation; the service does not invent a
second filesystem content format.

## Validation

Restore validates the complete input before opening a write transaction:

- parseable object-shaped JSON and required typed envelope properties;
- exact backup format, database identity, and current schema version;
- exact store set with array-shaped records;
- scalar values for every declared simple or compound key path;
- unique record identity within each store;
- catalog `enabledFlag` domain invariant (`0` or `1`);
- matching SHA-256 integrity digest.

Malformed, incompatible, altered, or conflicting input throws
`IndexedDbBackupValidationException`. Validation never clears or mutates a
store. Cross-version restore requires a future explicitly supported backup
migration; it is not guessed from IndexedDB migrations.

## Restore Modes

`IndexedDbRestoreMode` is mandatory; there is no implicit default.

- `Merge` adds missing records. An identical existing record is accepted as
  idempotent. A different record with the same key raises
  `backup.merge-conflict` and aborts the whole transaction.
- `Replace` clears all stores and writes the validated snapshot in one global
  transaction. Calling UI must obtain the explicit confirmation required by
  ADR 0018 before invoking this mode.

Neither mode deletes the database, performs silent cleanup, or retries by
discarding durable records. Real-browser rollback and reload evidence remains
owned by `P2-IDB-014`.

## Task List

- [x] Approve versioned JSON plus SHA-256 backup format.
- [x] Export all stores from one consistent read transaction.
- [x] Validate envelope, schema, store set, identities, invariants, and digest.
- [x] Implement explicit atomic merge and replace plans.
- [x] Reject differing merge collisions without mutation.
- [x] Add browser-free focused tests.
- [ ] Prove transaction rollback and reload in real-browser automation.