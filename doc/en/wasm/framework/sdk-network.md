> [Version française](../../../fr/wasm/framework/sdk-network.md)

# Network in the SDK

## Purpose

An app accesses the network only through the SDK (ADR 0055, ADR 0056 section 6):
name resolution, ping, HTTP and TCP port probing. The OS alone decides which
network serves a request: the simulated network first, then, if the host does not
exist there, the real network through the optional server's proxy, when it is allowed and
possible. The app sees neither the access token, nor the server address, nor even
whether a server exists.

This document describes the `KazoDns`, `KazoPing`, `KazoHttpClient`,
`KazoTcpClient` and `KazoClientWebSocket` facades, the `IAppNetworkGateway` gateway that carries them and its
`AppNetworkGateway` implementation in Platform Core. It was written by the
work item `rebuild/4-sdk-network` (ADR 0058).

## Architecture

```text
App (Browser, ping, curl, cat, nmap)
  └─ KazoDns / KazoPing / KazoHttpClient / KazoTcpClient     (Kazo.Os.AppSdk.Net)
       └─ KazoExecutionContext.Current.Network : IAppNetworkGateway
            └─ AppNetworkGateway                             (Platform.Core/Network)
                 ├─ capacité (ICapabilityChecker de l'instance)
                 ├─ ISimulatedNetworkService                 (toujours essayé en premier)
                 └─ repli réel : IServerConnectionService + IProxyClient
                      (seulement si network.real.access, serveur connecté, jeton valide)
```

| Type | Project | Role |
|---|---|---|
| `IAppNetworkGateway` | `Kazo.Os.AppSdk` (`Net/`) | Instance gateway: `ResolveAsync`, `ReverseLookupAsync`, `PingAsync`, `SendAsync`, `ProbeTcpAsync`, `DetectOperatingSystemAsync`, then `ConnectTcpAsync`, `ConnectUdpAsync` (ADR 0102) and `ConnectWebSocketAsync` (ADR 0235 §1). Network failures are a result (`KazoNetworkResult<T>`, `KazoNetworkError`), never an exception. |
| `IAppExecutionContext.Network` | `Kazo.Os.AppSdk` | Member with a "not supported" default implementation, so that existing contexts compile. |
| `KazoDns`, `KazoPing`, `KazoHttpClient`, `KazoCookieContainer`, `KazoTcpClient` | `Kazo.Os.AppSdk` (`Net/`) | Facades modeled on `System.Net`: they read the ambient context (ADR 0043) and throw .NET exceptions. |
| `AppNetworkGateway` | `Kazo.Os.Platform.Core` (`Network/`) | Trusted implementation: capabilities, simulated network, real fallback decision, server token. Built by `AppExecutionContextFactory` for each instance. |

`AppExecutionContextFactory` receives `ISimulatedNetworkService`,
`IServerConnectionService` and `IProxyClient` (optional parameters). Without a simulated
network, `Network` throws `NotSupportedException`; without a server or proxy, the real
fallback is simply absent.

Since `web/3-listener-dns`, `AppNetworkGateway` first goes through the
local `kazodns` resolver (`/etc/hosts`, loopback, web apps, hosted sites):
a local target is served by the OS port table, on `lo`, and a `.hack`
name is never relayed to the real network. See
[`listening-ports-and-dns.md`](listening-ports-and-dns.md).

## Capabilities

| Operation | Capability | Without the capability |
|---|---|---|
| DNS, ping, `GET`, `HEAD`, port probing, OS detection | `network.simulated.read` (normal) | `UnauthorizedAccessException` (internal: `AppGatewayAccessDeniedException`) |
| `POST` and any other HTTP method, TCP or UDP socket (`ConnectAsync`) | `network.simulated.write` (normal) | same |
| Fallback to the real network | `network.real.access` (dangerous), also checked by the server (ADR 0102 §4) | no fallback: "host not found" |
| WebSocket to a real server (`KazoClientWebSocket`) | `network.real.access` (dangerous), the same as for the relay (ADR 0235 §1) | `UnauthorizedAccessException` |
| Listen on a port (`KazoHttpListener`) outside its `web.urls` | `network.listen` (normal), plus `network.listen.privileged` (dangerous) below 1024 | `UnauthorizedAccessException` |

