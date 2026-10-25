# ADR 0019: App SDK Versioning, Compatibility, and Deprecation Policy

* **Status:** Accepted (DECISION: D-012)
* **Date:** 2026-08-03
* **Context:** As HackerOS ports and expands applications, third-party and ecosystem applications must build against a stable, versioned App SDK. A strict public API surface, backward-compatibility guarantee, and manifest version policy are required to prevent breaking existing applications upon operating system updates.

---

## Decision Drivers

* **Clean SDK Separation:** Application projects must reference only public App SDK libraries (`HackerOs.App.Abstractions`, `HackerOs.AppSdk`, `HackerOs.AppSdk.Blazor`). Referencing `HackerOs.Platform.Core` or `HackerOs.Infrastructure.Browser` directly in application assemblies is strictly prohibited.
* **Semantic Versioning:** The App SDK follows strict Semantic Versioning 2.0.0 (`MAJOR.MINOR.PATCH`).
* **Deprecation Notice:** Obsolete members are marked with `[Obsolete]` for at least one minor release prior to removal in a major version bump.
* **Manifest Capability & Compatibility:** The kernel enforces `sdkCompatibility.minimumVersion` and `maximumVersion` during assembly discovery and catalog build.

---

## Decision Outcome

**Selected Option:** Strict Public SDK Boundary with Semantic Versioning (DECISION: D-012).

### Architectural Rules

1. **Public Package Surface:**
   Applications may consume only:
   - `HackerOs.App.Abstractions` (Manifests, Capabilities, Authorities, VirtualPath, Operations)
   - `HackerOs.AppSdk` (Base classes: `AppBase`, `TerminalAppBase`, `ServiceAppBase`, Execution Context)
   - `HackerOs.AppSdk.Blazor` (`WindowAppBase`, Dialog abstractions, UI helpers)

2. **Internal Isolation:**
   Platform Core, IndexedDB Browser Infrastructure, and Host Ecosystem classes remain `internal` or unexposed to application assemblies.

3. **Compatibility Testing:**
   Automated regression tests verify that applications compiled against earlier SDK candidate releases (`v1.0.0-alpha`) load and run without binary or contract breakage on updated kernels.

4. **Deprecation Period:**
   APIs slated for removal must be marked with `[Obsolete("...", false)]` for a minimum of one minor release cycle before breaking changes are introduced in a major version.
