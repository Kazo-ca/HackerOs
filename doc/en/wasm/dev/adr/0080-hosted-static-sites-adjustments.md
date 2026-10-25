> [Version française](../../../../fr/wasm/dev/adr/0080-hosted-static-sites-adjustments.md)

# ADR 0080: adjustments to hosted static sites

## Status

Accepted on 2026-10-16, with the `web/5-static-sites` work item.

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Completes:** [ADR 0077](0077-hosted-static-sites.md). Sections 1 and 2
  below replace two bullets of ADR 0077 §2 and its consequence on the
  `www-data` account; the rest of ADR 0077 (`.site` files, two
  locations, Web Server Manager, external sites) is applied as is.
- **Also completes:** [ADR 0045](0045-logs-as-files.md) (daemon logs)
  and [ADR 0074](0074-listening-ports-and-name-resolution.md) §4 (source of the
  resolver's hosted sites).
- **Documentation:** [`static-sites.md`](../static-sites.md).

## Context

The implementation of ADR 0077 encountered four points that the ADR did not
settle, or settled in a way that the current platform does not
allow.

## Decision

1. **`hackwebd` runs on the main thread, not in a dedicated worker.** A
   Web Worker does not yet offer file watching
   (`HackFileSystemWatcher`: the remote gateway does not carry it, ADR 0072
   §3), on which the hot reload of ADR 0077 §2 depends. The manifest
   therefore declares `Execution = MainThread`. Serving a file only reads the
   file system through the SDK: the cost on the main thread is that of the
   explorer. It will move to a worker when workers can
   watch a folder, without changing its code.
2. **`www-data` is simulated, not an OS account.** The OS has no service
   accounts and adding one would touch authentication, `/etc/passwd`
   and sessions, outside the subject of this work item. `hackwebd` runs with
   the session user and applies the rights of `www-data` itself,
   which owns no file and is in no group:
   - a **system site** (`/etc/hackweb/sites`) is served only if every
     folder, from `/` down to the file, has the `o+x` bit, and the file `o+r`
     (a listed folder: `o+r` too); otherwise `403`, like Apache on a
     home folder with `0700`;
   - a **user site** (`~/.config/hackweb/sites`) is served with the
     rights of its owner, like a `python -m http.server` they would have
     launched.
   The Manager warns when `www-data` will not be able to read the chosen root.
3. **Daemon logs through `IAppLoggingGateway.AppendToLogFile`.** ADR
   0045 reserves writing to `/var/log` for the platform's writer; an
   app does not write there itself. To keep this rule and give
   `hackwebd` its `/var/log/hackweb/<site>.access.log` and `error.log`, the
   logging gateway gains `AppendToLogFile(relativePath, line)`:
   - reserved for **system apps** (ADR 0048); another app receives
     `UnauthorizedAccessException`;
   - relative path of exactly two segments, `<folder>/<file>.log`,
     never under `apps/` (reserved by ADR 0045);
   - the line is written as is (daemon format, for example Apache's
     "combined") by `LogFileWriter`, with the same rotation at
     1 MiB × 5 and mode `system:system 0644`; a line break in the line
     becomes `\n` so that no entry can forge another;
   - an app in a Web Worker calls it through the `log.file` operation of the
     worker protocol.
4. **The resolver's hosted-site source becomes asynchronous.**
   `IHostedSiteNameSource.FindSite(name)` becomes `FindSiteAsync(name,
   cancellationToken)`, because the answer comes from the `.site` files. The
   platform implements it with `HostedSiteNameSource`: it reads both
   locations with the rights of `root` (the resolver is the OS), keeps the
   valid and enabled sites, and publishes their names that are neither `localhost`,
   nor an IP address, nor a wildcard. A system site wins over a user
   site of the same name. The reading is cached per revision of the
   files.

Outside ADR 0077, two limits are set:

- an `https://` binding is refused at validation: there are no
  simulated certificates yet;
- only `GET`, `HEAD` and `OPTIONS` are served (`405` with `Allow` otherwise); no
  partial requests (`Accept-Ranges: none`).

## Consequences

- A system daemon can have its own logs under `/var/log` without an
  app writing into that folder; ordinary apps keep only
  `/var/log/apps/<appId>.log`.
- The log viewer (`Error Log Viewer`) does not yet list
  `/var/log/hackweb`: `cat`, `tail` and the Manager's "Logs" tab read it.
- The day the OS has real service accounts, section 2 will be
  replaced by a new ADR; the observed rights will stay the same.
