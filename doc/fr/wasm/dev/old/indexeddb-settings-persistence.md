# IndexedDB Settings Persistence

## Purpose

Persist canonical settings documents in browser storage without allowing stale
writers to overwrite a newer revision.

## Architecture

`IndexedDbSettingsDocumentService` implements the existing path-based
`ISettingsDocumentService`. Each registered definition supplies a structured
`SettingsDocumentKey`; the service serializes that key into the primary key and
stores its individual fields for future index rebuilding.

C# owns definition validation, authorization, initial content, and revision
semantics. The JavaScript adapter only provides browser primitives. Its generic
`compareAndPut` operation reads a property, compares it with an expected value,
and conditionally writes inside one IndexedDB read/write transaction.

## Behavior

- Initial documents are inserted at revision 1 through atomic `addIfAbsent`
  after the schema opens; an existing document is never overwritten.
- Reads resolve the projected virtual path to its canonical storage key.
- Writes validate authorization and content before opening a transaction.
- A matching expected revision commits the replacement at revision + 1.
- A stale expected revision returns `SettingsWriteStatus.Conflict` without a
  write.
- Persisted records retain structured scope and owner fields so derived indexes
  can be rebuilt from canonical documents.
- File-association consumers rebuild their in-memory lookup from the current
  canonical document on each resolution, so no stale derived index is persisted.

## Key Decisions

- Never split the revision check and write across C#/JavaScript calls.
- Keep `compareAndPut` domain-neutral; settings policy remains in C#.
- Keep initialization idempotence inside one IndexedDB transaction.
- Use explicit `JsonElement` reads to satisfy strict WASM trimming analysis.

## Task List

- [x] Add canonical structured keys to settings definitions.
- [x] Add atomic generic compare-and-put support.
- [x] Implement canonical IndexedDB settings reads and writes.
- [x] Rebuild derived association indexes from canonical records.
- [ ] Prove multi-tab conflicts in real-browser automation.