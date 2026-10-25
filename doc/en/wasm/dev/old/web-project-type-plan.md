# Technical plan: "Web" project type (hosted ASP.NET/Blazor, WebAssembly or Server)

**Status:** technical proposal, not implemented, no `D-xxx` decision
recorded, no code written. The choices below reflect the preferences already
expressed by the user in session (2026-08-17) on the four structuring
questions; they must still become formal ADRs before any code
(see §9).

## 1. Goal

Add a fourth `AppKind`, **`Web`**, next to `Window`, `Terminal` and
`Service`: an ASP.NET Core project (or Blazor Razor Class Library) referenced
by the host can be exposed in the HackerOS ecosystem and become reachable
from the **Browser** app (`org.hackeros.browser`) through a domain name, with
two execution modes:

- **WebAssembly**: the project is a Blazor WASM assembly lazy-loaded
  on demand, like the existing system apps (`docs/lazy-loading.md`).
- **Server**: the project runs on the `Server/HackerOs.Server` side and is reached
  from the Browser through the proxy bridge already in place (ADR 0028).

If no domain is declared, the default domain is derived from the project name
(assembly/namespace). An optional manifest block (`Web`, in
`app.manifest.json`, modeled on the existing `Terminal` block) can
specify the domain, the execution mode and advanced options.

The code editor must eventually allow creating this type of project
directly from HackerOS; see §8 for the scope retained for this
iteration.

## 2. Context and related documents

- [ADR 0021](../adr/0021-simulated-network-and-browser-rendering.md) deliberately
  rejected raw HTML/iframe rendering for **simulated sites**
  (`ISimulatedWebsiteController`/`SimulatedPage`), for reasons of
  testability and XSS security. This plan **does not challenge that
  decision**: real Web projects are a separate and parallel mechanism
  (§4, §6), not an extension of the `SimulatedPage` model.
- [ADR 0023](../adr/0023-optional-game-domain-and-proxy-fallback.md) establishes the
  dual-implementation gateway pattern (local / server proxy /
  fallback) via DI + manifest capability, reused here for the
  WebAssembly-vs-Server resolution and for the unified DNS resolution (§7).
- [ADR 0027](../adr/0027-server-hosted-blazor-ui.md) and
  [ADR 0028](../adr/0028-client-side-server-connection.md): `Server/HackerOs.Server`
  already hosts the Razor component tree and an `IProxyClient` for real network
  commands; the Server mode of this plan reuses these two
  mechanisms rather than inventing a third.
