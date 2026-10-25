# ADR 0099: OS backup and restore, low space and `ENOSPC`, corrupted PWA cache, tested IndexedDB version upgrades

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work item `v3/7-storage-backup`).

- **Complements:** ADR 0017 (PWA cache and offline), ADR 0018 (IndexedDB
  failures and recovery), [ADR 0094](0094-deferred-decisions-status-and-maximum-file-size.md)
  (16 MiB per file).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  line `v3/7-storage-backup`; gaps R13, R14, R15; deferred decision no. 6.

## Context

- `IndexedDbBackupService` (`Infrastructure/HackerOs.Infrastructure.Browser/Backup/`)
  exports the whole database as one JSON (`formatVersion: 1`, schema version,
  stores, SHA-256) and restores it as a merge or a replacement. It is tested
  but registered nowhere.
- `BrowserStorageManager.IsLowSpace` (less than 64 MiB or 10% free, according to
  `navigator.storage.estimate`) is computed and never used.
  `FileSystemErrorCode` has no "no space left" code.
- The service worker (`service-worker.published.js`) does not detect a corrupted
  cache entry; the PWA test even checks that the corrupted bytes are
  served as is.
- The IndexedDB schema is at version 4 (migrations v1 to v4), with no data from
  earlier versions to test the version upgrade.

## Decision

### 1. Backup: an archive that leaves the OS

- A backup contains the **whole** database: files of all users,
  `/etc`, accounts and password hashes. It is therefore restricted to the
  administrator (elevation of ADR 0042), like a system backup.
- The archive is the existing `formatVersion: 1` JSON, compressed with gzip
  (`GZipStream`), extension **`.hosbak`**. Restore also accepts the
  uncompressed JSON.
- The archive is **downloaded to the host machine**, not written to the OS
  file system: an OS archive quickly exceeds the 16 MiB per
  file limit (ADR 0094) and a copy inside the backed-up database would make
  no sense (it is the equivalent of a backup to an external disk). The
  plan provided for an "archive in `~`"; this limit prevents it.
- Access:
  - **Settings → System → Backup**: "Create a backup" (download)
    and "Restore…" (host file picker), with the choice of merge or
    replace;
  - **`hackbackup` command**: `sudo hackbackup create` triggers the
    download; `sudo hackbackup restore [--merge|--replace]` opens the
    host picker (pressing Enter counts as a user gesture
    for the browser; if the browser refuses, the command points to
    Settings).
- The download and the picker go through the shared JS module of the Blazor
  SDK (`download.js` and its counterpart for opening), the only API that C# cannot
  reach.
- **Replace**: the OS closes all sessions, restores, then reloads the
  page (restart). **Merge**: the archive's records overwrite those
  with the same key, the rest is kept.
- An archive from another schema version is rejected with a clear message
  (current behavior of the service). A creation or a restore is
  logged in `/var/log/auth.log` (who, when, mode).

### 2. Low space and no space left

- **Alert threshold**: when `IsLowSpace` becomes true, the OS displays a single
  notification per session ("Low storage space") that opens Diagnostics;
  `df` shows the real size, used and free space of the browser storage
  for `/`.
- **Blocks reserved for root**, like ext4: when the free space falls below **5% of
  the quota** (at most 256 MiB), writes by a non-administrator user
  fail with `ENOSPC`; those of the system and the administrator (logs,
  `/etc` settings, backup) continue until the real limit.
- **No space left**: `FileSystemErrorCode.NoSpace` ("No space left on
  device", `ENOSPC`) is added, along with `FileTooLarge` (`EFBIG`, ADR 0094). An
  IndexedDB `QuotaExceededError` becomes `NoSpace`; the write is cancelled
  entirely (transaction), never half written. `HackFile`/`HackStream` throw
  `HackIOException` with this code, and the shell displays the Linux message.
- When `LogFileWriter` can no longer write, it drops the lines and keeps
  a count, written when space returns first ("N lines lost").

### 3. Corrupted PWA cache

- Blazor checks the integrity of its startup resources. If startup
  fails on an integrity or loading error, the page asks the service
  worker to **clear its cache and reload**, only once (guard in
  `sessionStorage`); online, the resources are fetched again from the server.
- Offline, the startup error screen explains that the cache is damaged and
  that you need to reconnect once.
- The PWA test that checked the delivery of corrupted bytes is replaced by a
  recovery test. A quota test limits the origin's storage
  (Chromium's `Storage.overrideQuotaForOrigin`) and checks the notification,
  `ENOSPC` and that system writes keep working.

### 4. Tested IndexedDB version upgrades

- `Tests/fixtures/indexeddb/` receives a database dump at versions 1, 2 and 3,
  produced by starting the OS from a commit of each version (the procedure is
  written in `building-and-testing.md`).
- An E2E test creates the database at the old version with this data, starts the current
  OS and checks that the upgrade to version 4 keeps files, accounts and
  settings.
- Rule: any work item that increases `HackerOsIndexedDbSchema.CurrentVersion`
  adds the dump of the previous version.

## Consequences

- `v3/7` registers `IndexedDbBackupService` in the Ecosystem host (and the test
  host), adds the Settings page, `hackbackup`, the `NoSpace` and
  `FileTooLarge` codes, the root reserve, cache recovery and the tests.
- As long as the Server host renders the interface on the server side (ADR 0027), it has no
  IndexedDB and the backup page is hidden there. When it serves the same OS
  in WebAssembly as the Ecosystem (Benoit's choice of 2026-10-17, work item
  `exec/8`), backup will work there as in the Ecosystem, with nothing
  specific to the server.
- `sdk-reference.md` (error codes), `linux-commands.md`, `system-administration.md`
  and `building-and-testing.md` are updated.
