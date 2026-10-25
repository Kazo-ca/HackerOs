# ADR 0035: Single-Port TCP Reachability Probe for nmap's Real-Network Fallback

## Status

Accepted on 2026-08-17.

## Context

`docs/server-implementation-pass.md`'s "Pass N+1a" originally named `curl`, `nmap`, and `cat` as
candidates for the real-network fallback pattern `ping` already proved (ADR 0028). `curl -I` was
wired in a prior pass with no new server capability needed — `IProxyClient.ExecuteHttpRequestAsync`
already returned real status/headers over HTTP.

`nmap` is different in kind, not just scope. The existing HTTP proxy (`ProxyService.cs`) only ever
allows outbound requests to port 80/443 — a deliberate, narrow allow-list that is itself the
server's main defense against being used as a scanning tool against arbitrary internet hosts.
`nmap`'s entire purpose is probing arbitrary ports, often across a range (`-p 1-1000` by default).
Naively extending the HTTP proxy's port allow-list, or adding a new endpoint that accepts a list of
ports per request, would turn the optional server into a general-purpose port-scanning oracle
usable against any address the SSRF address-range checks don't already block — a materially larger
abuse surface than anything shipped so far, and one `docs/server-implementation-pass.md`'s own open
questions explicitly flagged as needing its own security review before building.

Presented with this trade-off, the user chose the narrowest option: a single-port TCP reachability
probe — mirroring `ping`'s own real-network fallback (one HTTP HEAD, not a crawl) rather than
building anything scan-shaped.

## Decision

### 1. Exactly one TCP connect attempt per request, no data exchanged, no ranges

New contracts `ProxyTcpProbeRequest`/`ProxyTcpProbeResponse` (`Server/HackerOs.Server.Contracts/Proxy/ProxyContracts.cs`)
carry a single host and a single port. There is no list-of-ports or range shape anywhere in the
contract — a client cannot ask for more than one port per call even if it wanted to. The server
(`ProxyService.ExecuteTcpProbeAsync`) does exactly one `Socket.ConnectAsync` and reports one of
three outcomes (`Open`/`Closed`/`Filtered`), matching real TCP scan semantics (successful handshake,
active RST, or timeout) without ever reading or writing application data.

### 2. Reuse every existing SSRF protection; do not reuse the HTTP port allow-list

`ExecuteTcpProbeAsync` reuses `ValidateDeviceOwnershipAsync`, `ValidateSimulatedDomain`, and the
full blocked-address-range check in `ResolveAndValidateAddressAsync` — loopback, RFC-1918, link-local,
cloud metadata, and the rest are exactly as blocked for a probe as for an HTTP request. The one
check deliberately **not** reused is the 80/443 port allow-list: `ResolveAndValidateAddressAsync`
gained an `enforceHttpPortAllowList` flag, `false` for the probe path, since accepting an arbitrary
target port is the entire point. This is the one place this feature is intentionally more permissive
than the HTTP proxy — everything else is at least as strict.

### 3. Fail fast: a 5-second cap, not 30

The HTTP proxy allows up to 30 seconds per request. A single-port probe is capped at 5 seconds
(`MaxTcpProbeTimeoutSeconds`) — a scan-shaped operation should fail fast rather than hold a
connection slot waiting on a filtered/unresponsive host. The existing per-device concurrency limit
(8 concurrent, shared with the HTTP proxy) still applies, and every probe is audit-logged
(`PROXY_TCP_PROBE`) the same way HTTP requests are.

### 4. `nmap`'s client-side trigger: exactly one explicit `-p <port>`, never a range or the default

`NmapCommand`'s real-network fallback only fires when the simulated network doesn't recognize the
target **and** the user passed `-p` with a single explicit port (`firstPort == lastPort` and the
argument contained no `-`). The default port range (1–1000) and any explicit range (`-p 1-100`)
always fall back to the pre-existing "Host seems down" simulated-not-found message — never a real
probe, regardless of connection state. This is enforced client-side by construction (the code path
to `NmapRealHostPortAsync` is simply unreachable for a range), not by a server-side limit that could
be bypassed by a different client — a range scanner would need its own, separately-designed and
separately-reviewed server capability, which this ADR does not add.

### 5. Testable connect boundary

`IProxyTcpConnector`/`SocketProxyTcpConnector` (`Server/HackerOs.Server/Services/ProxyNetworking.cs`)
abstracts the raw socket connect the same way `IProxyAddressResolver`/`IHttpClientFactory` already
abstract DNS resolution and HTTP transport for the existing proxy — unit tests exercise the full
authorization/SSRF/audit pipeline with a fake connector, never touching a real socket.

## Consequences

- `nmap -p <port> <unknown-host>` now returns a real open/closed/filtered result when the device is
  connected to an optional server, instead of always reporting "Host seems down."
- The server gains a second callable network primitive beyond the HTTP proxy, but one scoped to
  exactly one host:port per call with no range shape in the contract — a materially smaller surface
  than the range-scan alternative the user explicitly declined.
- Range scanning (`nmap`'s more common use case: `-p 1-1000` or no `-p` at all) remains
  simulation-only. If it's ever wanted for real, it needs its own ADR and its own security design —
  this decision does not open a path to it by extending the existing contract or endpoint.
- `curl`/`cat` full-body fetch remains blocked on the separate, already-tracked server-side proxy
  body-transfer gap (`docs/server-implementation-pass.md`), unrelated to this change.

## References

- ADR 0028: Client-Side Optional-Server Connection and Proxy Bridge (the `ping` real-network
  pattern this mirrors, and the existing SSRF/address-blocking machinery this reuses)
- ADR 0034: Wire the Terminal Command Catalog (the prerequisite pass that made `nmap` launchable at
  all)
- `docs/server-implementation-pass.md`