Classifications are unchanged (ADR 0049). A system app receives its capabilities
automatically.

## Usage

```csharp
IPAddress[] ips = await KazoDns.GetHostAddressesAsync("hackmail.com");      // SocketException(HostNotFound) si inconnu
KazoHostEntry entry = await KazoDns.GetHostEntryAsync(IPAddress.Parse("192.168.1.90"));

KazoPingReply reply = await KazoPing.SendAsync("hackersearch.net");          // KazoPingException si le nom ne résout pas
// reply.Status : Success, DestinationHostUnreachable, TimedOut ; reply.RoundtripTime : TimeSpan

using KazoHttpClient http = new(cookies);                                    // KazoCookieContainer gardé par l'app
KazoHttpResponse page = await http.GetAsync("https://hackmail.com/inbox");
KazoHttpResponse login = await http.PostAsync("https://hackmail.com/login", [new("username", "agent")]);
KazoHttpResponse put = await http.PutAsync("https://api.hack/items/7", KazoJsonContent.FromJson("{\"done\":true}"));
// page.StatusCode, ReasonPhrase, RequestUri (finale), Headers, ContentType, Content (texte),
// ContentBytes (octets), Page (site historique), RedirectCount — corps HTTP : http-bodies.md

IReadOnlyList<KazoPortProbeResult> ports = await KazoTcpClient.ProbeAsync("10.0.0.5", 1, 1024);
KazoOsFingerprint? os = await KazoTcpClient.DetectOperatingSystemAsync("10.0.0.5");
```

HTTP behavior:

- redirects are followed (10 at most); a `POST` redirected by 301 or
  302 continues as `GET`, 303 turns any method except `HEAD` into `GET`, 307 and
  308 keep the method and the body, like a browser and `HttpClient`
  (ADR 0074 §1, [`http-bodies.md`](http-bodies.md));
- cookies set along the way are stored in the client's `KazoCookieContainer`
  (the browser has one per window); a `Cookie` header put in
  `DefaultRequestHeaders` is sent along with them (it wins over a cookie of the same
  name), for the requested host only, and is never stored (ADR 0089);
- an error status (404, 500) is a response, not an exception;
  `EnsureSuccessStatusCode` and `GetStringAsync` throw `HttpRequestException`;
- no response (unknown host, host down, too many redirects) throws
  `KazoHttpRequestException`, which inherits from `HttpRequestException` and carries
  `NetworkError` and `ErrorCode` (`ERR_NAME_NOT_RESOLVED`..., displayed by the
  browser);
- every method carries a typed body (`KazoHttpContent`) and every response returns
  its bytes with their `Content-Type` ([`http-bodies.md`](http-bodies.md));
- the body of a historical site's page is its text rendering
  (`SimulatedPageTextFormatter`, `text/plain`), the one `curl` and
  `cat` display; the structured page remains in `Page` for the browser.

## Fallback to the real network

The OS only tries the real network if the host does not exist in the simulated network,
the app has `network.real.access`, the optional server is connected and
`EnsureAccessTokenAsync` returns a token. Since `v3/10-server-proxy`
(ADR 0102, 0108, [`server-network-relay.md`](../dev/server-network-relay.md)), the
server also checks agreement on its side and relays:

