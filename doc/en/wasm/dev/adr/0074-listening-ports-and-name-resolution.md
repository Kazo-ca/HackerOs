> [Version française](../../../../fr/wasm/dev/adr/0074-listening-ports-and-name-resolution.md)

# ADR 0074: listening ports, OS DNS resolver and real HTTP bodies

## Status

Accepted on 2026-10-16 (proposed the same day, then validated by Benoit with the
questionnaire of section 4 of the plan).

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Completes:** ADR 0055 and 0056 §6 (network through the SDK), ADR 0057 (`/proc/net`).

## Context

An app cannot listen on a port, the simulated HTTP response carries no
body (only a `SimulatedPage`), and names are resolved only by the simulated
network's table. Benoit wants each app to declare its URL in its
manifest and a DNS service to make the link, with a fallback to the server's
relay if it is available.

## Decision

### 1. Real HTTP bodies

`SimulatedHttpRequest` and `SimulatedHttpResponse` gain a byte body and
a `Content-Type`. `SimulatedPage` remains a possible body (existing sites);
the browser chooses the rendering according to the type. `HackHttpResponse.Content` gives
the text, `ContentBytes` the bytes. `curl` writes the raw body.

### 2. `HackHttpListener`

Modeled on `System.Net.HttpListener` (`Prefixes`, `Start`, `GetContextAsync`,
`HackHttpListenerContext.Request/Response`), in `HackerOs.AppSdk.Net`.

- Capability `network.listen` (normal). A port < 1024 requires
  `network.listen.privileged` (dangerous, administrator approval), like
  `CAP_NET_BIND_SERVICE` on Linux. A `Web` app automatically receives the right
  to listen on its own `Urls` (ADR 0073).
- The **table of listening ports** lives in Platform Core, next to
  `AppNetworkGateway`: `(address, port) → process`. It is released when
  the process terminates; a request to a closed port gives
  `ERR_CONNECTION_REFUSED`.
- `netstat -tlnp`/`ss` list the ports and the PID; `/proc/net/tcp` exposes
  them in Linux format.
- A listener in a worker (ADR 0070) receives its requests through the remote
  gateway: new `HttpRequest`/`HttpResponse` messages of the worker
  protocol.

### 3. On-demand activation

An installed `Web` app is not started at boot. The first call to one of
its `Urls` starts it (like systemd's socket activation), then the
request is handed to it. It then appears in the task manager and
can be stopped; the next request restarts it.

### 4. `hackdns` resolver

System service that answers `HackDns` and browser requests, in this
order:

1. `/etc/hosts` (real file, editable by the administrator);
2. `localhost` and `127.0.0.0/8` → the local machine;
3. the `Urls` of installed `Web` apps (manifests);
4. declared hosted sites (ADR 0077) that have a name;
5. the simulated network (registered hosts and sites);
6. the optional server's relay, if `network.real.access`, server connected and
   valid token (current behavior of `AppNetworkGateway`).

`https://` is accepted and treated as `http://` (no encryption in the
simulated network); the browser displays the padlock. Simulated certificates
may come later in a separate ADR.

Internal sites use the `.hack` domain (`intranet.hack`), which
does not exist on the Internet: fallback 6 never applies to this domain.

The result is cached with a TTL; a modified `/etc/hosts` clears the cache.
A `nslookup` command is added; it shows, like `HackDns`, the source
of the answer.

## Consequences

- `AppNetworkGateway` remains the only point of passage for traffic: it queries
  `hackdns` then the port table before the simulated network.
- The byte counting of `/proc/net/dev` (ADR 0057) also counts local
  traffic, on `lo`.
- The contracts of `HackerOs.Simulation.Abstractions` change: existing sites
  are adapted in the same work item.
