# ADR 0045: Logs as text files in `/var/log`, with rotation

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 1, Files ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 5.3, decision D19).
- **Replaces:** no ADR. Removes the diagnostics and audit IndexedDB stores
  as a log destination.
- **Default values retained** (plan, section 11): rotation at **1 MiB** per
  file, **5 archives**.

## Context

`/var/log` is created by `FileSystemSeeder` but stays empty. Entries go through
`HackerOsDiagnosticLoggerProvider` (for the platform's `ILogger<T>`)
and through `IAppLoggingGateway` (for apps), then into
`EventPublishingDiagnosticSink`, `BoundedDiagnosticSink` and
`IPersistentDiagnosticRepository` (IndexedDB store). The audit (`IAuditLog`,
`BoundedAuditLog`, `IndexedDbAuditRepository`) has its own circuit. Only Error
Log Viewer can read these logs, through `IAppDiagnosticsGateway`. Neither `cat`,
nor `tail`, nor `grep` can access them.

## Decision

### 1. Files

| File | Content | Owner and mode |
|---|---|---|
| `/var/log/syslog` | platform logs: all `ILogger` categories (`HackerOs.*`), startup, OS service errors, OS settings warnings (ADR 0044) | `system:system 0644` |
| `/var/log/auth.log` | all `IAuditLog` entries: existing actions `session.login` and `session.elevate` (`sudo`, ADR 0042), and later the permission approval decisions (phase 2 ADR) | `system:system 0600` (readable with `sudo` or an approval ticket) |
| `/var/log/apps/<appId>.log` | entries of `IAppLoggingGateway.Log` for that app, and its settings warnings (ADR 0044) | `system:system 0644` |

The `/var/log/apps` folder (`system:system 0755`) is created by `FileSystemSeeder`.
Files are created on first write. Apps **never** write
directly to `/var/log`: only the platform writer (section 4) does
so, with a system context. Ordinary readers go through the file
permissions, which requires `filesystem.system.read` since `/var` is a system
path.

### 2. Line format

An entry fits on **one** UTF-8 line terminated by `\n`:

```text
<timestamp> <LEVEL> <source>[<pid>]: <message>[ {key=value, ...}]
```

- `timestamp`: UTC simulation time (`ISimulationClock`), ISO 8601 to the
  millisecond, for example `2026-10-10T03:11:42.123Z`.
- `LEVEL`: `DiagnosticSeverity` translated to `TRACE`, `DEBUG`, `INFO`, `WARN`,
  `ERROR`, `CRIT`, padded to 5 characters.
- `source`: the `ILogger` category for `syslog`, the `appId` for app logs,
  the `Action` of the `AuditEntry` for `auth.log`. `[<pid>]` is present
  when a process is known.
- `message`: `\`, line breaks and tabs are escaped as `\\`,
  `\n` and `\t`, so that `grep` finds an entry on a single line.
- Properties: already redacted by `IDiagnosticRedactor`
  (`SensitiveKeyDiagnosticRedactor`), then sorted by key.
- Audit entry: `<timestamp> <LEVEL> <action>: actor=<Actor> subject=<Subject> outcome=<Success|Denied|Failed> {properties}`,
  with level `INFO` for `Success` and `WARN` otherwise.

Example:

```text
2026-10-10T03:11:42.123Z WARN  org.hackeros.text-editor[42]: settings: /etc/org.hackeros.text-editor/settings.config:12: invalid value for 'tab-size' (expected int64), using default
```

The public type `LogLineFormat` (`Shared/HackerOs.Simulation.Abstractions/Diagnostics/LogLineFormat.cs`)
contains `Format(...)` and `TryParse(string line, out LogLine entry)`. Error Log
Viewer and the tests use it to re-read lines.

### 3. Size and rotation

- **Maximum size: 1 MiB (1,048,576 bytes) per active file. Number
  of archives: 5.** These are the values retained by default. They are
  constants of `LogRotationPolicy` (`MaxFileBytes`, `MaxArchives`) and are not
  settings in phase 1.
- Before appending a batch, if `current size + batch size` exceeds
  `MaxFileBytes`, a rotation is performed, in the manner of `logrotate`:
  delete `<f>.5`, then rename `<f>.4` to `<f>.5`, ..., `<f>.1` to `<f>.2`, and
  `<f>` to `<f>.1`, then create an empty `<f>`. Each step is an atomic `MoveAsync`
  or `DeleteAsync` of the engine.
- Archives are not compressed. `.gz` is deferred.
- A single batch larger than `MaxFileBytes` is written as is to a new
  file. A line is never cut.

### 4. Batched background writing

New service `LogFileWriter` (`Platform/HackerOs.Platform.Core/Diagnostics/LogFileWriter.cs`),
registered as a singleton:

- `Enqueue(LogFileTarget target, string line)` is synchronous and never
  blocks: the line goes into a `Channel<T>` bounded to **10,000** lines. Beyond that,
  the oldest are lost and a counter is kept. At the next flush, a
  `WARN logging: <n> entries dropped` line is written to `syslog`.
- **Flush** every **1 s** of simulated time (`ISimulationClock.Schedule`),
  or as soon as **100** lines are waiting, and also on sign-out and shutdown.
  Only one flush runs at a time. Lines of the same file are grouped
  into **a single** append (read, concatenate, write with the revision read,
  like `AppendAllTextAsync` of ADR 0043).
- **Before the file system is ready:** lines accumulate in the
  channel. The startup sequence calls
  `LogFileWriter.AttachAsync(IFileSystemService)` once the IndexedDB bootstrap
  and `FileSystemSeeder` are finished. If the file system never becomes
  available, entries remain visible in the memory buffer (section 5)
  for the startup error screen.
- **No recursion:** a log write failure is never logged
  to a file. It is noted only once in the memory buffer, with the
  `logging` category.
- The cost of an append stays bounded: deduplicated block storage only rewrites
  the last 256 KiB block, and a file never exceeds 1 MiB.

### 5. Wiring

| Producer | Before | After |
|---|---|---|
| `ILogger<T>` (`HackerOsDiagnosticLoggerProvider`) | in-memory sink + `IPersistentDiagnosticRepository` | `LogFileWriter` → `syslog`, plus the memory buffer |
| `IAppLoggingGateway.Log` (`AppLoggingGateway`) | same sink | `LogFileWriter` → `apps/<appId>.log`, plus the memory buffer |
| `IAuditLog.Record` | `BoundedAuditLog` + `IndexedDbAuditRepository` | new implementation `FileAuditLog` → `auth.log`; `IAuditLog.Entries` is still served by a 512-entry memory buffer |

- `BoundedDiagnosticSink` and `EventPublishingDiagnosticSink` remain as the
  **memory buffer** of recent entries (`DiagnosticEntryRecordedEvent` event
  for live display). It is no longer a source of truth.
- `IPersistentDiagnosticRepository`, `IndexedDbDiagnosticRepository`,
  `IPersistentAuditRepository` and `IndexedDbAuditRepository` are no longer
  wired into the composition. Their IndexedDB stores are neither deleted nor
  migrated, and **the IndexedDB schema version does not change** (see
  [`old/common-pitfalls.md`](../old/common-pitfalls.md)). Old entries
  are not copied: they are ephemeral diagnostics.
- `IAppDiagnosticsGateway` (`Entries`, `Clear`) is **removed** from the SDK, as is
  `IAppExecutionContext.Diagnostics`. The `diagnostics.read` and
  `diagnostics.clear` capabilities are no longer evaluated. They remain in the catalog
  until the phase 2 ADR on permissions.
- Both hosts (`OS/HackerOs.Ecosystem` and `test/test`) go through
  `EcosystemServiceCollectionExtensions`. The server (ADR 0036) keeps its own
  composition, which is not modified.

### 6. Readers

- **Error Log Viewer** (`Apps/System/HackerOs.Apps.ErrorLogViewer`) reads
  `/var/log/syslog` and `/var/log/apps/*.log` (and their `.1` to `.5` archives on
  demand) with `HackFile`, parses lines with `LogLineFormat.TryParse` and
  follows new entries with `HackFileSystemWatcher`. "Clear" becomes
  "truncate the file", which requires the right to write to `/var/log`
  (`sudo` or approval, ADR 0042).
- `cat`, `tail`, `grep` and `less` read these files like any other.

## Consequences

- `tail /var/log/syslog` and `grep ERROR /var/log/apps/*.log` work in the
  terminal.
- A log occupies at most 6 MiB per file (the active one plus 5 archives).
- Logs arrive up to about 1 s after the event. An abrupt tab shutdown
  can lose the last second. This is accepted, since the memory buffer
  remains visible as long as the tab lives.
- `auth.log` at `0600` reserves the audit for the administrator, as on Linux.
- The tests of Error Log Viewer and of diagnostics that used
  `IAppDiagnosticsGateway` are rewritten on files.

## References

- ADR 0012: process and clock (`ISimulationClock`).
- ADR 0042: `sudo` and `session.elevate` audit.
- ADR 0043: `Hack*` API.
- ADR 0044: invalid settings warnings.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 5.3 and 11.
- `Platform/HackerOs.Platform.Core/Diagnostics/`, `Shared/HackerOs.Simulation.Abstractions/Diagnostics/DiagnosticsContracts.cs`.
