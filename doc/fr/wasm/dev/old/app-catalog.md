# App Catalog and Dependency Graph

## Purpose

Validate all selected app manifests and dependencies before any app assembly is
loaded or app code executes. A successful catalog provides deterministic startup
and shutdown order to the future lifecycle orchestrator.

## Architecture

`AppCatalog.Build` accepts manifests selected by build or package policy. It:

1. validates every manifest;
2. rejects duplicate app IDs;
3. verifies required dependencies exist;
4. permits missing optional dependencies;
5. checks installed dependency versions against Semantic Version 2.0.0 ranges;
6. rejects dependency cycles; and
7. computes dependency-first activation order using app ID as the deterministic
   tie-breaker.

Deactivation order is the exact reverse, ensuring dependents stop before their
dependencies.

The catalog is headless. Assembly discovery, app factories, runtime enablement,
and process/window lifecycle are later layers that consume a successful catalog.

## Error handling

Catalog construction returns structured errors rather than partially registering
apps. Error codes cover invalid manifests, duplicate IDs, missing dependencies,
incompatible versions, and cycles. No catalog is returned when any error exists.

This prevents discovery order or dictionary iteration from deciding which broken
configuration happens to run.

## Key decisions

- App IDs use ordinal deterministic ordering.
- All required selected apps form one acyclic dependency graph.
- Optional dependencies affect ordering only when present.
- Build metadata does not affect version compatibility.
- Prerelease versions follow SemVer precedence and do not satisfy a later release
  minimum.
- Invalid catalogs never load app entry-point types.

## Task list

- [x] Validate manifests before graph construction.
- [x] Reject duplicate app IDs.
- [x] Validate required and optional dependencies.
- [x] Validate SemVer dependency ranges.
- [x] Detect cycles.
- [x] Produce deterministic activation and reverse deactivation order.
- [x] Add focused catalog tests.
- [ ] Add build-profile enablement policy.
- [ ] Add assembly descriptors and reflection discovery.
- [ ] Add lifecycle/process orchestration over the validated catalog.