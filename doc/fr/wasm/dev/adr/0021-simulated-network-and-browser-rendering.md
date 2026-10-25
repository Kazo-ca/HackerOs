# ADR 0021 — Simulated Network Contracts and Browser Rendering Model

**Status:** Accepted  
**Date:** 2026-08-03  
**Deciders:** Architecture, Security  
**Supersedes:** —  
**Superseded by:** in part, [ADR 0074](0074-listening-ports-and-name-resolution.md) §1 (real HTTP bodies, 2026-10-16) and [ADR 0075](0075-browser-html-rendering.md) (isolated HTML rendering). `SimulatedPage` stays a valid response for the historical sites.  

---

## Context

Phase 4 Wave 4 ports the simulated network, browser app, and website controllers from `src/core/network.ts`,
`src/apps/browser.ts`, `src/websites/web-server.ts`, and related files.

Two decisions must be made:

### D-015 — Safe simulated website rendering

The legacy browser app wrote raw HTML strings into an iframe's document. That approach:
- Requires an actual iframe DOM element, which is unavailable in headless C# tests.
- Couples the test surface to a rendered HTML string, making gameplay content hard to inspect, test, or localize.
- Risks XSS if simulated page HTML ever contains user-controlled content (e.g., a form submission reflected back).
- Makes it difficult to apply HackerOS design tokens to simulated website surfaces.

**Options considered:**

| Option | Description | Pros | Cons |
|---|---|---|---|
| A — Raw iframe HTML | Legacy approach: write raw HTML strings into a sandboxed iframe | Simple to port | Untestable headlessly, XSS risk, token-incompatible |
| B — Sanitized iframe via srcdoc | Write server-produced HTML to a sandboxed `srcdoc` attribute with a restrictive CSP | Closer to legacy UX | Still iframe-dependent, CSP fragile, hard to test |
| C — Component model | Simulated websites return structured `SimulatedPage` objects (title, metadata, structured content sections); Blazor components render them using Platform design tokens | Testable, consistent UX, extensible, accessible | More upfront modeling work; deliberate departure from iframe fidelity |

**Decision: Option C — Component model.**

Rationale:
- Structured `SimulatedPage` objects are testable in headless xUnit without a browser.
- Content is never written as raw HTML, eliminating XSS risk from simulated form submissions or reflected data.
- Blazor components apply HackerOS Gothic/Hacker design tokens consistently, keeping the simulated web aesthetic controlled.
- Each simulated website section (`HeroSection`, `LoginSection`, `ProductGrid`, `Thread`, etc.) is a discriminated union variant, easily extensible.
- The legacy iframe HTML content is captured as behavioral reference and reproduced faithfully as typed sections, not verbatim HTML.

**Accepted:** 2026-08-03

---

## Decisions Made

### Network domain contracts

- Simulated network lives in `Shared/HackerOs.Simulation.Abstractions/Network/`.
- No real DNS, real TCP/IP, or real HTTP ever occurs from these contracts.
- `SimulatedHost` records carry hostname, IP, OS fingerprint, up/down state, latency, and an open/closed/filtered port map.
- `SimulatedDns` provides hostname→IP and reverse IP→hostname resolution.
- `SimulatedHttpRequest` and `SimulatedHttpResponse` model first-slice HTTP behavior: method, path, query, cookies, headers, status code, redirect URL, and a structured `SimulatedPage` body (not raw HTML).
- Cookies are scoped per simulated host and carried automatically by the network service (no raw `document.cookie` exposure).
- Redirect chains are followed up to a bounded limit (10 hops); infinite loops return a `TooManyRedirects` error.

### Website controller contracts

- `ISimulatedWebsiteController` is a pure C# interface implemented by each simulated website.
- Each controller declares the hostnames it handles, the registered routes (method + path pattern), and processes `SimulatedHttpRequest` → `SimulatedHttpResponse` (no JS, no async I/O required).
- Parameterized route patterns (`/account/:id`) use a simple `{param}` token; the extracted values are passed via `request.RouteParams`.
- Controllers register through `ISimulatedWebsiteRegistry`, which is seeded at boot from an enumerable of known controllers.

### Browser app rendering

- The Browser Window app (`org.hackeros.browser`) displays `SimulatedPage` sections using collocated Blazor components under `Apps/System/HackerOs.Apps.Browser/`.
- Navigation, history, bookmarks, and cookie jars live in per-window C# state; the window close disposes all session cookies.
- Simulated page content never requires `IJSRuntime`; only the URL input bar uses a minimal collocated JS module for focus-on-navigate behavior.
- Source inspector shows the raw typed section model, not reconstructed HTML.
- The network service resolves hostname→IP, looks up the controller registry, dispatches the request, and returns the response to the Blazor component, all synchronously in the simulation (no async I/O at the domain layer).

### curl/ping/nmap commands

- `curl` (`org.hackeros.cmd.curl`) sends a `SimulatedHttpRequest` through the network service and prints the response body sections as plain text.
- `ping` (`org.hackeros.cmd.ping`) resolves the hostname via `SimulatedDns` and reports synthetic latency from the `SimulatedHost` record.
- `nmap` (`org.hackeros.cmd.nmap`) performs a port scan by examining the target `SimulatedHost`'s port map within the requested range and printing results in nmap-like format.
- All three commands make zero real external network requests. Their `app.manifest.json` declares `network.simulated.read` capability only.

---

## Excluded from this ADR

- Real HTTP via `HttpClient` or WebSockets to external targets. This is Phase 5 optional server proxy work.
- TCP/UDP socket simulation. Deferred to Phase 5.
- Real browser `fetch()` interop for simulated requests. The simulation is pure C# domain logic.
- Server-Side Rendering or real website hosting.
- Persisting simulated cookies or browsing history to IndexedDB. First slice keeps session-scoped in-memory state.
- Simulated TLS/certificate verification UI.

---

## Consequences

- Simulated websites are significantly easier to write, test, and localize as C# classes.
- The visual fidelity of the simulated web depends on the quality of the `SimulatedPage` section model and Blazor components, not on verbatim HTML strings.
- Any simulated website that requires form-state simulation (e.g., a bank login) does so through typed request body parameters, not raw form HTML.
- A `network.simulated.read` capability distinguishes simulation access from the future real-proxy capability.