| Call | Real relay |
|---|---|
| `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `OPTIONS`... | proxy request with the request body (size and hash checked) and the response body |
| `HEAD` | proxy request without a response body |
| `KazoPing.SendAsync` | timed `HEAD`: a browser cannot send ICMP; `Address` is `null` |
| `KazoTcpClient.ProbeAsync` on **a single** port | proxy TCP probe (ADR 0035); a range is never relayed |
| `KazoTcpClient.ConnectAsync` | TCP stream over WebSocket (`KazoNetworkStream`) |
| `KazoUdpClient.ConnectAsync` | UDP datagrams over WebSocket |
| DNS, OS detection | never: "host not found" |

For HTTP, a proxy error becomes the error a browser would show
(ADR 0210 §6): `BlockedByAdministrator` (`ERR_BLOCKED_BY_ADMINISTRATOR`) for a
port, an address or an app the operator does not allow, `TimedOut`
(`ERR_TIMED_OUT`) for a timeout, `ConnectionRefused`, `HostUnreachable` or
`TooManyRedirects` when the relay says so, otherwise `ConnectionFailed`. The TCP
probe gives `ConnectionFailed` and a ping `TimedOut`. A refusal when opening a socket becomes the error
of a real stack (`SocketException`: `ConnectionRefused`, `HostUnreachable`
or `HostNotFound`, table in `server-network-relay.md`). Without a server, nothing
signals its absence: the result is that of a machine with no default
gateway.

### Real sites over HTTP: redirects, cookies, source (ADR 0210)

- The OS asks the server **not to follow** redirects
  (`ProxyHttpRequest.FollowRedirects = false`) and follows the hops itself, as
  for the simulated network: each hop sends the `KazoCookieContainer` cookies of
  the host and stores the `Set-Cookie` headers of the response.
- A `Set-Cookie` with `Domain=example.com` is stored under the key
  `.example.com` and sent to that domain and its subdomains
  (`KazoCookieContainer.DomainMatches`). `Max-Age=0` or an `Expires` in the past
  deletes it; `Path`, `Secure`, `HttpOnly` and `SameSite` are not kept.
- `KazoHttpResponse.Source` tells where the final response came from:
  `Simulated`, `LocalSite` (a site of this machine) or `RealNetwork` (relay). It
  says nothing about the server nor its token.
- `HttpProxyClient` keeps at most 6 relay calls in flight for the whole OS and
  tries a `QUOTA_EXCEEDED` refusal again 3 times; an HTTP call waits up to 30 s.

### TCP and UDP sockets

`KazoTcpClient.ConnectAsync(host, port)` returns a `KazoNetworkStream`
(`ReadAsync`, `WriteAsync`, `RemoteEndPoint`, `0` read when the target has closed);
`KazoUdpClient.ConnectAsync(host, port)` returns a "connected" UDP socket
(`SendAsync`, `ReceiveAsync` → `KazoUdpReceiveResult`, 65,507 bytes at
most). Both require `network.simulated.write`. The simulated network has no
raw streams: a host it knows, or this machine, refuses the connection.
Synchronous calls throw `NotSupportedException`.

### WebSocket to a real server (ADR 0235 §1)

`KazoClientWebSocket` follows `System.Net.WebSockets.ClientWebSocket` and
derives from `WebSocket`: an app can pass it wherever a `WebSocket` is
expected. Work item `real-network/1-websocket`, issue
[#395](https://github.com/Kazo-ca/Os/issues/395).

```csharp
using KazoClientWebSocket socket = new();
socket.Options.AddSubProtocol("text.ircv3.net");
await socket.ConnectAsync(new Uri("wss://irc.example.org/webirc"));   // UnauthorizedAccessException without network.real.access
await socket.SendAsync("NICK kazo"u8.ToArray(), WebSocketMessageType.Text, endOfMessage: true);
byte[] buffer = new byte[4096];
ValueWebSocketReceiveResult received = await socket.ReceiveAsync(buffer.AsMemory());
// socket.State, socket.SubProtocol, socket.CloseStatus, socket.CloseStatusDescription
await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
```

- **No server.** The OS opens the browser's `WebSocket` for the app
  (`AppNetworkGateway.ConnectWebSocketAsync`, a .NET `ClientWebSocket`, which
  is the browser's `WebSocket` under WebAssembly). It works in the Ecosystem
  host alone; the server's relay is not used.
- **Browser limits.** Only sub-protocols can be set
  (`Options.AddSubProtocol`): no headers, cookies or client certificate.
  `wss://` is required when the OS is served over `https` (mixed content). A
  refusal by the server, an unknown name or a page that does not accept the
  upgrade all give `WebSocketException` with `WebSocketError.Faulted` and the
  message "Unable to connect to the remote server …", without the HTTP status,
  as a browser does.
