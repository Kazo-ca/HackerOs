> [Version française](../../../fr/wasm/framework/system-backup-api.md)

# System backup for apps

This document describes what an app sees of the machine backup: the `HackSystemBackup` API, the `.hosbak` archive, the `system.backup` capability and the space error codes. The implementation (sequence, storage space, PWA cache, IndexedDB version upgrades, tests) is in [`../dev/system-backup.md`](../dev/system-backup.md).

## `.hosbak` archive

The `formatVersion: 1` JSON of `IndexedDbBackupService` (schema version,
all stores, SHA-256), compressed with gzip. `ReadArchive` recognizes gzip
by its first two bytes (`1F 8B`) and also accepts raw JSON. An archive that is
damaged, empty, tampered with (wrong hash) or from another schema version is
refused **before** any write, with a message that names both versions:

```text
This backup uses database schema version 3; this HackerOS uses version 4. It cannot be restored here.
```

Name: `hackeros-AAAAMMJJ-HHMMSSZ.hosbak` (UTC time of the export).

## SDK API

```csharp
// Capacité system.backup (dangereuse) et un administrateur à chaque appel.
HackBackupInfo backup = await HackSystemBackup.CreateAsync(AppContext);
HackBackupInfo? restored = await HackSystemBackup.RestoreAsync(AppContext, HackBackupRestoreMode.Replace);
// restored == null : l'utilisateur a fermé le sélecteur ; sinon la machine redémarre.
```

| Member | Effect |
|---|---|
| `IsSupported`, `IsSupportedFor(context)` | `false` on a host without IndexedDB. |
| `CreateAsync` | Asks for an administrator, exports, downloads the archive on the host, logs. |
| `RestoreAsync(mode)` | Opens the host's picker, validates the archive, **then** asks for an administrator, closes the session, restores, reboots. |
| `HackBackupInvalidException` | Archive refused (subclass of `HackIOException`). |

The order of `RestoreAsync` is deliberate: the browser only opens its picker
right after a user gesture (click, or Enter in the terminal), and
a wrong file must not cost anyone a password.

## Capability

`HackSystemBackup` requires the `system.backup` capability, which is dangerous (ADR 0049). Only the Settings system app declares it. An administrator is requested on every call.

## Rules

- Test `HackSystemBackup.IsSupported` (or `IsSupportedFor(context)`) before displaying an action: on a host without IndexedDB it is `false`, and the Backup tab of Settings is hidden.
- Trigger `RestoreAsync` from a user gesture (click, or Enter in the terminal): the browser only opens its picker right after a gesture. Archive validation comes before the administrator request, so a wrong file costs no password.
- Treat `null` as "the user closed the picker": the machine does not reboot.
- Handle `HackBackupInvalidException`: it is a `HackIOException`, and the refused archive wrote nothing.

## Root reserve

In the reserve, `FileSystemService` refuses `Create`, `Write` and `Copy` from a
caller whose effective authority is below Administrator, with `NoSpace`.
The system (logs, boot), root (`sudo`) and the administrator write
up to the real browser limit. Virtual mounts (`/proc`, `/dev`)
are never concerned.

## `NoSpace` and `FileTooLarge` codes

| Origin | Code | Shell message |
|---|---|---|
| IndexedDB `QuotaExceededError` (whole transaction canceled) | `NoSpace` | `No space left on device` |
| Root reserve | `NoSpace` | `No space left on device` |
| Content over 16 MiB | `FileTooLarge` | `File too large` |

`HackFile`/`HackFileStream` throw `HackIOException` with `ErrorCode`;
`ShellErrorText.Describe` and the commands (`chmod`, `mkdir`...) display the
Linux message.
