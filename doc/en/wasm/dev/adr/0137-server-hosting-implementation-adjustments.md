> [Version française](../../../../fr/wasm/dev/adr/0137-server-hosting-implementation-adjustments.md)

# ADR 0137: implementation adjustments for the OS served by `HackerOs.Server`

## Status

Accepted on 2026-10-18, work item `exec/8-server-wasm`.

- **Adjusts:** [ADR 0130](0130-server-serves-webassembly-os-and-api.md)
  (server that serves the OS in WebAssembly) and [ADR 0134](0134-automatic-server-connection-and-status-icon.md)
  (automatic connection and status icon) on implementation points;
  their decisions do not change.
- **Fixes:** two old defects of ADR 0028 (server accounts) that
  prevented a second device from connecting to an existing account.

## Context

While writing the work item, four points of ADR 0130 and 0134 did not hold
as written, and connecting to an existing account from another browser
could not succeed. This ADR records what was done instead.

## Decision

### 1. Detection through `hackeros-host.json`, not through a request that fails

ADR 0134 §1 has the OS query `GET /api/hackeros/host-info` on its own origin. On the PWA served by a static host, this address answers 404 and
Chromium writes "Failed to load resource" to the console at each startup
(the hosts' startup tests reject any console error).

- The Ecosystem ships a static file `wwwroot/hackeros-host.json`
  (`{"product":"HackerOS","isServer":false}`), read by the OS next to its
  `index.html` (`HttpHostInfoClient.FindServingServerAsync`, 5 s timeout,
  any error means "no server").
- `HackerOs.Server` answers this address itself (`isServer: true`, server
  and API version, `Cache-Control: no-store`) through a middleware placed
  before routing: `MapStaticAssets` also publishes the Ecosystem's file
  at the same address.
- `GET /api/hackeros/host-info` also exists, as ADR 0134 provides:
  the OS calls it on a known server to check that it answers and speaks
  API 1.x (otherwise **Error** state).
- The published service worker never caches `hackeros-host.json` (its
  content differs from the manifest hash on a server); offline, it answers
  `isServer: false` instead of failing.

### 2. OS pages are rewritten to `index.html` before routing

`MapFallbackToFile("index.html")` only finds the file in a published host
or in Development. The server therefore rewrites every page address (`/`, a deep
link, a reload: path without extension outside `/api`, `/health`,
`/openapi`, `/scalar`, `/_framework`, `/_content`) to `/index.html` before
`UseRouting`, and `MapStaticAssets` serves the page with its fingerprinted
references in all environments. An unknown API route remains a
404.

The Razor SDK never passes a project's isolated CSS bundle
(`HackerOs.Ecosystem.styles.css`, asset of the current project only) to a project that
references it: the same rewrite serves `HackerOs.Server.styles.css` in its place,
which imports the isolated CSS of the same projects (the server only references
the Ecosystem).

### 3. What "connected" means, and when the connection starts

- `ServerPresenceService` (Platform Core) finds the server (the account linked to
  the device, otherwise the one that served the OS if `auto-connect` is not turned off),
  checks that it answers (`host-info`), then rechecks it every 30 s; a
  lost server goes **Offline** and is retried after 5, 10, 20, 40 then
  60 s. Synchronization and the relay remain those of ADR 0101 and 0102:
  they use the linked account, unchanged.
- The loop starts with the desktop (the `ServerStatusIndicator` icon is in
  the taskbar's notification area) and stops at the session
  logout. The login screen never waits for the server.
- `auto-connect=false` in `/etc/hackeros/server.config` (key at the root or
  in `[server]`) only turns off the detection: a server linked by hand
  in Settings is still tracked. Only `false`, `no`, `off` and `0` turn it off.
- Settings is not prefilled: "Sign in" is done in the icon's panel,
  and "Server settings" opens the Settings app. The SDK does not
  change.
- The notification area is a new taskbar parameter,
  `Taskbar.NotificationAreaContent` (public addition to `HackerOs.Taskbar.Blazor`).

### 4. An existing account works from any device

- **Client salt.** `ServerConnectionService` drew a random PBKDF2 salt on
  each call; the hash sent at login could therefore never equal
  the one from creation. The salt is now derived from the account name
  (`SHA-256("hackeros-server-account:" + name)`, 16 bytes); the server
  always re-hashes what it receives with its own random salt. An account
  created before this change keeps its session (refresh token) but
  can no longer reconnect by password: another one must be created.
- **Unknown device.** `POST /api/auth/login` rejected an unregistered device
  (`DEVICE_NOT_REGISTERED`), and registration already required a
  token. Since the password has just been verified, login now registers
  the device (audit `DEVICE_REGISTERED_AT_LOGIN`); a revoked device
  remains rejected (`DEVICE_REVOKED`).

### 5. What disappears from the server

As in ADR 0130 §3: Razor components, `AddInteractiveServerComponents`,
direct clients, in-process assembly transport, ASP.NET Core host
environment, references to apps, and the `HackerOs:ResourceAccounting` section
of its `appsettings.json` (the OS reads the Ecosystem's). The server keeps
a single project reference to the OS: `OS/HackerOs.Ecosystem`.

## Consequences

- `build/verify-hosts.sh`: the `server` walkthrough expects `measured` measurements,
  workers and `kill -9` of a loop, and checks the icon ("Connected, not
  identified" alone, then "Connected" after creating an account from the
  panel); the other walkthroughs check that no server icon
  appears.
- Unreleased public additions (ADR 0132 §1): `HackerOs.Server.Contracts.Hosting`
  (`HostInfoResponse`, `HostInfoRoutes`), `IHostInfoClient`,
  `HttpHostInfoClient`, `ServerPresence*` (Platform Core),
  `ServerStatusIndicator` (Platform Blazor), `Taskbar.NotificationAreaContent`.
- Documentation: [`server-hosting.md`](../server-hosting.md).

## References

- ADR 0028, 0101, 0102, 0130, 0132, 0134.
