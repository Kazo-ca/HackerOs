# Gap analysis for a true WebAssembly ecosystem

**Status:** exploratory analysis, not implemented, no `D-xxx` decision
recorded, no code written.

## 1. Purpose

`integration-task-list.md` exhaustively documents the remaining work for the
phases already under way (1 to 6). This document covers something else:
gaps that appear **nowhere** in that plan, neither as an open task nor as a
deliberate exclusion, identified by asking what a developer or a user would
expect from a "real" WebAssembly ecosystem, beyond the mere technical
feasibility of loading apps.

Two points were judged structuring enough to receive an entry in the Problem
Register (`P-014`) and a reserved place in the existing phases; five others
received an entry in the Suggestion Register (`S-011` to `S-016`, section 40).
The rest remains at the discussion stage, deliberately, for lack of enough
material for an actionable plan item.

## 2. Two structuring gaps

### 2.1 Execution isolation between apps (not only against malicious code)

**Problem Register: `P-014`.**

`P-009` already covers the lack of isolation against *malicious code*:
"Runtime assemblies share one .NET process and are not malicious-code
isolated." This is a trust/security problem, resolved downstream by `D-020`.

The gap noted here is different: even an **honest but buggy** app (infinite
loop, heavy synchronous computation) currently blocks **the entire OS**, not
just itself. All apps run on the same main WASM thread of the browser: there
is no Web Worker per app, no watchdog, no timeout, and no recovery mechanism.
This is a *resilience* problem, not a *malice* problem: even a future
trust/signing model (`D-019`) would change nothing, since it only protects
against intent, not against a bug.

The new `docs/user-code-compilation-execution-plan.md` already touches on the
subject, but only for the special case of user code compiled on the fly
("dedicated Worker without JS-interop bindings... communication limited to a
typed `postMessage` channel"). Nothing exists for normal apps, first-party or
third-party, which all run in-process today.

### 2.2 Package distribution and discovery

**Suggestion Register: `S-011`.**

Phase 6 (`integration-task-list.md` section 33) fully answers the question
"can a package be installed at runtime?", never "where do you find it?". No
registry, catalog, search, or even the concept of a "store" for third-party
apps exists in the plan. Without that, a technically successful Phase 6 would
yield an installation mechanism with nothing to install: not an ecosystem.

## 3. Five additional gaps (Suggestion Register)

| Gap | ID | Dedicated location |
|---|---|---|
| Cache/sharing of dependencies between independently loaded apps | `S-012` | Phase 3: Build-Known Lazy Loading |
| On-demand permissions (instead of declaring everything in the manifest and granting at login) | `S-013` | Phase 1 extension: Policy/Capability Grants |
| External deep-linking (`web+hackeros://` protocol handler) | `S-014` | Phase 2: PWA Packaging |
| Independent update of an installed app (vs atomic global PWA update) | `S-015` | Phase 6: Package Format |
| Local development loop (run/hot-reload an app in HackerOS without a full rebuild) | `S-016` | Phase 3: Public SDK |

Details on each:

- **Dependency cache/sharing (`S-012`)**: each *build-known lazy* app
  (`docs/lazy-loading.md`) seems to bring its own assemblies independently;
  nothing says whether two apps referencing the same shared library
  deduplicate at download time or in memory. As more apps are lazy-loaded,
  this redundant cost grows.
- **On-demand permissions (`S-013`)**: the current model
  (`CleanProfileCapabilityGrantSeeder`) grants all capabilities declared in
  the manifest at login. There is no capability request *at the moment of
  need* (such as "allow webcam access now"), unlike most modern ecosystems
  (browser, mobile).
- **External deep-linking (`S-014`)**: no protocol handler is registered to
  open a HackerOS app/intent from a link located outside the PWA itself.
- **Independent app update (`S-015`)**: the current update model (`P2-PWA-*`)
  is that of the whole PWA, atomic, all or nothing. Once Phase 6 is in place,
  nothing provides for versioning/updating a third-party app independently
  of the rest.
- **Local development loop (`S-016`)**: the SDK has a manifest validator
  (`Tools/HackerOs.Tools.ManifestValidator`) and `dotnet new` templates, but
  nothing to run/test one's app *inside* HackerOS locally with hot-reload,
  without going through the full build of `HackerOs.Ecosystem`.

## 4. Gaps noted but not promoted (discussion only)

These points remain at the discussion stage: too early for an actionable plan
item, for lack of a product decision or enough technical material to write a
concrete scope. They are noted here so as not to lose them.

- **Non-.NET WASM interop**: the whole ecosystem is locked to Blazor/Mono.
  There is no mechanism to load a WASM module written in another language
  (Rust, C, AssemblyScript; no WASI, no component model). If "true
  WebAssembly ecosystem" implies interoperating with the WASM world at large
  rather than just ".NET compiled to wasm", this is a conceptual gap more than
  a task to plan.
- **Inter-app communication beyond typed intents**: intents cover launching,
  opening a file, and running a command, but there is no pub/sub bus, no
  generic "share sheet", and no real-time data exchange between two apps open
  at the same time.
- **Telemetry/crash reporting for third-party apps**: the existing
  diagnostics/logging serves the platform itself; nothing is exposed as an SDK
  for a third-party app to opt into.
- **Community trust signals**: `D-019` covers the *technical* signing/trust
  store, but nothing on the social layer (ratings, reviews, publisher
  verification, reputation) that a user ecosystem generally has.
- **Global search ("spotlight")**: no unified search across apps, files, and
  commands.
- **Desktop widgets / mini-surfaces**: nothing beyond the full windows managed
  by `HackerOs.Windowing.Core`.
- **Monetization/licensing**: absent everywhere; relevant only if the
  ambition includes paid third-party developers.

## 5. Reference documents

- [`integration-task-list.md`](integration-task-list.md): sections 33 (Phase
  6), 39 (Problem Register, `P-014`), 40 (Suggestion Register, `S-011` to
  `S-016`)
- [`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md):
  Web Worker isolation for compiled user code, an angle close to section 2.1
- [`lazy-loading.md`](lazy-loading.md): current deferred-loading mechanism,
  basis of the `S-012` gap
- [`server-implementation-pass.md`](server-implementation-pass.md):
  proxy/fallback pattern reusable for a possible server path for package
  distribution
- [`progress-and-plan-2026-08-17.md`](progress-and-plan-2026-08-17.md):
  overall sequencing in which to place these gaps once prioritized
