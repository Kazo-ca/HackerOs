# ADR 0003: Match Capabilities Exactly

## Status

Accepted on 2026-08-01.

## Context

Apps request capabilities in their manifests and trusted OS policy grants a
subset. Wildcard or case-insensitive matching can unintentionally turn a narrow
grant into broad access, especially as new capabilities are added.

## Decision

Capability IDs use lowercase namespaced strings from the App SDK catalog. Grants
match identifiers exactly with ordinal, case-sensitive comparison. Unknown
manifest capabilities fail validation. Resource limits such as paths, hosts,
ports, and quotas are separate policy constraints rather than wildcard IDs.

## Consequences

- Capability checks remain deterministic and auditable.
- Adding a capability never expands an existing wildcard grant.
- Apps must update their SDK range and manifest to request newly introduced
  capabilities.
- The future policy model needs structured resource constraints.