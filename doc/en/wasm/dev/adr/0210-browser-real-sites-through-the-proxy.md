> [Version française](../../../../fr/wasm/dev/adr/0210-browser-real-sites-through-the-proxy.md)

# ADR 0210: real sites in the browser through the server's proxy

## Status

Accepted on 2026-10-30. Issue [#266](https://github.com/Kazo-ca/HackerOs/issues/266)
(`todo/base` series). Order of the sources decided by Benoit on 2026-10-29:
simulated network and `.hack` sites, then the **HackerOS server's proxy** (this
ADR), then a public proxy that can be turned off and the Wayback Machine (later,
out of scope).

- **Complements:** [ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md) and
  [ADR 0108](0108-server-relay-implementation-adjustments.md) (server relay),
  [ADR 0075](0075-browser-html-rendering.md) and
  [ADR 0079](0079-browser-html-rendering-adjustments.md) (isolated rendering of
  the browser), [ADR 0089](0089-explicit-cookie-header.md) (explicit `Cookie`
  header).
- **Changes:** the server's `ProxyRelayPolicy.DefaultGrantedApps` list
  (ADR 0102 §4) and the class of the Browser
  ([ADR 0048](0048-system-apps-and-uninstall.md) §1).
- **Does not change:** the browser never opens an iframe on the real site; the
  page goes through the isolated rendering (`srcdoc` + `HtmlPageComposer`).

## Context

