> [Version française](../../../../fr/wasm/dev/adr/0191-installable-local-server.md)

# ADR 0191: HackerOS server installable on the computer or the local network

## Status

Proposed on 2026-10-29 (thread "Évaluer un serveur local installable"). Benoit
asked for the ADR and the tracking issue [#267](https://github.com/Kazo-ca/HackerOs/issues/267).
The choices in §2 to §5 are the recommended options of questions Q1 to Q4 asked
to Benoit in the thread; if he picks another one, this ADR is updated before the
first code chantier.

- **Complements:** [ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md) and
  [ADR 0108](0108-server-relay-implementation-adjustments.md) (relay),
  [ADR 0130](0130-server-serves-webassembly-os-and-api.md) (the server serves the OS),
  [ADR 0134](0134-automatic-server-connection-and-status-icon.md) (server
  connection), [ADR 0173](0173-automatic-releases-from-the-version-file.md) (releases).
- **Related:** issue [#266](https://github.com/Kazo-ca/HackerOs/issues/266) (gaps of
  the simulated browser with the relay).

## Context

- Benoit wants a user of the WebAssembly OS (GitHub Pages included) to download
  an application from Settings, install it on their computer or on another
  machine of their local network, without exposing it to the internet, and so
  get the network relay (HTTP, TCP, UDP).
- `HackerOs.Server` already has the relay, accounts, devices, sync, a
  configurable CORS (`Cors:AllowedOrigins`, today `http://localhost:5000` and
  `https://localhost:7000`), and it serves the OS itself. The OS can connect to a
  server whose address is entered in Settings (ADR 0134).
- Missing: service integration (Windows, systemd, launchd), a Docker image, an
  installer, and publishing the server from `release.yml`.
- Browser constraints for an HTTPS page (GitHub Pages):

| Target | Mixed content | Note |
|---|---|---|
| `http://localhost:port` | allowed (Chrome, Edge, Firefox; Safari to check) | Chrome "Local Network Access": permission prompt once per site |
| `http://192.168.x.x:port` | blocked, except the "Local Network Access" exemption of recent Chrome and Edge (private IP literal or `.local` name) | WebSockets were not covered at first |
| `https://…` with a generated certificate | allowed | the certificate must be trusted on each client device |

- The OS isolation (COEP, ADR 0131) blocks neither a CORS `fetch` nor a WebSocket
  to the server: the server only has to allow the origin.
- A server listening locally can be called by any site open in the browser. CORS
  prevents reading the responses and the relay requires a token, but a *DNS
  rebinding* attack bypasses CORS if the `Host` header is not checked
  (`AllowedHosts` is `*` today).

## Decision

### 1. One server, published as an installable application

We ship `HackerOs.Server` as it is (OS, accounts, sync, relay), not a "relay
only" program (Q3, option a). The version is the one of `build/Versions.props`:
the OS checks compatibility with `GET /api/version`.

### 2. Target: the computer first, the local network next over HTTPS

- **Step 1, local computer** (Q1, option a): the server listens on
  `http://localhost:<port>` (loopback only). The GitHub Pages OS calls it without
  mixed content.
- **Step 2, local network** (Q1, option b): the installer can listen on the
  network over HTTPS with a certificate it generates (a local authority specific
  to the installation). The server serves the authority's certificate and a guide
  to trust it on each device (Windows, macOS, Linux, Android, iOS).
- The OS served over `http://IP` (not secure, without service worker or
  isolation) and the Chrome-only exemption are **not** supported modes; they may
  work, with no guarantee.

### 3. Secure defaults

- Listen on loopback only; listening on the network is an explicit choice at
  install time (or `HACKEROS_URLS`).
- `AllowedHosts` filled at install time (`localhost`, `127.0.0.1`, `[::1]`, plus
  the name and IP chosen for the network): an unknown `Host` gets `400`.
- `Cors:AllowedOrigins` contains by default the HackerOS GitHub Pages origin and
  the local origins; the administrator can add more.
- Administrator account created at install time; the relay keeps its checks
  (token, `GrantedApps`, private ranges blocked unless `AllowPrivateNetworks`).
- The OS **never** probes `localhost` on its own from GitHub Pages: it only does
  so after the user enabled the local server in Settings, so that every visitor
  does not see the "Local Network Access" prompt.

### 4. Packaging

- Self-contained single-file `dotnet publish` for `win-x64`, `win-arm64`,
  `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`: .NET does not need to be
  installed.
- Service: `UseWindowsService()` and `UseSystemd()` in `Program.cs`, launchd plist
  for macOS.
- Format (Q2, option a): archive (`.zip` on Windows, `.tar.gz` elsewhere) with a
  script that installs and uninstalls the service (`install.ps1`, `install.sh`),
  plus a Docker image `ghcr.io/kazo-ca/hackeros-server`. Native installers (MSI,
  winget, `.deb`, Homebrew) may come later if the need is confirmed.
- **Unsigned** binaries for v1 (Q4): the documentation explains the SmartScreen
  warning and macOS "Open anyway".

### 5. Releases

`release.yml` (ADR 0173) adds a matrix job that publishes
`hackeros-server-<version>-<rid>.zip|tar.gz` and attaches them to the **same**
GitHub Release as the OS, and a job that pushes the Docker image (`:<version>`
and `:latest`).

### 6. OS Settings

"Local server" section:

- download link to the latest GitHub Release, with the file matching the detected
  system highlighted, and the link to the guide;
- server address, "Test" button, relay state (`GET /api/proxy/policy`);
- **pairing**: the server shows a one-time code (or a `…#pair=<code>` address);
  the OS exchanges it for a device token, without typing the password again.
  Signing in with an existing account stays possible.

## Consequences

- Proposed chantiers, tracked in #267: (1) local server security (§3);
  (2) service and self-contained publish; (3) Dockerfile; (4) `release.yml`;
  (5) Settings and pairing; (6) local network HTTPS; (7) documentation.
- The gaps of #266 (simulated browser capability, cookies, concurrent request
  quota) also apply to this server.
- Without an installed server nothing changes: the OS stays offline for the real
  network (AGENTS.md I.1).
