# ADR 0015: Browser Support Baseline and IndexedDB Adapter Approach

## Status

Accepted on 2026-08-02.

## Context

Phase 2A must persist users/sessions, canonical settings, the virtual
filesystem (metadata plus binary content), capability grants, the app catalog,
and audit/diagnostic records in the browser so HackerOS survives a page reload
without a server. `wasm2/HackerOs/docs/integration-task-list.md` section 10
(`P2-IDB-*`) depends on this decision (`D-008`) before any repository can be
implemented against real browser storage.

Two sub-decisions are required before that work starts:

1. Which browsers/versions HackerOS treats as supported, since that bounds
   which IndexedDB/Storage APIs may be relied upon.
2. How C# talks to IndexedDB: a hand-written minimal JS interop module, or an
   existing third-party IndexedDB wrapper package.

This ADR does not define the database name, object stores, indexes, key
design, chunking, or migration steps — those remain `P2-IDB-002` through
`P2-IDB-011`, scoped to `Infrastructure/HackerOs.Infrastructure.Browser/`.

## Decision — supported browsers

HackerOS v3 supports the current and immediately preceding stable release of:

- Chromium-based browsers (Chrome, Edge) — minimum Chromium 89.
- Firefox — minimum 90.
- Safari (desktop and iOS) — minimum 15.

This is a floor, not a target: it is chosen because it is the oldest version
line across these engines that has IndexedDB v2 (`getAll`, `getAllKeys`,
cursor `direction`, binary/array keys) and `navigator.storage.estimate()` /
`navigator.storage.persist()`, both required by `P2-IDB-010`. It is also no
older than the baseline already implied by targeting .NET 10 Blazor
WebAssembly, which requires a modern WebAssembly-capable, ES6-module-capable
browser. No non-evergreen browser (legacy Edge, IE11) is supported.

Browsers older than this floor are an unsupported-browser condition, not a
storage failure mode; `P2-IDB-012` (recovery UX, `D-010`) covers detection and
messaging, not a compatibility shim or polyfill.

Real-browser verification (`P2-IDB-013`/`P2-IDB-014`) is Chromium-only via the
existing Playwright tooling until Firefox/Safari automation is separately
justified; this gap is recorded in the Problem Register rather than silently
assumed to be equivalent.

## Decision — IndexedDB adapter approach

### Options considered

**Hand-written minimal collocated JS module(s), invoked through batched
`IJSRuntime` calls.** Mirrors the pattern already accepted in ADR 0009 for
`WindowChrome.razor.js`: JavaScript is a thin, reviewable primitive layer
(open database, run one transaction, read/write records, report quota); C#
remains the schema, validation, and migration owner. Interop calls are
batched per transaction (e.g. "write these N records and return the new
revision") rather than one call per field, per the cited Blazor JS interop
performance guidance.

**Third-party Blazor IndexedDB NuGet package** (e.g. community IndexedDB
wrapper libraries). Would reduce initial interop code, but every such package
inspected either exposes a generic untyped `object`/dictionary CRUD surface
that conflicts with `P2-IDB-005`'s repository contracts, or has no published
evidence of .NET 10 Release trimming compatibility. Adopting one would also
make it far harder to enforce that app code never receives raw IndexedDB
access (a `Shared`/`Platform` project boundary rule, not just a convention) — a
generic wrapper's public API is designed to be called from anywhere, not
gated behind `HackerOs.Simulation.Abstractions` repository interfaces.

**Emerging in-BCL browser storage API.** No such API exists in the .NET 10
runtime for browser-hosted WebAssembly; `System.IO` in `browser-wasm` does not
map to IndexedDB. Rejected as unavailable.

### Decision

Implement one or more small, collocated, static JS modules under
`Infrastructure/HackerOs.Infrastructure.Browser/wwwroot/` that wrap the native
IndexedDB API with transaction-oriented, batched primitives:
open/upgrade database, run a read/write transaction against named object
stores, and report `StorageManager` estimate/persist results. These modules
contain no HackerOS domain logic (no filesystem semantics, no settings
validation, no policy) — they accept and return plain JSON-serializable
shapes matching C#-defined DTOs.

C# repositories in `Infrastructure/HackerOs.Infrastructure.Browser/` are the
only callers of these modules and are the only place `IJSRuntime` for storage
is injected. They implement the existing `Platform.Core`/`Simulation.Abstractions`
repository contracts (`P2-IDB-005`, `P2-IDB-007`, `P2-IDB-009`) so callers
above this layer never see IndexedDB, transactions, or `IJSRuntime`.

Persistence repository operations are asynchronous and cancellation-aware.
`ValueTask` is used for repository contracts so in-memory implementations can
complete synchronously while browser implementations await IndexedDB transaction
completion. Lookup methods return a nullable record for absence; mutations do
not report success until the browser transaction commits. Sync-over-async and
unacknowledged write-behind are prohibited. This amendment resolves `P-007` and
was accepted on 2026-08-02 before the public SDK freeze.

Schema version numbering, migration ordering, and upgrade steps are owned and
sequenced by C# (`P2-IDB-006`), not embedded as business logic inside a JS
`onupgradeneeded` handler; the JS module's upgrade primitive only creates/
deletes object stores and indexes as instructed per call.

## Consequences

- Storage adapter code stays small, reviewable, and consistent with the JS
  isolation precedent already set for window chrome interop (ADR 0009).
- No new NuGet dependency is introduced for storage; trimming/Release risk is
  bounded to code this project owns and tests directly.
- App code and even most Platform Core code cannot reach IndexedDB directly —
  only `Infrastructure.Browser` repositories may.
- Firefox/Safari real-browser coverage is deferred and tracked as a known gap,
  not assumed passing from Chromium results.
- Any future third-party storage package requires a superseding ADR and the
  same trimming/scope-leak evidence this ADR found lacking.
- Existing in-memory repositories may retain synchronous convenience methods,
  but platform consumers use only the asynchronous repository interfaces.

## References

- ADR 0009: Purpose-Built Window Runtime (JS isolation precedent)
- `wasm2/HackerOs/docs/integration-task-list.md` section 10
- [MDN IndexedDB](https://developer.mozilla.org/docs/Web/API/IndexedDB_API)
- [MDN StorageManager estimate](https://developer.mozilla.org/docs/Web/API/StorageManager/estimate)
- [MDN persistent storage](https://developer.mozilla.org/docs/Web/API/StorageManager/persist)
- [Blazor JS interop performance](https://learn.microsoft.com/aspnet/core/blazor/performance/javascript-interoperability?view=aspnetcore-10.0)
