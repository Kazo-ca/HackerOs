# ADR 0027: Server-Hosted Blazor UI (Third Host, Single-Tenant Phase)

## Status

Accepted on 2026-08-14.

## Context

HackerOS ships two UI hosts today: `OS/HackerOs.Ecosystem`, the standalone
Blazor WebAssembly PWA (the static-deploy target), and `test/test`, an
ASP.NET Core Blazor Web App that renders the same component tree via
`AddInteractiveWebAssemblyRenderMode` as a developer debug harness. A third
process, `Server/HackerOs.Server`, exists only as an optional API (sync,
identity, proxy) — see `docs/hosting-model.md`.

`docs/hosting-model.md`'s "Future direction" section already states, as a
long-standing but unimplemented goal, that `HackerOs.Server` should become a
third way to serve the same Razor UI: "hosting the same Razor components the
way `test/test` does today, but as a real deployment target rather than a
debug harness, with backend-only contracts and services ... injected directly
into the composition root instead of being reached over HTTP from the WASM
client." That section explicitly gates implementation behind "its own ADR
(server-hosted composition, render-mode strategy, contract injection
boundary)." This ADR is that gate, triggered by backend-server integration
work starting now: all three deployment shapes (WASM-only via `test/test`,
static WASM via `OS/HackerOs.Ecosystem`, and the server-hosted UI) must build
and run side by side going forward.

Two facts from the existing composition root, `AddHackerOsEcosystem`
(`OS/HackerOs.Ecosystem/EcosystemServiceCollectionExtensions.cs`), bound what
this ADR can responsibly decide for a first implementation slice:

1. Nearly every service it registers — `IEventBus`, `INotificationQueue`,
   `ICapabilityGrantRepository`, the filesystem stack
   (`IFileSystemProvider`/`IFileSystemMountRouter`/`IFileSystemPathResolver`/
   `IFileSystemAuthorizer`/`IFileSystemService`), `ISessionService`,
   `IProcessManager`, `IAppEnablementRegistry`, `AppLifecycleOrchestrator`,
   `AppIntentDispatcher`, `WindowRuntime` and its coordinators, and every
   `IndexedDb*` repository — is registered `AddSingleton`: one instance for
   the whole process. That is correct for WASM (one browser tab is one
   process) and for `test/test` (one developer's browser tab at a time). It
   is not correct for a Blazor Server host serving multiple concurrent
   circuits: two simultaneously connected users would share one session, one
   filesystem, one window layout, and one capability-grant store. Converting
   every one of those registrations to circuit-scoped lifetimes, and
   replacing every `IJSRuntime`/IndexedDB-backed repository with an
   EF Core/SQLite-backed equivalent (IndexedDB storage lives in the browser,
   not the server process, so it cannot be shared across users the way a
   database can), is a large, separable project.
2. In Blazor Server's Interactive Server render mode, `IJSRuntime` calls
   still travel over that circuit's SignalR connection to the one connected
   browser's own JavaScript engine — the same mechanism ADR 0015 already
   relies on for IndexedDB access. So for exactly one active circuit, the
   existing browser-storage-backed composition keeps working completely
   unmodified: the same `IndexedDbFileSystemProvider`,
   `IndexedDbSettingsDocumentService`, `WebCryptoPasswordHasher`, and friends
   that back the WASM hosts back a single-circuit Blazor Server host too,
   with no new storage layer required.

Separately, two WASM-only seams in the shared UI code block Interactive
Server rendering outright, independent of the multi-tenancy question:

- `OS/HackerOs.Ecosystem/App.razor` injects `IWebAssemblyHostEnvironment`
  directly (used once, to gate exception detail in the fatal-error boundary).
  That service is not registered outside a WASM render context.
- The only `IBuildKnownAssemblyTransport` implementation,
  `WebAssemblyLazyAssemblyTransport`, wraps
  `Microsoft.AspNetCore.Components.WebAssembly.Services.LazyAssemblyLoader`,
  a WASM-only on-demand download API. The entire reason lazy loading exists
  (shrink the WASM payload — see `docs/lazy-loading.md`,
  `docs/startup-performance.md`) does not apply server-side, where every
  referenced assembly is already loaded in-process at startup.

## Options considered

### Where the UI hosting code lives

**Inside `Server/HackerOs.Server` in place.** Matches `docs/hosting-model.md`'s
own documented intent verbatim (quoted above) and requires no new project or
solution-folder wiring. `Server/HackerOs.Server.csproj` already uses
`Microsoft.NET.Sdk.Web`, the same SDK `test/test/test.csproj` uses to host a
Blazor render-mode component tree today — that SDK combination is already
proven in this repository.

**A new sibling project** (e.g. `Server/HackerOs.Server.BlazorHost`)
referencing `HackerOs.Server` for its services. Would keep the API-only
process free of Razor/component dependencies, but contradicts the "same
process" framing `docs/hosting-model.md` already commits to, duplicates
ASP.NET Core hosting boilerplate across two processes, and works against the
documented end state of injecting backend services directly into the UI
composition root — that only makes sense if the UI and the services sharing
that composition root live in one process.

Decision: modify `Server/HackerOs.Server` in place.

### Render mode

**Interactive Server** (`AddInteractiveServerRenderMode`). Keeps all app
logic executing in the same ASP.NET Core process where the backend services
(`IAccountService`, `ISyncService`, `IProxyService`) already live, which is a
prerequisite for the still-deferred direct-injection consumption described
in `docs/hosting-model.md` — a render mode that shipped component code to the
client (WASM-over-a-second-host, or static SSR) would foreclose that path.

Decision: Interactive Server render mode, `prerender: false` (matching
`test/test`'s existing choice — `EcosystemBootCoordinator`'s async boot
sequence and `IJSRuntime`-backed IndexedDB access are not prerender-safe, since
no circuit or browser JS engine exists yet during prerendering).

### Multi-tenancy scope for this phase

**Convert the composition root to circuit-scoped lifetimes and add
EF/SQLite-backed replacements for every `IndexedDb*` repository now**, so the
server-hosted UI supports concurrent multi-user circuits from day one.
Rejected for this phase: it is a large, separable project (roughly fifteen
singleton registrations to reason about individually, plus new
storage-backend implementations for filesystem, settings, sessions,
capability grants, and the app catalog) that blocks shipping any
server-hosted UI at all until it is fully done, and duplicates work that
belongs in its own ADR once the shape of a multi-tenant backend is actually
designed.

**Ship a single-tenant / single-active-circuit host now, document the gap.**
Reuses `AddHackerOsEcosystem` completely unmodified — no lifetime changes, no
new storage implementations — because a single circuit's `IJSRuntime` calls
reach that one browser's IndexedDB exactly like WASM does (see fact 2 above).
This mirrors how `test/test` is already used today: one developer, one
browser tab, at a time.

Decision: single-tenant / single-active-circuit for this phase. This is a
known, explicit limitation, not a silently accepted one — see Consequences.

## Decision

1. `Server/HackerOs.Server` becomes a third UI host by adding Razor
   Components hosting (`AddRazorComponents().AddInteractiveServerComponents()`,
   `MapRazorComponents<App>().AddInteractiveServerRenderMode()`) directly to
   its existing `Program.cs`, alongside its unchanged sync/identity/proxy API
   surface. A new `Components/App.razor` mirrors `test/test/Components/App.razor`,
   embedding the same shared `HackerOs.Ecosystem.App` root component.
2. Two render-mode-agnostic seams are introduced so the shared UI no longer
   hard-depends on WASM-only types:
   - `IEcosystemHostEnvironment` (`Platform/HackerOs.Platform.Blazor/Hosting/`),
     a one-property abstraction over "is this a development environment,"
     with a WASM-backed implementation (wraps `IWebAssemblyHostEnvironment`)
     registered by `OS/HackerOs.Ecosystem` and `test/test`, and an
     ASP.NET Core-backed implementation (wraps `IHostEnvironment`) registered
     by `Server/HackerOs.Server`. `App.razor` injects the abstraction instead
     of `IWebAssemblyHostEnvironment` directly. Each host registers its own
     implementation before calling `AddHackerOsEcosystem`, mirroring the
     existing pattern for `IBuildKnownAssemblyTransport` (that method never
     registers a transport itself either).
   - A new eager `IBuildKnownAssemblyTransport` implementation,
     `InProcessAssemblyTransport`, registered only by `Server/HackerOs.Server`,
     resolves requested assembly names from already-loaded process assemblies
     instead of downloading them.
3. `AddHackerOsEcosystem` itself is not modified. The server host is
   explicitly single-tenant for this phase: it is expected to serve one
   active browser circuit at a time, the same way `test/test` is used today.
   Concurrent multi-user access is out of scope and must not be assumed
   working.
4. `IAccountService`/`ISyncService`/`IProxyService` are not injected into the
   UI composition root in this phase. The server-hosted UI's local-user
   login flow works identically to the other two hosts (local IndexedDB
   users), with no new wiring to the server's own sync/identity/proxy
   capabilities.

## Implementation notes

Two non-obvious failures surfaced only by actually running the host (not by
code review) and are recorded here so they aren't rediscovered:

- **`AddHackerOsEcosystem` registers `HackerOsDiagnosticLoggerProvider` as
  `ILoggerProvider`.** ASP.NET Core eagerly constructs every registered
  `ILoggerProvider` while building the host's `ILoggerFactory`, as part of
  `WebApplicationBuilder.Build()` itself — before any Blazor Server circuit
  exists. That provider construct-injects the browser-storage diagnostic
  repository, which construct-injects `IJSRuntime`; resolving the real
  circuit-backed `IJSRuntime` implementation with no live circuit attached
  hung `Build()` indefinitely (confirmed by substituting a trivial
  non-framework `IJSRuntime` stub, which made the hang disappear). This is
  the single-tenant captive-dependency limitation above surfacing at its
  worst — a startup hang instead of a leaked-instance quirk. Fix: `Program.cs`
  removes only the `ILoggerProvider` descriptor `AddHackerOsEcosystem` itself
  added (tracked by service-collection count before/after the call), leaving
  every framework-registered `ILoggerProvider` and every other
  `AddHackerOsEcosystem` registration untouched. The diagnostic sink
  (`IDiagnosticSink`/`IPersistentDiagnosticRepository`) still works normally
  once a real circuit resolves it lazily; only its eager `ILoggerFactory`
  bridge is skipped for this host.
- **`MapRazorComponents<App>()` has no route to match without a `@page`
  component somewhere in scope.** `App.razor` (in both `test/test` and
  `Server/HackerOs.Server`) hardcodes `<HackerOs.Ecosystem.App />` in its
  body with no `<Routes>`/router — but an HTTP endpoint for `/` still doesn't
  exist unless *some* discovered type declares `@page "/"`. `test/test`
  has one only by accident: its unused template scaffold
  (`Components/Pages/Home.razor`, left over from `dotnet new blazor` the same
  way `Components/Routes.razor` is dead code) happens to declare `@page "/"`,
  which is what actually makes `/` resolvable there — `Home.razor`'s own
  content is never rendered, since `App.razor` ignores routing entirely.
  `Server/HackerOs.Server` needed the same minimal `Components/Pages/Home.razor`
  added deliberately (not as scaffold leftover) for `/` to resolve at all.

## Consequences

- A third host builds and runs, proving the shared composition root and
  `HackerOs.Ecosystem.App` component tree work outside a WASM render context,
  without disturbing either existing host or the existing API surface.
- Concurrent multi-user access to the server-hosted UI is unsupported until a
  separate future project converts the composition root's singleton
  registrations (enumerated in Context) to circuit-scoped lifetimes and
  replaces every `IndexedDb*` repository with an EF Core/SQLite-backed
  equivalent. That project needs its own ADR once undertaken — this decision
  does not pre-approve any particular scoped-conversion or storage design.
- Consuming `IAccountService`/`ISyncService`/`IProxyService` directly from UI
  code instead of over HTTP remains deferred. It will need a shared client
  abstraction (e.g. an `IAccountClient`-shaped interface) with an HTTP-backed
  implementation for the WASM hosts and a direct-injection implementation for
  the server-hosted UI, so the same UI component code compiles and runs
  across all three hosts without `HackerOs.Server`'s EF/SQLite-specific types
  leaking into shared UI code. That is its own future ADR.
- `IEcosystemHostEnvironment` and `InProcessAssemblyTransport` are new, small,
  render-mode-specific seams; neither changes behavior for the two existing
  hosts.
- No change to existing sync/identity/proxy endpoint behavior, authentication,
  or database schema in `Server/HackerOs.Server` — this is purely additive UI
  hosting capability.
- No production-hardening guidance (reverse-proxy/TLS termination for
  SignalR, session affinity/sticky sessions for scale-out) is established by
  this ADR — single-process, single-circuit, self-hosted-operator scope only,
  consistent with the multi-tenancy limitation above.

## Amendment (2026-08-22): most enumerated singletons are already Scoped

The Context section above enumerates ~15 `AddSingleton` registrations
(`IProcessManager`, `AppLifecycleOrchestrator`, `WindowRuntime` and its
coordinators, every `IndexedDb*` repository, etc.) as the thing a future
multi-tenancy project would need to convert to circuit-scoped lifetimes. That
enumeration is no longer accurate as a description of the current
registrations, though the ADR's decision and consequences below still hold.

Two unrelated fixes, both landed after this ADR was accepted, already
converted most of that list to `AddScoped`:

- **2026-08-18** (`a00b9597`, fixing the reusable-`FileView` migration's
  hosting bugs): ~25 `IJSRuntime`-touching services were converted from
  Singleton to Scoped because Blazor Web Apps construct-inject a component's
  `[Inject]` properties during the static-SSR pass even with
  `prerender: false` — the first resolution permanently captured an
  unattached `IJSRuntime` and broke JS interop for every real circuit
  afterward. This included `IProcessManager` and `AppLifecycleOrchestrator`.
- **2026-08-22** (`a39040d0`, fixing a window-restore hang after a hard
  reload): `WindowRuntime`, `WindowLaunchCoordinator`,
  `WindowCloseGuardRegistry`, and `AppBackHandlerRegistry` were converted from
  Singleton to Scoped because they still depended on the (by-then-Scoped)
  orchestrator's per-circuit app instances; leaving them Singleton meant a
  reload's fresh circuit inherited stale window entries pointing at app
  instances that no longer existed.

Neither fix was done *for* multi-tenancy — both target single-circuit
correctness bugs (broken JS interop, a stuck window after reload) that this
ADR's single-tenant/single-active-circuit phase does not otherwise address.
They do not close `P5-UI-003` (see `docs/implementation-status.md`): the
storage backend behind every `IndexedDb*` repository is still the connected
circuit's own browser IndexedDB, not an EF Core/SQLite-backed store shared
across circuits, so two concurrent users still cannot share sessions,
filesystem, or capability grants the way a real multi-tenant backend would
require. Scoped lifetime is a necessary precondition for that future work,
not a substitute for it. Before relying on a specific service's current
lifetime, check `AddHackerOsEcosystem` in
`EcosystemServiceCollectionExtensions.cs` directly rather than this ADR's
Context section.

## References

- ADR 0009: Purpose-Built Window Runtime (JS interop isolation precedent)
- ADR 0015: Browser Support Baseline and IndexedDB Adapter Approach (the
  single-circuit `IJSRuntime`-reaches-that-browser's-IndexedDB reasoning this
  ADR relies on)
- ADR 0024: Server Identity and Device Registration
- ADR 0025: Record Synchronization Envelope, Conflict Model, and Cursor
  Strategy
- `docs/hosting-model.md`
- `docs/lazy-loading.md`
- `docs/startup-performance.md`
