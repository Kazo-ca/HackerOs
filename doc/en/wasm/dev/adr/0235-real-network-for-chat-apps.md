> [Version française](../../../../fr/wasm/dev/adr/0235-real-network-for-chat-apps.md)

# ADR 0235: the real network for chat apps (WebSocket in the SDK, TLS by the relay, ports requested by the app)

## Status

Accepted on 2026-11-07; Benoit chose the recommendations (questions 3, 4, 5,
9, 10 and 11 of the thread "Planifier l'app IRC", see "Questions asked to
Benoit"). Code: issue [#395](https://github.com/Kazo-ca/Os/issues/395),
series `real-network/base`.

- **Completes:** [ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md)
  and [ADR 0108](0108-server-relay-implementation-adjustments.md) (server
  relay: TLS to the target and ports declared by the app).
- **Does not change:** the dangerous capability `network.real.access` and its
  approval, the server-side grant check, the blocked address ranges, the pinned
  address, the quotas.
- **For:** the IRC app ([Os-App-IRC](https://github.com/Kazo-ca/Os-App-IRC),
  plan `doc/en/plan.md`).

## Context

- A browser cannot open a TCP connection. An app reaches a real TCP server only
  through the relay of the **optional server**
  (`KazoTcpClient.ConnectAsync`, [network relay](../server-network-relay.md)).
  Without the server, a chat app reaches nothing, while no feature may depend
  on the server (AGENTS.md §I.1).
- The SDK has no WebSocket API. Yet many modern chat servers (IRC over
  WebSocket, IRCv3) accept a WebSocket connection that the browser can open
  on its own.
- TLS does not work over `KazoTcpClient`: `SslStream` does not exist in .NET
  for the browser. The encrypted IRC port (6697) is out of reach, and in clear
  text (6667) the account password travels in clear from the server to the
  internet.
- The shipped relay accepts only TCP ports 80 and 443 (`Tcp:AllowedPorts`).
  An app that needs another port depends on an administrator editing
  `appsettings.json`.

## Decision

### 1. `KazoClientWebSocket` in the SDK

- An SDK class `KazoClientWebSocket` (namespace `Kazo.Os.AppSdk.Net`) follows
  `System.Net.WebSockets.ClientWebSocket`, the way the rest of the SDK follows
  .NET with the `Kazo` prefix: `Options.AddSubProtocol`, `ConnectAsync(Uri)`,
  `SendAsync`, `ReceiveAsync`, `CloseAsync`, `State`, `SubProtocol`,
  `CloseStatus`.
- The OS opens the browser's real `WebSocket` (a JavaScript module of Browser
  Infrastructure, never of the app) and hands the frames to
  `AppNetworkGateway`, which counts the traffic in `/proc/net/dev` as it does
  for `RelayedSocket`.
- Capability: **`network.real.access`**, the same as for the relay. One grant
  says "this app talks to the real network". Without it:
  `UnauthorizedAccessException`.
- **The server is not needed.** The limits are the browser's: no free headers,
  `wss://` only when the OS is served over `https`, and a server refusal
  without detail (`WebSocketException` with `WebSocketError.Faulted`).
- An address of a simulated network host is refused for now
  (`WebSocketError.Faulted`, message "simulated host"): TCP to a simulated
  host is issue [#393](https://github.com/Kazo-ca/Os/issues/393).

### 2. TLS opened by the relay

- `KazoTcpClient.ConnectAsync` gets an overload with options
  (`KazoTcpConnectOptions`: `UseTls`, `TargetHost` for SNI and certificate
  checking).
- With `UseTls`, the stream route `GET /api/proxy/tcp` receives `tls=1`: the
  server connects the socket, performs the TLS handshake with the target
  (`SslStream`, certificates checked against the system store, name checked
  against `TargetHost`), and only then accepts the WebSocket. The app reads and
  writes clear text in the stream; encryption is between the server and the
  target.
- An invalid certificate refuses the connection with the new code
  `TLS_FAILED` (close `4001`), surfaced as an `AuthenticationException` in the
  SDK, like `SslStream`. No option accepts an invalid certificate.
- `ProxyStreamOpened` adds the negotiated TLS version and the certificate
  subject, so the app can show them.
- The browser → server leg stays protected by the server's own `https` (which
  the relay already requires for the token).

### 3. Ports requested by the app

- The app declares in its manifest the real ports it needs (TCP and UDP; the
  exact shape of the `[AppManifest]` property is set by the work item, for
  example `RealNetworkPorts = ["tcp/6697", "tcp/6667"]`).
- The `network.real.access` approval window shows them ("this app wants to
  reach real servers on ports 6697 and 6667"). The synced grant keeps the list
  of approved ports.
- The relay accepts a port if it is in `AllowedPorts` **or** declared and
  approved for that app, **unless** it is in the new policy list
  `BlockedPorts`. `BlockedPorts` always wins. Shipped values: at least 25
  (SMTP, against spam); the work item completes and documents the list.
- `AllowPrivateNetworks`, the always-blocked addresses (loopback, link-local,
  cloud metadata) and the quotas apply as before. `AllowAllPorts` keeps its
  meaning.
- Settings shows `BlockedPorts` and, for each granted app, its declared ports.

### 4. Order

A `real-network/base` series in this repository, one work item per point
(`real-network/1-websocket`, `real-network/2-relay-tls`,
`real-network/3-declared-ports`), then an OS release that ships them. The IRC
app work items that connect wait for that release; those that need no network
(solution, protocol, interface) start before.

## Questions asked to Benoit

Benoit's answers (2026-11-07): 3 C, 4 A, 5 A with the precision "apps can now
ask to connect to a port if no policy refuses it", then 9, 10 and 11: the
recommendations.

3. How does the IRC app connect?
   - A) Through the server relay only.
   - B) Direct WebSocket only (new SDK API).
   - C) Both: WebSocket when the network accepts it, otherwise the relay
     (option available only when the server is there). **(Chosen)**
