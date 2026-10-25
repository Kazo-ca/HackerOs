# ADR 0032: App Enablement Management

## Status

Accepted on 2026-08-15.

## Context

While planning "Pass N+4: Sync — AppCatalog + FileAssociations domains" (the next item in `docs/server-implementation-pass.md`), the user rejected the initial plan with a direct correction: *"For the app, we currently don't have a way to disable an app, so check how the user will be able to disable an app."*

Research confirmed this precisely. `IPersistentAppCatalogRepository.SetEnabledAsync` (`Shared/HackerOs.App.Abstractions/AppCatalogRepositoryContracts.cs`, IndexedDB-backed durable store) and `AppLifecycleOrchestrator.DisableAsync`/`Enable` (`Platform/HackerOs.Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs`, the in-memory enforcement mechanism — computes a dependency-disable closure, stops running processes for it, and actually blocks future launches via `AppEnablementRegistry.IsEnabled`) **both already existed and worked**, but had **zero production callers** — only tests exercised them (`P1-APP-010`/`P1-APP-011`). Even if something had called `DisableAsync`, it never persisted through `IPersistentAppCatalogRepository`, so a disable would not survive a page reload. There was no Settings UI, no button — nothing a user could actually click.

Building AppCatalog *sync* on top of this would have synced a feature nobody could use. This ADR makes app enablement a real, working, persisted feature first; ADR 0033 builds sync on top of it.

## Decision

### 1. `AppLifecycleOrchestrator` persists enablement changes when a durable repository is supplied

A new, optional, trailing constructor parameter — `IPersistentAppCatalogRepository? catalogRepository = null` — matching the existing `eventBus`/`descriptorLoader` optional-parameter convention, so no existing call site (production or test) breaks by omission.

- `DisableAsync` calls `SetEnabledAsync(appId, false)` for every app in the computed disable closure, after `AppEnablementRegistry.MarkDisabled` succeeds.
- `Enable` is renamed to **`EnableAsync`** (now returns `Task<AppEnableResult>` — it needed to become asynchronous to persist). It had exactly one production call site (none — zero callers) and one test call site, both updated. It calls `SetEnabledAsync(appId, true)` after `MarkEnabled` succeeds.

When `catalogRepository` is omitted (`null`), both methods behave exactly as before — in-memory only, no persistence — so contexts that don't need durability (most existing tests) are unaffected.

### 2. Boot-time hydration closes the enforcement wiring gap

`EcosystemBootCoordinator` gains a dependency on the concrete `AppEnablementRegistry` (which already exposed the mutable `MarkDisabled` seam — no change needed there). After `IPersistentAppCatalogRepository.ReconcileAsync(...)` returns in `BootAsync()`, the coordinator calls `enablement.MarkDisabled(reconciled.Where(e => !e.IsEnabled).Select(e => e.Manifest.Id))`.

Without this, a disable from a previous session (or, once ADR 0033 ships, one pulled from another device) would only take effect once someone happened to disable the app again in the current session — the durable flag and the live enforcement state would silently disagree from the moment of boot until the next explicit toggle. This mirrors, and actually closes, the wiring gap ADR 0031 (Grants) deliberately left open for its own domain — the difference is that here a real UI now exists to make the gap immediately observable and worth fixing.

### 3. New Settings UI tab: "Installed Apps"

Added to `SettingsWindow.razor`'s existing `MudTabs` list. Lists every app via `IPersistentAppCatalogRepository.ReadAllAsync()` (manifest name plus persisted `IsEnabled`), one row per app with an enable/disable toggle. Toggling calls `AppLifecycleOrchestrator.DisableAsync`/`EnableAsync` directly — already registered as a DI singleton and now injected into the Settings window — which both enforces live and persists durably in one call (Decision 1), then re-reads `ReadAllAsync()` to refresh the displayed state, since a disable's dependency closure can affect more rows than the one clicked.

This is a general OS admin feature, not something specific to any one host. It does **not** introduce server-hosted-only configuration — confirmed against the user's separate note that server-specific settings belong in a dedicated app available only when running as the server host; app enablement applies identically across the WASM, static, and server-hosted deployments, so it belongs among the always-visible Settings tabs like every existing one.

### 4. Deliberately out of scope

`AppLauncher.razor` (the start-menu/search UI) does not consult `IAppEnablementRegistry` and will still list a disabled app in search — it already correctly fails to *launch* (`AppLifecycleOrchestrator.LaunchAsync` already checked `IsEnabled` before this ADR), so this is a pre-existing minor UX gap, not a correctness gap this ADR needs to close to satisfy "the user can disable an app and it works."

## Consequences

- Disabling/enabling an app is now a real, working, persisted, user-facing feature — not just internal machinery exercised only by tests.
- `AppLifecycleOrchestrator.Enable` is a breaking rename to `EnableAsync` (async). The only production and test call sites were updated in this change.
- `EcosystemBootCoordinator`'s constructor gained a required `AppEnablementRegistry` parameter — all call sites (production DI registration, tests) updated.
- AppCatalog sync (ADR 0033) can now build on a domain that has a genuine local write path, the same way Settings/FileSystem sync always did — unlike Grants (ADR 0031), which stayed pull-only specifically because no such write path existed.
- `AppLauncher.razor` still doesn't filter disabled apps from search results — named here as a known, minor, pre-existing gap, not silently discovered later.

## References

- ADR 0031: Grants Domain Sync (Pull-Only) (the precedent for naming a live-enforcement wiring gap explicitly rather than silently leaving it; this ADR closes the equivalent gap for AppCatalog because a real UI now depends on it)
- `docs/server-implementation-pass.md`
