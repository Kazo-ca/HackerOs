> [Version française](../../../fr/wasm/dev/rename-migration.md)

# Data migration of the rename (HackerOS → Kazo OS)

## Purpose

Work item 5 of the rename ([ADR 0228](adr/0228-rename-to-kazoos.md)) also
renamed what the OS writes on disk: IndexedDB databases, storage keys, app ids,
configuration paths. A browser that ran a version before 3.0.0, or a `.kosbak`
backup made before 3.0.0, still holds the old names.
[ADR 0230](adr/0230-rename-migration-by-previous-version.md) decides to migrate
them: at boot, before the OS opens its database, and when a backup is restored,
before it is written. After the migration, no stored data carries the old name
any more, except what the user wrote in their own documents.

## Architecture

Everything is in `Infrastructure/Kazo.Os.Infrastructure.Browser/Rename/`:

| Element | Role |
|---|---|
| `RenamedNames` | The name table (rules of work item 5, in the same order): "HackerOS" → "Kazo OS", `HackerOs.` → `Kazo.Os.`, `Hack*`/`Hacker*` → `Kazo*`, `hackeros` → `kazoos`, `hackweb` → `kazoweb`, `hack-paint` → `kazo-paint`, `hackpkg` → `kazopkg`, `.hackapp` → `.kazoapp`..., then the short names of work item 9: `--hos-*` → `--kos-*`, `data-hos-*` → `data-kos-*`, `hos-*` classes → `kos-*`, `.hosbak` → `.kosbak` (a theme or setting stored with a design-system token stays valid). Every rule is idempotent. Also tells whether a database is old (`hackeros`, `hackeros-*`, `hackweb.*`) and whether a version predates 3.0.0. |
| `RenamedOsDatabaseRewriter` | Rewrites the records of the OS database (a pure function over JSON) and lists the writes to make. |
| `RenamedStorageMigration` | The boot sequence, over an `IRenamedStorageBrowser` interface (a fake in the tests). |
| `BrowserRenamedStorage` + `wwwroot/renamedStorage.js` | The browser: list and copy of databases, `localStorage`/`sessionStorage` keys, caches. The JavaScript only does what C# cannot reach; the decisions are in C#. |

It is called by `LoadInstalledAppsAsync` (`OS/Kazo.Os.Ecosystem.Host/InstalledAppsBoot.cs`),
which every browser host calls between `Build()` and `RunAsync()`: the first
point of the boot where the page runs and nothing has opened the database yet.
A failure is logged and the boot goes on; nothing is recorded, so the migration
starts again at the next boot.

## Previous version

Versions before 3.0.0 did not record their version in the browser. The version
that wrote the data is now kept in `localStorage`, key `kazoos.osVersion`:

- present and ≥ 3.0.0 (a 3.0.0 pre-release counts as 3.0.0): nothing to do,
  without even listing the databases;
- missing, while an old database, key or cache exists: the migration applies;
- missing on a fresh browser: the current version is only recorded.

A backup now carries `osVersion` (in the part covered by the SHA-256). A backup
whose database is named `hackeros`, without `osVersion` or with an older one, is
migrated.

## What is rewritten

1. **IndexedDB databases.** Each old database is copied under its new name
   (`hackeros` → `kazoos`, `hackeros-host-folders` → `kazoos-host-folders`,
   `hackweb.<app>` → `kazoweb.<app>`): same version, stores, indexes and
   records, all written in the upgrade transaction that creates the new
   database (an interrupted copy leaves no database). IndexedDB's structured
   clone keeps the values JSON cannot carry (the `FileSystemDirectoryHandle` of
   host folders). The old database is then deleted. The content of site
   databases is not rewritten: it is the sites' data.
2. **`localStorage` and `sessionStorage` keys** with the old name: renamed, their
   value rewritten; if the new key already exists, it wins.
3. **Caches** (Cache Storage) with the old name: deleted, the OS fills them
   again.
