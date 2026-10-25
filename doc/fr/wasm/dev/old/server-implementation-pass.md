# Server Implementation Roadmap

## Purpose

This is the standing checklist for what remains to connect HackerOS's browser-local
runtime to the optional `Server/HackerOs.Server` backend. Read this first before
starting any new server-integration work — it tracks what has shipped and exactly
what the next pass is, so scope doesn't need to be re-derived from scratch each
time. **Update this doc at the end of every pass that touches server integration**:
move the finished item into "Done" with a pointer to the ADR/commit that did it,
and adjust the next pass's description if reality diverged from the plan. This
is a living roadmap, not a one-time status snapshot — treat edits to it as part of
"done" for any server-integration change, the same way `AGENTS.md`'s
documentation-maintenance rule already requires for other docs under
`doc/fr/wasm/`.

Guiding constraint carried through every pass below: browser-local IndexedDB
storage remains the source of truth. The optional server is a bridge (real
network access via proxy) and a backup/reconciliation target (sync across an
offline-then-reconnected session, or across two computers) — never a
requirement for normal operation. See `docs/hosting-model.md`.

## Done

- **Third UI host** (ADR 0027, `docs/adr/0027-server-hosted-blazor-ui.md`) —
  `Server/HackerOs.Server` hosts the same `HackerOs.Ecosystem.App` component tree
  as the two WASM hosts, via Interactive Server render mode, single-tenant/
  single-active-circuit only for this phase. Existing sync/identity/proxy API
  endpoints unchanged.
- **Client-side server connection + proxy bridge** (ADR 0028,
  `docs/adr/0028-client-side-server-connection.md`) — per-device opt-in
  connection to the optional server (`IServerConnectionRepository`,
  `IAccountClient`, `IProxyClient`, `IServerConnectionService`, in
  `Platform/HackerOs.Platform.Core/ServerConnection/`, browser-independent by
  construction), a Settings UI panel to connect/disconnect, and a real-network
  fallback wired into `ping` only (an unknown host now attempts an HTTP HEAD
  proxy round-trip through the connected server before reporting unreachable).
  **`curl`/`nmap`/`cat` were named as candidates but not wired in this pass** —
  scoped down deliberately rather than spread thin; see Pass N+1a below.
- **Settings domain sync** (ADR 0029, `docs/adr/0029-settings-sync.md`) — the
  first real sync client for any domain. Explicit per-document `SyncEligible`
  opt-in (only `AppearanceSettingsDocuments` opted in so far), deterministic
  `RecordId` derivation, two new domain-agnostic IndexedDB stores
  (`syncCursors`/`syncRecordState`, schema v4) reused by every future sync
  pass, `ISyncClient`, `ISettingsSyncService`, and a "Sync now" button plus
  an on-connect trigger in the Settings panel. Conflicts resolve automatically
  in the server's favor (not surfaced to the user) — an explicit, recorded
  simplification for this low-stakes first domain, not a general policy.
