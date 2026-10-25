# Network in the SDK

## Purpose

An app accesses the network only through the SDK (ADR 0055, ADR 0056 section 6):
name resolution, ping, HTTP and TCP port probing. The OS alone decides which
network serves a request: the simulated network first, then, if the host does not
exist there, the real network through the optional server's proxy, when it is allowed and
possible. The app sees neither the access token, nor the server address, nor even
whether a server exists.

This document describes the `HackDns`, `HackPing`, `HackHttpClient` and
`HackTcpClient` facades, the `IAppNetworkGateway` gateway that carries them and its
`AppNetworkGateway` implementation in Platform Core. It was written by the
work item `rebuild/4-sdk-network` (ADR 0058).

## Architecture

```text
App (Browser, ping, curl, cat, nmap)
  └─ HackDns / HackPing / HackHttpClient / HackTcpClient     (HackerOs.AppSdk.Net)
       └─ HackExecutionContext.Current.Network : IAppNetworkGateway
            └─ AppNetworkGateway                             (Platform.Core/Network)
                 ├─ capacité (ICapabilityChecker de l'instance)
                 ├─ ISimulatedNetworkService                 (toujours essayé en premier)
                 └─ repli réel : IServerConnectionService + IProxyClient
                      (seulement si network.real.access, serveur connecté, jeton valide)
```

| Type | Project | Role |
|---|---|---|
| `IAppNetworkGateway` | `HackerOs.AppSdk` (`Net/`) | Instance gateway: `ResolveAsync`, `ReverseLookupAsync`, `PingAsync`, `SendAsync`, `ProbeTcpAsync`, `DetectOperatingSystemAsync`. Network failures are a result (`HackNetworkResult<T>`, `HackNetworkError`), never an exception. |
| `IAppExecutionContext.Network` | `HackerOs.AppSdk` | Member with a "not supported" default implementation, so that existing contexts compile. |
| `HackDns`, `HackPing`, `HackHttpClient`, `HackCookieContainer`, `HackTcpClient` | `HackerOs.AppSdk` (`Net/`) | Facades modeled on `System.Net`: they read the ambient context (ADR 0043) and throw .NET exceptions. |
| `AppNetworkGateway` | `HackerOs.Platform.Core` (`Network/`) | Trusted implementation: capabilities, simulated network, real fallback decision, server token. Built by `AppExecutionContextFactory` for each instance. |

`AppExecutionContextFactory` receives `ISimulatedNetworkService`,
`IServerConnectionService` and `IProxyClient` (optional parameters). Without a simulated
network, `Network` throws `NotSupportedException`; without a server or proxy, the real
fallback is simply absent.

Since `web/3-listener-dns`, `AppNetworkGateway` first goes through the
local `hackdns` resolver (`/etc/hosts`, loopback, web apps, hosted sites):
a local target is served by the OS port table, on `lo`, and a `.hack`
name is never relayed to the real network. See
[`listening-ports-and-dns.md`](listening-ports-and-dns.md).

## Capabilities

| Operation | Capability | Without the capability |
|---|---|---|
| DNS, ping, `GET`, `HEAD`, port probing, OS detection | `network.simulated.read` (normal) | `UnauthorizedAccessException` (internal: `AppGatewayAccessDeniedException`) |
| `POST` and any other HTTP method, TCP or UDP socket (`ConnectAsync`) | `network.simulated.write` (normal) | same |
| Fallback to the real network | `network.real.access` (dangerous), also checked by the server (ADR 0102 §4) | no fallback: "host not found" |
| Listen on a port (`HackHttpListener`) outside its `web.urls` | `network.listen` (normal), plus `network.listen.privileged` (dangerous) below 1024 | `UnauthorizedAccessException` |

Classifications are unchanged (ADR 0049). A system app receives its capabilities
automatically.

## Usage

```csharp
IPAddress[] ips = await HackDns.GetHostAddressesAsync("hackmail.com");      // SocketException(HostNotFound) si inconnu
HackHostEntry entry = await HackDns.GetHostEntryAsync(IPAddress.Parse("192.168.1.90"));

HackPingReply reply = await HackPing.SendAsync("hackersearch.net");          // HackPingException si le nom ne résout pas
// reply.Status : Success, DestinationHostUnreachable, TimedOut ; reply.RoundtripTime : TimeSpan

using HackHttpClient http = new(cookies);                                    // HackCookieContainer gardé par l'app
HackHttpResponse page = await http.GetAsync("https://hackmail.com/inbox");
HackHttpResponse login = await http.PostAsync("https://hackmail.com/login", [new("username", "agent")]);
HackHttpResponse put = await http.PutAsync("https://api.hack/items/7", HackJsonContent.FromJson("{\"done\":true}"));
// page.StatusCode, ReasonPhrase, RequestUri (finale), Headers, ContentType, Content (texte),
// ContentBytes (octets), Page (site historique), RedirectCount — corps HTTP : http-bodies.md

IReadOnlyList<HackPortProbeResult> ports = await HackTcpClient.ProbeAsync("10.0.0.5", 1, 1024);
HackOsFingerprint? os = await HackTcpClient.DetectOperatingSystemAsync("10.0.0.5");
```

HTTP behavior:

- redirects are followed (10 at most); a `POST` redirected by 301 or
  302 continues as `GET`, 303 turns any method except `HEAD` into `GET`, 307 and
  308 keep the method and the body, like a browser and `HttpClient`
  (ADR 0074 §1, [`http-bodies.md`](http-bodies.md));
