> [Version française](../../../fr/wasm/framework/listening-ports-and-dns.md)

# Listening ports, `hackdns` and local websites

Work item `web/3-listener-dns` of the web phase (ASP.Hack). Decisions: ADR 0073
(web app, shared web worker), ADR 0074 (listening ports, name resolution).
Plan: [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md).

## Purpose

An app can now **serve** HTTP, and not only consume it:

- `HackHttpListener` (SDK) opens a port, like `System.Net.HttpListener`;
- the OS keeps a **table of listening ports** (`ListeningPortTable`), with
  `EADDRINUSE` and the privileged port rule;
- a new kind of app, **`web`**, declares its URLs in its manifest
  (`web.urls`); the OS starts it **on demand**, at first access;
- a local resolver, **`hackdns`** (`HackDnsResolver`), reads `/etc/hosts`,
  the loopback, the web apps' URLs and the hosted sites before the simulated
  network;
- local traffic goes through `lo` (`/proc/net/dev` counters);
- `netstat -tlnp`, `/proc/net/tcp` and `nslookup` show all of this;
- the server code of **all** sites runs in **a single** Web Worker,
  the `web` worker (Benoit's decision, plan §4.1, answer 3).

Page rendering in the browser (`web/4`) and the ASP.Hack framework
(`web/6` and following, see [`asp-hack-mvc.md`](asp-hack-mvc.md)) are not part
of this work item. An ASP.Hack site is a web app like any other: its
process (`HackWebHost`) derives from `WebServerAppBase`.

## Architecture

```text
client (curl, navigateur, HackHttpClient)
  └─ AppNetworkGateway.SendAsync              (Platform.Core/Network)
       ├─ HackDnsResolver.ResolveAsync        hosts → loopback → web.urls → sites hébergés → réseau simulé
       │     └─ réponse locale ?
       │          ├─ ListeningPortTable.Route(host, port, path)       socket qui écoute
       │          ├─ sinon WebAppActivator.ActivateAsync(app)         démarrage à la demande
       │          ├─ sinon port ouvert : 400 Bad Request - Invalid Hostname
       │          └─ sinon ConnectionRefused
       │     └─ OsHttpListenerSocket.ExchangeAsync  ─►  HackHttpListener.GetContextAsync (app)
       │                    (worker web : message hreq ; réponse hres)
       └─ sinon : réseau simulé, puis repli réel (jamais pour .hack)
```

| Type | Project | Role |
|---|---|---|
| `AppKind.Web`, `AppExecutionHost.Web`, `WebAppManifest`, `WebAppUrls`, `WebBinding` | `HackerOs.App.Abstractions` | `web` kind, `web.urls` field, parsing and normalization of URLs and prefixes. |
| `HackHttpListener`, `HackHttpListenerContext/Request/Response` | `HackerOs.AppSdk` (`Net/`) | HTTP listening modeled on `System.Net.HttpListener`. |
| `WebServerAppBase` | `HackerOs.AppSdk` | Base of a web app: listens on its `Urls`, calls `ProcessRequestAsync` for each request. |
| `HackIPGlobalProperties` | `HackerOs.AppSdk` (`Net/`) | `GetActiveTcpListenersAsync`, `GetActiveTcpListenerEndPointsAsync` (what `netstat` reads). |
| `IAppNetworkGateway.ListenHttpAsync`, `GetTcpListenersAsync`, `IHackHttpListenerSocket`, `HackTcpListenerInfo` | `HackerOs.AppSdk` (`Net/`) | Gateway contract. |
| `ListeningPortTable`, `OsHttpListenerSocket` | `Platform.Core` (`Network/Local/`) | Socket table: binding, conflicts, routing, inodes, cleanup of dead processes. |
| `HackDnsResolver` | `Platform.Core` (`Network/Local/`) | Local resolver with cache (30 simulated seconds). |
| `WebAppActivator` | `Platform.Core` (`Network/Local/`) | On-demand activation, like systemd's socket activation. |
| `LocalNetworkServices`, `LocalNetworkCaller` | `Platform.Core` (`Network/Local/`) | What `AppExecutionContextFactory` passes to each gateway: table, resolver, activator, caller (manifest, principal, elevation). |
| `RemoteListenerTable`, `RemoteHttpListenerSocket` | `Platform.WorkerHost` (`Worker/`) | Worker side: a listener opened in a Web Worker. |

Wiring: `EcosystemServiceCollectionExtensions` registers the table, the
resolver and the activator (host scope) and passes them to the context factory
and to `/proc` (`ProcMetricsSources.Listeners`).

## Usage

### Listening (`HackHttpListener`)

```csharp
await using HackHttpListener listener = new();
listener.Prefixes.Add("http://localhost:8080/");
await listener.StartAsync();                          // HttpListenerException(98) si le port est pris

while (true)
{
    HackHttpListenerContext context = await listener.GetContextAsync(cancellationToken);
    context.Response.ContentType = "text/plain";
    await context.Response.OutputStream.WriteAsync("bonjour"u8.ToArray(), cancellationToken);
    context.Response.Close();
}
```

- `HttpListener` prefixes: `http://host:port/path/`, with `+` (any
  host) and `*` (any host that no other prefix takes); the path ends
  with `/`.
- Routing like `http.sys`: exact host, then `+`, then a loopback name,
  then `*`; for an equal host, the longest path wins.
- The request gives `HttpMethod`, `Url`, `Headers`, `QueryString`, `Cookies`,
  `ContentType`, `Body`/`InputStream`, `ReadForm()`, `RemoteEndPoint`
  (`127.0.0.1:<ephemeral port>`), `IsLocal`. The response gives `StatusCode`,
  `ContentType`, `Headers`, `OutputStream`, `RedirectLocation`,
  `AppendHeader("Set-Cookie", …)`, `Close()`.

### Capabilities

| Operation | Capability |
|---|---|
| Listen on a URL from its own manifest (`web.urls`) | none |
| Listen on another prefix, port 1024 or above | `network.listen` (normal) |
| Listen on another prefix, port below 1024 | `network.listen` and `network.listen.privileged` (**dangerous**, like `CAP_NET_BIND_SERVICE`) |
| List listening ports (`HackIPGlobalProperties`) | none; the PID and program are only visible to root or the same user, like `netstat -p` |

A port already in use gives `HackNetworkError.AddressInUse`, raised as
`HttpListenerException` code 98 (`EADDRINUSE`) with "0.0.0.0:8080: address
already in use". The simulated `localhost` daemons (ssh 22, mysql 3306)
occupy their port.

### Declaring a web app

```csharp
[AppManifest(Execution = AppExecutionHost.Web)]
public sealed class IntranetApp : AppDefinition
{
    public override AppKind Kind => AppKind.Web;
    public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
    …
}

public sealed class IntranetSite(AppManifest manifest) : WebServerAppBase(manifest)
{
    protected override Task ProcessRequestAsync(HackHttpListenerContext context, CancellationToken ct) { … }
}
```

Validator and generator rules:

| Code | Rule |
|---|---|
| `manifest.web.urls.required` | a `web` app declares at least one URL |
| `manifest.web.url.invalid` | absolute `http`/`https` URL, valid host name (no wildcard), path ending with `/`, no query or fragment |
| `manifest.web.url.duplicate` | two identical URLs after normalization |
| `manifest.web.forbidden` | `web` on an app that is not `web` |
| `manifest.web.filesystem`, `HOSAPP0050` | a site requests no `filesystem.*` capability (its data goes into the browser's IndexedDB, ADR 0073 §3) |
| `manifest.execution.web` | a `web` app runs in the `web` worker or on the main thread |
| `manifest.execution.web-only` | only a `web` app runs in the `web` worker |
| `catalog.web.url-conflict` | two installed apps do not declare the same host name |

The mapper normalizes URLs (`WebAppUrls.Normalize`: scheme and host in
lowercase, default port removed); the serializer keeps the declared order (the
first URL is the site's main address).

### On-demand activation

A web app is not started at boot. The first request to one of its
URLs launches it (`AppLifecycleOrchestrator`, non-interactive) **under the identity of
the caller**, waits for its socket to listen (30 s at most, worker startup
included), then passes it the request. Simultaneous requests share the same
startup. Once stopped (task manager, `kill`), it restarts on the
next request.

### Name resolution (`hackdns`)

Order, from first to last:

1. `/etc/hosts` (seeded `0644` root: `localhost`, `hackeros`), reread as soon as it
   changes;
2. the loopback (`localhost`, `*.localhost`, 127.0.0.0/8);
3. the URLs of installed and enabled web apps;
4. hosted sites (`IHostedSiteNameSource.FindSiteAsync`, implemented by
   `HostedSiteNameSource` since `web/5`: hosts of valid and enabled `.site` files,
   [`static-sites.md`](../dev/static-sites.md), ADR 0080 §4);
5. the simulated network: the default catalog (`hackersearch.net`, `hackmail.com`...)
   and the `.hackeros` test hosts (ADR 0085).

Responses are kept for 30 simulated seconds. A `.hack` name is **never**
relayed to the real network. A line of `/etc/hosts` pointing to a simulated host sends the
request to that host with its real name (`Host` header rewritten), like an alias.
`HackDns.GetHostEntryAsync` returns the source (`HackDnsSource`: `HostsFile`,
`Loopback`, `WebApp`, `HostedSite`, `SimulatedNetwork`).

### Commands

```text
$ netstat -tlnp
Active Internet connections (only servers)
Proto Recv-Q Send-Q Local Address           Foreign Address         State       PID/Program name
tcp        0      0 0.0.0.0:22              0.0.0.0:*               LISTEN      -
tcp        0      0 0.0.0.0:3306            0.0.0.0:*               LISTEN      -
tcp        0      0 0.0.0.0:80              0.0.0.0:*               LISTEN      57/intranet

$ nslookup intranet.hack
Server:		127.0.0.53
Address:	127.0.0.53#53

;; source: web app org.hackeros.intranet (web.urls, this machine)
Name:	intranet.hack
Address: 127.0.0.1
```

- `netstat` reads `HackIPGlobalProperties`; with `-p`, a hidden owner
  gives the net-tools note "(Not all processes could be identified…".
- `/proc/net/tcp` follows `proc(5)`: little-endian hexadecimal address,
  state `0A` (LISTEN), `uid` 0 for root and daemons, socket inode.
- `nslookup <name|address>` (capability `network.simulated.read`) indicates the
  source of the answer; an unknown name gives
  `** server can't find X: NXDOMAIN` and code 1.

### Local traffic on `lo`

`NetworkTrafficMeter` counts a local exchange (DNS, ping, HTTP, probe,
OS detection) on `lo` instead of `eth0`. A local ping answers in ~0.04 ms.

## Shared web worker (ADR 0073 §1)

- `WorkerRole.Web`: a single `web` worker for all sites
  (`WorkerPool.AcquireWebAsync`). The first site takes the ready worker (which is
  replaced); the following ones reuse it; it is terminated when its last
  site stops. It appears as `web` in `/proc/hackeros/workers`.
- Protocol (in addition to [`worker-processes.md`](../dev/worker-processes.md)):

| Message | Direction | Content |
|---|---|---|
| `call net.listen` | worker → OS | prefixes; response: socket number and normalized prefixes |
| `note net.unlisten` | worker → OS | close the socket |
| `call net.listeners` | worker → OS | read the table (`HackIPGlobalProperties`) |
| `hreq` | OS → worker | request: process, socket, exchange, method, URL, headers, body, endpoints |
| `hres` | worker → OS | response: status, reason, headers, type, body |

- A request that arrives before the response to `net.listen` is held by
  `RemoteListenerTable` and delivered as soon as the socket is known.
- The end of the process closes all its sockets, on both sides.

## Decisions

- **In-memory data, no socket file**: the table lives in the
  host (host scope), `/proc/net/tcp` reads it on demand, like the
  kernel.
- **`localhost` daemons**: the simulated catalog no longer opens 80 or 8080 on
  `127.0.0.1`; these ports are free for apps. ssh 22 and mysql 3306
  remain open (without a PID).
- **Open port, unknown host**: `400 Bad Request - Invalid Hostname`, the
  response of `http.sys`, rather than a connection refusal.
- **Identity**: a site activated on demand runs under the user who called
  it (single session for now).
- **No real fallback for `.hack`**: an internal site that does not answer gives
  "connection refused", never a request on the Internet.

## Tests

- `Tests/HackerOs.Platform.WorkerHost.Tests/LocalNetworkTests.cs` (with and without
  a worker): on-demand activation, cookies, redirects, `POST`, shared web worker
  and its end, stop then restart, `netstat`, `/proc/net/tcp`,
  listening permissions, `EADDRINUSE`, connection refused, 400, `nslookup`,
  `/etc/hosts` priority, `.hack` never relayed, alias to a simulated host.
- `Tests/HackerOs.App.Abstractions.Tests`: `WebAppUrlsTests`, `web` rules of the
  validator, `web.urls` serializer round trip, schema fixtures
  `web.valid.json`, `invalid-web-missing-web-block.json`,
  `invalid-web-on-service.json`.
- `CommandParityTests`: `nslookup` (64 commands).

## Task list for work item `web/3-listener-dns`

- [x] `web` kind, `web` host, `web.urls` (abstractions, validator, schema, serializer, mapper, generator `HOSAPP0050`, catalog conflict).
- [x] Capabilities `network.listen` (normal) and `network.listen.privileged` (dangerous).
- [x] `HackHttpListener`, `WebServerAppBase`, `HackIPGlobalProperties`, gateway contract.
- [x] `ListeningPortTable`: binding, `EADDRINUSE`, `http.sys` routing, `localhost` daemons, cleanup.
- [x] `HackDnsResolver`: `/etc/hosts` (seeded), loopback, web app URLs, hosted sites, cache, `.hack` never relayed.
- [x] `WebAppActivator`: on-demand activation under the caller's identity.
- [x] Gateway: local sending, cookies, redirects, 400 Invalid Hostname, traffic on `lo`, local probe and ping.
- [x] Shared `web` worker, `net.listen`/`net.unlisten`/`net.listeners`, `hreq`/`hres`.
- [x] `/proc/net/tcp`, `netstat -tlnp`, `nslookup`.
- [x] Tests (end to end in both modes, abstractions, parity).
- [x] Documentation and index.
