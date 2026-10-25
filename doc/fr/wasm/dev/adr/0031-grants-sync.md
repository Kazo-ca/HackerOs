# ADR 0031: Grants Domain Sync (Pull-Only)

## Status

Accepted on 2026-08-15.

## Context

Following ADR 0029 (Settings sync) and ADR 0030 (FileSystem sync), `docs/server-implementation-pass.md` names Grants as the next sync pass. Grants is architecturally different from the previous two domains in ways that change the design, not just the payload shape.

- **Grants are server-authoritative by design.** ADR 0025 already established that tombstones are blocked and only `ServerWins` conflict resolution is allowed for this domain, enforced and tested server-side (`Tests/HackerOs.Server.Tests/SyncServiceTests.cs`'s `Push_Tombstone_ForGrantDomain_IsBlocked`, `ResolveConflict_GrantDomain_OnlyServerWinsAllowed`, `ResolveConflict_Merge_NotAllowedForGrantsDomain`).
- **No code path in the repository ever originates a user- or admin-approved grant.** The only production writer of capability grants is `CleanProfileCapabilityGrantSeeder` (`Platform/HackerOs.Platform.Core/Policy/CleanProfileCapabilityGrantSeeder.cs`), called from `LocalSessionService` at every login, which grants exactly what each installed app's manifest declares (`CapabilityGrantSource.BuildProfile`) — a deterministic byproduct of app installation, not something that varies per device and needs reconciling. There is no interactive user/admin grant-approval flow anywhere yet.
- **The server does not validate pushed Grants payload semantics.** `Server/HackerOs.Server/Services/SyncService.cs`'s `PushAsync` blocks tombstones and checks revision/hash for this domain like every other, but nothing stops a pushed payload from claiming a wider capability or constraint than the account actually has. The roadmap's own note flagged needing "explicit test coverage proving a compromised/buggy client can never widen its own grants via a crafted push" — but a client that pushes at all, with no server-side semantic validation to lean on, only has that property by *trusting* the client not to be compromised. Since nothing today legitimately needs the client to push a grant in the first place, the safer and more honest design is: **the client never pushes.** This matches ADR 0025's own phrasing that revocation "must go through the authorized grant API," implying a future server-side grant-issuing authority is the intended source, with sync only distributing records to devices.
- **A real wiring gap, separate from sync**: `ICapabilityGrantRepository` (in-memory, what every runtime capability check — `AppCapabilityChecker`, `AppLifecycleOrchestrator`, `FileSystemAuthorizer` via `AppOperationContext.GrantedCapabilities` — actually reads) and `IPersistentCapabilityGrantRepository` (IndexedDB-backed) are completely disconnected today. The in-memory one is rebuilt from scratch every login purely from manifest declarations; nothing reads the durable one back into it. Wiring a Grants pull into live enforcement would mean also changing `LocalSessionService`'s login seeding — a materially bigger change than adding a sync adapter, and one that needs its own design once there's an actual reason to widen local grants beyond manifest declarations.
- **A concrete repository gap that blocks even a pull-only design**: `IPersistentCapabilityGrantRepository.GrantAsync` always mints a new `Guid` internally — there was no way to persist a grant durably under a caller-supplied ID. `CapabilityGrant`'s own constructor already accepts an explicit `CapabilityGrantId` (`Shared/HackerOs.App.Abstractions/Policy/CapabilityGrant.cs`); only the repository's public mutation methods didn't expose it. A pull adapter needs to apply the server's `RecordId` as the *local* grant's identity too, so a later re-pull of the same record (e.g. a revocation) updates the existing row instead of creating a duplicate.
- **Revocation isn't a tombstone for this domain** (tombstones are blocked). It's modeled as a field on the payload, mirroring how the durable repository's own `GrantRecord` already tracks revocation as a field mutation (`RevokedAtUtcMs`/`RevokedRevision`), not a deletion.

## Decision

### 1. Pull-only domain — no client push, in this pass or conceptually

`IGrantsSyncService` exposes only `PullAsync`. There is no `PushAsync` method at all — not a no-op stub, genuinely absent — since nothing today has anything legitimate to push. This makes "a crafted push can't widen access" true by construction rather than by trusting unvalidated server-side acceptance. The Settings UI's "Sync now" flow calls `GrantsSync.PullAsync()` alongside the existing Settings/FileSystem push+pull calls.

### 2. `RecordId` = `CapabilityGrantId.Value`, reused directly

Same rationale as ADR 0030's `FileSystemEntryId` reuse: already a stable `Guid` per grant, no derived/hashed `RecordId` needed.

### 3. New repository capability: `IPersistentCapabilityGrantRepository.ImportAsync`

```csharp
ValueTask<CapabilityGrantMutationResult> ImportAsync(
    CapabilityGrantId id, string appId, string userId, string capability,
    CapabilityGrantSource source, IEnumerable<CapabilityConstraint>? constraints,
    bool isRevoked, AppAuthority actingAuthority, CancellationToken cancellationToken = default);
```

Upserts by `id` (create-or-update-in-place, unlike `GrantAsync`'s always-new-ID behavior) and audits the mutation under a distinct action (`"capability.sync-import"`, not `"capability.grant"`/`"capability.revoke"`) so the audit trail can tell a server-issued sync application apart from a locally-authorized mutation. This method is **not** exposed on `ICapabilityGrantRepository` (the in-memory one) — consistent with Decision 4.

### 4. Pull writes only to `IPersistentCapabilityGrantRepository` — not wired into live enforcement

Explicitly scoped down, the same way ADR 0028 scoped down curl/nmap/cat and ADR 0030 scoped down the pull-side local-content dedup check. This pass proves the sync mechanics and gives multi-device durability for grants (the roadmap's own "backup/reconciliation" framing) but does not change `LocalSessionService`'s login-time seeding or make a pulled/revoked grant take effect for already-evaluated `ICapabilityGrantRepository` checks. A named follow-up, not a silent gap.

### 5. Payload shape

A flattened DTO (`Shared/HackerOs.Simulation.Abstractions/Sync/GrantsSyncContracts.cs`) carrying `AppId`/`UserId`/`Capability`/`Source` (the `CapabilityGrantSource` enum name), a list of flattened constraint records (one shape covering all three `CapabilityConstraintKind` values), and `IsRevoked`. No server-side producer of this shape exists yet (there is no grant-issuing endpoint at all, per Context above) — this pass builds the client-side consumer in anticipation of a future grant-issuing/admin API, the same forward-looking posture ADR 0028 took for the connection layer before any UI used it.

### 6. The real, still-open server-side gap is carried forward, not hidden

Never pushing resolves the "crafted push widens access" risk for this client, but `SyncService.PushAsync` still has no semantic validation for a Grants payload from any client that calls the raw HTTP API directly. Recorded as an explicit open question in `docs/server-implementation-pass.md` rather than implied to be solved — it's a genuine, separate, still-unsolved server-side hardening item independent of this client's own behavior.

## Consequences

- Grants sync is real for the pull direction, reusing the domain-agnostic `syncCursors`/`ISyncClient` scaffolding from ADR 0029 (no new IndexedDB stores or schema version bump needed — `ISyncRecordStateRepository` wasn't needed either, since a pull-only domain has nothing to diff a push against and `ImportAsync`'s upsert is already idempotent against redelivery).
- Pulled grants are durable and visible across devices, but do not yet affect what a running app can actually do — enforcement still reads only from the login-seeded, manifest-only in-memory repository. This asymmetry is a known, named gap, not an oversight.
- The repository gained a genuinely new capability (`ImportAsync`) that any future pass needing externally-identified grant import can reuse, not just sync.
- The server's lack of Grants payload semantic validation remains open and is now explicitly tracked, rather than being incidentally "fixed" by this client simply not exercising it.

## References

- ADR 0025: Record Synchronization Envelope, Conflict Model, and Cursor Strategy (defines the Grants domain's server-authoritative, `ServerWins`-only, tombstone-blocked model this pass builds a client against)
- ADR 0029: Settings Domain Sync (First Client Sync Implementation) (the `syncCursors`/`ISyncClient` scaffolding this pass reuses)
- ADR 0030: FileSystem Domain Sync (the `RecordId`-reuse precedent and the "record explicit simplifications" style this ADR follows)
- `docs/server-implementation-pass.md`
