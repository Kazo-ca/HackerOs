# ADR 0033: AppCatalog and FileAssociations Domain Sync

## Status

Accepted on 2026-08-15.

## Context

This is the last item ("Pass N+4") in the original five-domain sync roadmap from `docs/server-implementation-pass.md`, following ADR 0029 (Settings), ADR 0030 (FileSystem), ADR 0031 (Grants, pull-only), and ADR 0032 (App Enablement Management — the prerequisite this ADR depends on, added after the user pointed out that syncing AppCatalog would have synced a feature nobody could use).

Research (an Explore pass over both remaining domains) found they need different treatment from each other, despite the roadmap grouping them as "smallest scope, do together":

- **FileAssociations already exists as a settings document** (`Platform/HackerOs.Platform.Core/Intents/FileAssociationSettingsDocuments.cs`) — one document at `/etc/hackeros/file-associations.json`, `OsAdmin` scope. ADR 0029's own text already named it as a future opt-in candidate. But `SettingsSyncService` hard-codes `SyncDomain.Settings` on every envelope, while `SyncDomain.FileAssociations` is a distinct, separately-partitioned domain server-side (`SyncService` filters by `(RecordId, Domain)`; ADR 0025's conflict-rule table lists it as its own row). Simply flipping `SyncEligible: true` would route the document into the wrong domain.
- **AppCatalog is about device-local app-enablement flags**, not "which apps are installed." The build-time `AppCatalog` (`Platform/HackerOs.Platform.Core/AppCatalog.cs`) is a deployment artifact and never syncs. The sync candidate is `IPersistentAppCatalogRepository` (`Shared/HackerOs.App.Abstractions/AppCatalogRepositoryContracts.cs`), keyed by `appId` string (no stable Guid, unlike `FileSystemEntryId`/`CapabilityGrantId` reused directly in ADR 0030/0031 — needs a derived `RecordId`, the same hash-of-key approach ADR 0029 used for Settings). Unlike Grants, this domain now has a real, working local write path (`SetEnabledAsync`, driven by ADR 0032's Installed Apps UI), so it gets push **and** pull, not pull-only.

## Decision

### 1. FileAssociations: a narrow sibling of `SettingsSyncService`, not a generalization of it

New `IFileAssociationsSyncService`/`FileAssociationsSyncService` (`Platform/HackerOs.Platform.Core/ServerConnection/IFileAssociationsSyncService.cs`), the same push/pull/conflict shape as `ISettingsSyncService` but scoped to exactly `FileAssociationSettingsDocuments` and hard-coded to `SyncDomain.FileAssociations`. `RecordId` derived the same way ADR 0029 did (`SHA256` of the document key). `FileAssociationSettingsDocuments.CreateDefinition()` now passes `SyncEligible: true`.

A dedicated sibling class rather than teaching `SettingsSyncService` about a per-document domain was chosen deliberately: it avoids reopening ADR 0029's already-shipped, already-tested design for one document's sake, and matches this session's own precedent of one adapter class per domain (Settings, FileSystem, Grants each got their own).

### 2. AppCatalog: push + pull, `RecordId` = hash of `appId`

New `IAppCatalogSyncService`/`AppCatalogSyncService`. Payload is `AppCatalogSyncPayload(string AppId, bool IsEnabled)` — never the manifest, which is a build artifact (ADR 0025: *"enablement flags are device-local opinion"*). Push diffs `IPersistentAppCatalogRepository.ReadAllAsync()` against `SyncRecordTrackingState` exactly like Settings/FileSystem. Pull applies via `SetEnabledAsync(appId, isEnabled)`; when it returns `false` (this device's own build doesn't have that app), the record is skipped silently — an expected outcome across devices with different app selections, not an error.

**Conflict handling reuses Settings' proven server-wins pattern**, not the `ClientWins` ADR 0025 named as preferred for this domain. Nothing could produce an AppCatalog conflict before ADR 0032 existed (no writer at all), so there is no real usage to validate a bespoke `ClientWins` retry policy against yet. Recorded here as a deliberate, revisitable divergence — the same "record the simplification, don't hide it" posture ADR 0030 and ADR 0031 both used for their own divergences.

**Pull takes effect immediately, not just at next boot.** ADR 0032 built `EcosystemBootCoordinator` to hydrate `AppEnablementRegistry` from `IPersistentAppCatalogRepository` at boot specifically anticipating this pass (its own text says so: *"or, once ADR 0033 ships, one pulled from another device"*). `AppCatalogSyncService.PullAsync` completes that by calling `AppEnablementRegistry.MarkEnabled`/`MarkDisabled` directly after a successful `SetEnabledAsync` — a raw registry update, the same shape boot-time hydration itself uses, not a full `AppLifecycleOrchestrator.DisableAsync`. This means a currently-running instance of an app newly disabled by a pull is **not** stopped mid-session (no dependency-closure computation, no process termination) — it will simply fail to relaunch, consistent with what boot-time hydration already does today. Named here explicitly rather than left as a surprise.

### 3. Both wired into the same "Sync now" flow

`SettingsWindow.razor`'s `SyncNowAsync()` gains push-then-pull calls for both, matching the ordering already used for Settings/FileSystem, followed by a refresh of the Installed Apps tab's displayed list (a pull can change enablement for apps whose rows are already on screen).

### 4. This completes the original five-domain sync roadmap

Settings (ADR 0029), FileSystem (ADR 0030), Grants (ADR 0031), and now AppCatalog + FileAssociations are all implemented. Recorded explicitly in `docs/server-implementation-pass.md` rather than left implicit.

## Consequences

- Every domain named in the original roadmap now has a working client-side adapter.
- FileAssociations sync reuses the Settings document plumbing entirely — no new local storage, no new payload shape decisions, the smallest of the five passes as originally predicted (once ADR 0032 unblocked AppCatalog specifically, not FileAssociations).
- AppCatalog sync's server-wins conflict handling diverges from ADR 0025's `ClientWins` recommendation; revisit once real cross-device enablement conflicts are observed in practice.
- A pulled AppCatalog disable does not stop an already-running instance of that app on this device mid-session — a named, minor gap, not a silent one.
- Two domains (Grants, pull-only per ADR 0031; the rest, push+pull) now demonstrate genuinely different sync shapes are possible on top of the same domain-agnostic scaffolding (`ISyncClient`, `syncCursors`, `ISyncRecordStateRepository`) introduced in ADR 0029 — none of the last three passes needed new scaffolding, only new domain-specific adapters.

## References

- ADR 0025: Record Synchronization Envelope, Conflict Model, and Cursor Strategy (defines the `file_associations`/`app_catalog` domains and the `ClientWins` recommendation this ADR explicitly diverges from for AppCatalog)
- ADR 0029: Settings Domain Sync (First Client Sync Implementation) (the pattern `FileAssociationsSyncService` mirrors)
- ADR 0032: App Enablement Management (the prerequisite that gives AppCatalog a real local write path, and the boot-time hydration mechanism this pass's pull path complements)
- `docs/server-implementation-pass.md`
