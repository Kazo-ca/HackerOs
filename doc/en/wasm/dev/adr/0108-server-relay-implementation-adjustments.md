# ADR 0108: implementation adjustments for the server relay

## Status

Accepted on 2026-10-17 (work item `v3/10-server-proxy`).

- **Complements:** [ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md)
  (server relay), without changing its decisions.
- **Document:** [`server-network-relay.md`](../server-network-relay.md).

## Context

While implementing ADR 0102, five points were not settled in it or could not be
as written:

1. §4 wants the server to check the `network.real.access` grant in **its**
   synchronized grants. But the OS never pushes a grant (ADR 0031 §1) and
   no server API issues any: the server database contains none.
   Applied alone, the check would reject everything, `curl` included.
2. §2 and §3 open the streams as WebSockets, but a browser `WebSocket`
   cannot carry an `Authorization` header, and it also cannot
   read the HTTP status of a refused upgrade.
3. §5 does not say what happens to `MaxRedirectHops` when quotas are
   disabled.
4. ADR 0102 does not say which local capability a stream requires, nor what
   happens to a connection to a host of the simulated network, which has no
   raw byte stream.
5. The server can also serve the OS with server-side rendering (ADR 0027): the
   direct client (ADR 0036) must open a stream without a WebSocket.

## Decision

### 1. Server grant: operator list, plus synchronized grants

The server grants the relay to an app if its identifier appears in
`HackerOs:Proxy:GrantedApps` (`appsettings.json`) **or** if the latest
revision of a `grants` record of the account names this app with
`network.real.access` without being revoked. The default list contains the
system commands that declare `network.real.access`: `curl`, `wget`,
`ping`, `cat` and `nmap`, which the OS already grants through its system policy.

This is the recommended option of the question asked to Benoit in the
thread "V3 10: server relay" on 2026-10-17; if his answer chooses another,
a new ADR replaces this paragraph.

The app identifier remains declared by the client: the check prevents a modified
client from opening the relay to an app the server does not know,
not from impersonating `curl`. The server is single-user
(ADR 0027); the real boundary remains the account and the device.

### 2. Opening a stream

- The server validates the request and connects the socket **before** accepting the
  WebSocket. On success, its first frame is a **text** frame
  (`ProxyStreamOpened`, JSON: pinned address and port); all
  following frames are binary.
- A refusal accepts the WebSocket then closes it immediately with code `4001`
  and the reason `« <ProxyErrorCode> <message> »`. The other codes: `4000`
  the target closed, `4002` inactivity.
- The access token can be passed in the `access_token` query parameter,
  on the two stream routes only, like SignalR. Elsewhere, only the
  `Authorization` header is accepted.

### 3. Quotas disabled

`Quotas:Enabled = false` lifts the limits on size, concurrency, streams
and inactivity. `MaxRedirectHops` remains applied: without it a redirect
loop never ends.

### 4. OS side

- A TCP or UDP socket requires `network.simulated.write`: it carries
  arbitrary bytes in both directions, like a `POST` request.
- The simulated network has no raw streams: a connection to a host it
  knows, or to this machine, is refused (`ECONNREFUSED`). An unknown host
  goes through the relay if the app has `network.real.access`, otherwise it is
  not found.
- A relay refusal becomes the error of a real stack: blocked port or range
  and target refusal give `ConnectionRefused`, silent target
  `HostUnreachable`, missing grant or relay turned off `HostNotFound` (no
  route).

### 5. In-process streams

`IProxyClient.OpenStreamAsync` has two implementations: `HttpProxyClient`
(WebSocket, WebAssembly hosts) and `DirectProxyClient` (the server's own
socket, OS served with server-side rendering), with the same validations.

## Consequences

- The real network works as soon as a device is connected, for system
  commands, without configuration.
- A third-party app that wants the relay must be added by the operator to
  `GrantedApps`, or receive a grant issued by the server (no API does so
  yet, as ADR 0031 already noted).
- The configuration binder appends to lists initialized in code instead of
  replacing them: default values are set by
  `ProxyRelayPolicy.From`, and an empty or missing list keeps the default.
