> [Version française](../../../fr/wasm/dev/server-network-relay.md)

# Network relay of the optional server

Work item `v3/10-server-proxy` of the [completion plan](plans/wasm-v3-completion-plan.md),
decisions in [ADR 0102](adr/0102-server-network-relay-tcp-udp-and-policy.md)
and its adjustments in [ADR 0108](adr/0108-server-relay-implementation-adjustments.md).

## Goal

The optional server (`Server/Kazo.Os.Server`) relays to the real network what
a browser cannot do alone:

- HTTP requests of **all** methods, with their body;
- **TCP streams** (`KazoTcpClient.ConnectAsync`);
- **UDP datagrams** (`KazoUdpClient`).

An app that reaches a **WebSocket** server does not need the relay:
`KazoClientWebSocket` opens the browser's `WebSocket`, without the server, with
the same `network.real.access` capability (ADR 0235 §1,
[`sdk-network.md`](../framework/sdk-network.md#websocket-to-a-real-server-adr-0235-1)).

It checks itself that the app is allowed to use the relay, applies a
policy and quotas configured in `appsettings.json`, and writes a
warning at startup when that policy is weakened. Without a server,
nothing changes: the OS behaves like a machine without a default
gateway.

The relay is an API independent of the rendering mode of the interface served by
the server (ADR 0102): this work item does not touch rendering, which `exec/8`
takes up.

## Architecture

```
App (SDK)                         OS (Platform Core)                     Serveur
KazoHttpClient ──► AppNetworkGateway.SendRealAsync ──► IProxyClient ──► POST /api/proxy/http
KazoTcpClient  ──► AppNetworkGateway.ConnectTcpAsync ─┐                  GET  /api/proxy/tcp (WebSocket)
KazoUdpClient  ──► AppNetworkGateway.ConnectUdpAsync ─┴► OpenStreamAsync ► GET  /api/proxy/udp (WebSocket)
Paramètres     ──► KazoServerConnection.GetRelayPolicyAsync ────────────► GET  /api/proxy/policy
```

### Contracts (`Kazo.Os.Server.Contracts/Proxy`)

| Type | Role |
|---|---|
| `ProxyHttpRequest` | carries `Body` and `ContentType`; `Create(...)` computes `BodyBytes` and `BodyHash` (lowercase hexadecimal SHA-256). |
| `ProxyPolicyResponse` | effective policy: relay active, HTTP/TCP/UDP ports, blocked TCP/UDP ports, private ranges, quotas, open streams, granted apps, approved ports per app (`AppPorts`), warnings. |
| `ProxyStreamRoutes`, `ProxyStreamCloseCodes` | routes, query parameters and close codes of the streams. |
| `ProxyStreamRequest`, `ProxyStreamOpened`, `ProxyStreamClosure` | opening of a stream (with `UseTls` and `TargetHost`, ADR 0235 §2), pinned address (with `TlsProtocol` and `CertificateSubject`), reason for closing. |
| `IProxyStreamChannel` | an open stream: `SendAsync`, `ReceiveAsync` (`null` at the end), `Closure`. |
| `ProxyStreamException` | refusal to open with its `ProxyErrorCode`. |

New error codes: `GRANT_MISSING` (403), `RELAY_DISABLED` (503),
`BODY_MISMATCH` (400), `CONNECTION_REFUSED` and `HOST_UNREACHABLE` (502),
`TLS_FAILED` (stream refused, close `4001`, ADR 0235 §2).

### Server

| Class | Role |
|---|---|
| `ProxyOptions`, `ProxyRelayPolicy` | `KazoOs:Proxy` section; `ProxyRelayPolicy.From` sets the default values and computes the effective limits. |
| `ProxyGrantAuthority` | `GetGrantAsync`: the app is in `GrantedApps` or a synchronized `grants` record of the account gives it `network.real.access`; also returns the approved ports of that record (`realNetworkPorts`, ADR 0235 §3). `ListGrantsWithPortsAsync` lists them for the policy. |
| `ProxyService` | common validations (relay active, app, grant, port, pinned address, blocked ranges, quotas); `ExecuteHttpRequestAsync` sends the body; `OpenStreamAsync` connects the socket. |
| `SocketProxyStreamConnector`, `SocketProxyStreamChannel` | TCP/UDP connection (10 s), idle timeout, byte counters, audit at close; the channel reads and writes through the TLS session when there is one. |
| `IProxyTlsNegotiator`, `SslStreamProxyTlsNegotiator` | TLS handshake with the target (`SslStream`, 10 s), certificate checked by the system store and the `TargetHost` name; `TLS_FAILED` otherwise. |
| `ProxyStreamWebSocketPump` | accepts the WebSocket and pumps frames in both directions. |
| `ProxyPolicyStartupReporter` | startup warnings (events 1020 to 1022). |

### OS

| Class | Role |
|---|---|
| `HttpProxyClient` | HTTP and streams (WebSocket `ws`/`wss`, token in `access_token`). |
| `WebSocketProxyStreamChannel` | waits for the opening text frame or the `4001` close; splits a TCP send into 64 KiB blocks. |
| `AppNetworkGateway` | `ConnectTcpAsync`, `ConnectUdpAsync`, HTTP relay with body. |
| `RelayedSocket` | `IKazoSocket` over a stream: counts traffic (`/proc/net/dev`) and kernel memory. |
| `AppServerConnectionGateway.GetRelayPolicyAsync` | server policy for Settings, without ever exposing the token. |

## Stream protocol

1. The client opens `GET /api/proxy/tcp?host=…&port=…&appId=…&access_token=…`
   (or `/udp`) as a WebSocket. The `access_token` parameter is accepted only on
   these two routes; elsewhere only the `Authorization` header counts. Over TCP,
   `tls=1` asks for TLS to the target and `targetHost=…` gives the name to check
   when it differs from `host` (ADR 0235 §2).
2. The server validates the request and connects the socket **before** accepting the
   WebSocket. With `tls=1`, it also completes the TLS handshake with the target
   before accepting.
3. Success: the first frame is a **text** JSON frame
   `ProxyStreamOpened` (pinned address, port; with TLS, `tlsProtocol`, for
   example `TLS 1.3`, and `certificateSubject`). All the following ones are
   binary: a TCP message is a sequence of bytes (64 KiB at most), a
   UDP message is a datagram (65,507 bytes at most, empty allowed).
4. Refusal: the WebSocket is accepted then closed with `4001` and the reason
   `"<ProxyErrorCode> <message>"`; the client throws `ProxyStreamException`.
5. End: `4000` when the target closes, `4002` after inactivity, `1000` when
   the client closes.

## TLS opened by the relay (ADR 0235 §2)

The browser has no `SslStream`: an app cannot encrypt a TCP stream itself.
With `UseTls`, the server opens TLS to the target and the app reads and writes
clear text in the stream; the encryption runs between the server and the
target, and the browser → server trip is protected by the server's own `https`.

- Order: socket connected, then TLS handshake (SNI = `TargetHost`, or `host`
  without it), then only the WebSocket accepted. A refused target never hands
  the app a byte.
- Validation: the chain must end at a root of the server's system store, the
  name must match, the certificate must be valid at this date. The versions are
  those the system accepts (TLS 1.2 and 1.3). Revocation follows the
  `SslStream` default (no OCSP/CRL request), like `HttpClient`.
- **No option to accept an invalid certificate**, neither in
  `appsettings.json` nor in the SDK.
- Refusal: untrusted, expired or wrong-name certificate, clear-text target, or
  a handshake that does not finish within 10 s → `TLS_FAILED`, close `4001`,
  audit `PROXY_TLS_FAILED`. TLS over UDP (DTLS) or a `TargetHost` that is not a
  host name → `MALFORMED_REQUEST`; a simulated `TargetHost` →
  `SIMULATED_DOMAIN_BLOCKED`.
- The `PROXY_TCP_OPEN` audit records the TLS version (`"tls":"TLS 1.3"`, or `null`).
- Ports are still filtered by `Tcp:AllowedPorts` (6697 is not shipped; ports
  declared by the app come with ADR 0235 §3).

## Configuration (`appsettings.json`)

```json
"KazoOs": {
  "Proxy": {
    "Enabled": true,
    "Http": { "AllowedPorts": [ 80, 443 ] },
    "Tcp":  {
      "AllowedPorts": [ 80, 443 ],
      "BlockedPorts": [ 25, 111, 135, 137, 138, 139, 445, 465, 587, 2049 ]
    },
    "Udp":  {
      "AllowedPorts": [ 53, 123 ],
      "BlockedPorts": [ 19, 111, 137, 138, 389, 1900, 11211 ]
    },
    "AllowAllPorts": false,
    "AllowPrivateNetworks": false,
    "Quotas": {
      "Enabled": true,
      "MaxResponseBytes": 10485760,
      "MaxRequestBytes": 10485760,
      "MaxConcurrentPerDevice": 8,
      "MaxStreamsPerDevice": 4,
      "MaxRedirectHops": 5,
      "StreamIdleTimeoutSeconds": 300
    },
    "GrantedApps": [
      "org.kazoos.commands.curl", "org.kazoos.commands.wget",
      "org.kazoos.commands.ping", "org.kazoos.cmd.cat",
      "org.kazoos.commands.nmap", "org.kazoos.browser"
    ]
  }
}
```

These are the shipped values. A missing or empty list keeps the
default value: the binder adds to lists initialized in code, so
the defaults are set by `ProxyRelayPolicy.From`.

- **Allow everything**: `AllowAllPorts: true` opens all ports,
  `AllowPrivateNetworks: true` the private ranges (10/8, 172.16/12,
  192.168/16, fc00::/7…). Loopback, link-local, cloud
  metadata (169.254.169.254) and unspecified addresses always remain
  blocked.
- **Disable quotas**: `Quotas:Enabled: false` lifts the limits on
  size, concurrency, streams and inactivity. `MaxRedirectHops` is still
  applied (ADR 0108 §3).
- **Turn off the relay**: `Enabled: false`; every request receives
  `RELAY_DISABLED`.

At startup, the server logs one warning per weakening
(`AllowAllPorts`, `AllowPrivateNetworks`, quotas disabled, an empty TCP or UDP
`BlockedPorts`), a reminder that the
server is single-user if there is at least one, and a warning if the
relay is turned off. The same sentences are rendered in
`ProxyPolicyResponse.Warnings` and displayed by Settings.

## Ports declared by the app and `BlockedPorts` (ADR 0235 §3)

An app that needs other ports than `AllowedPorts` (IRC: 6697 and 6667)
declares them in its definition, with `network.real.access`:

```csharp
public override IReadOnlyList<string> Capabilities => [AppCapabilities.NetworkRealAccess];
public override IReadOnlyList<string> RealNetworkPorts => ["tcp/6697", "tcp/6667"];
```

The generator writes `"realNetworkPorts": ["tcp/6667", "tcp/6697"]` in
`manifest.json` (sorted, absent when the list is empty). The manifest
validator, hence the generator and `kazopkg`, refuses a badly written port
(form `tcp/N` or `udp/N`, N from 1 to 65535, no range, no leading zero), a
repeated port, an empty list, or ports without `network.real.access`.

The approval window of `network.real.access` and the terminal's text mode show
these ports under the capability ("Real servers on ports: TCP 6667, TCP 6697",
`RealNetworkPortText`).

The relay decides as follows, for a TCP or UDP stream:

1. port in the protocol's `BlockedPorts`: **refused** (`BLOCKED_PORT`), even
   when it is in `AllowedPorts`, declared and approved, or with `AllowAllPorts`;
2. otherwise `AllowAllPorts`, or port in `AllowedPorts`: accepted;
3. otherwise port declared by the app **and** present in the
   `realNetworkPorts` of its synchronized grant (`GrantsSyncPayload.RealNetworkPorts`): accepted;
4. otherwise refused (`BLOCKED_PORT`).

A declared TCP port does not open the same UDP port. An invalid entry of a
grant is ignored, never widened. An HTTP request follows the TCP
`BlockedPorts` (HTTP runs over TCP); the single-port probe of `nmap`
(ADR 0035) is not concerned.

Shipped lists: TCP 25, 465, 587 (mail, against spam sent from the server's
address), 111, 135, 137, 138, 139, 445, 2049 (RPC, Windows file sharing,
NFS); UDP 19, 111, 137, 138, 389, 1900, 11211 (services known for
amplification attacks). A missing list keeps these values; `"BlockedPorts": []`
empties it (`ProxyOptions.KeepExplicitEmptyLists`, the binder cannot bind an
empty array) and the server warns about it at startup.

Settings (server section) shows the blocked TCP and UDP ports and, for each
app whose grant carries ports, those ports.

**Current limit**: no code issues a synchronized grant with its ports yet.
The OS never pushes a grant (ADR 0031) and the server has no route to issue
one; how the OS approval reaches the server is a question put to Benoit in the
thread "Accepter les ports déclarés par les apps". Until then, only the apps of
`GrantedApps` use the relay, with `AllowedPorts`.

## Usage in an app

```csharp
// TCP : un flux comme NetworkStream.
await using KazoNetworkStream stream = await KazoTcpClient.ConnectAsync("example.com", 443);
await stream.WriteAsync(request);
int read = await stream.ReadAsync(buffer); // 0 quand la cible a fermé

// TLS opened by the relay (ADR 0235 §2): the app reads and writes clear text.
await using KazoNetworkStream irc = await KazoTcpClient.ConnectAsync(
    "irc.libera.chat", 6697, new KazoTcpConnectOptions { UseTls = true }, cancellationToken);
string? version = irc.TlsProtocol;               // "TLS 1.3"
string? subject = irc.RemoteCertificateSubject;  // "CN=irc.libera.chat"

// UDP: a socket "connected" to one target.
await using KazoUdpClient udp = await KazoUdpClient.ConnectAsync("pool.ntp.org", 123);
await udp.SendAsync(ntpRequest);
KazoUdpReceiveResult answer = await udp.ReceiveAsync();

// POST relayé avec son corps si l'hôte n'est pas simulé.
using KazoHttpClient http = new();
KazoHttpResponse response = await http.PostAsync(url, new KazoStringContent(json, "application/json"));
```

- A socket requires `network.simulated.write`; the relay additionally requires
  `network.real.access` (dangerous).
- Synchronous reads and writes throw `NotSupportedException`: the
  browser has only one thread.
- A host of the simulated network, or this machine, has no raw stream: the
  connection is refused (`SocketError.ConnectionRefused`).
- Without a server or without `network.real.access`, an unknown host gives
  `SocketError.HostNotFound`.

| Relay refusal | `SocketError` |
|---|---|
| `BLOCKED_PORT`, `BLOCKED_ADDRESS`, `CONNECTION_REFUSED` | `ConnectionRefused` |
| `HOST_UNREACHABLE` | `HostUnreachable` |
| `TLS_FAILED` | `AuthenticationException` (as `SslStream`), `KazoNetworkError.TlsFailed` in the gateway |
| `GRANT_MISSING`, `RELAY_DISABLED`, `SIMULATED_DOMAIN_BLOCKED` | `HostNotFound` |
| other | `ConnectionFailed` (SDK network error) |

Settings (server section) displays the policy of the connected server with
`KazoServerConnection.GetRelayPolicyAsync()`: ports, blocked ports
(`TcpBlockedPorts`, `UdpBlockedPorts`), private ranges, quotas, open streams,
granted apps, ports declared by the approved apps (`AppPorts`) and warnings.

## Key decisions

- Grant checked on the server side by `GrantedApps` **or** by the synchronized
  grants (ADR 0108 §1): without the list, no grant would exist on
  the server and the relay would refuse everything. Question put to Benoit in the
  thread "V3 10: server relay".
- Socket connected before accepting the WebSocket, refusal as close code
  `4001`: a browser `WebSocket` does not read the status of a refused
  upgrade (ADR 0108 §2).
- Token in the request only on stream routes: a browser `WebSocket` cannot
  send `Authorization`.
- Address resolved once and pinned for the connection, against DNS
  rebinding.
- `HEAD` does not ask for a body; any other method relays it, checked by
  its size and hash. A 303 redirect, or 301/302 after a `POST`,
  becomes a `GET` without a body.

- Real sites in the browser (ADR 0210): the `Cookie` header is relayed (only
  `Host`, `Authorization`, `X-Forwarded-For` and `X-Real-IP` stay blocked); with
  `FollowRedirects = false`, the server returns the redirect as is (`Location`,
  `Set-Cookie`) and the OS follows the hops; the Browser (`org.kazoos.browser`)
  is in the default `GrantedApps`. The quota of 8 requests per device stays a
  refusal: the OS keeps at most 6 in flight and retries a `QUOTA_EXCEEDED`.

## Task list

- [x] HTTP request bodies: contract, size and hash check, all methods, redirects.
- [x] `KazoOs:Proxy` options (ports, private ranges, quotas, granted apps) and effective policy.
- [x] Server-side grant check (`GrantedApps` and synchronized grants).
- [x] TCP and UDP streams: connector, channel, WebSocket pump, close codes, audit.
- [x] Token in the request on stream routes only.
- [x] Startup warnings and in the policy response.
- [x] OS WebSocket client and direct server client.
- [x] `KazoTcpClient.ConnectAsync`, `KazoNetworkStream`, `KazoUdpClient`, errors of a real stack.
- [x] `KazoServerConnection.GetRelayPolicyAsync` and Settings panel.
- [x] Tests: service, WebSocket endpoints, direct client, network gateway, policy.
- [x] ADR 0108 and documentation.
- [x] Ports declared by the app (ADR 0235 §3): manifest `RealNetworkPorts`, validation, approval window and terminal, relay (`BlockedPorts`, approved ports of the grant), policy and Settings, empty-list warning, tests.
- [ ] Server-side issuing of the grant with its ports (question 1 of the thread "Accepter les ports déclarés par les apps").
- [x] TLS opened by the relay (ADR 0235 §2, work item `real-network/2-relay-tls`): `tls=1`, `targetHost`, `SslStream` before accepting the WebSocket, `TLS_FAILED`, `ProxyStreamOpened` with the session, `KazoTcpConnectOptions`, tests with a test TLS server (trusted, untrusted and wrong-name certificate, clear-text target, clear-text stream unchanged).

See also [ADR 0191](adr/0191-installable-local-server.md): server installable on the computer or the local network.
