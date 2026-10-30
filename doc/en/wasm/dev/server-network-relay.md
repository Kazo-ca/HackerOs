> [Version française](../../../fr/wasm/dev/server-network-relay.md)

# Network relay of the optional server

Work item `v3/10-server-proxy` of the [completion plan](plans/wasm-v3-completion-plan.md),
decisions in [ADR 0102](adr/0102-server-network-relay-tcp-udp-and-policy.md)
and its adjustments in [ADR 0108](adr/0108-server-relay-implementation-adjustments.md).

## Goal

The optional server (`Server/HackerOs.Server`) relays to the real network what
a browser cannot do alone:

- HTTP requests of **all** methods, with their body;
- **TCP streams** (`HackTcpClient.ConnectAsync`);
- **UDP datagrams** (`HackUdpClient`).

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
HackHttpClient ──► AppNetworkGateway.SendRealAsync ──► IProxyClient ──► POST /api/proxy/http
HackTcpClient  ──► AppNetworkGateway.ConnectTcpAsync ─┐                  GET  /api/proxy/tcp (WebSocket)
HackUdpClient  ──► AppNetworkGateway.ConnectUdpAsync ─┴► OpenStreamAsync ► GET  /api/proxy/udp (WebSocket)
Paramètres     ──► HackServerConnection.GetRelayPolicyAsync ────────────► GET  /api/proxy/policy
```

### Contracts (`HackerOs.Server.Contracts/Proxy`)

| Type | Role |
|---|---|
| `ProxyHttpRequest` | carries `Body` and `ContentType`; `Create(...)` computes `BodyBytes` and `BodyHash` (lowercase hexadecimal SHA-256). |
| `ProxyPolicyResponse` | effective policy: relay active, HTTP/TCP/UDP ports, private ranges, quotas, open streams, granted apps, warnings. |
| `ProxyStreamRoutes`, `ProxyStreamCloseCodes` | routes, query parameters and close codes of the streams. |
| `ProxyStreamRequest`, `ProxyStreamOpened`, `ProxyStreamClosure` | opening of a stream, pinned address, reason for closing. |
| `IProxyStreamChannel` | an open stream: `SendAsync`, `ReceiveAsync` (`null` at the end), `Closure`. |
| `ProxyStreamException` | refusal to open with its `ProxyErrorCode`. |

New error codes: `GRANT_MISSING` (403), `RELAY_DISABLED` (503),
`BODY_MISMATCH` (400), `CONNECTION_REFUSED` and `HOST_UNREACHABLE` (502).

### Server

| Class | Role |
|---|---|
| `ProxyOptions`, `ProxyRelayPolicy` | `HackerOs:Proxy` section; `ProxyRelayPolicy.From` sets the default values and computes the effective limits. |
| `ProxyGrantAuthority` | `IsGrantedAsync`: the app is in `GrantedApps` or a synchronized `grants` record of the account gives it `network.real.access`. |
| `ProxyService` | common validations (relay active, app, grant, port, pinned address, blocked ranges, quotas); `ExecuteHttpRequestAsync` sends the body; `OpenStreamAsync` connects the socket. |
| `SocketProxyStreamConnector`, `SocketProxyStreamChannel` | TCP/UDP connection (10 s), idle timeout, byte counters, audit at close. |
| `ProxyStreamWebSocketPump` | accepts the WebSocket and pumps frames in both directions. |
| `ProxyPolicyStartupReporter` | startup warnings (events 1020 to 1022). |

### OS

| Class | Role |
|---|---|
| `HttpProxyClient` | HTTP and streams (WebSocket `ws`/`wss`, token in `access_token`). |
| `WebSocketProxyStreamChannel` | waits for the opening text frame or the `4001` close; splits a TCP send into 64 KiB blocks. |
| `AppNetworkGateway` | `ConnectTcpAsync`, `ConnectUdpAsync`, HTTP relay with body. |
| `RelayedSocket` | `IHackSocket` over a stream: counts traffic (`/proc/net/dev`) and kernel memory. |
| `AppServerConnectionGateway.GetRelayPolicyAsync` | server policy for Settings, without ever exposing the token. |

## Stream protocol

1. The client opens `GET /api/proxy/tcp?host=…&port=…&appId=…&access_token=…`
   (or `/udp`) as a WebSocket. The `access_token` parameter is accepted only on
   these two routes; elsewhere only the `Authorization` header counts.
2. The server validates the request and connects the socket **before** accepting the
   WebSocket.
3. Success: the first frame is a **text** JSON frame
   `ProxyStreamOpened` (pinned address, port). All the following ones are
   binary: a TCP message is a sequence of bytes (64 KiB at most), a
   UDP message is a datagram (65,507 bytes at most, empty allowed).
4. Refusal: the WebSocket is accepted then closed with `4001` and the reason
   `"<ProxyErrorCode> <message>"`; the client throws `ProxyStreamException`.
5. End: `4000` when the target closes, `4002` after inactivity, `1000` when
   the client closes.

## Configuration (`appsettings.json`)

```json
"HackerOs": {
  "Proxy": {
    "Enabled": true,
    "Http": { "AllowedPorts": [ 80, 443 ] },
    "Tcp":  { "AllowedPorts": [ 80, 443 ] },
    "Udp":  { "AllowedPorts": [ 53, 123 ] },
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
      "org.hackeros.commands.curl", "org.hackeros.commands.wget",
      "org.hackeros.commands.ping", "org.hackeros.cmd.cat",
      "org.hackeros.commands.nmap", "org.hackeros.browser"
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
(`AllowAllPorts`, `AllowPrivateNetworks`, quotas disabled), a reminder that the
server is single-user if there is at least one, and a warning if the
relay is turned off. The same sentences are rendered in
`ProxyPolicyResponse.Warnings` and displayed by Settings.

## Usage in an app

```csharp
// TCP : un flux comme NetworkStream.
await using HackNetworkStream stream = await HackTcpClient.ConnectAsync("example.com", 443);
await stream.WriteAsync(request);
int read = await stream.ReadAsync(buffer); // 0 quand la cible a fermé

// UDP : un socket « connecté » à une cible.
await using HackUdpClient udp = await HackUdpClient.ConnectAsync("pool.ntp.org", 123);
await udp.SendAsync(ntpRequest);
HackUdpReceiveResult answer = await udp.ReceiveAsync();

// POST relayé avec son corps si l'hôte n'est pas simulé.
using HackHttpClient http = new();
HackHttpResponse response = await http.PostAsync(url, new HackStringContent(json, "application/json"));
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
| `GRANT_MISSING`, `RELAY_DISABLED`, `SIMULATED_DOMAIN_BLOCKED` | `HostNotFound` |
| other | `ConnectionFailed` (SDK network error) |

Settings (server section) displays the policy of the connected server with
`HackServerConnection.GetRelayPolicyAsync()`: ports, private ranges, quotas,
open streams, granted apps and warnings.

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
  `Set-Cookie`) and the OS follows the hops; the Browser (`org.hackeros.browser`)
  is in the default `GrantedApps`. The quota of 8 requests per device stays a
  refusal: the OS keeps at most 6 in flight and retries a `QUOTA_EXCEEDED`.

## Task list

- [x] HTTP request bodies: contract, size and hash check, all methods, redirects.
- [x] `HackerOs:Proxy` options (ports, private ranges, quotas, granted apps) and effective policy.
- [x] Server-side grant check (`GrantedApps` and synchronized grants).
- [x] TCP and UDP streams: connector, channel, WebSocket pump, close codes, audit.
- [x] Token in the request on stream routes only.
- [x] Startup warnings and in the policy response.
- [x] OS WebSocket client and direct server client.
- [x] `HackTcpClient.ConnectAsync`, `HackNetworkStream`, `HackUdpClient`, errors of a real stack.
- [x] `HackServerConnection.GetRelayPolicyAsync` and Settings panel.
- [x] Tests: service, WebSocket endpoints, direct client, network gateway, policy.
- [x] ADR 0108 and documentation.

See also [ADR 0191](adr/0191-installable-local-server.md): server installable on the computer or the local network.
