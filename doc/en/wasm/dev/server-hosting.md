# OS served by `HackerOs.Server` and automatic connection

Work item `exec/8-server-wasm` (`exec` series), ADRs
[0130](adr/0130-server-serves-webassembly-os-and-api.md),
[0134](adr/0134-automatic-server-connection-and-status-icon.md) and
[0137](adr/0137-server-hosting-implementation-adjustments.md).

## Goal

- `HackerOs.Server` serves **the same OS as the PWA**: the published files of
  `OS/HackerOs.Ecosystem`, as is. The OS runs in the browser in
  WebAssembly, with its Web Workers, its metrics and `/proc/hackeros`. The
  server keeps only its API (accounts, synchronization, network relay,
  administration).
- An OS served by a server **connects to it on its own**, without asking, and an
  notification area icon shows the state. It keeps working without a
  server (AGENTS.md I.1).

## Architecture

```text
 Navigateur                                        HackerOs.Server
 ──────────                                        ───────────────
 index.html, _framework/, apps  ◄── MapStaticAssets ── fichiers publiés de l'Ecosystem
 GET hackeros-host.json         ◄── middleware ──────── {"isServer":true, ...}
 ServerPresenceService
   GET /api/hackeros/host-info  ◄── VersionEndpoints ── version de l'API
   comptes, synchro, relais     ◄── API (HTTP, WebSocket)
 ServerStatusIndicator (barre des tâches, zone de notification)
```

| Part | File |
|---|---|
| Server pipeline | `Server/HackerOs.Server/Program.cs` |
| Rewriting pages to `index.html` | `Server/HackerOs.Server/Endpoints/OsPageRewrite.cs` |
| `host-info` and `hackeros-host.json` | `Server/HackerOs.Server/Endpoints/VersionEndpoints.cs` |
| Contract | `Server/HackerOs.Server.Contracts/Hosting/HostInfoContracts.cs` |
| PWA "no server" file | `OS/HackerOs.Ecosystem/wwwroot/hackeros-host.json` |
| Detection and verification | `Platform/HackerOs.Platform.Core/ServerConnection/HostInfoClient.cs` |
| State and retry | `Platform/HackerOs.Platform.Core/ServerConnection/ServerPresenceService.cs` |
| Icon and panel | `Platform/HackerOs.Platform.Blazor/Shell/ServerStatusIndicator.razor` (`.razor.cs`, `.razor.css`) |
| Notification area | `Platform/HackerOs.Taskbar.Blazor/Taskbar.razor` (`NotificationAreaContent`) |
| Composition | `OS/HackerOs.Ecosystem.Host/EcosystemServiceCollectionExtensions.cs`, `HackerOsBrowserHostExtensions.cs` |

## Starting the server

```bash
dotnet run --project Server/HackerOs.Server   # http://localhost:5000 par défaut
```

The browser opens the server's address and receives the OS. A middleware placed
right after `builder.Build()` adds `Cross-Origin-Opener-Policy` and
`Cross-Origin-Embedder-Policy: credentialless` to all responses, according to
`HackerOs:CrossOriginIsolation` in the server's `appsettings.json` (`auto` by
default; `off` adds nothing): the page is isolated (ADR 0131, 0143). In production,
`dotnet publish Server/HackerOs.Server` copies the Ecosystem files into
its `wwwroot`. Any page address (`/`, a deep link) receives
`index.html`; `/api/...`, `/health`, `/openapi`, `/scalar` and files are
never rewritten.

## Automatic connection

1. At desktop startup, the OS reads `/etc/hackeros/server.config`. If
   `auto-connect` is not `false` (or `no`, `off`, `0`), it reads
   `hackeros-host.json` on its own origin: the PWA answers "no
   server", `HackerOs.Server` answers "server".
2. The chosen server is the one of the account linked to the device (Settings, ADR
   0028), otherwise the one that served the OS.
3. The OS calls `GET /api/hackeros/host-info`: a response in API 1.x =
   connected; another version = **Error**; no response = **Offline**,
   retry after 5, 10, 20, 40 then 60 s. Once connected, it rechecks every
   30 s.

```ini
# /etc/hackeros/server.config (facultatif, administrateur)
[server]
auto-connect=false
```

## Status icon

| State | Icon | Meaning |
|---|---|---|
| Connected | cloud with check mark, green | the server answers, the device has an account |
| Connected, not identified | cloud, blue | the server answers, no account yet |
| Connecting… | spinning circle | check in progress |
| Offline | crossed-out cloud, orange | server unreachable, the OS keeps going |
| Error | cloud with alert, red | incompatible API version or invalid response |

Without a server, the icon does not appear. A click opens the panel: state,
address, account, last synchronization, error detail, and the buttons
"Sign in" (account name and password, "Log in" or "Create an
account"), "Reconnect" and "Server settings". Escape closes the
panel; the state change is announced (`aria-live`).

## Accounts from several devices

The client's PBKDF2 salt is derived from the account name and the login registers
a new device: you can create the account in one browser and log in to it
from another (ADR 0137 §4). An account created before this change
can no longer log in with a password.

## Key decisions

- Published Ecosystem files served as is, with no second startup to
  maintain (ADR 0130 §2, E1 confirmed by Benoit).
- Connection without asking, icon in the notification area (ADR 0134, E2
  from Benoit).
- Detection through a file that exists everywhere, so as never to write an
  error in the console of a PWA without a server (ADR 0137 §1).

## Tests

- `Tests/HackerOs.Server.Tests/ServerOsHostingIntegrationTests.cs`: OS page
  on `/` and a deep link, 404 for an unknown API route, detection
  on both addresses, account created then used from a second device,
  wrong password refused.
- `Tests/HackerOs.Platform.Core.Tests/ServerConnection/ServerPresenceServiceTests.cs`:
  connection without account and without asking, static host without icon, server linked
  by hand, offline then retry with increasing delay, incompatible API,
  "Sign in", `auto-connect=false`, identical salt from one device to another.
- `build/verify-hosts.sh` (`Tests/HackerOs.HostSmoke.Tests`): `server`
  run with workers, metrics and icon; no icon on the others.

## Task list

- [x] Serve the published Ecosystem files from `HackerOs.Server`.
- [x] Remove Interactive Server, direct clients and references to apps.
- [x] Rewrite OS pages to `index.html` in all environments.
- [x] `host-info` and `hackeros-host.json` (server and PWA).
- [x] `ServerPresenceService`: detection, verification, retry, `auto-connect`.
- [x] Icon and panel in the taskbar notification area.
- [x] Stable client salt and device registered at login.
- [x] Server and Platform Core tests and run of the three hosts.
- [x] Docs, ADR 0137 and index.
