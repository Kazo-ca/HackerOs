> [Version française](../../../../fr/wasm/dev/adr/0230-rename-migration-by-previous-version.md)

# ADR 0230: rename migration based on the previous version

## Status

Accepted on 2026-11-04 (asked by Benoit, series `rename/base`, branch
`rename/6a-adr-version-migration`).

- **Complements:** [ADR 0228](0228-rename-to-kazoos.md) and
  [ADR 0229](0229-kazo-os-display-name-and-new-address.md).
- **Replaces:** the "User data" point of ADR 0228 §2 (database by database
  copy at first boot) and the content of work item 6 of ADR 0228 §3.

## Context

Benoit (2026-11-04): "We also change what is written on disk. No mention of
HackOs any more. We do not really have users, so no migration is mandatory, but
on principle, at the first run, or at a restore, we look for the version used
before, and if it is lower than the new version 3.0.0, we look for every mention
of HackerOs and replace it by kazo os or kazo.os or kazo depending on the
context."

Work item 5 therefore also renamed the stored names: IndexedDB databases
`kazoos*` and `kazoweb.*`, `localStorage` keys, app ids `org.kazoos.*`, paths
`/etc/kazoos`..., configuration section `KazoOs`.

## Decision

1. **When.** The migration runs before the OS starts, in two cases: at the
   first start of a browser that holds data of an earlier version, and when a
   backup is restored.
2. **Previous version.** The OS reads the version that wrote the data: the
   version recorded by version tracking (ADR 0174) in the old storage, or the
   one in the backup header. If it is lower than `3.0.0`, or missing while an
   old `hackeros*` database exists, the migration applies; otherwise nothing
   happens.
3. **What.** Every mention of the old name is replaced by context, with the
   table of ADR 0228 §1 and the names of work item 5:
   - IndexedDB database names (`hackeros*` → `kazoos*`, `hackweb.*` →
     `kazoweb.*`): the database is copied under the new name, then the old one
     is deleted;
   - `localStorage` and `sessionStorage` keys;
   - virtual file system paths (`/etc/hackeros` → `/etc/kazoos`,
     `~/.config/hackeros` → `~/.config/kazoos`, `/etc/hackweb` →
     `/etc/kazoweb`...);
   - stored ids (`org.hackeros.*` → `org.kazoos.*`, theme, cursor and sound
     ids, `hackeros.*` contracts) and file extensions;
   - text content of the OS configuration files: "HackerOS" → "Kazo OS",
     `HackerOs.` → `Kazo.Os.`, `HackerOs`/`hackeros` → `KazoOs`/`kazoos`,
     `hackweb` → `kazoweb`.
   User files outside configuration (documents, `~/` except `~/.config`) are
   not rewritten.
4. **Resumable.** The migration records its end (version 3.0.0 stored under
   the new name). Interrupted, it resumes at the next start; a database already
   copied is not copied again.
5. **Backup.** A `.hosbak` backup made before 3.0 can be restored: the restore
   applies the same migration to its content before writing it.
6. **Tests.** The recorded fixtures `Tests/fixtures/indexeddb` (`hackeros`
   databases) do not change: the upgrade tests check that they arrive under the
   new names, with no mention of the old name in the migrated data.

## Consequences

- A user from before 3.0 keeps accounts, files and settings at the same
  address; on `os.kazo.ca` they start from an empty machine (ADR 0229 §3) or
  restore a backup, migrated at restore time.
- After the migration, no stored data carries the old name any more, except
  what the user wrote themselves in their documents.
