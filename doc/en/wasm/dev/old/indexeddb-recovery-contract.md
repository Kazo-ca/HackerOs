# IndexedDB Recovery Contract

## Purpose

Define the renderer-independent states and safe commands used when browser
storage is unavailable, full, corrupt, conflicting, or fails migration. The
contract does not render UI and never mutates IndexedDB by itself.

## Architecture

`StorageRecoveryContracts.cs` in Simulation Abstractions defines closed recovery
states, contexts, allowed action flags, correlation IDs, boot blocking, export
availability, and destructive confirmation. It has no browser or Blazor
dependency.

`BrowserStorageRecoveryClassifier.cs` in Browser Infrastructure translates
typed persistence exceptions and IndexedDB DOM failures into that contract.
Raw exception messages remain diagnostic input and are not user-facing text.
The future host recovery renderer consumes stable error codes and typed states.

## Recovery Policy

- Ordinary quota exhaustion does not retroactively block an already-ready OS.
  It offers retry, export, and explicit bounded cleanup.
- Boot or migration failures block readiness when persisted state cannot be
  established safely.
- Migration failure retains read-only diagnosis and export actions. It never
  automatically deletes or upgrades past the failed transaction.
- Unavailable storage offers retry and read-only diagnosis, but does not claim
  export is available when IndexedDB cannot be accessed.
- Invalid content preserves export when readable and may offer validated merge
  or replacement restore. Invalid backups do not directly offer replacement.
- Recoverable optimistic conflicts offer retry without blocking boot.

## Destructive Confirmation

Replacement and reset are separate confirmation states. The affected data must
be displayed and input must exactly match the case-sensitive targeted phrase:

| Action | Required phrase |
| --- | --- |
| Replace from validated backup | `REPLACE` |
| Reset local HackerOS storage | `RESET` |

Whitespace and different casing do not confirm the action. Export remains
available while confirmation is pending. No exception classification can
implicitly enter or complete a destructive operation.

## Stable Error Codes

- `storage.quota-exhausted`
- `storage.backup-invalid`
- `storage.operation-conflict`
- `storage.migration-failed`
- `storage.unavailable`
- `storage.content-invalid`
- `storage.failure`
- `storage.confirm-replace`
- `storage.confirm-reset`

Each presentation also carries a non-empty correlation ID for diagnostics and
support export.

## Verification and Remaining Work

Focused unit tests cover quota, migration, unavailable storage, invalid backup,
action safety, and exact replacement/reset phrases. Host rendering and command
wiring remain part of the host recovery work; real IndexedDB rollback, reload,
quota, and backup/restore evidence remains `P2-IDB-014`.

## Completed Task List

- [x] Define renderer-independent recovery states and contexts.
- [x] Define safe actions, boot blocking, export availability, and correlation.
- [x] Classify browser storage failures without mutation.
- [x] Require exact targeted confirmation for replacement and reset.
- [x] Add focused contract tests.
- [x] Document host and real-browser boundaries.