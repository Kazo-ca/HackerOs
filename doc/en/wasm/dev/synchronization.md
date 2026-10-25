> [Version française](../../../fr/wasm/dev/synchronization.md)

# Synchronization between devices

Work item `v3/9-sync`, [ADR 0101](adr/0101-synchronized-deletions-and-conflicts.md)
(completes ADR [0025](adr/0025-record-synchronization-envelope-and-conflict-model.md)
and [0030](adr/0030-filesystem-sync.md)).

## Goal

When the OS is connected to the optional server, the user's home folder, file
associations and grants travel between their devices:

- deletions propagate (tombstones) and arrive in the other device's trash, so
  they are recoverable;
- a conflict is never settled on the user's behalf: a dialog immediately asks
  which version to keep, and the file stays frozen until they answer;
- sync runs by itself, every 5 minutes, on reconnection and 10 seconds after a
  change;
- without a server, nothing changes: the Ecosystem never depends on the server.

## Architecture

| Role | File |
| --- | --- |
| Home folder sync (push, pull, conflicts, resolution) | `Platform/HackerOs.Platform.Core/ServerConnection/IFileSystemSyncService.cs` (`FileSystemSyncService`) |
| Operations on the files of the sync (write, move, trash) | `ServerConnection/SyncFileOperations.cs` |
| Conflict contracts and `sync-conflicts.json` file | `ServerConnection/SyncConflicts.cs` |
| Periodic sync | `ServerConnection/SyncScheduler.cs` |
| State of the conflict dialog | `ServerConnection/SyncConflictCenter.cs` |
| `sync.config` settings | `ServerConnection/SyncSettingsDocuments.cs` |
| File associations (with tombstone) | `ServerConnection/IFileAssociationsSyncService.cs` |
| Record states and cursors (IndexedDB, schema v4) | `Infrastructure/HackerOs.Infrastructure.Browser/Sync/IndexedDbSyncRecordStateRepository.cs`, `IndexedDbSyncCursorRepository.cs` |
| OS conflict dialog | `Platform/HackerOs.Platform.Blazor/Sync/SyncConflictOverlay.razor` (in `DesktopShell` and `MobileShell`) |
| Synchronization tab | `Apps/System/HackerOs.Apps.Settings/SettingsWindow.razor` |
| SDK | `Shared/HackerOs.AppSdk/Administration/HackServerConnection.cs`, `ServerConnectionContracts.cs` |
| Automatic connection to the server that serves the OS, status icon ([`server-hosting.md`](server-hosting.md)) | `ServerConnection/ServerPresenceService.cs`, `HostInfoClient.cs`, `Platform/HackerOs.Platform.Blazor/Shell/ServerStatusIndicator.razor` |
| Tombstone purge (server) | `Server/HackerOs.Server/Services/SyncService.cs` (`PurgeExpiredTombstonesAsync`), `SyncTombstonePurgeService.cs` |

### State tracked per record

For each entry already synchronized, `SyncRecordTrackingState` keeps the
revision, the hash of the local description, the relative path, the local
entry's identifier when it differs from the record's (entry created by a pull),
whether it is a tombstone, and the user. This is what makes it possible to
recognize a deletion: a tracked entry, of this user, absent from the folder at
push time, leaves as a tombstone. An entry never synchronized leaves none.
States written before ADR 0101 (without a user) are completed at the next push
and never produce a tombstone.

The tracked hash of a folder ignores its modification dates (they change with
every addition to the folder): a new file does not send all of its parent
folders.

### Deletions (ADR 0101 §1)

- Moving to the trash is a rename to `~/.local/share/Trash`; emptying the trash
  produces the tombstones.
- A **received** tombstone moves the local entry to the trash (never a
  permanent deletion), with two extension keys in the `.trashinfo`:

  ```ini
  [Trash Info]
  Path=/home/alice/note.txt
  DeletionDate=2026-10-17T08:00:00
  X-HackerOS-Origin=deleted-on-another-device
  X-HackerOS-Device=<identifiant de l'appareil qui a supprimé>
  ```

  These copies are specific to the device: they are never sent back
  (pseudo-domain `filesystem.local-trash` in the state store). An entry already
  in the trash is permanently deleted.
