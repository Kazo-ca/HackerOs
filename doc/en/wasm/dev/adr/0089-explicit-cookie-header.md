> [Version française](../../../../fr/wasm/dev/adr/0089-explicit-cookie-header.md)

# ADR 0089: `Cookie` header written by the app

## Status

Accepted on 2026-10-17, with the `web/14-cookie-header` work item.

- **Plan phase:** web phase, continued ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Completes:** ADR 0074 §1 and §2 (HTTP exchanges of the network gateway,
  delivery to the listening process) and ADR 0056 §6 (`HackHttpClient` and its
  `HackCookieContainer`). Replaces no decision.

## Context

The OS network gateway (`AppNetworkGateway`) sends at each hop the
cookies of the client's container (`HackHttpRequest.Cookies`, the "cookie jar").
A `Cookie` header that the app wrote itself, for example
`curl -H 'Cookie: .HackSession=...'`, was removed before delivery to the local
site and never reached the simulated network's controllers, which only read
`SimulatedHttpRequest.Cookies`. Noted by `web/6` (`docs/asp-hack-mvc.md`),
the limitation prevented replaying a session or an anti-forgery token by
hand from the terminal, which any real `curl` does.

## Decision

1. **The header is sent.** The `name=value` pairs of each `Cookie` header
   of the request (RFC 6265 §4.2; only the first `=` separates the name from the
   value) are merged with the jar's cookies for the hop's host, into a
   single `Cookie` header toward a local site and into the request's `Cookies` toward
   a simulated site.
2. **What is typed wins.** An explicit pair replaces the jar's cookie of the
   same name; the other cookies of the jar are also sent, in their order.
3. **Same host only.** The header follows redirects that stay on the requested
   host and is removed when a redirect goes to another host,
   like `curl` since 7.83 (it does not leak to a third party).
4. **Nothing is stored.** A request header does not create a cookie: explicit
   pairs never enter the jar, do not come back as
   `Set-Cookie` in the response and are not added to the client's
   `HackCookieContainer`. Only the site's `Set-Cookie` headers enter it.
5. **The browser does not change.** Its transport already removes any `Cookie`
   header coming from a page (`SdkBrowserTransport`), like a browser that
   forbids it in `fetch`; its cookies remain those of the tab. The relay to
   the real Internet already forwarded headers as is.

## Consequences

- `curl -H 'Cookie: session=...' http://intranet.hack/` reaches the site; the jar
  per `curl` execution keeps its role for cookies set along the
  way (redirect after login).
- Tests: `LocalNetworkTests.A_cookie_header_given_to_curl_reaches_the_site_without_entering_the_jar`
  (local site, two execution modes) and
  `Wave4NetworkTests.HackHttpClient_sends_an_explicit_cookie_header_to_a_simulated_site_without_storing_it`
  (HackMail).
