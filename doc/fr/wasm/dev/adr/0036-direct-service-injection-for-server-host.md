# ADR 0036: Direct Service Injection for the Server-Hosted UI (Pass N+5)

## Status

Accepted on 2026-08-17.

## Context

ADR 0027 shipped `Server/HackerOs.Server` as a third UI host reusing
`AddHackerOsEcosystem` completely unmodified, and explicitly deferred one
piece of it (Decision 4 / Consequences): the server-hosted UI's own
`IAccountClient`/`IProxyClient`/`ISyncClient` are the same `HttpClient`-backed
implementations the two WASM hosts use, so when the server-hosted UI's own
Settings panel connects to "the optional server" (`SettingsWindow.razor` →
`IServerConnectionService.ConnectWithNewAccountAsync`/`ConnectWithExistingAccountAsync`),
or when a terminal command's real-network fallback fires (`curl`, `nmap`,
`cat`), the server-hosted circuit makes a real HTTP round-trip **to its own
process** — `Server/HackerOs.Server` calling itself over its own Kestrel
listener. This works, but is wasteful and adds a needless network hop and
failure mode (DNS/port/TLS issues reaching yourself) for a request that could
be a plain in-process method call. ADR 0027 gates fixing this behind "its own
ADR for the composition-root wiring (how a request picks HTTP vs. direct
implementation)" — this is that ADR.

`IAccountClient`/`IProxyClient`/`ISyncClient` were deliberately designed in
ADR 0028 to speak only in `HackerOs.Server.Contracts` DTOs and primitives —
no `HttpClient`, no ASP.NET-specific types — specifically so a second,
direct-injection implementation would be possible without redesigning the
interfaces. That design intent holds up: no interface changes were needed.

Two lifetime facts, confirmed by reading the actual registrations rather than
assumed, shaped the wiring:

1. `IAccountClient`/`IProxyClient`/`ISyncClient` are registered `AddSingleton`
   in the shared composition root (`EcosystemServiceCollectionExtensions.cs`),
   used by all three hosts.
2. `IAccountService`/`ISyncService`/`IProxyService` are registered
   `AddScoped` in `Server/HackerOs.Server/Program.cs`, and (per `ProxyService`'s
   own constructor) transitively hold `HackerOsServerDbContext` — an EF Core
   `DbContext`, itself `AddScoped` by `AddDbContext`, which is not safe to
   reuse across unrelated concurrent operations. `Server/HackerOs.Server/Program.cs`
   already disables DI scope validation (`ValidateScopes = false`,
   `ValidateOnBuild = false`) for the unrelated single-tenant
   `IndexedDb*`-repository captive-dependency shape ADR 0027 documents — that
   flag would *silently allow* a Singleton to construct-inject a Scoped
   service directly (capturing one `DbContext` instance forever) rather than
   throwing, which would be a real, hard-to-diagnose correctness bug (stale
   connections, thread-safety violations under concurrent requests on the
   same circuit), not merely a lint warning.

## Decision

### 1. Composition-root switch: `TryAddSingleton`, host registers first

`EcosystemServiceCollectionExtensions.cs`'s three registrations changed from
`AddSingleton<TClient, HttpXClient>()` to `TryAddSingleton<TClient, HttpXClient>()`.
`Server/HackerOs.Server/Program.cs` registers its own direct implementations
*before* calling `AddHackerOsEcosystem`, so `TryAddSingleton` finds the
service type already registered and no-ops — the exact same "each host
registers its own implementation first" pattern ADR 0027 already established
for `IEcosystemHostEnvironment` and `IBuildKnownAssemblyTransport`. Neither
`OS/HackerOs.Ecosystem` nor `test/test` change: nothing registers these
service types before their calls to `AddHackerOsEcosystem`, so `TryAddSingleton`
installs the same `Http*Client` defaults they already had.

### 2. Direct clients resolve their Scoped dependency through a fresh `IServiceScope` per call

