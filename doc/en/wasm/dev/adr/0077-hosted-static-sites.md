> [Version française](../../../../fr/wasm/dev/adr/0077-hosted-static-sites.md)

# ADR 0077: hosted static sites, `hackwebd` service and Web Server Manager

## Status

Accepted on 2026-10-16 (proposed the same day, then validated by Benoit with the
questionnaire of section 4 of the plan).

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Depends on:** ADR 0074 (ports, resolver), ADR 0075 (HTML rendering).
- **Follows:** ADR 0044 (settings in files).

## Context

Benoit wants to be able to declare a static website from the file
system and manage it with an IIS-style app, backed by an emulated
background service that only drives the OS through the SDK. Local files are served
on `localhost`.

## Decision

### 1. Declarations in files

A hosted site is a file `/etc/hackweb/sites/<name>.site` (JSON), editable
by the administrator, like IIS's `applicationHost.config` or
nginx's `sites-available`:

```json
{
  "name": "docs",
  "root": "/home/benoit/www/docs",
  "bindings": ["http://localhost:8080/", "http://docs.hack/"],
  "defaultDocuments": ["index.html", "index.htm"],
  "directoryBrowsing": false,
  "enabled": true
}
```

A name other than `localhost` in `bindings` is published by `hackdns`
(ADR 0074 §4).

Who can declare a site:

| Where | Who | Ports |
|---|---|---|
| `/etc/hackweb/sites/` | the administrator (approval) | all, including < 1024 |
| `~/.config/hackweb/sites/` | any user, for themselves | 1024 and above only |

This is the difference between Apache configured by the administrator and a
`python -m http.server` launched by a user. `hackwebd` reads both
locations; a user site that requests a port < 1024 is refused and
the error goes to `/var/log/hackweb/error.log`.

### 2. The `hackwebd` service

System app of kind `Service` (dedicated worker), started at boot:

- reads `/etc/hackweb/sites/`, watches the folder (`HackFileSystemWatcher`) and
  applies changes on the fly;
- opens an `HackHttpListener` per binding; serves files with the rights of the
  `www-data` service account (read-only on `root`): MIME types,
  default documents, folder listing if permitted, `304` through `ETag` and
  `Last-Modified`, `404` and `403`;
- logs accesses in `/var/log/hackweb/<name>.access.log` and errors
  in `/var/log/hackweb/error.log` (ADR 0045);
- uses only the public SDK: neither Platform Core nor any internal service.

### 3. The Web Server Manager app

Windowed app that drives `hackwebd` through the files of §1 and
`HackServiceController` (like `systemctl`): list of sites and their state, creation from a
folder (folder picker), bindings, default documents, start/stop
a site, restart the service, open the site in the browser, view its
logs. A user site is written without approval into
`~/.config/hackweb/sites/`; writing to `/etc/hackweb` requires a dangerous capability
(`filesystem.system.write`, administrator approval).

The explorer gains "Publish this folder as a site", which opens the Manager
prefilled.

### 4. Project sites

The sites of `Web` apps (ADR 0073) do not go through `hackwebd`: they are
served by their own process. The Manager lists them read-only
("external"), with their URLs.

## Consequences

- New system app `hackwebd` and new "Web Server Manager" app
  (catalog, `IsSystemApp`).
- The `www-data` service account is added to the OS's system accounts (it
  does not exist yet).
- `systemctl status hackwebd` displays the service like the others.
