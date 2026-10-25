# ADR 0005: Deterministic App Dependency Order

## Status

Accepted on 2026-08-01.

## Context

Assembly discovery order and dictionary iteration are not stable contracts. App
startup must honor dependencies and produce identical results across builds,
browsers, and tests. Shutdown must not stop a dependency while its dependent is
still running.

## Decision

The app catalog requires an acyclic graph. It uses dependency-first topological
ordering with ordinal app ID as the tie-breaker. Deactivation reverses that order.
Missing required dependencies, incompatible versions, duplicates, and cycles
reject the complete catalog before app code runs.

## Consequences

- Startup and tests are reproducible.
- Dependents always stop before dependencies.
- Dependency cycles are configuration errors, not runtime deadlocks.
- Optional dependencies contribute graph edges only when installed.