`DirectAccountClient`/`DirectProxyClient`/`DirectSyncClient`
(`Server/HackerOs.Server/ServerConnection/DirectServerConnectionClients.cs`)
are registered `AddSingleton`, matching the interfaces' existing lifetime, but
never construct-inject `IAccountService`/`ISyncService`/`IProxyService`
directly. Each method instead takes an `IServiceScopeFactory`, creates a
scope, resolves the Scoped service from *that* scope, and disposes the scope
when the call completes — the standard, correct pattern for a Singleton that
needs a Scoped dependency on demand rather than held. This sidesteps the
DbContext-capture bug in Context above entirely, independent of whether scope
validation happens to be enabled for this host.

### 3. Access-token validation reuses `ITokenService` directly — no new security logic

`IProxyClient`/`ISyncClient`'s methods take an opaque `accessToken` string
(the same one `IServerConnectionService` already manages via
`EnsureAccessTokenAsync`); the HTTP path validates it through ASP.NET Core's
bearer-auth middleware (`HackerOsBearerHandler`), which itself just calls
`ITokenService.ValidateAccessTokenAsync(accessToken, ct)` — an in-memory
lookup against issued tokens, not JWT/crypto validation. `ITokenService` is
already `AddSingleton`, so the direct clients call it exactly the same way
the middleware does, with no new validation logic to get wrong: `IsValid`
false → throw `ServerConnectionException`, matching what the HTTP path
surfaces on a 401. This reuses trusted, already-audited logic rather than
reimplementing any part of token validation.

### 4. Every service-layer exception is translated to `ServerConnectionException`

Callers across the codebase (`IServerConnectionService`, `CurlCommand`,
`NmapCommand`, `CatCommand`, `SettingsWindow.razor`) already only handle
`ServerConnectionException` (thrown uniformly by every `Http*Client` on
failure) — never `AccountService`'s `InvalidOperationException`/
`UnauthorizedAccessException`/`KeyNotFoundException`, `SyncService`'s
`ArgumentException`/`KeyNotFoundException`, or `ProxyService`'s
`ProxyRequestException`. The direct clients catch each of these at the
service-call boundary and rethrow as `ServerConnectionException`, so calling
code behaves identically regardless of which `IAccountClient`/`IProxyClient`/
`ISyncClient` implementation is wired in — this parity is the whole point of
ADR 0028's original interface design, and would silently break without it
(an unhandled `ProxyRequestException` propagating out of `CurlCommand`'s
`catch (ServerConnectionException or HttpRequestException)` block, for
example, would surface as a confusing `EntryPointFault` instead of a clean
"could not resolve host" message).

### 5. `IContentTransferClient` stays HTTP-only for now

ADR 0027's deferred item named `IAccountService`/`ISyncService`/`IProxyService`
specifically; `IContentTransferClient` (file-sync content chunks, ADR 0030)
was not named and is left out of this pass to keep it focused — the
server-hosted host still makes a loopback HTTP round-trip for file-content
sync. Extending the same `TryAddSingleton` + direct-client pattern to it is
straightforward future work if wanted, not a redesign.

## Consequences

- The server-hosted UI's Settings panel (connect/disconnect) and every
  terminal command's real-network fallback (`curl`, `nmap`, `cat`) now call
  `IAccountService`/`ISyncService`/`IProxyService` in-process when running on
  `Server/HackerOs.Server`, instead of looping back through HTTP to itself.
  Both WASM hosts are completely unaffected — same `Http*Client`s, same
  behavior, verified by the two hosts' `AddHackerOsEcosystem` call sites
  needing zero changes.
- `IContentTransferClient` remains HTTP-only on all three hosts; extending
  direct injection to it is explicitly left as future work, not silently
  bundled into this pass.
- This does not touch ADR 0027's still-open multi-tenancy limitation (Pass
  N+6) — the server-hosted host remains single-tenant/single-active-circuit;
  this pass only removes an unnecessary network hop within that existing
  single-circuit model.

## References

- ADR 0027: Server-Hosted Blazor UI (the deferred item this ADR closes)
- ADR 0028: Client-Side Optional-Server Connection and Proxy Bridge (the
  `IAccountClient`/`IProxyClient`/`ISyncClient` interfaces, deliberately kept
  ASP.NET-free so this direct implementation needed no redesign)
- ADR 0035: Single-Port TCP Reachability Probe for nmap's Real-Network
  Fallback (added `IProxyService.ExecuteTcpProbeAsync`, covered by
  `DirectProxyClient` the same as the other two methods)
- `docs/server-implementation-pass.md`
