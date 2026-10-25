# IndexedDB Operational Records

## Purpose

Persist HackerOS diagnostics and security audit records across browser reloads
without exposing synchronous IndexedDB behavior or storing structured secrets.
This is the first completed slice of `P2-IDB-009`.

## Architecture

`IPersistentDiagnosticRepository` and `IPersistentAuditRepository` are
cancellation-aware asynchronous durability contracts. They remain separate from
the synchronous in-memory `IDiagnosticSink` and `IAuditLog` runtime facades, so
no caller can receive success before an IndexedDB commit or rely on write-behind.

`IndexedDbDiagnosticRepository` and `IndexedDbAuditRepository` live exclusively
in Browser Infrastructure. Both receive `IDiagnosticRedactor` and redact every
structured property before constructing the value sent through JS interop.
Persisted records use epoch milliseconds, numeric enum values, compact GUIDs,
and string dictionaries. Manual `JsonElement` decoding rejects malformed enum
values and record shapes and remains compatible with WASM trimming.

## Ordering And Retention

The `timestampUtcMs` index returns records from oldest to newest. IndexedDB
orders duplicate index keys by primary key, so the auto-incremented `id` is the
stable tie-breaker for records written in the same millisecond.

Diagnostics have an injected positive capacity. One `DiagnosticsAppend`
transaction adds the new record and executes `trimOldest`, which counts the
store and deletes excess keys from the beginning of the timestamp index. A
failed transaction commits neither the append nor the eviction.

General audit is append-only and receives no automatic business eviction. Its
`AuditAppend` transaction contains one add. Explicit maintenance, export, or
recovery policy may manage storage later; quota errors never silently erase
security records.

## Usage

The future host composition supplies the shared sensitive-key redactor and a
diagnostics capacity, then registers the persistent repositories alongside the
runtime facades. Callers await `AppendAsync` and may load chronological records
with `ReadAllAsync`.

## Key Decisions

- Durable repositories use new async contracts; synchronous runtime contracts
  are not converted and do not hide background persistence.
- Diagnostics are bounded; general audit is retained append-only.
- Equal timestamps are ordered by the auto-incremented primary key.
- Redaction occurs before serialization and JS interop, never only on display.
- Policy grant mutations will update grant, audit, and policy revision in one
  transaction. Revocation state remains on the immutable grant record so reload
  preserves the distinction between revoked and missing policy.
- Build-profile manifests are authoritative during catalog reconciliation;
  existing local enablement survives manifest replacement, while records absent
  from the current build are retained disabled and excluded from runtime input.

## Task List

- [x] Define async persistent diagnostic and audit contracts.
- [x] Redact structured properties before browser storage.
- [x] Append and trim diagnostics atomically.
- [x] Preserve append-only general audit records.
- [x] Read both stores oldest-first with a stable equal-time tie-breaker.
- [x] Add focused browser-free repository tests.
- [x] Implement atomic persistent capability grants and policy revision.
- [x] Implement persistent catalog records and enablement reconciliation.
- [x] Add focused catalog reconciliation tests.
- [ ] Validate reload behavior in the real-browser IndexedDB test phase.