- [`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md):
  sister plan (same status: proposal not recorded) for Roslyn compilation
  and execution of user C#, local (WASM) or server. Phase 2 of
  the code editor (§8) depends directly on it and cannot move forward until
  it is accepted.
- [`docs/lazy-loading.md`](lazy-loading.md): lazy-load mechanism
  (`BlazorWebAssemblyLazyLoad`, `WebAssemblyLazyAssemblyTransport`,
  `BuildKnownLazyAppDescriptorRegistry`) reused as is for the
  WebAssembly mode (§5).
- [ADR 0020](../adr/0020-editor-framework-and-script-sandbox.md) and
  [`code-editor.md`](code-editor.md): strictly limit the editor to
  `vfs.read`/`vfs.write`; any execution stays behind
  `user-scripts.execute`. File scaffolding (§8, phase 1) respects
  this limit; the compiled preview (§8, phase 2) cannot without the
  `D-xxx` decision of the compilation/execution plan.
- `Shared/HackerOs.App.Abstractions/AppManifest.cs`, `AppKind.cs`: existing
  manifest model, anchor point of the new `Web` block (§4).
- `Shared/HackerOs.Simulation.Abstractions/Network/SimulatedNetworkServiceContracts.cs`:
  existing `ISimulatedWebsiteRegistry`/`ISimulatedDns`, not modified by this
  plan (§7 adds a parallel registry, not an extension of these).

## 3. Two production paths, a single runtime execution model

Two distinct mechanisms feed the same runtime concept (an `AppKind.Web`
app addressable by domain):

1. **Build-time project reference**: a developer adds a real
   ASP.NET/Blazor `.csproj` as a project reference to the host (same family
   as the explicit referenced assembly discovery described by `P1-APP-002`
   for the existing `Window`/`Terminal`/`Service` apps). This is the scenario
   requested first: "an asp.net project added as a reference to the
   host project".
2. **Runtime authoring via the code editor**: a game user creates a
   Web project from the Code Editor app, without touching the host on the
   developer side. This path depends on the hybrid compilation/execution plan
   (§8, phase 2) and is out of scope for the first iteration.

Both paths converge on the same manifest (`AppKind.Web`, `Web` block),
the same domain resolution mechanism (§7) and the same rendering component
in the Browser (§6). The difference concerns only *how* the assembly
or the source code arrives in the system, not *how* it runs
once there. This convergence avoids duplicating the rendering/resolution logic
between the "repository project" case and the "in-game created project" case, exactly
as the compilation/execution plan avoids duplicating the compilation logic
between client and server.

## 4. `AppKind.Web` and manifest model

### 4.1 New enumeration value

```csharp
// Shared/HackerOs.App.Abstractions/AppKind.cs
public enum AppKind
{
    Window,
    Terminal,
    Service,
    Web, // nouveau
}
```

### 4.2 New optional manifest block

Modeled exactly on `TerminalCommandManifest Terminal` already present on
`AppManifest`:

```csharp
/// <summary>Gets hosting metadata for a Web application.</summary>
public WebAppManifest? Web { get; init; }

/// <param name="Domain">
/// Domaine explicite (ex. "mycompany.hos"). Si null, le domaine par défaut
/// dérive du nom d'assembly/namespace du projet — voir §4.3.
/// </param>
/// <param name="ExecutionMode">WebAssembly (lazy-load client) ou Server (hébergé par HackerOs.Server).</param>
/// <param name="RootComponentType">
/// Nom qualifié du composant Razor racine à monter (mode WebAssembly
/// uniquement). Convention par défaut si absent : voir §5.2.
/// </param>
public sealed record WebAppManifest(
    string? Domain,
    WebExecutionMode ExecutionMode,
    string? RootComponentType = null);

public enum WebExecutionMode
{
    WebAssembly,
    Server,
}
```

`AppManifestValidator` gains a rule symmetrical to the one that already requires the
`Terminal` block for `AppKind.Terminal`: the `Web` block is optional (just
like the `Domain` field within it), but if present, it is valid only
for `AppKind.Web` (same pattern as the current rejection of `dialogs.*` for
non-`Window` apps, `P1-CAP-003`).

**The full manifest is still required**: HackerOS today has no
notion of an app without `app.manifest.json` (schema, Id, capabilities, etc. are all
`required`). "Optional manifest" for this plan means: *the `Web` block and
its `Domain` field* are optional, not the manifest as a whole.

### 4.3 Default domain resolution

Resolution order, from most specific to most generic:

1. `manifest.Web.Domain` if set.
2. Otherwise, normalized assembly name of the entry point
   (`manifest.EntryPoint.Assembly`, e.g. `MyCompany.Website` →
   `mycompany-website`), lowercase, with dots replaced by
   hyphens (avoids any collision with the subdomain syntax).
3. A deterministic zone suffix (e.g. `.hos` or `.local`, to be settled in an ADR)
   distinguishes this zoning from the existing simulated domains (`hackersearch.net`,
   etc.) so that a reader/player can visually tell a narrative
   simulated site from a real Web app of the build.

## 5. WebAssembly mode: lazy-load + component mounting

### 5.1 Loading

Full reuse of the existing mechanism (`docs/lazy-loading.md`):

- The assembly of the Web project is declared `<BlazorWebAssemblyLazyLoad>` in
  `HackerOs.Ecosystem.csproj`, exactly like the current system apps.
- Its `app.manifest.json` is embedded and discovered by
  `BuildKnownLazyAppDescriptorRegistry` just like the
  `Window`/`Terminal`/`Service` manifests.
- Actual loading is triggered at the Browser's first navigation
  to its domain (not when launching an "app" from the launcher; a `Web`
  app does not appear in the launcher/taskbar by default, it is
  addressed by domain; `Presentation` is nevertheless still required by the current model,
  at a minimum for the inventory/settings).

### 5.2 Mounting: no iframe

The declared root component (`WebAppManifest.RootComponentType`, or by
convention the only public type implementing a new `IWebAppRootComponent`
marker in the assembly if not declared explicitly) is
instantiated dynamically and mounted **directly as a render fragment** in
the Browser's content panel; no iframe, no HTML
serialization. This is the same trust model as any other top-level Blazor
app of the system: the project is C#/Razor compiled with
the host, not untrusted user content.

This path suits only Blazor projects (Razor Class Library targeting
WASM), not classic ASP.NET Core MVC/Razor Pages, which assumes
a real HTTP request/response cycle. A classic MVC/Razor Pages project must
use Server mode (§6).

## 6. Server mode: hosting in `HackerOs.Server`, access through proxy

### 6.1 Hosting

The referenced ASP.NET project is deployed/run **inside the existing
`Server/HackerOs.Server` process**, alongside its current sync/identity/proxy APIs
(ADR 0027 already added a third Razor hosting mode there;
this plan adds a fourth, dedicated to referenced Web projects).

Two possible approaches for the concrete mounting, to be settled in a
dedicated ADR:

- **Sub-app mapped by domain/path**: each referenced Web project is
  registered as a minimal API endpoint group / branch middleware
  (`app.MapWhen`/`UseWhen` on the `Host` header or a path prefix
  derived from the domain resolved in §4.3), in the same process, at
  compile time.
- **Reinforced isolation**: takes up the thinking already recorded in
  `user-code-compilation-execution-plan.md` §3 on the absence of real
  isolation in WASM and the need for OS isolation on the server side (container
  or separate process). For this plan, the trust distinction is
  important: a project referenced at build time by the host developer
  does **not** have the same threat model as a future project compiled on the fly
  from user code (Phase 2, §8); the former can reasonably
  share the `HackerOs.Server` process without an additional sandbox; the
  latter cannot. This plan therefore pre-approves in-process hosting
  only for projects referenced at build time.

### 6.2 Access from the Browser

The Browser never opens a direct HTTP connection to the real host. It
goes through `IProxyClient` (ADR 0028), exactly like the real fallback of
`ping`. The proxied request targets `HackerOs.Server` with the resolved domain
(§4.3) passed as a header or in the path, so that the §6.1 middleware routes
to the right sub-app.

The real response body (HTML/CSS/JS) goes through the proxy bridge, so this plan
inherits the limitation already noted by ADR 0028: `ProxyHttpResponse`
is today **metadata-only** (no binary/text body streaming).
The Server mode of this plan is therefore blocked as long as this gap
(`docs/server-implementation-pass.md`, "Pass N+1a") is not filled; to be
noted explicitly as a prerequisite, not as an implementation detail.

### 6.3 Rendering: sandboxed iframe

Unlike the WebAssembly mode (§5.2), the content received here is real HTML
produced by a real HTTP server, not a C# component tree. It is
displayed through a **sandboxed iframe** (`sandbox="allow-scripts"` at a minimum,
`srcdoc` fed by the proxied body once the §6.2 blocker is lifted, restrictive
CSP). This point deliberately reintroduces what ADR 0021 ruled out
for *simulated* sites, hence the need for a **new ADR**
that explicitly documents this exception as scoped to *real hosted*
Web apps, never to the existing simulated sites
(`ISimulatedWebsiteController` stays unchanged, structured, without raw HTML).

## 7. Domain resolution in the Browser

### 7.1 Separate registry

New contract, parallel to `ISimulatedWebsiteRegistry` rather than an
extension of it (which stays scoped to structured simulated sites):

```csharp
public interface IHostedWebAppRegistry
{
    HostedWebAppDescriptor? FindByDomain(string domain);
}

public sealed record HostedWebAppDescriptor(
    string Domain,
    string AppId,
    WebExecutionMode ExecutionMode);
```

Fed at boot from the discovered `AppKind.Web` manifests (same
pipeline as `AppLifecycleOrchestrator`/`AppEntryPointDiscovery` for the
other `AppKind` values), with the domain already resolved according to §4.3.

### 7.2 Unified resolution order

So that a single address typed in the Browser bar (and, eventually,
`curl`) behaves consistently, resolution follows a
deterministic order, on the same fallback principle already established by ADR 0023
(`IGameDomainGateway.IsAvailable`) and ADR 0028 (real network proxy):

1. `ISimulatedDns`/`ISimulatedWebsiteRegistry`: existing narrative simulated
   sites (unchanged).
2. `IHostedWebAppRegistry`: real Web apps of this plan.
3. Real network fallback through `IProxyClient`, if connected (ADR 0028), for any
   domain not resolved by 1 or 2.

This order is deliberately centralized in a single resolution point
shared by the Browser and the network commands (`curl`, `ping`), rather than
each querying the three sources independently, which avoids a
behavior divergence between "navigate to X" and "curl X".

## 8. Code editor: creating Web projects

User decision: pure scaffolding for this iteration; the compiled/executed
preview is planned but explicitly **after** acceptance of the hybrid
compilation/execution plan. Two phases therefore, the second not being
committed by this document:

### Phase 1: Scaffolding (within the scope of this plan once accepted)

- New action in Code Editor ("New Web project") that writes a skeleton of files into the
  app-scoped VFS: `Program.cs` (or `App.razor` +
  `Pages/`), minimal `wwwroot/`, and an `app.manifest.json` pre-filled
  with `Kind: "web"`, a `Web` block whose `Domain` is empty (to force
  default resolution §4.3 or invite the user to fill it in), and
  `ExecutionMode: "webAssembly"` by default.
- Stays strictly within the `vfs.read`/`vfs.write` capabilities already covered
  by ADR 0020: no new capability, no compilation, no
  execution. The created files become a functional `Web` app only
  if they are then integrated as a build-time project reference (§3,
  path 1); runtime scaffolding does not, by itself, make the site
  navigable from the Browser.

### Phase 2: Compiled/executed preview (out of scope, depends on external D-xxx)

- Once `user-code-compilation-execution-plan.md` is accepted and its server/local
  phase implemented, Phase 2 of this plan connects the Code
  Editor preview to `ICompilationService`/`IExecutionSandbox` (path 2 of §3), then
  reuses **the same mounting component** as the WebAssembly mode (§5.2)
  for the live preview; not a third rendering mechanism.
- Do not start this phase before the `D-xxx` decision of the sister plan.

## 9. D-xxx decisions still to formalize (next steps)

This document freezes the preferences expressed in session, but nothing is
`Accepted` as long as dedicated ADRs do not exist. Recommended split,
in this order (each step can be a separate ADR):

1. **ADR: `AppKind.Web`, manifest, WebAssembly mode.** Covers §4 and §5:
   new enum value, `WebAppManifest`, default domain resolution,
   component mounting without iframe. Depends on nothing other than
   what exists.
2. **ADR: Server hosting in `HackerOs.Server` and iframe exception.**
   Covers §6: sub-app mounting, proxy routing by domain, and the explicit
   exception to ADR 0021 for iframe rendering scoped to real hosted apps.
   Depends on (1) for the manifest model, and blocked by the proxy's
   binary body streaming (`server-implementation-pass.md`,
   Pass N+1a).
3. **ADR: `IHostedWebAppRegistry` and unified domain resolution.** Covers
   §7. Depends on (1) and (2).
4. **Extension to the plan `user-code-compilation-execution-plan.md`**
   to connect Phase 2 of the editor (§8) once that sister plan is itself
   accepted; not an ADR of this plan.

## 10. Out of scope for now

- Hosting Web projects on an external real server not operated by
  `HackerOs.Server` (ruled out by the user's decision in §Context).
- Multi-tenancy/multi-user isolation of hosted Server apps:
  inherits the single-tenant limitation already recorded by ADR 0027.
- On-the-fly compilation/execution of Web projects created in-game (Phase 2,
  §8) as long as the sister plan is not accepted.
- Extension of `ISimulatedWebsiteController`/`ISimulatedPage`: the simulated
  sites model stays unchanged and separate (§7.1).
- Binary body streaming for `IProxyClient`: external prerequisite already
  identified by ADR 0028, not taken up here in detail.
- Final DNS zone suffix for real Web domains (`.hos` vs
  other): to be settled in ADR (1).

## 11. Reference documents

- [ADR 0020](../adr/0020-editor-framework-and-script-sandbox.md)
- [ADR 0021](../adr/0021-simulated-network-and-browser-rendering.md)
- [ADR 0023](../adr/0023-optional-game-domain-and-proxy-fallback.md)
- [ADR 0027](../adr/0027-server-hosted-blazor-ui.md)
- [ADR 0028](../adr/0028-client-side-server-connection.md)
- [`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md)
- [`lazy-loading.md`](lazy-loading.md)
- [`hosting-model.md`](hosting-model.md)
- [`code-editor.md`](code-editor.md)
- [`server-implementation-pass.md`](server-implementation-pass.md)