- cookies set along the way are stored in the client's `HackCookieContainer`
  (the browser has one per window); a `Cookie` header put in
  `DefaultRequestHeaders` is sent along with them (it wins over a cookie of the same
  name), for the requested host only, and is never stored (ADR 0089);
- an error status (404, 500) is a response, not an exception;
  `EnsureSuccessStatusCode` and `GetStringAsync` throw `HttpRequestException`;
- no response (unknown host, host down, too many redirects) throws
  `HackHttpRequestException`, which inherits from `HttpRequestException` and carries
  `NetworkError` and `ErrorCode` (`ERR_NAME_NOT_RESOLVED`..., displayed by the
  browser);
- every method carries a typed body (`HackHttpContent`) and every response returns
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
| `HackPing.SendAsync` | timed `HEAD`: a browser cannot send ICMP; `Address` is `null` |
| `HackTcpClient.ProbeAsync` on **a single** port | proxy TCP probe (ADR 0035); a range is never relayed |
| `HackTcpClient.ConnectAsync` | TCP stream over WebSocket (`HackNetworkStream`) |
| `HackUdpClient.ConnectAsync` | UDP datagrams over WebSocket |
| DNS, OS detection | never: "host not found" |

A proxy error (`ServerConnectionException`, `HttpRequestException`)
becomes `HackNetworkError.ConnectionFailed` for HTTP and probing, and
`TimedOut` for a ping. A refusal when opening a socket becomes the error
of a real stack (`SocketException`: `ConnectionRefused`, `HostUnreachable`
or `HostNotFound`, table in `server-network-relay.md`). Without a server, nothing
signals its absence: the result is that of a machine with no default
gateway.

### TCP and UDP sockets

`HackTcpClient.ConnectAsync(host, port)` returns a `HackNetworkStream`
(`ReadAsync`, `WriteAsync`, `RemoteEndPoint`, `0` read when the target has closed);
`HackUdpClient.ConnectAsync(host, port)` returns a "connected" UDP socket
(`SendAsync`, `ReceiveAsync` → `HackUdpReceiveResult`, 65,507 bytes at
most). Both require `network.simulated.write`. The simulated network has no
raw streams: a host it knows, or this machine, refuses the connection.
Synchronous calls throw `NotSupportedException`.

## Migrated apps

- **Browser**: no more injection; `HackHttpClient` and one `HackCookieContainer`
  per window. A login (redirected `POST` form) lands directly
  on the target page. Since `web/4-browser-html` (ADR 0075), the browser
  renders HTML in an isolated iframe and relays `fetch`, XHR, links,
  forms and `document.cookie` through this same client; `document.cookie`
  deletes an expired cookie with `HackCookieContainer.Remove`; details in
  [`browser-html-rendering.md`](../dev/browser-html-rendering.md).
- **`ping`**: four `HackPing.SendAsync`, statistics computed over the
  replies received (min/avg/max/deviation).
- **`curl`**: `HackHttpClient.SendAsync`; `-I` sends a `HEAD` and displays
  the headers; `-d` now follows the redirect, like `curl -L` (`-L`
  already had no effect). Since `web/2-http-bodies` (ADR 0074 §1), `curl`
  writes the raw body, sends `-d`/`--json` as is and, like the real
  `curl`, fails (code 22, no body) on a status ≥ 400 only with `-f`;
  details in [`http-bodies.md`](http-bodies.md).
- **`cat <url>`**: `HackHttpClient.GetAsync`, same rendering as `curl`.
- **`nmap`**: `HackDns` for the address, `HackPing` for host
  discovery, `HackTcpClient` for probing and `-O`. `-p 80` and `-p 80-80` are
  equivalent for the real fallback (a single port).
- **`cd`**: `ShellErrorText` moved from Platform Core to the SDK
  (`HackerOs.AppSdk`, next to `IShellSession`).

`HackerOs.Commands` no longer references `Platform.Core` or `Server.Contracts`;
`HackerOs.Apps.Browser` and `HackerOs.Commands` left the SDK boundary
exemption list.

## Decisions

- **Network failures as a result in the gateway, as .NET exceptions in the
  facades.** The gateway stays simple to test and implement; app code
  reads like .NET code (`SocketException`, `HttpRequestException`).
- **`HackTcpClient.DetectOperatingSystemAsync`**, absent from ADR 0056:
  `nmap -O` needs it and it is a detection through the TCP/IP stack, so it belongs
  next to port probing. Simulated only.
- **`HackPingReply.RoundtripTime` is a `TimeSpan`**, not whole milliseconds
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

## Task list for work item `rebuild/4-sdk-network`

- [x] `IAppNetworkGateway`, `HackNetworkResult<T>`, `HackNetworkError` and `IAppExecutionContext.Network`.
- [x] `HackDns`, `HackPing`, `HackHttpClient` (`HackCookieContainer`, `HackHttpResponse`, `HackHttpRequestException`), `HackTcpClient` facades.
- [x] `AppNetworkGateway` in Platform Core: capabilities, simulated network, real fallback and token handled by the OS.
- [x] Wiring in `AppExecutionContextFactory` and the Ecosystem host.
- [x] `ShellErrorText` moved into the SDK.
- [x] Migration of Browser, `ping`, `curl`, `cat`, `nmap`; `HackerOs.Commands` without `Platform.Core` or `Server.Contracts`.
- [x] Exemptions for `HackerOs.Apps.Browser` and `HackerOs.Commands` removed.
- [x] Tests: commands on the real gateway, facades, capabilities, real fallback refused without `network.real.access`.
- [x] Documentation: this document, `app-sdk.md`, `shell-interpreter.md`, index.
