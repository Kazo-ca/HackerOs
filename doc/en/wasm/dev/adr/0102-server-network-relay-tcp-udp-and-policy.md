# ADR 0102: optional server network relay (HTTP body, TCP streams, UDP, verified grant, policy in `appsettings`)

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work item `v3/10-server-proxy`).

- **Complements:** ADR 0023 (relay part, the rest is replaced by ADR 0092),
  [ADR 0035](0035-nmap-tcp-probe-fallback.md) (TCP probe of `nmap`), ADR 0056 §6
  (fallback to the real network decided by the OS, dangerous capability
  `network.real.access`), ADR 0031 (synchronized grants, the server is
  authoritative).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  line `v3/10-server-proxy`; gap R19.

## Context

- `ProxyEndpoints` exposes `POST /api/proxy/http`, `POST /api/proxy/tcp-probe` and
  `GET /api/proxy/policy`. `Program.cs:21` announces "HTTP/TCP/UDP", but
  `ProxyProtocol.Tcp` and `.Udp` are rejected by `ValidateRequest`
  (`ProxyService.cs:386`) and have no stream contract.
- `ProxyHttpRequest` carries the hash and size of the body, never its bytes:
  a `POST` arrives empty.
- The SSRF protections (`ProxyService.cs`) are constants: simulated suffixes
  rejected, ports 80/443, private ranges blocked, IP pinned against DNS
  rebinding. The quotas too (`MaxResponseBytes` 10 MiB,
  `MaxConcurrentPerDevice` 8, 5 redirects, 5 s probe).
- The server checks the device and the presence of an `AppId`, but not that
  the app has the `network.real.access` grant.

## Decision

### 1. HTTP request bodies

`ProxyHttpRequest` receives the body (`Body`, bytes, at most 10 MiB by default)
with its content type; the server checks that it matches the announced hash and
size, then sends it. `HackHttpClient` (ADR 0056) passes it on when it
falls back to the real network.

### 2. TCP streams

- `GET /api/proxy/tcp?host=…&port=…&appId=…` opened as a **WebSocket**: each
  binary message is a chunk of the stream in one direction, closing the
  WebSocket closes the TCP connection (and conversely, with the close code
  telling who closed).
- Same validations as HTTP (simulated suffixes, private ranges, pinned IP),
  but the allowed ports come from `Proxy:Tcp:AllowedPorts`.
- On the OS side, `HackTcpClient` (ADR 0056) uses it when the target is not on the
  simulated network and the app has `network.real.access`.

### 3. UDP

- `GET /api/proxy/udp?host=…&port=…&appId=…` as a WebSocket: one binary message
  is **one** datagram, in each direction, to a single destination ("connected"
  UDP socket). No broadcast or multicast.
- Allowed ports: `Proxy:Udp:AllowedPorts`, by default `53` (DNS) and `123`
  (NTP). New `HackUdpClient` in the network SDK.

### 4. Grant verified by the server

The server rejects (`403`, `ProxyErrorCode.GrantMissing`) a request whose
app does not have the `network.real.access` grant for this user in **its**
synchronized grants (ADR 0031, the server is authoritative). The OS check
remains; the server's check prevents a modified client from bypassing it.

### 5. Policy in `appsettings`

`HackerOs:Proxy` section of `appsettings.json`, read at startup through
`IOptions`:

| Key | Default | Effect |
|---|---|---|
| `Enabled` | `true` | turns off the whole relay |
| `Http:AllowedPorts` | `[80, 443]` | HTTP ports |
| `Tcp:AllowedPorts` | `[80, 443]` | TCP stream ports (`nmap` keeps its probe on any port, ADR 0035) |
| `Udp:AllowedPorts` | `[53, 123]` | UDP ports |
| `AllowAllPorts` | `false` | "allow everything": no port filter |
| `AllowPrivateNetworks` | `false` | allows private ranges (the local network of the server machine); loopback and cloud metadata (`169.254.0.0/16`) remain blocked |
| `Quotas:Enabled` | `true` | "disable quotas" |
| `Quotas:MaxResponseBytes`, `MaxRequestBytes`, `MaxConcurrentPerDevice`, `MaxStreamsPerDevice`, `MaxRedirectHops`, `StreamIdleTimeoutSeconds` | 10 MiB, 10 MiB, 8, 4, 5, 300 | limits |

- At startup, the server writes a **warning** for each risky setting
  (`AllowAllPorts`, `AllowPrivateNetworks`, disabled quotas) and reminds that
  it is single-user.
- The relay is a server API, independent of how the server serves the
  interface: it works the same way today (server-side rendering,
  ADR 0027) and when the server serves the OS in WebAssembly with only its
  API (Benoit's choice of 2026-10-17, work item `exec/8`).
- `GET /api/proxy/policy` returns the effective policy, which the OS displays
  in Settings (server connection).
- The comment in `Program.cs` says what really exists.

## Consequences

- `v3/10` adds the contracts (`HackerOs.Server.Contracts/Proxy`), the two
  WebSocket endpoints, the options, the grant check, `HackUdpClient`, and
  server tests (body passed through, TCP stream round trip against a local echo with
  `AllowPrivateNetworks`, UDP datagram, missing grant, warnings).
- The Ecosystem host without a server does not change: without a server, the real network
  is simply not available.
- `sdk-network.md`, `sdk-reference.md`, `capabilities.md` and the server docs
  (`docs/old/hosting-model.md` by reference, `project-overview.md`) are updated.
