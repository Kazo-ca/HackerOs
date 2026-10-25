# Historical BlazorWindowManager lessons

- The prior `wasm2/HackerOs/Ecosystem/HackerOs.Ecosystem` implementation is not present on the `wasm-total-conversion` branch as of 2026-08-01. Do not treat the old project paths or run commands as current.
- The clean v3 solution is `wasm2/HackerOs/HackerOs.sln`, targets .NET 10, and as of 2026-08-02 contains app/simulation abstractions, headless and Blazor App SDKs, Platform Core (catalog, canonical settings/policy projection, virtual filesystem, Section 7 session/process/clock/event/diagnostics/resource-simulation, Section 7.1 app execution context + scoped gateways, Section 8 app entry-point discovery/lifecycle orchestration/capability-gated intent dispatch/file-association resolution — see `docs/app-intents-and-associations.md` — and the Section 9 canonical manifest JSON Schema/build profile — see `docs/build-profile.md`), 344 passing tests across 4 test projects (`dotnet test HackerOs.sln --no-restore` runs all of them; `HackerOs.Platform.Core.Tests` alone reports 255, `HackerOs.App.Abstractions.Tests` reports 75). **The Phase 1 exit gate (`P1-GATE-001` through `P1-GATE-005`) is fully complete** — see the dedicated section below before touching Phase 2 tasks. The exhaustive remaining execution plan is `wasm2/HackerOs/docs/integration-task-list.md` and must be maintained during implementation.
- `WindowAppBase` now seals Blazor lifecycle overrides and exposes `OnApp*` hooks, preventing the historical skipped-base-call bug. The platform window runtime and drag/resize interop are not implemented yet.

## ROOT CAUSE of "drag/resize don't work" bug (found via Playwright runtime debugging, 2026-07-01)
`WindowBase.razor.Lifecycle.cs`'s `OnAfterRenderAsync(firstRender)` loads `_jsModule` (JS interop for
drag/resize) and calls `UpdateWindowDisplay()`/keyboard nav registration. Any subclass of `WindowBase`
(e.g. `WindowAppBase`) that overrides `OnAfterRenderAsync` WITHOUT calling `await base.OnAfterRenderAsync(firstRender)`
silently skips all of that setup — `_jsModule` stays null forever, so `OnTitleBarMouseDown`/`StartResize`'s
guard `if (_jsModule != null && ...)` never invokes JS, so dragging/resizing silently no-ops with NO console
errors and NO exceptions. This was very hard to detect because:
  - Compilation succeeds fine (nothing checks the missing base call).
  - Button clicks (@onclick for minimize/maximize/close) still work since they don't depend on _jsModule.
  - The JS module file itself loads fine via plain fetch()/import() when tested directly — the bug is
    purely "the C# code path that imports it is skipped", not a missing/404 file.
  - Diagnosis required adding temporary `JSRuntime.InvokeVoidAsync("console.log", ...)` instrumentation
    directly in `OnAfterRenderAsync` and `OnTitleBarMouseDown` to prove the import never ran.
Fixed by adding `await base.OnAfterRenderAsync(firstRender);` at the end of the override in:
  - `HackerOs.Ecosystem/Modules/WelcomeApp.razor.cs`
  - `HackerOs.Ecosystem/Modules/SystemMonitorApp.razor.cs`
**Lesson**: Any new `WindowAppBase`/`WindowBase`-derived component that overrides `OnAfterRenderAsync`,
`OnInitializedAsync`, etc. MUST call the base method, or window chrome features (drag, resize, keyboard nav)
silently break with zero errors. Worth grepping for `override.*OnAfterRenderAsync` in new app modules and
verifying `base.OnAfterRenderAsync` is called.

## Playwright MCP tool families (do not mix)
- Family A: `mcp_playwright_browser_navigate/snapshot/click/take_screenshot` — implicit current page, `target` is bare ref like `e10`.
- Family B: `open_browser_page` (returns GUID pageId) + `run_playwright_code` (raw Playwright JS against `page`) — needed for real mouse gesture simulation (`page.mouse.move/down/up`), `page.evaluate`, console/network listeners.
Mixing pageIds between them causes "Page not found". Stick to one family per session; Family B is required for drag/resize testing.