4. Encrypted connection (TLS, 6697)?
   - A) The relay opens TLS to the target (OS change + ADR).
     **(Recommended, chosen)**
   - B) 6667 in clear text only first, TLS later.
5. IRC ports on the relay?
   - A) Add the IRC ports to the shipped ports. **(Recommended, chosen, with the
     precision above, detailed by question 9)**
   - B) Change nothing, the administrator adds the ports.
9. "Ask for a port if no policy refuses it"?
   - A) The app declares its ports in its manifest, the approval shows them,
     the server accepts them unless `BlockedPorts`. **(Recommended, chosen)**
   - B) Every port for every app except `BlockedPorts`, no declaration.
10. How are the OS changes organized?
    - A) One ADR in three sections, one `real-network/base` series.
      **(Recommended, chosen)**
    - B) Three ADRs and three series.
11. Capability for the direct WebSocket?
    - A) The same `network.real.access`. **(Recommended, chosen)**
    - B) A new capability `network.real.websocket`.

## Consequences

- A chat app works **without the server** with networks that accept
  WebSocket, and reaches the others (TCP, TLS included) when the server is
  connected.
- A third-party app no longer needs an administrator to open its ports: it
  declares them, the user sees them at approval, the server keeps the last
  word with `BlockedPorts`.
- The SDK grows (`KazoClientWebSocket`, `KazoTcpConnectOptions`, manifest
  property): `PublicAPI.Shipped.txt`, `sdk-compat` fixture and a minor SDK
  version.
- Planned tests: WebSocket refused without the capability, traffic counted;
  TLS accepted, invalid certificate refused (`TLS_FAILED`), wrong target name
  refused; declared and approved port accepted, declared port in
  `BlockedPorts` refused, undeclared port refused.
- Docs to update with the code: [`server-network-relay.md`](../server-network-relay.md),
  [`capabilities.md`](../../framework/capabilities.md), `features.md`,
  `changes/unreleased` entry.