4. **OS database** (`RenamedOsDatabaseRewriter`), in a single transaction:
   - **file names** (`fsLinks`) in a system area, that is outside `/home`,
     `/root`, `/tmp`, `/mnt` and `/media`, plus the hidden
     folders of each account (`~/.config`, `~/.local`, `~/.hackeros`...): `/etc/hackeros` → `/etc/kazoos`, `~/.config/hackeros` →
     `~/.config/kazoos`, `/var/log/hackweb` → `/var/log/kazoweb`.
     `~/Documents/hackeros` keeps its name. A name already taken is never
     overwritten;
   - **configuration files** (`/etc/**`, `~/.config/**`) in UTF-8 text: content
     rewritten, stored under its new SHA-256 (chunks of 256 KiB), length
     updated; the old chunks are deleted once no file uses them any more.
     Documents are never rewritten;
   - **every other record** (catalog, grants, settings, sessions, logs, file
     metadata, symbolic link targets in a system area): each string is
     rewritten (app, theme and sound ids, capabilities, paths), except secrets
     (`credential`), content addressing (`contentHash`, `dataBase64`) and the
     names people chose (`loginName`, `displayName`). A record whose key changes
     (the id of a catalog app) is deleted under its old key.
5. **End.** The current version is written under `kazoos.osVersion`.

Restoring a backup made before 3.0.0 goes through the same step 4 in
`IndexedDbBackupService` (validation, then rewrite before writing): the SHA-256
is checked on the original content, the restore writes the migrated content.

## Resuming

`kazoos.renameMigration` (`localStorage`) keeps, during the migration, the list
of databases already copied. An interrupted migration starts again at the next
boot: a recorded database is not copied again, only deleted; a database of the
new name that the migration did not create (made by another build) is never
overwritten, the old one is then kept and a warning is logged. Every rewrite is
idempotent.

## Server

A server installed before 3.0.0 (Docker volume, data folder) has `hackeros.db`.
At start, `LegacyDatabaseFile.MoveIfNeeded` (`Server/Kazo.Os.Server/Data/`)
renames `hackeros.db` (and its `-wal`, `-shm`, `-journal` files) to `kazoos.db`
when the configured database is named `kazoos.db`, does not exist yet and
`hackeros.db` sits next to it.

## Decisions

- The previous version is read from `localStorage`, not from the database: it
  must be known before any database is opened, and versions before 3.0.0 wrote
  none.
- Databases are copied in JavaScript (structured clone); records are rewritten
  in C#.
- The fixtures `Tests/fixtures/indexeddb` stay as recorded (database
  `hackeros`): they serve as the data of a version before 3.0.0.

## Tests

- `Kazo.Os.Infrastructure.Browser.Tests`: `RenamedNamesTests` (name table,
  idempotence, versions), `RenamedOsDatabaseRewriterTests` (v2 and v3 fixtures
  with no old name outside documents, configuration rewritten under a new hash,
  documents kept, shared chunks kept, secrets and chosen names untouched, second
  pass without changes), `RenamedStorageMigrationTests` (databases, keys and
  caches, version recorded, immediate stop from 3.0.0 on, resume without a
  second copy, failure finished at the next boot, existing database never
  overwritten, pre-3.0.0 backup migrated and tampered backup refused),
  `IndexedDbUpgradeFixtureCatalogTests` (the fixtures name a pre-3.0.0 database
  that the migration moves to `kazoos`).
- `Kazo.Os.Server.Tests`: `LegacyDatabaseFileTests`.
- `Kazo.Os.UI.E2E.Tests`: `IndexedDbUpgradeFixtureTests` boots the OS over the
  `hackeros` v2 and v3 databases and checks, besides the sign-in, the file and
  the setting, that `hackeros` is gone and that no `kazoos` record holds an old
  name.

## Task list

- [x] Name table `RenamedNames`.
- [x] Rewrite of the OS database (`RenamedOsDatabaseRewriter`).
- [x] Boot migration, resuming, version `kazoos.osVersion`.
- [x] Pre-3.0.0 backups migrated at restore; `osVersion` in new backups.
- [x] `hackeros.db` renamed by the server.
- [x] Unit and E2E tests.
