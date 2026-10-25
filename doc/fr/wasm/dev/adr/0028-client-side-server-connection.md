# ADR 0028: Client-Side Optional-Server Connection and Proxy Bridge

## Status

Accepted on 2026-08-14.

## Context

`Server/HackerOs.Server` already implements two capabilities the browser-local
HackerOS is meant to reach when the optional server is available: an
HTTP/TCP/UDP proxy (`IProxyService`, real network access for apps like
`curl`/`ping`/`nmap`) and a sync/backup bridge (`ISyncService`, versioned
push/pull with conflict resolution, ADR 0025) for the case where an offline
session comes back online, or the same profile is used from two computers.
Browser-local IndexedDB storage remains the source of truth in both cases —
the server is a bridge and a backup target, never a requirement for normal
operation. See `docs/hosting-model.md`.

This design intent is already documented and partially seeded in the code:

- ADR 0025 (accepted) defines the sync envelope/conflict model; the server
  side (`ISyncService`/`SyncRecordEntity`) already implements it.
- ADR 0023 (accepted) already names the intended fallback — "network commands
  fall back to the Phase 5 Server Proxy ... when Game Domain is unavailable" —
  but `Apps/Commands/HackerOs.Commands.Curl/CurlCommand.cs` today only calls
  `ISimulatedNetworkService`; its own docstring states "Makes zero real
  network or HTTP calls." No gateway abstraction for a *real* proxy call
  exists yet, only the documented intent to add one.
- `Infrastructure/HackerOs.Infrastructure.Browser/Schema/HackerOsIndexedDbSchema.cs`
  already reserves a `syncMetadata` object store, but its own doc comment
  scopes it to "local revision/consistency bookkeeping only — it is not
  multi-device network sync, which remains excluded."
- `docs/integration-task-list.md` §32 lists "browser client adapters in
  Browser Infrastructure" as in-scope, but every relevant task (`P5-SYNC-003`
  through `006`, `P5-PROXY-002` through `007`) is still open, and a
  repository-wide search found zero `HttpClient`/`IHttpClientFactory`/
  `HackerOs.Server.Contracts` references anywhere outside `Server/`.

So the client-to-server networking itself is greenfield. The one genuinely new
decision this ADR makes — not previously decided anywhere — is how a
browser-local user becomes "connected" to a server account at all.
`LocalUser` (browser-only, no server concept) and `AccountEntity` (the
server's own identity model, ADR 0024, opaque bearer tokens) are today two
completely disconnected identity systems with no bridging field, flow, or UI.

Full sync (five domains: Settings, FileSystem, Grants, AppCatalog,
FileAssociations) is substantial, separable work in its own right — each
domain needs its own serialize-to-`SyncRecordEnvelope`/apply-conflict-
resolution adapter against a different existing repository. This ADR decides
the connection model and ships the proxy bridge; sync is deferred to
follow-up passes tracked in `docs/server-implementation-pass.md`, one domain
at a time, matching how the repository already tracks `P5-SYNC-003..006` as
distinct open items rather than one task.

## Decision

### 1. Connection model: per-device, not per-local-user

A device (already a stable concept — `DeviceId`, ADR 0024) registers with the
optional server once; every local OS user on that browser profile shares the
same device-level server connection.

**Rejected alternative — per-local-user linking.** Would require a second
bridging table (`LocalUserId` ↔ `AccountId`) with no server-side counterpart
to anchor it to, since the server's own identity model has no concept of
"which local OS user" made a request — only which device and account. It also
doesn't match the motivating scenarios (an offline session reconnecting, the
same profile from two computers): both are about *devices* reconnecting, not
about which of several local OS users happens to be active on one browser at
a given moment.

Connection state (`AccountId`, `DeviceId`, the configured server base URL, and
an opaque refresh token) is stored once per device, independent of which
local user is logged in.

### 2. Discovery/configuration: explicit opt-in, no auto-discovery

The user enters a server URL and either creates an account or logs into an
existing one, via a new panel in `Apps/System/HackerOs.Apps.Settings`
(reusing the `CreateAccountRequest`/`LoginRequest` shapes already defined in
`HackerOs.Server.Contracts` — no new wire DTOs needed). Until this is done,
every network command and every future sync attempt behaves exactly as today
(pure simulation / local-only). This is strictly additive — no existing
behavior changes for a user who never connects a server.

### 3. Where connection state lives

A new dedicated repository (`IServerConnectionRepository`), IndexedDB-backed,
new object store, rather than overloading `syncMetadata` (already scoped to
local-only bookkeeping by its own doc comment, and not intended to hold
authentication material) or an ordinary settings document (the settings
projection has a `.config` export/import path; a refresh token should not
flow through it).

### 4. HTTP client shape

`Infrastructure/HackerOs.Infrastructure.Browser/ServerConnection/` hosts:

- `IServerConnectionService` — connect (create-or-login, persist tokens) /
  disconnect (clear the repository) / `EnsureAccessTokenAsync()` (refresh
  on demand from the stored refresh token, per ADR 0024's rotation model —
  access tokens are not persisted, only the longer-lived refresh token is).
- `IAccountClient` — thin `HttpClient` wrapper over `/api/account`,
  `/api/auth/login`, `/api/auth/refresh`.
- `IProxyClient` — thin `HttpClient` wrapper over `/api/proxy/http`,
  `/api/proxy/policy`.