- **Simulated host refused.** An address of the simulated network, of this
  machine (`localhost`, 127.0.0.0/8) or of the internal `.hack` domain is
  refused before anything is opened: `WebSocketException` (`Faulted`), message
  "simulated host … (issue #393)". The simulated network carries no WebSocket
  yet ([#393](https://github.com/Kazo-ca/Os/issues/393)).
- **Traffic counted.** Every frame goes through `MeteredWebSocket` (Platform
  Core): the handshake counts the SYN, the SYN-ACK, the `Upgrade` request and
  the `101` answer; each frame counts one TCP segment of its RFC 6455 header
  (2, 4 or 10 bytes, plus 4 of mask for a sent frame) and its bytes, on `eth0`
  and in the `rchar`/`wchar` of the process; the socket buffers (144 KiB) are
  charged to the process until it is closed (ADR 0068).
- **Web Worker.** The gateway of an app running in a Web Worker
  (`RemoteNetworkGateway`) opens no socket yet, as for
  `KazoTcpClient.ConnectAsync`: `ConnectAsync` throws `NotSupportedException`.
- **Compatibility.** `IAppNetworkGateway.ConnectWebSocketAsync` has a default
  implementation that throws `NotSupportedException`: a gateway written for
  SDK 3.0 still compiles and loads.

## Migrated apps

- **Browser**: no more injection; `KazoHttpClient` and one `KazoCookieContainer`
  per window. A login (redirected `POST` form) lands directly
  on the target page. Since `web/4-browser-html` (ADR 0075), the browser
  renders HTML in an isolated iframe and relays `fetch`, XHR, links,
  forms and `document.cookie` through this same client; `document.cookie`
  deletes an expired cookie with `KazoCookieContainer.Remove`; details in
  [`browser-html-rendering.md`](../dev/browser-html-rendering.md).
- **`ping`**: four `KazoPing.SendAsync`, statistics computed over the
  replies received (min/avg/max/deviation).
- **`curl`**: `KazoHttpClient.SendAsync`; `-I` sends a `HEAD` and displays
  the headers; `-d` now follows the redirect, like `curl -L` (`-L`
  already had no effect). Since `web/2-http-bodies` (ADR 0074 §1), `curl`
  writes the raw body, sends `-d`/`--json` as is and, like the real
  `curl`, fails (code 22, no body) on a status ≥ 400 only with `-f`;
  details in [`http-bodies.md`](http-bodies.md).
- **`cat <url>`**: `KazoHttpClient.GetAsync`, same rendering as `curl`.
- **`nmap`**: `KazoDns` for the address, `KazoPing` for host
  discovery, `KazoTcpClient` for probing and `-O`. `-p 80` and `-p 80-80` are
  equivalent for the real fallback (a single port).
- **`cd`**: `ShellErrorText` moved from Platform Core to the SDK
  (`Kazo.Os.AppSdk`, next to `IShellSession`).

`Kazo.Os.Commands` no longer references `Platform.Core` or `Server.Contracts`;
`Kazo.Os.Apps.Browser` and `Kazo.Os.Commands` left the SDK boundary
exemption list.

## Decisions

- **Network failures as a result in the gateway, as .NET exceptions in the
  facades.** The gateway stays simple to test and implement; app code
  reads like .NET code (`SocketException`, `HttpRequestException`).
- **`KazoTcpClient.DetectOperatingSystemAsync`**, absent from ADR 0056:
  `nmap -O` needs it and it is a detection through the TCP/IP stack, so it belongs
  next to port probing. Simulated only.
- **`KazoPingReply.RoundtripTime` is a `TimeSpan`**, not whole milliseconds
  like `PingReply`: simulated latencies have decimals.
- **Ping jitter in the OS**, not in the command: ±5% of the host's latency,
  deterministic and cyclic per instance, as a real link varies.
- **No option to not follow redirects**: no app needs it
  and the simulated network does not expose a single-hop request for `GET`.
- **Byte counting** for `/proc/net/dev`: `AppNetworkGateway` is the
  single point of passage. Since the work item `4-proc-metrics` (ADR 0057),
  each public method counts its exchange on `eth0` and in the `rchar`/`wchar`
  of the app's process (`NetworkTrafficMeter`); a call refused for lack of a
  capability counts nothing. Details: [`system-metrics.md`](../dev/system-metrics.md).

- **.NET `ClientWebSocket` rather than a JavaScript module** (work item
  `real-network/1-websocket`): ADR 0235 §1 planned a JavaScript module of
  Browser Infrastructure. Under WebAssembly, `ClientWebSocket` *is* the
  browser's `WebSocket` (the relay already uses it in `HttpProxyClient`),
  opened by the OS and never by the app: no JavaScript to write (AGENTS.md
  §I.2), the xUnit tests talk to a real WebSocket server (Kestrel), and the
  same class will serve in a Web Worker. `AppNetworkGateway.WebSocketConnector`
  lets tests point at their server.

## Task list for work item `real-network/1-websocket`

- [x] `KazoClientWebSocket` and `KazoClientWebSocketOptions` (`Kazo.Os.AppSdk.Net`), modelled on `ClientWebSocket`.
- [x] `IAppNetworkGateway.ConnectWebSocketAsync` with a default implementation (SDK 3.0 compatibility).
- [x] `AppNetworkGateway.ConnectWebSocketAsync`: `network.real.access`, simulated host refused, the browser's `WebSocket`.
- [x] `MeteredWebSocket`: frames counted in `/proc/net/dev` and `/proc/<pid>/io`, buffers charged.
- [x] `PublicAPI.Unshipped.txt` of the SDK and of Platform Core.
- [x] Tests (`Wave4NetworkTests.WebSocket.cs`): refusal without the capability, simulated host refused, frame exchange and sub-protocol with a real WebSocket server, refused handshake, traffic counted, context without WebSocket.
- [x] Documentation: this document, `capabilities.md`, `server-network-relay.md`, `app-sdk.md`, `sdk-reference.md`, `features.md`, indexes, `changes/unreleased` entry.

## Task list for work item `rebuild/4-sdk-network`

- [x] `IAppNetworkGateway`, `KazoNetworkResult<T>`, `KazoNetworkError` and `IAppExecutionContext.Network`.
- [x] `KazoDns`, `KazoPing`, `KazoHttpClient` (`KazoCookieContainer`, `KazoHttpResponse`, `KazoHttpRequestException`), `KazoTcpClient` facades.
- [x] `AppNetworkGateway` in Platform Core: capabilities, simulated network, real fallback and token handled by the OS.
- [x] Wiring in `AppExecutionContextFactory` and the Ecosystem host.
- [x] `ShellErrorText` moved into the SDK.
- [x] Migration of Browser, `ping`, `curl`, `cat`, `nmap`; `Kazo.Os.Commands` without `Platform.Core` or `Server.Contracts`.
- [x] Exemptions for `Kazo.Os.Apps.Browser` and `Kazo.Os.Commands` removed.
- [x] Tests: commands on the real gateway, facades, capabilities, real fallback refused without `network.real.access`.
- [x] Documentation: this document, `app-sdk.md`, `shell-interpreter.md`, index.