- **FileSystem domain sync** (ADR 0030, `docs/adr/0030-filesystem-sync.md`) —
  push/pull of every entry under the active user's `/home/{userId}` (recursive
  walk, not the whole filesystem). Fixed a real server-side gap along the way:
  `ContentBlobService.GetChunkAsync` was a stub returning zero bytes, so
  content download was completely broken — now content-addressed and
  session-free (`GET /api/sync/content/download/{contentHash}/chunks/{index}`),
  covered by the first server-side content-blob tests
  (`Tests/HackerOs.Server.Tests/ContentBlobServiceTests.cs`). New
  `IContentTransferClient`/`HttpContentTransferClient` (chunked upload/download,
  browser-independent, mirrors `ISyncClient`'s placement) and
  `IFileSystemSyncService`/`FileSystemSyncService` (the domain adapter —
  metadata via `SyncRecordEnvelope`, content via the separate chunked
  protocol, tied together only by `ContentHash`). Unlike Settings, a push
  conflict is **never** auto-resolved in either direction (ADR 0030 Decision
  5) — and a pull-side guard added during implementation prevents the
  matching gap: without it, a pull immediately following a conflicted push
  would have silently reapplied the server's copy over the very edit that
  just failed to push. Wired into the same "Sync now" button and a
  "N files have unresolved sync conflicts" indicator in the Settings panel.
  Known, deliberate simplifications carried forward rather than solved here:
  no deletion propagation (a file removed on one device isn't removed on
  another), and pull always re-downloads file content rather than checking
  for a local dedup hit first (the plan's "check local `fsContent` by hash"
  optimization needs an abstraction that doesn't exist yet — correctness was
  prioritized over that optimization for this pass).
- **Grants domain sync — pull-only** (ADR 0031, `docs/adr/0031-grants-sync.md`) —
  no client push exists for this domain, deliberately: nothing in the
  codebase legitimately originates a client-side grant to push (the only
  production grant-writer, `CleanProfileCapabilityGrantSeeder`, seeds exactly
  what each app's manifest declares at every login), and the server doesn't
  validate pushed Grants payload semantics — so the client simply never
  pushes rather than trusting an unvalidated server not to accept a crafted
  widening push. Added `IPersistentCapabilityGrantRepository.ImportAsync`
  (upserts a grant under a caller-supplied ID, needed since `GrantAsync`
  always mints a new one) so a pulled server-issued grant can apply under
  the server's own `RecordId` and a later re-pull (e.g. a revocation)
  updates the same row. **Not wired into live capability enforcement** —
  pulled grants land only in the durable `IPersistentCapabilityGrantRepository`;
  the in-memory `ICapabilityGrantRepository` every runtime capability check
  actually reads is still rebuilt from manifest declarations at every login,
  completely disconnected from the durable store. Wiring that up is a
  separate, larger change (touches `LocalSessionService` login seeding) that
  needs its own design once there's a real reason to widen local grants
  beyond manifest declarations — tracked as an open question below, not
  silently assumed solved.
- **App enablement management** (ADR 0032, `docs/adr/0032-app-enablement-management.md`)
  — not itself a sync pass, but a prerequisite the user identified while
  reviewing the plan for Pass N+4: `IPersistentAppCatalogRepository.SetEnabledAsync`
  and `AppLifecycleOrchestrator.DisableAsync`/`Enable` already existed but had
  zero production callers and no UI, so AppCatalog sync would have synced a
  feature nobody could use. Added a durable persistence path (`DisableAsync`/
  the renamed async `EnableAsync` now call `SetEnabledAsync`), boot-time
  hydration of the live `AppEnablementRegistry` from the durable store (closing
  the same kind of wiring gap ADR 0031 left open for Grants, but closed here
  because a real UI now depends on it), and a new "Installed Apps" Settings tab.
- **AppCatalog + FileAssociations domain sync** (ADR 0033,
  `docs/adr/0033-appcatalog-and-fileassociations-sync.md`) — completes the
  original five-domain sync roadmap. FileAssociations is a narrow sibling of
  `SettingsSyncService` scoped to the one `FileAssociationSettingsDocuments`
  document and `SyncDomain.FileAssociations` (not a generalization of the
  Settings adapter — the two domains are partitioned separately server-side).
  AppCatalog syncs only the ADR 0032 enablement flag (never the manifest,
  which is a build artifact) and, unlike Grants, gets push **and** pull since
  ADR 0032 gave it a real local write path; pulled changes take effect
  immediately via `AppEnablementRegistry`, not just at next boot. Conflict
  handling reuses Settings' server-wins pattern rather than ADR 0025's
  suggested `ClientWins`, recorded as a deliberate divergence since nothing
  could produce an AppCatalog conflict before this pass existed.

**The original five-domain sync roadmap (Settings, FileSystem, Grants,
AppCatalog, FileAssociations) is now complete.** Remaining work below is
either scoped-down follow-ups named along the way, or the two larger,
independently-optional items (Pass N+5, Pass N+6) ADR 0027 always described
as separate, deferred decisions.

- **Wire the terminal command catalog** (ADR 0034,
  `docs/adr/0034-wire-terminal-command-catalog.md`) — an unplanned prerequisite
  discovered while starting Pass N+1a below: verifying `ping`'s "proven
  end-to-end" real-network fallback against a real app launch (not just direct
  unit-test construction) surfaced that none of the 28 `Apps/Commands/*`
  projects were referenced by `HackerOs.Ecosystem.csproj` at all — the entire
  terminal command suite (including plain `ls`/`cat`/`mkdir`, not just
  curl/nmap/ping) was invisible to every host. Fixed three compounding gaps:
  wired 24 of the 28 command projects into the shared catalog (excluding
  `cd`/`pwd`/`clear`/`help`, which `TerminalWindow.razor` intercepts as
  built-ins before catalog resolution); made `AppLifecycleOrchestrator`
  construct terminal/service apps via `ActivatorUtilities.CreateInstance` so
  commands needing injected services (`PingCommand`, `CurlCommand`,
  `NmapCommand`) actually launch instead of silently failing construction;
  registered `ISimulatedNetworkService` for the first time with a small,
  explicitly-labeled `SmokeTestNetworkSeed` (`example.hackeros`,
  `empty.hackeros`) — deliberately not an attempt at the "Game domain" content
  pack ADR 0023 scoped separately. This is what actually makes Pass N+1a
  testable; it was blocked without this fix. Live verification in a real
  browser session (not just `dotnet test`) then surfaced a fourth, independent
  gap: `mkdir`/`touch`/`rm`/`chmod` declared only the `write` filesystem
  capability despite each calling `StatAsync` (a `read` operation) on their
  target's parent, and `alias`'s `app.manifest.json` had a stale empty
  `capabilities: []` that didn't match its already-correct C# manifest — both
  silently denied by the deny-by-default `CapabilityGrantRepository` and never
  observable before this pass, since none of these five commands had ever
  actually launched. Fixed by declaring `filesystem.user-home.read` alongside
  `write` for the first four, and by fixing `alias`'s JSON to match its C#
  source. See ADR 0034 Decision 4.
- **`curl -I` real-network fallback** (Pass N+1a, first of three originally-named
  commands) — extends the pattern `ping` already proved end-to-end
  (`PingCommand.cs`'s `PingRealHostAsync`) to `curl`: when `-I` is passed and
  the simulated network doesn't recognize the target host, `CurlCommand` now
  does a real HTTP HEAD proxy round-trip through the optional server (when
  connected), printing the real status line and headers, exactly matching
  `IProxyClient`'s own doc comment (*"Callers that only need
  reachability/status/headers, e.g. `curl -I`, are fully served today"*).
  Normal (non-`-I`) `curl` against an unrecognized host is untouched — still
  reports "Could not resolve host" — since full-body fetching needs the
  server-side proxy body-transfer gap closed first (see below). Regression
  and new-path coverage added in `Tests/HackerOs.Network.Tests/Wave4NetworkTests.cs`
  (`CurlCommand_HeadersOnly_UnknownHost_With/WithoutServerConnection_*`);
  live-verified in the browser for both the simulated-host and
  disconnected-unknown-host cases.
- **Fixed `IndexedDbFileSystemProvider.ReadAsync` treating an unwritten file as
  a provider failure** (found during ADR 0034 live verification) — `cat`
  couldn't read a file `touch` just created: `touch` only calls `CreateAsync`
  (never `WriteAsync`), so the persisted entry has `ContentHash == null`, and
  `ReadAsync` (`Infrastructure/HackerOs.Infrastructure.Browser/FileSystem/IndexedDbFileSystemProvider.cs`)
  treated that as `FileSystemErrorCode.ProviderFailure` instead of a
  legitimate empty file — `StatAsync`/`EnumerateAsync` both succeeded on the
  same path (they only read metadata, not content), so the file visibly
  existed everywhere except `cat`. `InMemoryFileSystemRepository` (the
  in-memory provider `dotnet test` exercises) already returns an empty
  content stream for an unwritten file, which is why this was invisible to
  the test suite and only surfaced through live browser verification — the
  two providers had diverged on what "file exists but was never written"
  means. Fixed by returning `Stream.Null` (with the entry's stored, default
  Binary descriptor) instead of failing when `ContentHash` is null, matching
  `FromMetadata`'s own defaults and the in-memory provider's behavior.
  Regression test added in
  `Tests/HackerOs.Infrastructure.Browser.Tests/IndexedDbFileSystemProviderTests.cs`
  (`ReadAsync_FileCreatedButNeverWritten_ReturnsEmptyContentNotFailure`);
  live-verified: `touch hello.txt` then `cat hello.txt` now succeeds with
  empty output instead of "No such file or directory".
- **`nmap` single-port real-network fallback** (ADR 0035,
  `docs/adr/0035-nmap-tcp-probe-fallback.md`) — the second of three originally
  named commands. Given a choice between a bounded multi-port scan endpoint
  (a materially bigger new server capability and abuse surface) and a
  ping-style single-port probe, the user chose the latter. New
  `ProxyTcpProbeRequest`/`ProxyTcpProbeResponse` contracts and
  `POST /api/proxy/tcp-probe` carry exactly one host and one port — no range
  or list shape exists anywhere in the contract. `ProxyService.ExecuteTcpProbeAsync`
  reuses every existing SSRF protection (device ownership, simulated-domain
  block, blocked-address ranges) but does **not** reuse the HTTP proxy's
  80/443 port allow-list, since an arbitrary target port is the point;
  `IProxyTcpConnector`/`SocketProxyTcpConnector` abstracts the raw socket
  connect the same way `IProxyAddressResolver` abstracts DNS, keeping the
  path fully unit-testable. `NmapCommand`'s fallback only fires when the
  simulated network doesn't recognize the host **and** the user passed
  `-p <single-port>` — a default range or any explicit range always stays on
  the pre-existing simulated "Host seems down" path, enforced client-side by
  construction (the real-probe code path is unreachable for a range). Full
  test coverage added server-side (`ProxyServiceTests.cs`) and client-side
  (`Wave4NetworkTests.cs`); live-verified: a known simulated host is
  unaffected, `nmap -p 443 <unknown-host>` attempts the real fallback and
  gracefully reports "Host seems down" when disconnected (matching
  ping/curl's disconnected behavior), and `nmap -p 1-100 <unknown-host>`
  never attempts a real probe regardless of connection state.
- **Proxy body-transfer + full-body `curl` real-network fallback** (ADR 0028
  follow-up, no new ADR — this extends an already-reviewed endpoint rather
  than adding a new capability) — closes the server-side gap tracked below:
  `ProxyHttpRequest` gained an `IncludeBody` flag (default `false`, so `-I`
  and `ping`'s HEAD probes are unaffected) and `ProxyHttpResponse` gained a
  matching `BodyBase64` field. The response body was already being fetched
  and size-capped server-side to compute the hash every response carries —
  it was just being discarded before this change; `IncludeBody` now controls
  whether it's also base64-encoded into the response, subject to the same
  `MaxResponseBytes` cap as every other proxy response. No new endpoint, no
  new SSRF surface, no new capability — this was a direct "simpler streaming
  response" implementation of the option `docs/server-implementation-pass.md`
  itself named as the alternative to a chunked sync-style transfer, which
  would have been unjustified complexity for a bounded, one-shot fetch.
  `CurlCommand`'s real-network fallback now also fires for a plain GET (not
  just `-I`) against a host unknown to the simulated network, printing the
  fetched body. A real-network POST (`-d`) against an unrecognized host
  stays explicitly out of scope. Tests added server-side (`ProxyServiceTests.cs`:
  `IncludeBody` on/off, empty-body edge case, size cap still enforced) and
  client-side (`Wave4NetworkTests.cs`: disconnected declines, connected
  fetches real content, a 4xx/5xx status doesn't print a body); live-verified
  in the browser (known simulated host unaffected, unknown host without a
  connection still reports "Could not resolve host").
- **`cat` gained URL-reading (simulated + real), previously a missing
  feature entirely** — ADR 0023 named `cat` alongside `ping`/`curl` as a
  "network command" requiring dual-mode support, but `CatCommand.cs` was
  purely a VFS file reader with zero URL detection, found during the
  previous pass and deliberately left unbuilt pending its own scoping.
  Scoping decision made here: an argument is read as a URL only when it
  starts with `http://` or `https://` (a bare filename like
  `cat example.hackeros`, with no scheme, stays a VFS lookup — no ambiguity,
  no behavior change for any existing use of `cat`). A recognized URL uses
  `_network.Navigate` and renders through the same
  `SimulatedPageTextFormatter` `curl` uses; an unrecognized host falls back
  to the same real-network `IncludeBody` GET `CurlCommand` uses, with
  identical messaging. That formatter (`page → plain text`, ~150 lines of
  section-type rendering) was extracted from `CurlCommand.cs` into
  `Shared/HackerOs.Simulation.Abstractions/Network/SimulatedPageTextFormatter.cs`
  once `cat` became a second consumer, rather than duplicated — the first
  duplication (curl only) was fine as one-off logic; a second identical
  consumer crossed into genuine shared domain logic worth not drifting apart.
  `CatCommand.cs` needed new `HackerOs.Simulation.Abstractions`/
  `HackerOs.Platform.Core` project references it didn't have before (it was
  the one command project with zero network capability). Tests added in
  `CoreCommandsTests.cs` (VFS-only fixture, updated constructor, a
  `NullSimulatedNetworkService` fake proving VFS args never touch the
  network) and `Wave4NetworkTests.cs` (known-host renders like curl,
  unknown-host disconnected/connected, a routing-guard test proving a bare
  filename never reaches the network fakes); live-verified all four cases
  in the browser, including confirming `cat example.hackeros` (no scheme)
  correctly stays a VFS lookup rather than being misread as a URL.

## Pass N+1a: status

All commands ADR 0023/0028 named as candidates for the real-network fallback
pattern are now wired: `curl -I`, `nmap` (single-port probe), `curl`
full-body, and `cat` (VFS + URL, simulated + real). What's explicitly still
out of scope, by design, not by omission: `nmap` range/multi-port scanning
(ADR 0035 Consequences) and real-network POST via `curl -d`.

## Pass N+5: Direct service injection for the server-hosted host

ADR 0027's other explicitly deferred item. Once `IAccountClient`/`IProxyClient`
(and the `ISyncClient` sibling this roadmap implies once Pass N+1 exists) are
proven over HTTP, give `Server/HackerOs.Server`'s own circuits a
direct-injection implementation of the same interfaces that calls
`IAccountService`/`ISyncService`/`IProxyService` in-process instead of over
HTTP — the interfaces were deliberately kept ASP.NET-free in ADR 0028 so this
doesn't require redesigning them. Needs its own ADR for the composition-root
wiring (how a request picks HTTP vs. direct implementation) before
implementation, per ADR 0027 Consequences.

## Pass N+6: Multi-tenant concurrent circuits

ADR 0027's other explicitly deferred item — converting the shared composition
root's per-process singletons to per-circuit scoped lifetimes so
`Server/HackerOs.Server` could serve multiple simultaneous browser users with
correct isolation. **Only revisit this if the product actually needs live
concurrent server-hosted UI sessions.** Per the direction set in ADR 0028,
browser-local IndexedDB remains the permanent source of truth and the
proxy/sync bridge model is the intended path for offline-reconnect and
multi-device use — this pass may never be needed. If it is, note (from prior
research) that a full server-side EF/SQLite reimplementation of the entire
IndexedDB-equivalent schema is *not* required for correctness — each Blazor
Server circuit already gets its own correctly-scoped `IJSRuntime` pointing at
that circuit's own connected browser's IndexedDB, so a lifetime-only
conversion (`AddSingleton` → `AddScoped` for the ~20 per-user-state services in
`AddHackerOsEcosystem`) is sufficient for isolation; EF-backed storage would
only be needed for a different, unrelated goal (server-persisted state
independent of any one browser).

## Open questions carried forward

- **Scope divergence recorded during ADR 0033 implementation**: AppCatalog
  sync's conflict handling reuses Settings' server-wins pattern rather than
  ADR 0025's suggested `ClientWins` for this domain — nothing could produce an
  AppCatalog conflict before ADR 0032 gave it a real writer, so there was no
  real usage to validate a bespoke policy against. Revisit once cross-device
  enablement conflicts are actually observed. Also: a pulled AppCatalog
  disable updates `AppEnablementRegistry` directly (so future launches are
  blocked) but does not stop an already-running instance of that app on this
  device mid-session, the same scope boot-time hydration already has.
- **Server-side gap found during ADR 0031 implementation**: `SyncService.PushAsync`
  blocks tombstones and enforces `ServerWins`-only conflict resolution for the
  `grants` domain, but has no semantic validation of a pushed Grants payload —
  nothing stops a payload claiming a wider capability/constraint than the
  account actually has. This client never pushes Grants (ADR 0031 Decision 1),
  so it can't exploit this, but the gap is still real for any client that
  calls `POST /api/sync/push` directly. Also: there is still no server-side
  grant-issuing/admin endpoint at all — the only way a Grants record could
  ever be created today is through the generic sync push path, and ADR 0025's
  own text presumes a dedicated "authorized grant API" exists. Both need a
  real design pass once an actual grant-issuing authority is built.
- **Found during ADR 0034 implementation**: `SmokeTestNetworkSeed` is
  intentionally minimal (two hosts, one page) — just enough to prove
  `curl`/`ping`/`nmap` launch and resolve `ISimulatedNetworkService` at all.
  It is not, and should not grow into, the "Game domain" simulated-internet
  content pack ADR 0023 scopes separately; that remains a from-scratch content
  effort with its own design pass, not an incremental extension of this seed.
- **Wiring gap found during ADR 0031 implementation**: `ICapabilityGrantRepository`
  (in-memory, what every runtime capability check reads) and
  `IPersistentCapabilityGrantRepository` (IndexedDB-backed, what sync pulls
  into) are completely disconnected — the in-memory one is rebuilt from
  manifest declarations at every login, and nothing reads the durable one
  back into it. A pulled/revoked grant is durable and visible across devices
  but does not affect what a running (or even freshly launched) app can
  actually do until `LocalSessionService`'s login seeding is changed to also
  hydrate from the durable store — a separate, larger change than the sync
  adapter itself, deferred with its own design needed first.
- ~~Server-side gap found during ADR 0028 implementation: `POST /api/proxy/http`
  only ever returns metadata, not the fetched body~~ **Resolved**: `ProxyHttpRequest.IncludeBody`
  / `ProxyHttpResponse.BodyBase64` close this — the simpler direct streaming
  response option this entry itself named, not the heavier chunked
  `ContentTransferContracts.cs`-style approach (unjustified for a bounded,
  one-shot fetch). Wired into full-body `curl`'s real-network fallback; see
  "Pass N+1a" above. `cat` still has no URL-reading capability at all to
  wire this into — a separate, larger gap, also noted above.
- ~~Does `ping`/`nmap`'s "real" mode need a TCP/UDP proxy call shape beyond
  `IProxyService.ExecuteHttpRequestAsync`'s HTTP-only contract?~~ **Resolved
  for `nmap`**: ADR 0035 added `IProxyService.ExecuteTcpProbeAsync`, a
  single-host/single-port TCP-connect contract with no range or multi-port
  shape — deliberately narrower than a general TCP/UDP proxy, after the user
  declined a bounded-scan alternative on abuse-surface grounds. `ping` never
  needed this — its real fallback already reuses the HTTP proxy (a HEAD
  request approximates reachability well enough). A true range/multi-port
  `nmap` scan capability, if ever wanted, still needs its own ADR and
  security design; ADR 0035 explicitly does not open a path to it.
- ~~Does real network access need its own capability ID...~~ **Resolved**:
  `AppCapabilities.NetworkRealAccess` (`network.real.access`) was added during
  ADR 0028 implementation and is declared by `ping`'s manifest.