The browser only showed the simulated network. The server relay already served
`curl`, `wget`, `ping`, `cat` and `nmap`, but a real site in the browser lacked
(findings of issue #266):

- the right: the Browser did not have `network.real.access`, and the server only
  granted it to the commands;
- cookies: the OS sent no cookies and lost the `Set-Cookie` headers; the server
  blocked the `Cookie` header and followed redirects itself, losing the cookies
  of the intermediate hops (no way to sign in to an account);
- load: the server refuses more than 8 requests at once per device, while a page
  loads all its resources at the same time;
- display: nothing told where a page came from, and a relay refusal gave a
  generic error.

## Decision

### 1. The Browser has `network.real.access` and becomes a system app

- `BrowserApp.Capabilities` declares `network.real.access`.
- This capability is dangerous. To avoid the approval prompt at first launch,
  even without a server, the host declares the Browser a system app
  (`IsSystemApp="true"` in `HackerOs.Ecosystem.csproj`), like `curl` and `wget`.
  It still does not ask for `filesystem.system.read`.
- The server adds `org.hackeros.browser` to `DefaultGrantedApps` and to the
  shipped `appsettings.json`. An operator who wrote their own
  `HackerOs:Proxy:GrantedApps` list must add it there.

### 2. Relayed cookies

- The server relays the `Cookie` header. It still blocks `Host`,
  `Authorization`, `X-Forwarded-For` and `X-Real-IP`.
- On each real hop the OS sends the cookies its container (`HackCookieContainer`)
  holds for the host, plus a `Cookie` header the app wrote for the requested host
  (ADR 0089 rule).
- The `Set-Cookie` headers of a real site are applied as a browser does
  (RFC 6265 §5.2 and §5.3), simplified:
  - a `Domain` attribute matching the host gives a domain cookie, stored under
    the key `.example.com` and sent to that domain and its subdomains; a
    `Domain` of another site or of a single label (`com`) drops the cookie;
  - `Max-Age` of zero or less, or an `Expires` in the past, deletes the cookie;
  - `Path`, `Secure`, `HttpOnly` and `SameSite` are not kept.
- `HackCookieContainer.GetCookies(uri)` also returns the domain cookies matching
  the host (`DomainMatches`), so `document.cookie` sees them.

### 3. Redirects are followed by the OS

- New contract field: `ProxyHttpRequest.FollowRedirects` (`init` property, true
  by default for older clients).
- False: the server returns the first response as is, a redirect included, with
  `Location` and `Set-Cookie`. It still checks every target (SSRF, port,
  address), since each hop is a new call.
- The OS always sends `FollowRedirects = false` and follows the hops in the same
  loop as for the simulated network (10 at most, method rules of
  RFC 9110 §15.4). Each hop is routed again: a real site may redirect to a
  simulated one.

### 4. The source of the response

- SDK: `HackHttpResponse.Source` (`init` property) of type
  `HackHttpResponseSource`: `Simulated`, `LocalSite` (a site of this machine),
  `RealNetwork` (relay). It names neither the server's address nor its token
  (ADR 0056 §6). It also crosses the Web Worker protocol.
- Browser: `BrowserResponse.Source` adds `File`. The address bar shows a source
  icon before the scheme icon:
  - `⇄` "Real site, through the HackerOS server's proxy";
  - `⌂` "Site of this machine";
  - `◎` "Site of the simulated network";
  - nothing for a file (its scheme icon is enough) or an error page.
- The lock stays for `https:`. For `http:`, the text "Not secure" shows next to
  the icon, as in Chromium.

### 5. Load of a page

- `HttpProxyClient` (an OS singleton) keeps at most **6** relay calls in flight
  (HTTP and probes), as a browser limits its connections per host. The others
  wait for their turn.
- A `QUOTA_EXCEEDED` refusal (another tab, or an operator who lowered the limit)
  is tried again 3 times, after 250, 500 then 750 ms.
- The timeout of a relayed HTTP call goes from 10 to 30 s, the server's maximum.
  `ping`'s `HEAD` keeps 10 s.
- Limits kept and documented: 10 MiB per response, body in base64 inside the
  JSON (about +33 %).

### 6. Clear errors

- `ServerConnectionException.ProxyErrorCode` gives the relay's code.
- Two values are added to `HackNetworkError`:
  - `TimedOut` (`ERR_TIMED_OUT`) for `TIMEOUT`;
  - `BlockedByAdministrator` (`ERR_BLOCKED_BY_ADMINISTRATOR`) for
    `BLOCKED_ADDRESS`, `BLOCKED_PORT`, `DNS_REBINDING_BLOCKED`,
    `GRANT_MISSING`, `RELAY_DISABLED`, `CAPABILITY_DENIED` and
    `SIMULATED_DOMAIN_BLOCKED`.
- `CONNECTION_REFUSED`, `HOST_UNREACHABLE` and `TOO_MANY_REDIRECTS` keep their
  error; the rest (quota still full, response too large…) gives
  `ConnectionFailed`.
- The browser has a "Your administrator has blocked …" page. The "address not
  found" page suggests connecting a HackerOS server for real sites. Without a
  server nothing changes: "host not found" (ADR 0056 §6).

### 7. Scripts of the page

The frame's CSP stays `connect-src 'none'`: a `fetch` or `XMLHttpRequest` of
the page goes through `PageBridge`, so through the same transport, relay and
cookies as the page.

## Consequences

- With a connected server, the browser opens real sites, stays signed in to
  them and shows where the page came from.
- SDK additions (`PublicAPI.Unshipped.txt`, next minor): `HackHttpResponse.Source`,
  `HackHttpResponseSource`, `HackNetworkError.TimedOut`,
  `HackNetworkError.BlockedByAdministrator`, `HackCookieContainer.DomainMatches`.
- Known limits: no `Path` nor `Secure` for relayed cookies; a cookie the site
  deletes is not removed from the app's container (only added cookies come
  back); no WebSocket for pages; a script navigation (`location.href`) keeps the
  ADR 0079 limit.
- Out of scope: third-party public proxy, Wayback Machine fallback.

## Task list

- [x] Browser's `network.real.access` capability, system app, server grant by
      default.
- [x] Relayed `Cookie` header, `FollowRedirects`, cookies of every hop.
- [x] Source of the response (SDK, Web Workers, browser).
- [x] Source icon, "Not secure", error pages.
- [x] Relay queue and retry, 30 s timeout.
- [x] Tests: relay with cookies, redirects with `Set-Cookie`, domain cookies,
      page of 20 resources, refusal without `network.real.access`, error codes,
      source icon, `fetch` through the bridge.
- [x] Docs in French and English.
