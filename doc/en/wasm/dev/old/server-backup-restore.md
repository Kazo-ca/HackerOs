# Optional Server Backup and Restore

## Purpose

`IServerDatabaseBackupService` creates and restores consistent SQLite snapshots
for a trusted HackerOS server operator. It is deliberately not an HTTP API:
database restore is a deployment operation, not a client capability.

## Architecture

- `ServerDatabaseBackupService` opens the configured SQLite database and uses
  SQLite's backup API to copy a consistent snapshot.
- Snapshots are constrained to `ServerBackup:Root` and must be simple `.db`
  file names. Traversal and arbitrary paths are rejected.
- The source database is configured through
  `ConnectionStrings:HackerOsDb`; deployment-scoped overrides use
  `HACKEROS_ConnectionStrings__HackerOsDb`.
- On a fresh deployment with no EF migrations, the host calls
  `EnsureCreatedAsync` so `/health` cannot report success against an empty
  SQLite file. A future generated migration set takes the normal
  `MigrateAsync` path instead.

## Operator use

Resolve `IServerDatabaseBackupService` only from trusted server-host management
code, then call `CreateAsync("snapshot.db", cancellationToken)` or
`RestoreAsync("snapshot.db", cancellationToken)`. Restore must be coordinated
with the operator's maintenance procedure so normal writes are paused.

## Verification

`ServerStartupIntegrationTests.Startup_MigratesConfiguredDatabase_BacksUpRestores_AndReportsHealthy`
proves the configured database is initialized, the protected account route
rejects anonymous access, a snapshot is created, a later write is removed by
restore, traversal is rejected, and `/health` is healthy.

## Task status

- [x] Bounded SQLite snapshot and restore boundary.
- [x] Configuration, fresh-schema, health, anonymous rejection, authenticated
  ownership, persistence, backup, and restore integration evidence.
- [ ] The currently stubbed account export/deletion lifecycle remains separate
  `P5-SRV-003` server work.
