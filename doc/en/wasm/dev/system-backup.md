> [Version française](../../../fr/wasm/dev/system-backup.md)

# Machine backup and storage space

This document covers the implementation of backup and storage space. What an app sees (the `HackSystemBackup` API, the `.hosbak` archive, the `NoSpace` and `FileTooLarge` codes) is in [`../framework/system-backup-api.md`](../framework/system-backup-api.md).

## Purpose

Work item `v3/7-storage-backup`, following [ADR 0099](adr/0099-os-backup-and-low-storage.md):

- **Backup and restore** of the whole machine (files of all users, `/etc`,
  accounts) in a `.hosbak` archive downloaded to the host computer, from
  Settings or the `hackbackup` command.
- **Low space**: one notification per session, `df` with the browser's real
  figures, a reserve for root like ext4's reserved blocks.
- **No more room**: `NoSpace` (`ENOSPC`) and `FileTooLarge` (`EFBIG`, 16 MiB
  per file, ADR 0094) codes, all the way to the shell's Linux message; logs
  count the lost lines.
- **Damaged PWA cache** and **tested IndexedDB version upgrades**: see
  [PWA cache](#damaged-pwa-cache-adr-0099-3) and
  [Version upgrades](#indexeddb-version-upgrades-adr-0099-4).

```text
alice@hackeros:~$ sudo hackbackup create
hackbackup: exporting the whole machine...
hackbackup: hackeros-20261017-071200Z.hosbak (1.2M, 3481 records) downloaded to the host computer
alice@hackeros:~$ mkdir projets   # le libre est passé sous la réserve de root
mkdir: cannot create directory 'projets': No space left on device
```

## Architecture

| Layer | Files |
|---|---|
| Simulation contracts | `Shared/HackerOs.Simulation.Abstractions/FileSystem/FileSystemResults.cs` (`NoSpace = 1800`, `FileTooLarge = 1801`), `Metrics/StorageSpaceContracts.cs` (`StorageCapacity`, `IStorageCapacityProbe`, `IStorageSpaceGuard`, `StorageSpacePolicy`), `Notifications/NotificationContracts.cs` (`NotificationAction.LaunchApp`) |
| SDK | `Shared/HackerOs.AppSdk/Administration/HackSystemBackup.cs`, `SystemBackupContracts.cs` (`IAppSystemBackupGateway`, `HackBackupRestoreMode`, `HackBackupInfo`, `HackBackupInvalidException`); `IO/HackFileSystemErrorTranslator.cs` and `ShellErrorText.cs` for the two codes |
| Blazor SDK | `Interop/BrowserFilePicker.cs` and `wwwroot/pickFile.js` (counterpart of `download.js` for opening a host file) |
| Platform Core | `Backup/SystemBackupService.cs` (`ISystemBackupDevice`, sequence), `Administration/AppSystemBackupGateway.cs`, `Storage/StorageSpaceMonitor.cs`, `FileSystem/FileSystemService.cs` (reserve), `Diagnostics/LogFileWriter.cs` (lost lines), `Power/SystemPowerService.cs` (`RestartAfterAsync`) |
| Infrastructure.Browser | `Backup/IndexedDbBackupService.cs` (gzip, `ReadArchive`, `Inspect`, merge by `put`), `Storage/BrowserStorageManager.cs` (`IStorageCapacityProbe`), `FileSystem/IndexedDbFileSystemProvider.cs` (`QuotaExceededError` → `NoSpace`), `IndexedDbFileContentRepository.cs` (16 MiB → `FileTooLarge`), `IndexedDbStorageUsageProvider.cs` (`df`) |
| Ecosystem host | `Administration/BrowserSystemBackupDevice.cs`, `EcosystemServiceCollectionExtensions.cs` (registrations), `EcosystemBootCoordinator.cs` (space monitoring, audit entry of a restore) |
| Apps | `Apps/System/HackerOs.Apps.Settings/BackupPanel.razor` (*Backup* tab), `Apps/Commands/HackerOs.Commands/Hackbackup/`; `Platform/HackerOs.Platform.Blazor/Shell/ClockPanel.razor` (notification action buttons) |

The three hosts call `AddHackerOsEcosystem`. Backup is wired only in a browser
(`OperatingSystem.IsBrowser()`): the Server host with server-side rendering has
no IndexedDB, `HackSystemBackup.IsSupported` is `false` there and the tab is
hidden (ADR 0099, consequences).

## Backup and restore (ADR 0099 §1)

The archive, the SDK API and the `system.backup` capability intended for apps are in [`../framework/system-backup-api.md`](../framework/system-backup-api.md). What follows describes the backup and restore sequence.

### `SystemBackupService` sequence

**Creation**: export, download, `system.backup.create` entry in
`/var/log/auth.log` (`admin`, `requestedBy`, `file`, `bytes`, `records`).

**Restore**, like a system backup tool:

1. host picker and full validation (nothing is stopped);
2. administrator request (`system.backup.restore`);
3. `SystemPowerService.RestartAfterAsync`: log flush, session close (each
   process is cancelled);
4. the restore entry is kept **outside the database** (`localStorage`, key
   `hackeros.backup.restore-record`), state `started`;
5. restore in one IndexedDB transaction: **merge** = `put` of each record of
   the archive (same key: overwritten; the rest is kept), **replace** = `clear`
   of each store then `put`;
6. the entry changes to `success` or `failure:<raison>`;
7. host restart (`location.reload`), even after a failure;
8. at the next startup, `EcosystemBootCoordinator` writes the kept entry to
   `/var/log/auth.log` (`system.backup.restore`, `mode`, `outcome`) then
   erases it.

### Settings and `hackbackup`

- **Settings → Backup** (tab visible only if the host can back up): "Create
  backup", *Merge* or *Replace* choice as cards, "Restore…", error message for
  a rejected archive.
- **`hackbackup create | restore [--merge|--replace]`**: must run as root
  (`sudo hackbackup …`), otherwise `must be run as root` and code 1, without
  opening the graphical request; default mode `--merge`; picker closed:
  `no file chosen`, code 1; browser that refuses the picker: pointer to
  Settings; invalid usage: code 2. The command runs on the main thread
  (`Execution = MainThread`) to keep the user's gesture.

## Storage space (ADR 0099 §2)

### Measurement and thresholds

`BrowserStorageManager` reads `navigator.storage.estimate()` and returns
`StorageCapacity(UsageBytes, QuotaBytes)` (`null` if the browser gives no
quota). `StorageSpacePolicy` sets the thresholds:

| Threshold | Value |
|---|---|
| Low space (`IsLow`) | less than 64 MiB **or** 10 % of the quota free |
| Root reserve (`ReserveBytes`) | 5 % of the quota, at most 256 MiB |

`StorageSpaceMonitor` (scoped service, started at boot) measures every 30 s,
and shortly after a persistent write that requests it
(`IStorageSpaceGuard.RequestRefresh`, at most one measurement every 5 s). A
write never waits for it: `FileSystemService` only reads the cached state
`IsRootReserveReached`.

### Notification

When space becomes low, the session's user receives **one** notification per
session (a new login warns again): "Low storage space", warning severity,
action "Open Diagnostic". Notification actions of the form `launch:<appId>`
(`NotificationAction.LaunchApp`) are buttons in the clock panel that launch the
app then close the notification.

### Root reserve

The write refusal an app sees when space drops below the root reserve is described in [`../framework/system-backup-api.md`](../framework/system-backup-api.md).

The `NoSpace` and `FileTooLarge` codes as seen by apps, with their message and the list of their origins, are in [`../framework/system-backup-api.md`](../framework/system-backup-api.md). The browser-side origins appear in the architecture table above.

### `df`

`/` now shows the browser's **real** used space and quota
(`IndexedDbStorageUsageProvider`): everything the origin stores, not only the
file contents. With no known quota, `df` falls back to the bytes of the file
contents. This replaces the rule of ADR 0057 §7 for the figures of `df`
([`system-metrics.md`](system-metrics.md)).

### Logs with no room

When a log write fails with `NoSpace`, `LogFileWriter` abandons the batch and
counts its lines (`PendingNoSpaceLoss`). At the first successful write to
`syslog`, it adds a single line
`WARN logging: N lines lost (No space left on device)`
([`logging.md`](logging.md)).

## Damaged PWA cache (ADR 0099 §3)

At startup, an integrity or loading error of Blazor's resources makes the
service worker cache be emptied and the page reloaded only once; offline, a
screen explains that one must reconnect once. See
[`pwa-cache-recovery.md`](pwa-cache-recovery.md).

## IndexedDB version upgrades (ADR 0099 §4)

Dumps of versions 2 and 3 in `Tests/fixtures/indexeddb/` (version 1 was never committed, ADR 0110 §1), E2E test `IndexedDbUpgradeFixtureTests` and procedure: see
[`building-and-testing.md`](building-and-testing.md#indexeddb-version-upgrade-fixtures).

## Decisions

- The clarifications below are recorded in
  [ADR 0110](adr/0110-backup-and-storage-implementation-adjustments.md).
- **A merge also restarts.** ADR 0099 describes the restart for replace; merge
  does it too, because the running OS keeps in memory paths, settings and
  accounts that the restore has just changed under it.
- **The restore entry waits outside the database**: a replace overwrites
  `/var/log/auth.log` itself. It is written at the next startup.
- **`hackbackup` requires `sudo`** instead of opening the graphical request,
  like a system tool (`apt`: "are you root?").
- **Picker before administrator** (see above).
- The **`system.backup`** capability is dangerous (ADR 0049): only Settings, a
  system app, declares it.

## Tests

- `Tests/HackerOs.Infrastructure.Browser.Tests`: `IndexedDbBackupServiceTests`
  (export then atomic replace, forged archive rejected without writing, merge
  by `put` without `clear`, gzip and raw JSON round trip, damaged gzip, other
  schema version); `IndexedDbFileSystemProviderTests` (`QuotaExceededError` →
  `NoSpace` without writing anything, more than 16 MiB → `FileTooLarge` before
  any write).
- `Tests/HackerOs.Platform.Core.Tests`: `SystemAdministrationGatewayTests`
  (capability, host without backup, creation downloaded and logged,
  administrator refusal without download, picker closed or invalid archive
  without request or stop, restore order, failure that restarts and logs at the
  next startup); `Storage/StorageSpaceMonitorTests` (one notification per user,
  5 % reserve capped, unknown quota); `FileSystemServiceTests` (reserve: user
  refused, system accepted, new measurement requested); `LogFileWriterTests`
  (lost lines counted then reported once).
- `Tests/HackerOs.AppSdk.Tests/IO/HackErrorTranslationTests.cs`: two codes
  translated into `HackIOException` and into a Linux message.
- `Tests/HackerOs.Commands.Wave5.Tests/HackbackupCommandTests.cs`: usage, root
  required, creation, default merge, replace, archive rejected, host without
  backup.
- `Tests/HackerOs.UI.E2E.Tests/LowStorageSpaceTests.cs`: Chromium quota lowered
  (`Storage.overrideQuotaForOrigin`) to 3 % free; the notification arrives, the
  "Open Diagnostic" action is there, and the administrator reconnects after a
  reload (the system still writes into the reserve).
- `Tests/HackerOs.App.Abstractions.Tests/AppCapabilitiesTests.cs`:
  `system.backup` dangerous.

## Task list

- [x] `NoSpace` and `FileTooLarge` codes, translated in the SDK and the shell.
- [x] `QuotaExceededError` and content over 16 MiB translated by the IndexedDB provider.
- [x] `.hosbak` archive (gzip), full validation, merge by overwriting.
- [x] `StorageSpaceMonitor`: measurement, single notification, root reserve.
- [x] Reserve enforced by `FileSystemService`; `df` with the real figures.
- [x] `LogFileWriter`: lost lines counted then reported.
- [x] `HackSystemBackup`, gateway, `SystemBackupService`, browser device, `pickFile.js`.
- [x] Settings *Backup* tab and `hackbackup` command.
- [x] Notification actions that launch an app.
- [x] Damaged PWA cache: empty and reload once, offline screen.
- [x] IndexedDB fixtures v2 and v3 (no v1, ADR 0110) and upgrade test.
- [x] Quota E2E test (`LowStorageSpaceTests`: notification, root reserve).
- [x] Unit tests and documentation.
