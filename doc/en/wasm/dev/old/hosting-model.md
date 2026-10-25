> [Version française](../../../../fr/wasm/dev/old/hosting-model.md)

# Hosting Model

> **Fait le 2026-10-18 (`exec/8-server-wasm`) :** l'[ADR 0130](../adr/0130-server-serves-webassembly-os-and-api.md)
> remplace les ADR 0027 et 0036. `HackerOs.Server` sert l'OS de l'Ecosystem en
> WebAssembly et ne garde que son API ; l'OS s'y connecte tout seul (ADR 0134).
> État actuel : [`../server-hosting.md`](../server-hosting.md). Ce document
> décrit l'état d'avant.

## Purpose

HackerOS is one set of Razor components, apps, and Platform/Infrastructure
services that can be composed by more than one host process. This document
describes the current hosts, what each one is for, and the long-term direction
for the optional server. It is a reference for orienting new work, not a task
list — it does not add or reopen anything in
`wasm2/HackerOs/docs/integration-task-list.md`.

## Guiding constraint

The browser is the local-first authority. Every component and service must
keep working when only the WebAssembly host is present, offline, with no
server reachable. Hosts other than `HackerOs.Ecosystem` exist to make
development or future deployment easier — none of them may become a required
dependency for ordinary use.

## Current hosts

