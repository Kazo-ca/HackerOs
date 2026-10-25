# App logs (`/var/log`): API and files produced by an app

This document describes the log files that an app, a command or a site produces, the APIs that write them, and what was removed from the SDK. The implementation (`LogFileWriter`, rotation, channel, Error Log Viewer) and the system logs (`syslog`, `kern.log`, `auth.log`, `audit.log`) are in [`dev/logging.md`](../dev/logging.md).

## Files produced by an app

| File | Content | Mode |
|---|---|---|
| `/var/log/apps/<appId>.log` | The app's `IAppExecutionContext.Logging` | `system:system 0644` |
| `/var/log/<daemon>/<file>.log` | `IAppLoggingGateway.AppendToLogFile` of a **system app** only, line written as is (ADR 0080 §3); for example `/var/log/hackweb/error.log` of `hackwebd` ([`static-sites.md`](../dev/static-sites.md)) | `system:system 0644` |
| `/var/log/hackweb/<appId>.log` | Debug log of a **site** (`Web` app) that enables it with `builder.Logging.EnableDebugLog()`: `ILogger<T>`, requests, exceptions, through `IAppLoggingGateway.AppendToSiteDebugLog` (ADR 0088 §5, [`asp-hack-site-data.md`](asp-hack-site-data.md)) | `system:system 0644` |

## Writing to a log

In an app, `IAppExecutionContext.Logging` (type `IAppLoggingGateway`) writes to `/var/log/apps/<appId>.log`. A system app writes a line as is to a file of its daemon with `AppendToLogFile(relativePath, line)`. A site that enables the debug log (`builder.Logging.EnableDebugLog()`) writes with `AppendToSiteDebugLog(line)`.

## Line format

- `LogLineFormat` (`Shared/HackerOs.Simulation.Abstractions/Diagnostics/`):
  `Format`, `FormatAudit` and `TryParse`. An entry fits on one line:
  `2026-10-10T03:11:42.123Z WARN  org.hackeros.text-editor[42]: message {clé=valeur}`.
  The message escapes `\`, `\n`, `\r`, `\t` and also `{` (as `\{`), so that a
  message is never read back as a property block. Inside that block, keys and
  values additionally escape `,`, `=` and `}`. Spaces in a source become `_`.

## Rotation

- `LogRotationPolicy`: `MaxFileBytes` = 1 MiB, `MaxArchives` = 5. Rotation like
  `logrotate` (`.5` deleted, `.4`→`.5` … `<f>`→`.1`, new empty `<f>`).
  An empty file never rotates: a batch larger than 1 MiB is written whole.

## Delays and losses

The lines of a log are flushed in batches: every 1 s of simulated time, as soon as 100 lines are pending, on disconnection and at shutdown. Beyond 10,000 pending lines, the oldest are lost; a `WARN logging` line reports it at the next flush.

## What was removed

- `IAppDiagnosticsGateway`, `AppDiagnosticsGateway` and `IAppExecutionContext.Diagnostics`
  are removed. Reading the logs means reading files.
- `diagnostics.read` and `diagnostics.clear` are removed from the catalog by
  ADR 0049 ([`capabilities.md`](capabilities.md)).
