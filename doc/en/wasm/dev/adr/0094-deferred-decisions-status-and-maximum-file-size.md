# ADR 0094: status of the deferred decisions of the v3 migration and maximum file size

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`).

- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md), row `v3/1-adr`.
- **Source:** `doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md`, §20 "Deferred Decisions"
  and the box `[ ] Resolve Phase 0 deferred decisions in architecture decision records`
  of §22; gap R22 of the 2026-10-16 inventory.

## Context

The v3 migration analysis (2026-08-01) left twelve decisions "to be made before
their phase", each of which was to become a short ADR. Several have been settled
since by ADRs that do not cite them, one has become moot, and
some remain open. For lack of a table, the 2026-10-16 inventory counted them
all as missing.

## Decision

1. **Correspondence table.** Each deferred decision now has a written
   status:

   | No. | Deferred decision | Status |
   |---|---|---|
   | 1 | LTS version of .NET and supported browsers | **Settled by the code**: `net10.0` in all projects, SDK `10.0.100` pinned by `global.json` (`rollForward: latestFeature`). Browsers: Chromium (target of E2E and PWA tests); Firefox and Safari on a best-effort basis, with no automated test. |
   | 2 | Format, signing and revocation of app packages | **Deferred** to the "runtime app installation" plan (Benoit's answer 1, 2026-10-17). |
   | 3 | Untrusted runtime-loaded apps and isolation | **Deferred** to the same plan. Current isolation is that of phase 5 (Web Workers, ADR 0070). |
   | 4 | Shell grammar | **Settled**: ADR 0050 then ADR 0064 (POSIX shell). |
   | 5 | Splitting, deduplication and maximum size of a file | **Settled** by `FileContentStoragePolicy`, recorded in point 2 below. |
   | 6 | IndexedDB quota, user-side export and restore | **Settled** by [ADR 0099](0099-os-backup-and-low-storage.md) (work item `v3/7`). |
   | 7 | Conflict algorithms per sync domain and offline history depth | **Settled** by ADR 0025 (table per domain) and [ADR 0101](0101-synchronized-deletions-and-conflicts.md) (work item `v3/9`). |
   | 8 | Local users and synchronized identities | **Settled**: ADR 0024 and 0028. |
   | 9 | Sensitive settings and client-side encryption | **Settled** by [ADR 0100](0100-sensitive-settings-and-protected-write-audit.md) (work item `v3/8`). |
   | 10 | PWA restart after a runtime installation | **Deferred** to the "runtime app installation" plan. |
   | 11 | SDK support and deprecation window, package updates | **Settled** by ADR 0019 (SemVer, `[Obsolete]` one minor version before removal) and [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md) (work item `v3/6`). |
   | 12 | Recovery interface when Settings and Terminal are disabled | **Moot**: there is no more disabling (ADR 0048), and a system app cannot be uninstalled. Settings and Terminal are system apps, therefore always present. |

2. **Maximum file size.** The rule already applied by
   `Infrastructure/HackerOs.Infrastructure.Browser/Schema/FileContentStoragePolicy.cs`
   becomes the documented rule:
   - a file is at most **16 MiB** (`DefaultMaxFileSizeBytes`);
   - its content is split into chunks of at most **256 KiB**
     (`DefaultMaxChunkSizeBytes`), addressed by their SHA-256 and deduplicated;
   - a chunk that is no longer referenced is kept for 30 days before being deleted.

   A write that would exceed 16 MiB is already refused by
   `IndexedDbFileContentRepository` (line 67), but with a generic error.
   Work item `v3/7` gives it its own code, `FileSystemErrorCode.FileTooLarge`
   (the equivalent of `EFBIG` on Linux), alongside `NoSpace` (`ENOSPC`, see
   ADR 0099). Changing these values requires a
   new ADR, because they fix the format stored in IndexedDB and the sync
   chunk protocol (ADR 0030).

3. The deferred decisions (no. 2, 3 and 10) are no longer gaps in the
   v3 migration: they belong to the runtime installation plan.

## Consequences

- The §22 box of the analysis can be ticked by `v3/16-final`, with a reference to
  this ADR.
- `sdk-developer-guide.md` and `sdk-reference.md` (`HackFile` section) cite the
  16 MiB limit when the work item that adds the "file too large" error
  to the SDK (`v3/7`, with `ENOSPC`) updates them.
