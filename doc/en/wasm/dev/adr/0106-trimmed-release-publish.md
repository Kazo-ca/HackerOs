# ADR 0106: trimmed Release publish of the Ecosystem host

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work item `v3/14-trimming`).

- **Complements:** ADR 0047 (generated manifests, lazy loading of apps),
  ADR 0070 (processes in workers), ADR 0082 (generated ASP.Hack route table),
  ADR 0084 (Blazor sites).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  line `v3/14-trimming`; gap R12. AOT out of scope.

## Context

- `HackerOs.Ecosystem.csproj:12` has `PublishTrimmed=False`. Yet
  `Directory.Build.props` sets `IsTrimmable=true` and `EnableTrimAnalyzer=true`
  on all non-test projects (except the server and the E2E support), and
  `TreatWarningsAsErrors` is active: the analysis runs, the trimming never.
- The built-in apps are lazily loaded (`BlazorWebAssemblyLazyLoad`,
  `build/HackerOs.Apps.targets:310-333`) then instantiated by reflection:
  `Activator.CreateInstance` of the entry point (`AppLifecycleOrchestrator.cs:136-144`),
  type looked up by its name (`AppEntryPointDiscovery.cs:99`), loading by name
  in the workers (`WorkerAssemblyLoader.cs:38-47`, `WorkerProcessHost.cs:295-300`),
  exported types of the sites (`HackSiteRouter.cs:231`), MVC model binding
  (`HackModelBinder.cs`). Several trimming warnings are suppressed there
  by attribute.
- These paths only break in a trimmed Release, never in Debug.

## Decision

1. **`PublishTrimmed=True` and `TrimMode=partial`** for the Ecosystem: only the
   assemblies marked `IsTrimmable` (ours) are trimmed; third-party packages
   (MudBlazor, Monaco, etc.) stay whole. Moving to `full` will require a
   new ADR, with measurements.
2. **Generated roots, no blind suppression.** The host targets
   generate, from `BuiltInApplications.json`, an ILLink descriptor
   (`TrimmerRootDescriptor`) that keeps for each app its entry point, its
   `AppDefinition` class, its embeddable components (ADR 0063) and, for a
   site, its controllers, views and routable components. The types thus
   built by reflection are therefore always present.
3. Where a generated table already exists (ASP.Hack routes and views, ADR 0082,
   0083, 0086), the code uses it instead of walking `GetExportedTypes`. A
   warning suppression that remains carries a comment saying which root
   makes it safe; a suppression without justification is a defect.
4. An app from another repository (package, ADR 0098) is trimmed at the publish of
   the host that includes it: the `HackerOs.App.Build` package enables the trimming
   analysis in app projects, and the templates ship with
   `IsTrimmable=true`.
5. **Test**: `build/publish-trimmed.sh` publishes the Ecosystem as a trimmed Release
   and a Playwright suite serves it and checks startup, login, the
   launch of each system app, a command in a worker, an ASP.Hack site
   (Intranet example) and a Blazor site, offline after the first
   load. This suite is part of the local merge gate of `v3/14`
   and the following work items.
6. The server (`IsTrimmable=false`) and the test host are not trimmed.

## Consequences

- `v3/14` fixes the trimming warnings of all shipped projects,
  adds the generated descriptor, the script and the E2E suite, and records the size of
  the publish before and after in `building-and-testing.md`.
- The work item comes after the work items that add APIs (plan, §4), so as not to
  redo the roots with each addition.
- `building-and-testing.md` and `project-overview.md` are updated.
