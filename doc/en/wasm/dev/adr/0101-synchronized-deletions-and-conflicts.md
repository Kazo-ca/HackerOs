> [Version française](../../../../fr/wasm/dev/adr/0101-synchronized-deletions-and-conflicts.md)

# ADR 0101: synchronized deletions, user-resolved conflicts, periodic sync and remaining sync domains

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work item `v3/9-sync`).
§2 follows Benoit's answer of 2026-10-17: ask the user right away
which version to keep.

- **Complements:** [ADR 0025](0025-record-synchronization-envelope-and-conflict-model.md)
  (envelope, tombstones, conflict table) and
  [ADR 0030](0030-filesystem-sync.md) (sync of `/home/{user}`).
- **Replaces:** the AppCatalog part of [ADR 0033](0033-appcatalog-and-fileassociations-sync.md)
  (§3 below). ADR 0029 (settings sync) is already replaced by
  ADR 0044.
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  line `v3/9-sync`; gap R18; deferred decision no. 7.

## Context

- The server handles tombstones (`SyncService.cs:141`) and conflict
  resolution (`SyncService.cs:162`). On the client side, `FileSystemSyncService` and
  `FileAssociationsSyncService` always send `IsTombstone: false`
  (`IFileSystemSyncService.cs:119`, `IFileAssociationsSyncService.cs:73`): a
  local deletion is never propagated.
- `ISyncClient.ResolveConflictAsync` exists but nothing calls it. Conflicts
  are only kept in memory (`UnresolvedConflicts`) and lost on
  reload.
- Sync only starts by hand (`AppServerConnectionGateway.SyncAsync`).
  No timer.
- There is no client for the `app_catalog` domain; there has been no `settings`
  client since ADR 0044.

## Decision

### 1. Deletions

- For each entry already synced, the client keeps its record state
  (IndexedDB state store, schema version 4). When such an entry is
  deleted, it pushes an envelope `IsTombstone: true` with the next
  revision. An entry that was never synced leaves no tombstone.
- Moving a file to the trash is a move into
  `~/.local/share/Trash` (ADR 0052), hence a synchronized rename, not a
  deletion. Emptying the trash produces the tombstones.
- A **received** tombstone does not delete permanently: the local file
  is moved to the user's trash, with the origin "deleted on another
  device" in its `.trashinfo`. The user can therefore recover it.
- The server keeps a tombstone for **90 days**. A device that stayed offline
  longer performs a full sync (hash comparison) instead of a
  cursor sync; this is the offline history depth of deferred
  decision no. 7.
- `FileAssociationsSyncService` also pushes a tombstone if
  `/etc/hackeros/file-associations.json` is deleted.

### 2. Conflicts: the user chooses right away

Benoit's choice of 2026-10-17 ("Ask right away").

- When a sync finds a conflict on a file (modified on both sides,
  or modified here and deleted on the other device), the OS **opens a dialog
  right away** for the user concerned, if they are logged in, otherwise at their
  next login. The dialog shows the path, the date and the device of
  each version, their size, and for a text file a preview of both.
- Three choices per file:
  - **Keep mine**: `ResolveConflictAsync(ClientWins)`; the local version
    is sent.
  - **Keep the other device's**: `ServerWins`; the local version goes to
    the trash (ADR 0052), so it can be recovered.
  - **Keep both**: the server version takes the original path and the local
    version is renamed `<name>.sync-conflict-<YYYYMMDD-HHMMSS>-<device>.<ext>`
    in the same folder, then sent as a new file.
- Several conflicts that arrive together are in a single dialog, with one choice
  per file and "Apply to all".
- **The file stays frozen** until the answer: it keeps its local version,
  remains editable on this device, but is neither sent nor replaced by
  sync. "Later" closes the dialog without choosing; the conflict stays
  pending, signaled by a notification and listed in **Settings →
  Synchronization**, and the dialog returns at the next login.
- Pending conflicts are saved in
  `~/.local/state/hackeros/sync-conflicts.json` (local state, never synced,
  ADR 0044), so they are kept after a reload.
- The domains where the server is authoritative (grants, ADR 0025) resolve
  themselves as `ServerWins`, without asking anything.

### 3. Remaining domains

- **`filesystem`**: `/home/{user}` except `~/.local/state` (ADR 0030, 0044).
  The user's settings (`~/.config`) and their choice of uninstalled apps
  (`~/.config/hackeros/apps.config`, ADR 0048) travel through this domain, like
  any file.
- **`file_associations`** and **`grants`**: unchanged, with the
  tombstones.
- **`app_catalog`**: **no client**. The user's choice is already in
  `~/.config` (see above), and the machine state (`/etc/hackeros/apps.config`)
  remains specific to each device, like the packages installed on two Linux
  machines. The server keeps the domain (no contract break) but the OS does not
  use it. This replaces the AppCatalog part of ADR 0033 and the "client
  for the AppCatalog domain" of the plan.
- **`settings`**: no client since ADR 0044; the server keeps the
  domain for the same reason.

### 4. Periodic sync

- When the OS is connected to the server, a platform service syncs
  every **5 minutes**, at each reconnection, and 10 seconds after the last
  modification of a synced file (grouping of writes).
- The interval is set in `/etc/hackeros/sync.config` (administrator);
  the user can pause their sync in `~/.config/hackeros/sync.config`.
- Offline or without a server, the service does nothing and the OS works as
  before (the Ecosystem never depends on the server).
- Each pass is written to `/var/log/syslog` (domains, sent, received,
  conflicts).

## Consequences

- `v3/9` modifies the two client sync services, adds the periodic
  service, the conflict dialog, the Settings page,
  the saving of conflicts and client and server
  tests (propagated deletion, received tombstone moved to the
  trash, conflict dialog and its three choices, file frozen until
  there is an answer, resume after reload, device offline more than
  90 days).
- The server purges tombstones older than 90 days.
- The server connection and sync docs (`system-administration.md`,
  `trash.md`, `settings-files.md`) are updated.