## Project reference direction (checked 2026-08-01)
`HackerOs.App.Abstractions` has ZERO ProjectReferences (base project). `HackerOs.Simulation.Abstractions`
references `App.Abstractions`. `HackerOs.Platform.Core` references both. Rule: any new type that needs both
an App.Abstractions concept (e.g. `AppOperationContext`, `AppCapabilities`, `AppAuthority`) AND a
Simulation.Abstractions concept (e.g. `SettingsScope`) MUST live in `Simulation.Abstractions` or higher —
never in `App.Abstractions`. Before creating cross-cutting policy/settings code, check
`wasm2/HackerOs/docs/implementation-status.md`'s dependency diagram or the csproj files first to avoid
creating a file in the wrong project and having to delete/relocate it.

## `.config` settings document `schemaVersion` gotcha
`ConfigDocumentFormat.Serialize()` (Shared/HackerOs.Simulation.Abstractions/Settings/ConfigDocumentFormat.cs)
always writes `schemaVersion=N` as the first line of every document, but no `SettingsSchema` declares
`schemaVersion` as a field. `SettingsSchema.Validate()` MUST special-case and skip the literal key
`"schemaVersion"` in its unknown-key check, otherwise every schema-validated document (including at
`InMemorySettingsDocumentService` construction time) throws `settings.unknown-key:schemaVersion`.

## Record validation syntax — do NOT use `public RecordName { ... }` body on a positional record
There is no C# feature that lets you write `public sealed record Foo(int X) { public Foo { /* validate X */ } }`
— that `public Foo { ... }` block is NOT valid primary-constructor-validation syntax (I misremembered this).
It produces a cascade of ~80 confusing CS1001/CS1014/CS1513/CS8124 errors pointing at unrelated later lines.
The actual codebase convention (see `FileSystemEntryMetadata.cs`'s `FileSystemTimestamps`) is: declare the
record WITHOUT a positional parameter list, write an explicit constructor with lowercase parameter names that
validates and assigns, and declare each property as a manual `public T X { get; }`. Follow this pattern for
every validated record in `Shared/HackerOs.Simulation.Abstractions/` and similar contract projects
(e.g. `LocalUser`, `AuthenticatedPrincipal`, `LocalPasswordCredential` in `Sessions/`).

## Section 7 (Session/Process/Clock/Events/Diagnostics) — completed 2026-08-01
All of `P1-SYS-001` through `P1-SYS-011` in `docs/integration-task-list.md` are implemented and tested
(95 new tests; 277 total solution tests, 0 failures, warnings as errors). Key implementation notes:

- `CancellationTokenSource.Dispose()` does NOT call `Cancel()` first — disposing an un-cancelled token
  source leaves `IsCancellationRequested` false forever. Any process/session teardown path that needs
  observers to see cancellation MUST call `.Cancel()` explicitly before `.Dispose()`. This bug caused
  `InMemoryProcessManager`'s `Kill`/`Complete`/`Fault` to fail a test until fixed (`Finish()` helper in
  `Platform/HackerOs.Platform.Core/Processes/InMemoryProcessManager.cs`).
- `SeededSimulationRandom.GetStream(domainKey)` creates a BRAND NEW `Random` instance every call — it does
  NOT cache/remember previous state per key. Calling it repeatedly with the same key always replays the
  same first value(s). To get a progressing/evolving deterministic sequence across simulation ticks (e.g.
  per-process jitter in `DeterministicResourceSimulator`), the CALLER must cache the returned
  `ISimulationRandomStream` itself (e.g. in a `Dictionary<ProcessId, ISimulationRandomStream>`) and reuse it
  across ticks, rather than calling `GetStream` again each tick.
- Test fixtures needing a full session (`LocalSessionService`) require the same
  `InMemoryFileSystemRepository` + `FileSystemMountRouter` + `FileSystemPathResolver` +
  `FileSystemAuthorizer` + `FileSystemSeeder` wiring used in `FileSystemSeederTests.cs` — copy that pattern
  rather than re-deriving it (session login seeds `/home/{loginName}` via the seeder).