- Each day the server purges tombstones older than 90 days (all rows of the
  record, if the tombstone is still its latest revision; never the row with the
  highest sequence).
- A device whose last pull is more than 90 days old restarts without a cursor
  and compares everything: a tracked record that the server no longer knows is
  treated as a received deletion.
- If the root of the home folder cannot be listed, the push stops: an
  unreadable folder is never mistaken for an empty folder.

### Same path created on two devices

Each device creates the standard folders on first connection, each with its own
identifiers. When two records target the same path, the one with the smallest
identifier keeps it, the same rule on each device: the local entry is tracked
under the winning record, and the loser leaves as a tombstone, which the
devices that adopted the winner ignore. Two **different** files created at the
same path become a conflict.

### Conflicts (ADR 0101 §2)

A conflict arises when:

- the server refuses a push (`CONCURRENT_EDIT`);
- a pull brings a new version of a file modified locally since the last sync,
  or the deletion of a file modified locally;
- two different files were created at the same path.

Pending conflicts are in `~/.local/state/hackeros/sync-conflicts.json` (local
state, never synchronized), so they are kept after a reload. As long as a file
is in it, it is neither sent nor replaced; the other device's version is only
retained for the dialog. A conflict resolves itself when both sides deleted the
file or have the same content.

| Choice | Effect |
| --- | --- |
| Keep mine | The content is sent, then `ClientWins` with the local description (file deleted here: `ServerWins` then the tombstone leaves at the next push). |
| Keep the other device's | `ServerWins`, the local version goes to the trash, the server's version is written. |
| Keep both | The local version is copied to `<nom>.sync-conflict-<AAAAMMJJ-HHMMSS>-<appareil>.<ext>` (first 8 hexadecimal digits of the device, number added if the name is taken), sent as a new file; the server's version takes the original path. |

The "Sync conflict" dialog opens by itself on a new conflict and at connection
if any remain. Each file shows the date, size and device of both versions, and
on request a text preview (2,000 characters at most, UTF-8 without a null byte;
remote content of at most 1 MiB). With several files, an "Apply to all" row
appears. "Later" (or Esc) closes the dialog, leaves the files frozen and sends
a notification that points to **Settings → Synchronization**. The choices that
need the remote version stay disabled until it is received. The dialog follows
the session language (ADR 0104): the labels quoted are those of an English
session ("Keep mine", "Keep both", "Later"...), and the date and size (B/KiB/MiB)
follow its `LC_TIME` culture.

### Periodic sync (ADR 0101 §4)

`SyncScheduler` (one per session) starts with the shell, at connection, and
stops at disconnection. A pass does: push and pull of the home folder, pull of
the grants, push and pull of the associations. It is launched:

- every `intervalMinutes` minutes (5 by default, between 1 minute and 1 day);
- on reconnection to the server;
- 10 seconds after the last change to a synchronized folder (subscription to
  the `FileSystemTopics.ForDirectory` topics, refreshed after each pass); the
  sync's own writes are ignored;
- by hand ("Sync now"), even when sync is suspended.

Offline, it retries every 30 seconds without writing anything more. Each pass
writes a line to `/var/log/syslog` (sent, received, conflicts), plus a line
when the server becomes reachable again or a pass fails.

```ini
# /etc/hackeros/sync.config (administrateur ; l'intervalle n'est lu que là)
intervalMinutes = 5

# ~/.config/hackeros/sync.config (utilisateur)
paused = true
```

### Domains

| Domain | Client | Note |
| --- | --- | --- |
| `filesystem` | `FileSystemSyncService` | `/home/{user}` except `~/.local/state`. |
| `file_associations` | `FileAssociationsSyncService` | Tombstone if the file is deleted; a received tombstone restores the default associations. |
| `grants` | `GrantsSyncService` | Pull only; the server is authoritative. |
| `app_catalog`, `settings` | none | The server keeps the domains; the OS does not use them (ADR 0101 §3, ADR 0044). |

