> [Version française](../../../../fr/wasm/dev/adr/0130-server-serves-webassembly-os-and-api.md)

# ADR 0130: `HackerOs.Server` serves the OS in WebAssembly and keeps only its API

## Status

Accepted on 2026-10-18. Two questions are asked to Benoit (§2 and §4); what
follows applies the recommended answer while awaiting it.

- **Plan:** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  decision 8 (Benoit's answer Q7: "Server = OS in WebAssembly + API");
  implemented by `exec/8-server-wasm`.
- **Replaces:** [ADR 0027](0027-server-hosted-blazor-ui.md) (third host
  in Interactive Server) and [ADR 0036](0036-direct-service-injection-for-server-host.md)
  (direct injection of the server's services into the interface).
- **Makes moot:** ADR 0066 §3 (measurements turned off by default on the Server
  host), ADR 0067 §6, ADR 0070 §6, ADR 0072 §7 (no worker
  on this host) and ADR 0108 §5 (direct client streams without WebSocket).

## Context

Today (`Server/HackerOs.Server/Program.cs`), the server renders the whole OS
interface **on the server side** (`AddInteractiveServerComponents`,
`Components/App.razor`), a single circuit at a time, and the OS calls the
server's services directly there (`DirectServerConnectionClients`). App code
therefore runs on server threads: no Web Worker, no reliable
measurement, and an execution mode different from the other two hosts.

Benoit thought the server already hosted the WebAssembly client and
only executed what must be (relay, synchronization,
authentication). This is what this ADR decides.

## Decision

### 1. The server sends the same OS as the Ecosystem

- The browser receives the OS **in WebAssembly** from the server and
  runs it like the Ecosystem: same workers, same measurements, same
  `/proc/hackeros`, same offline mode.
- The server keeps its API endpoints (identity, synchronization, HTTP relay,
  TCP and UDP, administration) with no contract change.
- The OS talks to the server **over HTTP and WebSocket**, like the Ecosystem connected
  to a server (ADR 0028, 0102, 0108).

### 2. How the server serves the OS (question asked to Benoit)

Applied while awaiting (recommended): **the published files of the Ecosystem**,
as is. The server references `OS/HackerOs.Ecosystem` as a hosted WebAssembly
client, serves `_framework/`, `index.html`, the PWA manifest and the
Ecosystem's service worker (`MapStaticAssets`, fallback to `index.html`), and
no longer has any Razor component.

- Advantage: a single startup to maintain (the Ecosystem's `index.html`),
  identical PWA and service worker, cross-origin isolation of ADR 0131
  served the same way.
- The other choice: a Blazor Web App in `InteractiveWebAssembly` with its
  own `App.razor`, like `test/test`. It keeps a second startup to
  maintain and a different service worker, for a prerender that the OS does not use
  (it depends on IndexedDB). If Benoit chooses it, a short ADR replaces this
  paragraph.

### 3. What is removed from the server

`AddInteractiveServerComponents`, `MapRazorComponents<App>`,
`Components/App.razor`, `Components/Pages/Home.razor`,
`DirectServerConnectionClients`, `InProcessAssemblyTransport`,
`AspNetCoreEcosystemHostEnvironment`, the references to the system app projects
(they come with the Ecosystem). The `TryAddSingleton` calls of
`AddHackerOsEcosystem` placed for ADR 0036 remain: they cost nothing.

### 4. The OS recognizes the server that served it (question asked to Benoit)

Applied while awaiting (recommended): at startup, the OS queries
`GET /api/hackeros/host-info` on its own origin (short timeout, ignored
offline). If the server answers, Settings › Server is **prefilled** with
that address and offers to connect; the user decides. The OS never
depends on the server (AGENTS.md I.1).

The other choice: nothing special, the user enters the address as with
the Ecosystem. The "mandatory connection" option is not offered: it would
contradict the rule "nothing must require the server".

### 5. Hosts after the change

| Host | Role |
|---|---|
| `OS/HackerOs.Ecosystem` | Standalone PWA, offline, no server (unchanged). |
| `test/test` | Interactive debugging of WebAssembly components (unchanged). |
| `Server/HackerOs.Server` | Serves the Ecosystem OS **and** the optional API. No more server-side rendering. |

## Consequences

- `build/verify-hosts.sh` and `HackerOs.HostSmoke.Tests`: the Server host has
  workers and `/proc/hackeros/workers`; `kill -9` of a loop is checked there.
- The server tests that went through the direct client go through HTTP.
- Docs: `AGENTS.md` (hosts section), [`project-overview.md`](../project-overview.md),
  [`building-and-testing.md`](../building-and-testing.md),
  [`old/hosting-model.md`](../old/hosting-model.md) (banner pointing to this ADR),
  [`server-network-relay.md`](../server-network-relay.md),
  [`worker-processes.md`](../worker-processes.md).
- Several users can open the OS from the same server: each has
  their own OS in their browser. The API remains today's (single-tenant
  for administration, ADR 0102).

## References

- ADR 0027, 0028, 0036, 0066, 0067, 0070, 0072, 0102, 0108, 0131.
- Hosting a Blazor WebAssembly client with ASP.NET Core.