- Cross-cutting tests combining session + process manager + event bus + audit log + resource simulator live
  in `Tests/HackerOs.Platform.Core.Tests/Processes/CrossCuttingLifecycleTests.cs` — good template for future
  end-to-end Section-spanning test files.

## Section 7.1 (App Execution Context and Scoped Gateways) — completed 2026-08-01
All of `P1-EXEC-001` through `P1-EXEC-008` implemented and tested (16 new tests; 293 total solution
tests). See `wasm2/HackerOs/docs/app-execution-context.md`. Key gotchas found:

- **Records with get-only properties (the standard validated-record pattern here) cannot use `with`
  expressions** — confirmed again this section on `FileSystemSelectedResourceHandle`. Always reconstruct
  via the public constructor when a mutated copy is needed (see `RevokeLocked` helper in
  `FileSystemSelectedResourceHandleRegistry.cs`).
- **Two independent capability-check systems coexist and are easy to conflate.** Gateways built on
  `ICapabilityChecker` (`AppNotificationGateway`, `AppProcessGateway`, and `context.Capabilities.Evaluate`
  directly) delegate to the LIVE `ICapabilityGrantRepository.Evaluate(...)` — a capability must have been
  explicitly `Grant`-ed there, full stop. The `IAppFileSystemGateway`, by contrast, only checks the plain
  `AppOperationContext.GrantedCapabilities` set baked in at context-construction time (via
  `AppExecutionContextFactory.Create`'s `grantedCapabilities` parameter), because `IFileSystemService`
  enforces path-scoped capability policy internally. **A test (or real caller) that passes capabilities only
  to the factory's `grantedCapabilities` param will still get `Missing` denials from the
  notification/process gateways** unless it ALSO calls `ICapabilityGrantRepository.Grant(...)` for the same
  capability. When wiring a test fixture, do both in one helper.
- **`FileSystemSelectedResourceHandleRegistry.Issue` bug (fixed):** it passed
  `ICapabilityGrantRepository.CurrentPolicyRevision` straight into the handle constructor, which throws
  `ArgumentOutOfRangeException` because `CurrentPolicyRevision` starts at `0` and the handle ctor requires
  `policyRevision >= 1`. Fixed by clamping to `Math.Max(_grantRepository.CurrentPolicyRevision, 1)`,
  mirroring the clamp already used by `CapabilityGrantRepository.Evaluate`'s `DenyMissing` path. Any new
  code reading `CurrentPolicyRevision` for a downstream validated value >= 1 needs the same clamp.
- **`CapabilityGrantRepository.Grant(...)` requires the ACTING authority to already satisfy
  `AppAuthority.Administrator`** (`AppAuthorityPolicy.Satisfies(actingAuthority, AppAuthority.Administrator)`)
  or it returns `AuthorityDenied` instead of actually granting — pass `AppAuthority.Administrator` (or
  `System`) as the acting authority in test seeding, not `AppAuthority.User`.
- **`ManualSimulationClock.DelayAsync` never completes unless `Advance(tickCount)` is called explicitly** —
  it schedules a callback for a future tick and nothing fires it automatically. Any test that calls an
  `async` process-manager method depending on `DelayAsync` (e.g. `IProcessManager.StopAsync`, which awaits
  `Task.WhenAny(stopSignal, clock.DelayAsync(timeout))`) will hang forever unless the clock is advanced or
  the process independently completes the stop signal. Prefer the synchronous `Kill(pid)` over
  `StopAsync(pid, timeout)` in tests unless you actually want to exercise the timeout/graceful-stop path.
- **FileSystemSeeder-created home-directory ownership uses the exact string passed as its `userId`
  parameter, not any Guid identity** — `LocalSessionService.LoginAsync` auto-seeds via
  `_homeSeeder.SeedAsync(user.LoginName.Value, user.PrimaryGroupId.ToString())`, so the real seeded
  `/home/{loginName}` directory's owner is the LOGIN NAME string ("alice"), while
  `AppExecutionContextFactory.Create` builds `AppOperationContext.UserId` from `principal.UserId.ToString()`
  (a GUID). These two do NOT match. A test exercising the FS gateway against a real home directory must
  seed its OWN directory using the same `userId` string the gateway will present (e.g.
  `Seeder.SeedAsync(principal.UserId.ToString(), "users")`) rather than relying on the
  session's auto-seeded `/home/{loginName}` path.

## `SettingsDocumentDefinition` read vs write authority — pick read authority by "who needs to resolve this", not by symmetry with write authority
When defining a new settings document (`SettingsDocumentDefinition(path, defaultContent, mediaType, readCapability,
writeCapability, minimumReadAuthority, minimumWriteAuthority, validator)`), do NOT default
`minimumReadAuthority` to the same value as `minimumWriteAuthority` just because it "looks locked down".
`InMemorySettingsDocumentService.ReadAsync` returns `Denied` (not an exception) when the caller's
`AppOperationContext.UserAuthority` is below `minimumReadAuthority`, and callers like
`FileAssociationResolver` silently treat a denied/failed read as "no configured default exists" and fall
through to candidate-based resolution — so this bug manifests as a wrong RESULT (e.g.
`ChooserRequired` instead of `ConfiguredDefault`), never a thrown exception or explicit auth error,
making it easy to misdiagnose as a resolver logic bug. Rule of thumb: routine, read-only, ordinary-user
operations that any app legitimately performs as part of everyday use (e.g. resolving file-type
associations to open a file) should have `minimumReadAuthority = AppAuthority.User`; only the WRITE side
needs `AppAuthority.Administrator` to prevent unprivileged changes. Reserve
`minimumReadAuthority = Administrator` for genuinely sensitive documents (e.g. `PolicySettingsDocuments`
security policy) that ordinary user-context code should never even be able to read.

## Section 9 manifest JSON Schema (`P1-BLD-001`/`P1-BLD-003`) — completed 2025-06
`AppManifest` expanded with Presentation, OsCompatibility, Localizations, Resources, Settings,
Intents, Assets, Update, AutoStart (7 new files in `Shared/HackerOs.App.Abstractions/`). Draft
2020-12 schema at `Shared/HackerOs.App.Abstractions/Schema/manifest.schema.v1.json`, embedded as a
resource (`ManifestSchemaResource.LoadCurrentSchemaJson()`), fixtures in `Schema/Fixtures/`. 334
total solution tests (up from 322). See `docs/build-profile.md`. Key gotchas:

- **Adding new `required` members to a widely-constructed record has a huge blast radius.** `AppManifest`
  is hand-constructed via object initializers in ~12 different test factory methods across ALL 4 test
  projects (no shared test-manifest builder exists). Adding `Presentation`/`Resources` as `required` broke
  every one of them with CS9035. Grep exhaustively with MULTIPLE patterns (`Kind = AppKind`, `new AppManifest`,
  `AppManifest manifest = new()`) before assuming all call sites are found, then do a full solution
  `dotnet build --no-restore` to catch stragglers — two files (`WindowAppBaseTests.cs`,
  `AppExecutionContextTests.cs`) were missed on the first grep pass.
- **App.Abstractions cannot reference Simulation.Abstractions**, so manifest-level settings
  value-type/scope/sensitivity enums and the resource-profile weights had to be independently redeclared
  in `App.Abstractions` (mirroring but not reusing the Simulation.Abstractions equivalents by name). Trusted
  Platform Core mapping code between the two will be needed later but is out of scope for the manifest model.
- **Validation-heavy manifest records intentionally do NOT throw in their constructors** (unlike runtime
  `ResourceProfile`) — `AppResourceProfileManifest` etc. are deliberately unvalidated at construction so
  `AppManifestValidator.Validate()` can return a complete list of errors instead of throwing on the first bad
  field. Follow this pattern for any new manifest-level record.
- **JSON Schema (structural) and `AppManifestValidator` (semantic) are deliberately split and BOTH tested.**
  Anything expressible declaratively (shape, enum membership, `additionalProperties: false`, app-kind
  conditionals via `allOf`/`if`/`then`) goes in the schema; cross-field business rules (e.g. "icon path must
  reference a declared asset") stay in the C# validator. `JsonSchema.Net` (NuGet) is the library used, scoped
  only to `HackerOs.App.Abstractions.Tests` — do not add JSON-schema packages to shipping projects.
- **sha256 placeholder fixture strings are easy to miscount by hand** — always verify hex-string fixture
  lengths programmatically (e.g. `$json.field.Length` in PowerShell) rather than eyeballing repeated `0`
  characters; a 62-vs-64-char mistake silently fails schema pattern validation with a confusing message.
- **Test fixtures are read directly from source (not copied to output)** — `ManifestSchemaConformanceTests`
  walks up from `AppContext.BaseDirectory` to find `HackerOs.sln`, then combines with the known relative path
  to `Schema/Fixtures`. This avoids any MSBuild content-copy wiring; reuse this pattern for any future
  test needing fixture files that live outside the test project.

## Phase 1 exit gate (`P1-GATE-001` through `P1-GATE-005`) — completed 2026-08-02
All 5 gate tasks in `docs/integration-task-list.md` are `[x]`. `P1-CAP-001` remains legitimately
`[ ]`/deferred (no Phase 2 app manifests exist yet to audit) — this is NOT a gate blocker, don't
try to force it complete before Phase 2 starts. Key reusable notes for future gate-style work:

- **Enabling the trim analyzer on a pure-library solution (no host/publish target yet) is valid and
  useful.** Add to `Directory.Build.props`, scoped to non-test projects via
  `Condition="!$(MSBuildProjectName.EndsWith('.Tests'))"`:
  ```xml
  <PropertyGroup Condition="!$(MSBuildProjectName.EndsWith('.Tests'))">
    <IsTrimmable>true</IsTrimmable>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
  </PropertyGroup>
  ```
  Combined with the solution-wide `TreatWarningsAsErrors=true`, this turns IL2026/IL2072/etc into
  Release build errors immediately (`dotnet build HackerOs.sln -c Release --no-incremental`) — no
  need to wait for an actual WASM publish step to catch trim-unsafe reflection.
- **How the 3 real warnings this surfaced were fixed (not suppressed-and-forgotten):**
  `AppEntryPointDiscovery.Discover` (calls `Assembly.GetType(string, bool)` by manifest-declared
  name) got `[RequiresUnreferencedCode("...")]` on the method itself — this is honest: the method
  IS fundamentally reflection-based by design (explicit host-assembly-list discovery, never
  `AppDomain` scanning), so the requirement is declared and propagates to callers instead of being
  hidden. The two `Activator.CreateInstance(descriptor.EntryPointType, ...)` call sites in
  `AppLifecycleOrchestrator` (`RunTerminalAsync`, `StartService`) got
  `[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "...")]` with a justification
  referencing that `EntryPointType` was already verified against the host allowlist by
  `AppEntryPointDiscovery` — plus a note that a future Phase 2/6 host-publish step must add matching
  trim root descriptors. Test projects are excluded from the analyzer, so test code calling
  `Discover` does not need to itself carry `[RequiresUnreferencedCode]`.
- **Reusable end-to-end integration test template:** see
  `Tests/HackerOs.Platform.Core.Tests/HeadlessKernelIntegrationTests.cs` for a template covering
  boot → login → home seed → capability grant → discovery → launch (Terminal + Service) → real FS
  create/write/read through scoped gateways → settings read → `StopAllAsync(Shutdown)` → `DisableAsync`
  → `LogoutAsync`, asserting audit log entries. **Important ordering lesson:** call
  `Orchestrator.StopAllAsync(ProcessExitReason.Shutdown)` BEFORE `Session.LogoutAsync()` in tests —
  explicit stop is deterministic and immediately observable, whereas session logout's token-
  cancellation cascade racing with cooperative service shutdown is not guaranteed to have completed
  by the time a synchronous assertion runs right after `LogoutAsync()` returns.

## `P2-IDB-002` complete (2026-08-02) — new `HackerOs.Infrastructure.Browser` project
Created `Infrastructure/HackerOs.Infrastructure.Browser/` (added to `HackerOs.sln`
via `dotnet sln add`, no ProjectReferences yet — schema is self-contained) plus
`Tests/HackerOs.Infrastructure.Browser.Tests/`. Schema lives in
`Schema/IndexedDbSchemaModel.cs` (validated `IndexedDbIndexDefinition`/
`IndexedDbObjectStoreDefinition`/`IndexedDbTransactionBoundary` records, same
explicit-constructor-with-validation pattern as `FileSystemEntryMetadata`) and
`Schema/HackerOsIndexedDbSchema.cs` (static class: `DatabaseName="hackeros"`,
`CurrentVersion=1`, 11 stores, 10 transaction boundaries). Solution is now 357
tests (was 344; +13). Key facts for the next `P2-IDB-*` session:
- Store name constants are on `HackerOsIndexedDbSchema` (e.g. `UserStoreName`,
  `GrantStoreName`) — reference those constants, don't re-type string literals.
- `fsContent`'s key path (currently just `["entryId"]`) is EXPLICITLY provisional
  pending `P2-IDB-003`/`D-009` (chunking/hashing/dedup) — expect to revise it,
  not treat it as frozen.
- IndexedDB index keys cannot be a raw boolean (spec only allows
  number/string/Date/binary/Array-of-those) — `catalog.enabledFlag` is stored as
  a 0/1 int for this reason; apply the same pattern to any future boolean-like
  index.
- `audit`/`diagnostics` use `autoIncrement: true` with a single-segment `id`
  keyPath (`IndexedDbObjectStoreDefinition`'s ctor throws if `autoIncrement` is
  combined with a compound keyPath) and store timestamps as
  `timestampUtcMs` (epoch-ms number, not `Date`) specifically for
  deterministic cross-browser index ordering.
- `docs/browser-storage.md` has the full store/index/transaction-boundary table
  — read that instead of re-deriving the schema from code comments.

## Phase 2A started — `P2-IDB-001` complete (2026-08-02)
ADR 0015 (`docs/adr/0015-browser-storage-and-indexeddb-adapter.md`) resolves D-008:
supported browsers = Chromium 89+/Firefox 90+/Safari 15+ (evergreen only); IndexedDB
adapter = hand-written minimal collocated JS module(s) under
`Infrastructure/HackerOs.Infrastructure.Browser/wwwroot/` with batched
transaction-oriented primitives (mirrors the ADR 0009 `WindowChrome.razor.js`
isolation precedent), NOT a third-party IndexedDB NuGet package; C# owns schema
version/migration ordering. Dedicated doc: `docs/browser-storage.md`. Solution
test count unchanged at 344 (this was docs/ADR-only, no code). **Important
process note confirmed this session:** tasks tagged `**DECISION: D-xxx**` in
`integration-task-list.md` that are still `[ ]` are NOT literally blocked
waiting on the user — they ARE the task that produces the decision, resolved by
the agent writing the ADR itself (same pattern as the already-`[x]` P1-ADR-001
through P1-ADR-007). Only phase-GATE approval (section 0.4) requires explicit
user sign-off, not each individual ADR. Next open Phase 2A decision task in file
order is `P2-UI-001` (MudBlazor version/usage boundary, ADR 0016, `D-013`) in
section 10.1, then `P2-IDB-002` (needs the ADR 0015 decision just made).
`Infrastructure/HackerOs.Infrastructure.Browser/` project does not exist yet —
create it as part of `P2-IDB-002` onward, not before.

## `AppEnablementRegistry` mutators (`MarkDisabled`/`MarkEnabled`) are `public`, not `internal`
No `InternalsVisibleTo` is declared between `HackerOs.Platform.Core` and its Tests project, so any
`internal` member on a concrete class (as opposed to on `IAppEnablementRegistry`, the public read-only
interface) is invisible to test fixtures in a separate project. `AppEnablementRegistry.MarkDisabled`/
`MarkEnabled` were deliberately made `public` on the concrete class (while `IAppEnablementRegistry` itself
stays read-only) specifically so lighter test fixtures (e.g. `FileAssociationResolverTests`) can flip
enablement state directly without needing a full `AppLifecycleOrchestrator`. Follow this pattern
(public mutator on concrete class, interface stays read-only) instead of adding `InternalsVisibleTo`.
