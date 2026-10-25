# ADR 0004: Settings Files Are Canonical Projections

## Status

Accepted on 2026-08-01.

## Context

HackerOS settings must be editable with normal text editors like Linux
configuration files. Persisting one copy in a settings database and another in
the virtual filesystem would create synchronization and authorization races.

## Decision

The settings service owns one canonical revisioned document. Virtual settings
paths are projections that delegate reads and writes to that service. File writes
perform the same capability, authority, revision, and schema checks as settings
UI writes.

## Consequences

- Text editors and settings UI always observe the same revision.
- Invalid edits cannot corrupt or partially replace settings.
- Virtual filesystem routing must recognize projected paths before ordinary file
  storage.
- Browser persistence stores canonical records, not duplicate file content.