Both clients take only `Guid`/string/`HackerOs.Server.Contracts` DTO
parameters — no ASP.NET-specific types — mirroring the shape their
server-side counterparts (`IAccountService`/`IProxyService`) already have.
This is deliberate: it means a future direct-injection implementation of the
same interfaces for the server-hosted host (ADR 0027's other deferred item)
does not require redesigning them, even though building one is not part of
this ADR. `HackerOs.Server.Contracts` already documents this intent in its
own project file: "Referenced by both the ASP.NET Core server and the browser
client adapter" — this ADR is the first thing to actually reference it from
the browser side.

Registered in `AddHackerOsEcosystem` so all three hosts get it uniformly.

### 5. Proxy bridge for network commands

`curl`/`ping`/`nmap`/`cat` (ADR 0023's own named list) each gain an
availability check: if `IServerConnectionService` reports a connected,
reachable server, and the target isn't resolved by the existing simulated-web
domain routing (`ISimulatedNetworkService` stays authoritative for the
simulated web; real proxying is for everything else), route through
`IProxyClient.ExecuteHttpRequestAsync`; otherwise, keep today's pure-
simulation behavior unchanged. This mirrors the fallback-check pattern ADR
0023 already established for `IGameDomainGateway.IsAvailable`, applied here to
the proxy gateway instead of the game domain one.

### 6. Sync deferred

Not implemented by this ADR. Tracked as five separate follow-up passes in
`docs/server-implementation-pass.md` (Settings, FileSystem, Grants, AppCatalog
+ FileAssociations, in that order), each reusing this ADR's connection
foundation (`IServerConnectionService`/`IAccountClient`) but adding its own
per-domain sync adapter against the already-accepted `ISyncService`/
`SyncRecordEnvelope` contracts.

## Consequences

- Connecting to a server is fully opt-in and per-device; a user who never
  connects sees no behavior change anywhere.
- `curl`/`ping`/`nmap`/`cat` gain real network reach when a server is
  connected and reachable, matching ADR 0023's originally documented intent.
- No local-user-to-server-account bridge is introduced — server accounts
  remain a device-level concept. If a future requirement needs distinguishing
  which local OS user triggered a server action, that needs its own ADR; this
  one does not pre-approve any particular design for it.
- Full sync remains unimplemented until the five follow-up passes land; the
  `syncMetadata` object store's existing scope (local bookkeeping only) is
  unchanged by this ADR.
- Direct service injection for the server-hosted host (ADR 0027's other
  deferred item) remains unimplemented; this ADR only ensures the new client
  interfaces don't foreclose it later.
- `docs/server-implementation-pass.md` is the durable roadmap for the
  remaining passes; it must be updated at the end of each one.

## Implementation notes

Two findings from actually implementing this ADR, not just designing it:

- **`ProxyHttpResponse` is metadata-only.** `POST /api/proxy/http`
  (`ProxyEndpoints.ExecuteHttpAsync`) returns status/headers/content-hash but
  never streams the actual request or response body — no endpoint for that
  separate binary payload exists yet, even though `ProxyContracts.cs`'s own
  doc comments already describe the body as "transmitted as a separate binary
  payload." This means `IProxyClient` today can only serve callers that need
  reachability/status/headers (e.g. a headers-only request), not fetched
  content. Recorded as an open question in `docs/server-implementation-pass.md`
  rather than worked around.
- **Scope narrowed from four commands to one.** Decision 5's fallback pattern
  was proven end-to-end on `ping` only (an unrecognized host now attempts a
  real HTTP HEAD proxy round-trip before reporting unreachable) — `curl`,
  `nmap`, and `cat` were named as candidates in the original design but were
  deliberately not wired in the same pass, both because of the metadata-only
  gap above (blocks `curl`'s/`cat`'s normal content-fetching use case
  entirely) and to land one well-tested integration rather than four thin
  ones. Tracked as Pass N+1a in `docs/server-implementation-pass.md`.
- **`IAccountClient`/`IProxyClient`/`IServerConnectionService` live in
  `Platform/HackerOs.Platform.Core/ServerConnection/`, not
  `Infrastructure.Browser`.** All three are plain `HttpClient`-based with no
  JS interop — placing them in Infrastructure.Browser as first drafted would
  have forced every consumer (including browser-independent terminal command
  projects like `HackerOs.Commands.Ping`) to take an unnecessary
  `Microsoft.JSInterop` dependency. `IServerConnectionService`'s password
  hashing goes through the same optional `KeyDerivationAsyncDelegate` seam
  `LocalPasswordHasher` already uses (falling back to managed PBKDF2), rather
  than depending on the concrete browser `WebCryptoPasswordHasher` type
  directly — the composition root (`AddHackerOsEcosystem`) supplies the
  Web-Crypto-backed delegate at registration time, matching the existing
  `LocalSessionService` pattern exactly. Only `IServerConnectionRepository`'s
  IndexedDB-backed implementation remains in Infrastructure.Browser, since
  browser storage access is what actually requires `IJSRuntime`.

## References

- ADR 0023: Optional Game Domain Integration and Network Proxy Fallback
  (the fallback-check pattern this ADR reuses)
- ADR 0024: Server Identity and Device Registration (the device/token model
  this ADR's connection state is built on)
- ADR 0025: Record Synchronization Envelope, Conflict Model, and Cursor
  Strategy (the contract the deferred sync passes will implement against)
- ADR 0027: Server-Hosted Blazor UI (the other deferred-work source; this
  ADR's client shape is designed to remain compatible with ADR 0027's
  still-open direct-injection item)
- `docs/hosting-model.md`
- `docs/server-implementation-pass.md`
- `docs/integration-task-list.md` §32
