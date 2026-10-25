> [Version française](../../../../fr/wasm/dev/adr/0110-backup-and-storage-implementation-adjustments.md)

# ADR 0110: implementation adjustments for backup, storage space and IndexedDB fixtures

## Status

Accepted on 2026-10-17 (work item `v3/7-storage-backup`).

- **Complements:** [ADR 0099](0099-os-backup-and-low-storage.md). Replaces a
  sentence of its §4 (version 1 fixture).
- Number: `0107` (`v3/5`) and `0108` (`v3/10`, renumbered after a
  collision) are taken; this work item takes `0110` to leave `0109` to a
  parallel work item and avoid another collision.

## Context

The implementation of ADR 0099 ran into four points that the ADR does not
settle, or that it describes in a way that cannot be implemented.

## Decision

1. **No version 1 fixture.** Version 1 of the IndexedDB schema never
   existed in the repository history: the first commit (`fdca68e`)
   is already at version 2. `Tests/fixtures/indexeddb/` therefore contains
   versions 2 (`e46e897`) and 3 (`7a20401`), produced by starting the OS from these
   commits. The step towards version 1 of the migration plan remains covered:
   it runs at every startup on an empty database. The rule "any work item
   that increases `CurrentVersion` adds the dump of the previous version"
   applies from version 2 on and is checked by a test without a
   browser (`IndexedDbUpgradeFixtureCatalogTests`).
2. **A merge restarts too.** The ADR describes the restart for a
   replace. A merge also closes the session and reloads the page: the running OS
   keeps in memory paths, settings and accounts that the restore
   has just changed underneath it.
3. **The audit entry of a restore waits outside the database.** A
   replace overwrites `/var/log/auth.log` itself. The OS keeps the entry in
   the host's `localStorage` during the restore and writes it to
   `auth.log` at the next startup, with the result (`success` or
   `failure:<reason>`).
4. **`hackbackup` requires `sudo`**: without root, the command stops
   (`must be run as root`) instead of opening the graphical administrator
   prompt, like a system tool. The file picker opens
   **before** the administrator prompt, because the browser only opens it
   right after a user gesture.

## Consequences

- [`system-backup.md`](../system-backup.md), [`building-and-testing.md`](../building-and-testing.md)
  and [`pwa-cache-recovery.md`](../pwa-cache-recovery.md) describe the actual
  behavior.
- No other ADR changes.