## Usage / API

SDK (`HackServerConnection`, dangerous capability `server.connection.manage`):

| Method | Effect |
| --- | --- |
| `GetConflictsAsync()` | Pending conflicts (`HackSyncConflict(Domain, Path, DetectedAt)`). |
| `ShowConflictsAsync()` | Opens the OS conflict dialog. |
| `GetSyncStatusAsync()` | `HackSyncStatus`: active, suspended, online, last pass, sent, received, last error, interval. |
| `SetSyncPausedAsync(bool)` | Writes `paused` in the user layer; a resume launches a pass. |
| `SyncNowAsync()` | One full pass right away. |
| `SyncAsync(domaine, sens)` | Unchanged: one domain in one direction. |

Platform (`IFileSystemSyncService`): `PushAsync` and `PullAsync` return a
`SyncPassResult(Sent, Received, Conflicts)`; `GetConflictsAsync`,
`GetConflictPreviewAsync`, `ResolveConflictAsync(id, SyncConflictChoice)`, the
`ConflictsChanged` event, `ListSynchronizedDirectoriesAsync`.

Command: `trash list` adds "(deleted on another device)" to the items trashed
by the sync; `HackTrashItem.DeletedOnAnotherDevice` tells apps.

## Implementation decisions

- Sync starts from the shell's conflict dialog, the only component that exists
  for the whole connected session.
- Entries created by a pull receive a new local identifier; the link with the
  record goes through `LocalEntryId`.
- After a pull, the tracked hash is that of the local description of the
  result, and a pull ignores already known revisions: a received file is never
  sent back.
- Pushes are sent in batches of 100 records (server limit).
- The interval is read only from `/etc`, like a root scheduled task that a user
  cannot change; `paused` follows the layers (an administrator can lock it).

## Known limits

- The pull cursor and the time of the last pull are shared by all users of the
  same device (IndexedDB store without a user); the record states, however,
  carry the user.
- No `app_catalog` client (intended, ADR 0101 §3).
- A pass launched during another waits for the first to finish (one pass at a
  time).

## Tests

- `Tests/HackerOs.Platform.Core.Tests/ServerConnection/FileSystemSyncDeletionAndConflictTests.cs`:
  two devices and an in-memory server with the same rules as the real one
  (propagated deletion, received tombstone moved to the trash, entry never
  synchronized, rename, emptied trash, frozen file and resume after reload,
  preview, three choices, modified here and deleted there, automatic
  resolution, same path created on both sides, offline for more than 90 days,
  name of the conflict copy).
- `FileSystemSyncServiceTests.cs`, `FileAssociationsSyncServiceTests.cs`
  (tombstone sent once, never for a file never synchronized, received
  tombstone that restores the defaults).
- `Tests/HackerOs.Server.Tests/SyncServiceTests.cs`: purge after 90 days, row
  with the highest sequence kept.
- `Tests/HackerOs.AppSdk.Tests/IO/HackTrashTests.cs`: origin read from the
  `.trashinfo`.

## Task list

- [x] States tracked per record (path, local entry, tombstone, user) and time of the last pull.
- [x] Tombstones sent and received (home folder, associations).
- [x] Received tombstone moved to the trash, marked "deleted on another device".
- [x] Server purge after 90 days and full sync after 90 days offline.
- [x] Conflicts recorded, file frozen, automatic resolution when there is nothing to choose.
- [x] Three choices (`ClientWins`, `ServerWins`, `.sync-conflict-…` copy).
- [x] Same path created on two devices.
- [x] OS conflict dialog, "Apply to all", "Later" and notification.
- [x] Periodic sync, `sync.config`, `/var/log/syslog` journal.
- [x] Synchronization tab in Settings and SDK API.
- [x] Client and server tests.
