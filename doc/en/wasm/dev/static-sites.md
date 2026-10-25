> [Version française](../../../fr/wasm/dev/static-sites.md)

# Static sites: `.site` files, `hackwebd` and Web Server Manager

The format of `.site` files and the `HackWebSiteFile` API intended for apps and tools are in [`static-site-files.md`](../framework/static-site-files.md).

Work item `web/5-static-sites` of the web phase (ASP.Hack). Decisions:
[ADR 0077](adr/0077-hosted-static-sites.md) and its adjustments,
[ADR 0080](adr/0080-hosted-static-sites-adjustments.md). Plan:
[`asp-hack-plan.md`](plans/asp-hack-plan.md) §5. Builds on `web/3`
([`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md)) for ports and
names, and on `web/4` ([`browser-html-rendering.md`](browser-html-rendering.md))
to display pages.

## Purpose

Publish a folder as a website, as with IIS, Apache or nginx:

- a site is a `.site` **file** (JSON), readable with `cat` and editable by
  hand;
- the **`hackwebd`** service reads these files, opens one port per binding and
  serves the files: MIME types, default documents, folder listing if allowed,
  `301`, `304`, `403`, `404`, `405`, nginx-style logs;
- the **Web Server Manager** app (IIS manager style) only writes these files
  and drives the service through the public SDK;
- the explorer gains **"Publish as a web site…"** on a folder.

## Architecture

```text
 Web Server Manager ─┬─ écrit ───────────► /etc/hackweb/sites/*.site      (admin, tous ports, servi en www-data)
 (fenêtre)           │                     ~/.config/hackweb/sites/*.site (utilisateur, ports ≥ 1024)
                     ├─ HackServiceController (start/stop/restart hackwebd)
                     └─ HackIPGlobalProperties (sockets de hackwebd → état des sites)

 hackwebd (service, worker dédié, AutoStart)
   ├─ HackFileSystemWatcher "*.site" sur les deux dossiers ─► rechargement à chaud
   ├─ HackHttpListener par préfixe normalisé ─► StaticFileHandler
   │      └─ WwwDataAccess (droits « other » pour un site système)
   └─ context.Logging.AppendToLogFile ─► /var/log/hackweb/<site>.access.log, error.log

 hackdns (OS) ── HostedSiteNameSource (lit les .site en root) ─► « docs.hack » → 127.0.0.1
```

| Element | Project | Role |
|---|---|---|
| `HackWebdApp`, `HackWebdService` | `Apps/System/HackerOs.Apps.WebServer` | The service: reconciliation of listeners, accept loop, logs. |
| `StaticFileHandler`, `WwwDataAccess`, `HackWebdLog` | same, `Daemon/` | Request handling, `www-data` permissions, log formats. |
| `WebServerManagerApp`, `WebServerManagerWindow`, `WebSiteStore` | same, `Manager/` | The windowed app and its interface-free logic. |
| `WebServerManagerApp.RegisterFileContextMenu`, `PublishSiteIntent` | `Apps/System/HackerOs.Apps.WebServer` | "Publish as a web site…" entry of the folder menus, registered by Web Server Manager (ADR 0151 §8, ADR 0159): `Directory` scope, a single folder, intent `org.hackeros.web-server-manager.publish-site.v1` received at launch or through `IntentReceived`. Was `PublishSiteContextMenuProvider`, then `FileExplorerMenuEntries.PublishSite`. |
| `HostedSiteNameSource` | `Platform/HackerOs.Platform.Core/Network/Local` | Source 4 of the resolver (`IHostedSiteNameSource`, asynchronous). |
| `IAppLoggingGateway.AppendToLogFile`, `LogFileTarget.ForDaemon` | `Simulation.Abstractions`, `Platform.Core` | System daemon logs (ADR 0080 §3). |

The service and the Manager are in **the same assembly**: the Manager drives
its companion service without `services.manage`, as the rule for services of
the same app provides. Both are system apps (`IsSystemApp` on the hosts'
`ProjectReference`).

## How it works

### `hackwebd`

- Started at boot (`AutoStart`), visible through `systemctl status hackwebd`,
  stoppable through `systemctl stop hackwebd` (the ports are released:
  `curl` then answers `Failed to connect`, code 7).
- On each change to a `.site` (or at startup), it **reconciles**: one
  `HackHttpListener` per normalized prefix; a prefix already taken (by another
  site or another program) is logged as `[emerg]` and the site stays
  "partial" or "down".
- Request: `GET`, `HEAD`, `OPTIONS` (otherwise `405` + `Allow`); decoded path,
  `.` and `..` segments resolved by the URL parser then rejected if any remain,
  `\` and NUL rejected (`400`): never a file outside `root`.
- Folder without a trailing `/`: `301`. Folder: first default document found,
  otherwise "Index of" listing if `directoryBrowsing`, otherwise `403`.
- File: `Content-Type` by extension (`SimulatedFileTypes`), `ETag`
  (`"<révision>-<taille>"`), `Last-Modified`, `304` on `If-None-Match` then
  `If-Modified-Since`; `HEAD` sends `Content-Length` without a body.
- Error pages and `Server: hackwebd/1.0` header in the nginx style.

### Logs

| File | Content |
|---|---|
| `/var/log/hackweb/<site>.access.log` | "combined" format: `127.0.0.1 - - [16/Oct/2026:12:00:00 +0000] "GET / HTTP/1.1" 200 512 "-" "curl/8.5.0"` |
| `/var/log/hackweb/error.log` | nginx format: `2026/10/16 12:00:00 [error] 42#0: docs: open() "/…" failed (13: Permission denied for www-data)`; startup, listeners (`[notice]`), conflicts (`[emerg]`), denials, `exiting`. |

Written by `LogFileWriter` (rotation 1 MiB × 5, `system:system 0644`).

### Name resolution

`HostedSiteNameSource` reads the `.site` files of both locations with `root`
permissions, keeps the valid and enabled sites and publishes their hosts (no
`localhost`, no IP, no wildcard). `nslookup docs.hack` answers
`;; source: hosted site docs (this machine)`. The name stays published when
`hackwebd` is stopped (the file still exists): the connection is then refused,
like a stopped server behind a valid DNS.

### Web Server Manager

- Three groups: **System** (`/etc/hackweb/sites`), **My sites**
  (`~/.config/hackweb/sites`), **External** (`Web` apps from ADR 0073, read-only
  with their URLs).
- Per-site state (dot): running, partial, down, stopped, invalid, external,
  computed from the sockets owned by `hackwebd`.
- Top bar: Refresh, Start/Restart/Stop `hackwebd`, "+ New site".
- Form: name, scope (my sites / system), root ("Browse…" button, folder
  picker), bindings (one per line), default documents, folder listing,
  enabled; `Validate` errors shown live; warning if `www-data` will not be able
  to read the root of a system site.
- Save, Start/Stop site (toggles `enabled`), Open in browser
  (`org.hackeros.browser`), Delete (confirmation), **Logs** tab (the site's
  access log, the lines of `error.log` that concern it).
- Writing to `/etc/hackweb` is a system write: the platform's administrator
  approval is displayed, then the Manager asks to press Save again.
- Launched with a folder as an argument (explorer menu), it directly opens a
  prefilled new site: name taken from the folder, first free port from 8080 on
  `localhost`.

## Usage

```sh
mkdir -p ~/www && echo '<h1>Hello</h1>' > ~/www/index.html
mkdir -p ~/.config/hackweb/sites
cat > ~/.config/hackweb/sites/docs.site <<'EOF'
{ "name": "docs", "root": "/home/benoit/www", "bindings": ["http://docs.hack:8080/"] }
EOF
curl http://docs.hack:8080/          # <h1>Hello</h1>
netstat -tlnp                         # 0.0.0.0:8080 … LISTEN  <pid>/hackwebd
tail /var/log/hackweb/docs.access.log
systemctl restart hackwebd
```

Or, in the explorer, right-click a folder → **Publish as a web site…**, then
Save in the Manager and **Open in browser**.

## Key decisions

- Files rather than a configuration database: what the Manager shows is exactly
  what `cat` shows (ADR 0077 §1).
- A single daemon for all static sites, one listener per prefix: routing by host
  and by path remains that of `http.sys` (ADR 0074).
- Simulated `www-data`, logs through `AppendToLogFile`, asynchronous resolver:
  [ADR 0080](adr/0080-hosted-static-sites-adjustments.md). `hackwebd` runs in a
  dedicated worker since file watching crosses the worker boundary
  ([ADR 0124](adr/0124-remote-event-and-watch-gateways.md) §6, which makes the
  main thread of ADR 0080 §1 moot).
- `enabled` is a `set` property (not `init`): the JSON generator gives
  `default` to an absent `init` property, which would have stopped every site
  whose file omits `enabled`.

## Known limits

- No `https://`, partial requests (`Range`), compression, or URL rewriting.
- The Error Log Viewer does not list `/var/log/hackweb` (read with
  `cat`/`tail` or the Manager's Logs tab).

## Tests

- `Tests/HackerOs.Platform.WorkerHost.Tests/StaticSitesTests.cs`: full OS
  (processes on the main thread and in workers): user site served (type,
  default document, `404`, `nslookup`), `304` by `ETag`, `403` then folder
  listing after hot reload, refusal of a privileged port and log formats,
  system site on port 80 served as `www-data` (`403` on a `0600` file and on a
  home folder), `netstat`, `systemctl stop`.
- `Tests/HackerOs.Apps.WebServer.Tests`: URL → file mapping (traversals
  rejected), site states, suggestion of a site from a folder, log formats.
- `Tests/HackerOs.AppSdk.Tests/Net/HackWebSiteFileTests.cs`: reading,
  validation and writing of `.site` files.
- `Tests/HackerOs.Platform.Core.Tests/Diagnostics/DaemonLogFileTests.cs`:
  `LogFileTarget.ForDaemon` and the "system apps only" rule.

## Task list

- [x] `.site` format in the SDK (`HackWebSite`, `HackWebSiteFile`), validation in the style of `nginx -t`.
- [x] `/etc/hackweb/sites` created by `FileSystemSeeder`; `~/.config/hackweb/sites` created by `hackwebd`.
- [x] System daemon logs (`AppendToLogFile`, `LogFileTarget.ForDaemon`, workers' `log.file` operation).
- [x] `HostedSiteNameSource` and asynchronous `IHostedSiteNameSource`; `HackAppInfo.Urls` (external sites).
- [x] `hackwebd`: hot reconciliation, files, default documents, listing, MIME, `301`/`304`/`403`/`404`/`405`, `www-data`.
- [x] Web Server Manager: groups, states, form, scope, Browse, Save/Start/Stop/Delete, browser, logs, service control.
- [x] Explorer: "Publish as a web site…" on a folder.
- [x] Hosts (Ecosystem, test, Server) and solution.
- [x] End-to-end and unit tests.
- [x] Documentation, ADR 0080, index.