All three hosts are booted to the desktop by one command,
`build/verify-hosts.sh` (see [`../building-and-testing.md`](../building-and-testing.md#verify-the-three-hosts)).
A host's `Components/App.razor` must link its own scoped-CSS bundle
(`@Assets["{Project}.styles.css"]`, which imports every referenced library's
scoped CSS) and declare an icon; the smoke test fails otherwise.

### 1. `OS/HackerOs.Ecosystem` — the product host

`OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj` is the standalone,
self-contained Blazor WebAssembly PWA: its own `wwwroot/index.html`, web app
manifest, and service worker. It has no ASP.NET Core process behind it and no
compile-time dependency on the server. This is the host that ships — it must
build, publish, and run fully offline on its own. See
[`ecosystem-host.md`](ecosystem-host.md).

> **Update (v3/6, ADR 0098 §2):** the OS itself (`App.razor`, boot coordinator,
> `AddHackerOsEcosystem`, browser adapters, `css/app.css`) moved to the Razor
> Class Library `OS/HackerOs.Ecosystem.Host` (namespace unchanged, so
> `HackerOs.Ecosystem.App` now lives in that assembly). `OS/HackerOs.Ecosystem`
> keeps its page, service worker, app references and a three-line `Program.cs`
> calling `AddHackerOsBrowserHost`. See [`../sdk-packages.md`](../../framework/sdk-packages.md).

### 2. `test/test` — the interactive debug harness

`test/test/test.csproj` is a Blazor **Web App** (ASP.NET Core host, `Program.cs`
+ `Components/App.razor`) that renders the exact same `HackerOs.Ecosystem.App`
component tree via `AddInteractiveWebAssemblyRenderMode`. It exists solely
because a Blazor Web App host gives a better local debugging loop (server-side
build/reload, `UseWebAssemblyDebugging`) than debugging the standalone WASM PWA
directly. It references the same app assemblies as `HackerOs.Ecosystem` and
adds MudBlazor's CDN font/CSS/JS for convenience during development. Its
browser side runs the Ecosystem's `Program.cs`, so it has the same Web Worker
process pool (ADR 0070, [`../worker-processes.md`](../worker-processes.md));
`HackerOs.Server` has none and runs every process in the server process.

This host is not a distribution target and is not a dependency of any other
project — it is expected to remain a permanent developer-facing harness, not a
temporary scaffold to delete once the migration is "done."

### 3. `Server/HackerOs.Server` — the optional backend, and a third UI host

`Server/HackerOs.Server/HackerOs.Server.csproj` is an ASP.NET Core process
(EF Core/SQLite) that is entirely optional at runtime. It provides three
backend capabilities:

- **Sync** — versioned record push/pull with conflict resolution (ADR 0025).
  Server-side only so far; no client consumes it yet — see
  `docs/server-implementation-pass.md`.
- **Identity** — account/device registration and token management (ADR 0024).
  Consumed client-side per ADR 0028 (`IAccountClient`, opt-in per-device
  connection via Settings).
- **Proxy** — server-validated HTTP/TCP/UDP proxying for authorized apps
  reaching the real network. Consumed client-side per ADR 0028
  (`IProxyClient`). Since `v3/10-server-proxy` it relays every HTTP method
  with its body, TCP and UDP streams over WebSockets, checks the app's grant
  itself and reads its policy from `appsettings.json` — see
  [`../server-network-relay.md`](../server-network-relay.md).

Browser-local storage (IndexedDB) remains the source of truth regardless of
whether a device is connected to the optional server — connecting is always
opt-in and never required for normal use.

Per ADR 0027, it also hosts the same `HackerOs.Ecosystem.App` Razor component
tree the other two hosts render, via Interactive Server render mode
(`Components/App.razor`, mapped in `Program.cs` alongside the unchanged API
endpoints). This mirrors how `test/test` wires the shared component tree in,
but as a real deployment target rather than a debug harness. **This host is
single-tenant / single-active-circuit for this phase**: `AddHackerOsEcosystem`
is reused completely unmodified. Most of its services are now `AddScoped`
(converted after ADR 0027 was accepted, by unrelated single-circuit
correctness fixes — see that ADR's 2026-08-22 amendment), but every
`IndexedDb*` repository still talks to the connected circuit's own browser
IndexedDB rather than a shared server-side store, which is what actually
blocks concurrent multi-user access — the same assumption `test/test` already
makes. See ADR 0027 for the full reasoning.

See [`server-security.md`](server-security.md) and
[`server-backup-restore.md`](server-backup-restore.md) for the backend
capabilities, and ADR 0027 for the UI-hosting addition.

## Future direction: multi-tenant server hosting and direct service injection

Two extensions to the server-hosted UI added by ADR 0027 remain future work,
not yet scheduled:

- **Multi-tenant concurrent access.** Most of the composition root's services
  are already circuit-scoped (see ADR 0027's amendment); replacing every
  `IndexedDb*`-backed repository with an EF Core/SQLite-backed equivalent
  (browser storage cannot be shared across users the way a database can)
  remains the actual blocker. This needs its own ADR once undertaken.
- **Direct service injection instead of HTTP.** Consuming
  `IAccountService`/`ISyncService`/`IProxyService` directly from UI code when
  hosted in `HackerOs.Server`, instead of over HTTP as the WASM hosts must.
  This needs a shared client abstraction (e.g. an `IAccountClient`-shaped
  interface with an HTTP-backed implementation for the WASM hosts and a
  direct-injection implementation for the server-hosted UI) so the same UI
  component code compiles and runs across all three hosts without
  `HackerOs.Server`'s EF/SQLite-specific types leaking into shared UI code.
  This also needs its own ADR.

Consequences this implies for any component/service work done in the
meantime, so it doesn't need to be redone later:

- Nothing in `Platform`, `Infrastructure`, or app projects should assume there
  is exactly one way to reach backend capabilities (HTTP call vs. injected
  service). Prefer depending on the existing abstractions/contracts rather than
  concrete HTTP client types where practical.
- The WASM-only composition (`AddHackerOsEcosystem`) must keep working
  unmodified whether or not a server-hosted mode exists — this is additive, not
  a replacement for the standalone PWA.

## Non-goals

- This document does not change which host is "the" build target — that
  remains `OS/HackerOs.Ecosystem`.
- The server-hosted UI added by ADR 0027 does not support concurrent
  multi-user circuits — see "Future direction" above. Treat it as a
  single-operator deployment target, not a multi-tenant service, until that
  gap is closed by a future ADR.
