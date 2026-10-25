# Canonical Manifest JSON Schema and Build Profile

## Purpose

Publish a versioned JSON Schema for `app.manifest.json` that structurally
validates every field introduced by migration analysis section 7.2, per
[ADR 0010](../adr/0010-manifest-json-and-schema.md). This document also now
captures the first build-profile contract used to select included apps, grants,
defaults, associations, locales, themes, and optional server features for the
Phase 1 headless profile slice.

## Scope

- The canonical `AppManifest` C# record
  (`Shared/HackerOs.App.Abstractions/AppManifest.cs`) and its satellite types
  now cover every field group from analysis section 7.2: identity, entry
  point, SDK/OS compatibility, presentation (category, launch visibility, icon
  assets), localizations, capabilities, resource profile, settings schema,
  intents, dependencies, assets, update/migration metadata, file handlers,
  terminal metadata, and the Service-only `AutoStart` flag.
- A new build-profile model in
  `Shared/HackerOs.App.Abstractions/BuildProfileManifest.cs` defines the
  build-time selection shape for included packages, boot-critical status,
  default-enabled apps, required grants, default associations, locales, themes,
  and optional server features.
- The runtime built-in catalog is now `HackerOs.Theming.Abstractions.ThemeCatalog`
  (see [`theming.md`](theming.md)). `BuildProfileManifest.Themes` remains a
  headless build-selection contract; it does not currently prune that catalog
  or its shared static CSS asset. Wiring those IDs into packaging/allowlisting
  requires explicit validation work rather than silently treating profile text
  as a selector.
- A Draft 2020-12 JSON Schema document,
  `Shared/HackerOs.App.Abstractions/Schema/manifest.schema.v1.json`, describes
  the equivalent lower-camel-case JSON shape with a stable `$id`
  (`https://hackeros.org/schema/app-manifest/1/manifest.schema.json`).
- `AppManifestValidator` (semantic validation) and the JSON Schema (structural
  validation) are intentionally complementary, matching ADR 0010's validation
  order: JSON structure → schema version/versioned JSON Schema → (future)
  source-generated deserialization → semantic `AppManifestValidator` →
  build-profile cross-reference (not yet implemented).

## Architecture

- **Schema authoring**: `manifest.schema.v1.json` sets
  `"additionalProperties": false` at every object level so unknown fields are
  rejected structurally instead of silently ignored. `allOf`/`if`/`then`
  conditionals enforce app-kind-specific structure directly in the schema:
  - `kind == "terminal"` requires a `terminal` block.
  - `kind != "window"` forbids `fileHandlers`.
  - A settings field with `valueType == "enum"` requires `allowedValues`.
- **Embedding**: The schema file is embedded as a resource in
  `HackerOs.App.Abstractions.csproj` (`LogicalName`
  `HackerOs.App.Abstractions.Schema.manifest.schema.v1.json`) and loaded via
  `ManifestSchemaResource.LoadCurrentSchemaJson()`. This keeps the published
  schema and the assembly that defines the manifest shape in lock-step — there
  is exactly one copy of the schema text, not a duplicate maintained by
  consuming test/tooling projects.
- **Fixtures**: `Schema/Fixtures/` holds:
  - One complete valid manifest per app kind: `window.valid.json`,
    `terminal.valid.json`, `service.valid.json`.
  - Nine invalid fixtures, each isolating a single structural violation:
    schema version mismatch, malformed app id, a missing required
    (`resources`) field, an unknown top-level property, a terminal manifest
    missing its `terminal` block, file handlers declared on a service app, a
    malformed SHA-256 asset hash, an out-of-range resource weight, and an enum
    setting missing `allowedValues`.
- **Conformance tests**: `Tests/HackerOs.App.Abstractions.Tests/Schema/ManifestSchemaConformanceTests.cs`
  loads the embedded schema with `JsonSchema.Net`, evaluates every fixture, and
  asserts valid fixtures pass while invalid fixtures fail. Fixtures are read
  directly from `Shared/HackerOs.App.Abstractions/Schema/Fixtures/` by walking
  up from the test output directory to `HackerOs.sln`, so there is no
  build-copy step to keep in sync.

## Key decisions

- **JSON Schema validates structure; `AppManifestValidator` validates
  semantics.** Anything expressible as a plain structural constraint (shape,
  enum membership, required-field conditionals, pattern matching) lives in the
  JSON Schema. Anything requiring cross-field business rules that are awkward
  or impossible to express declaratively (e.g. "an icon asset path must
  reference a declared asset", "the burst weight of a resource dimension must
  not be below its baseline weight") stays in `AppManifestValidator`. Both
  layers are exercised by dedicated tests so a regression in either is caught
  independently.
- **No second hand-maintained schema copy.** The schema is embedded directly
  in `HackerOs.App.Abstractions` and loaded through `ManifestSchemaResource`
  rather than copied into the test project, so schema and code cannot drift
  apart silently.
- **`JsonSchema.Net` chosen for schema evaluation.** It is the most complete
  Draft 2020-12 implementation available on NuGet for .NET and is scoped only
  to the `HackerOs.App.Abstractions.Tests` project (a `<PackageReference>` in
  that project's `.csproj`); no schema-validation dependency is added to any
  shipping (non-test) project.
- **`System.Text.Json` source-generated (de)serialization now exists** via
  `AppManifestJsonSerializer` and `AppManifestJsonSerializerContext` in
  `Shared/HackerOs.App.Abstractions/`. The serializer uses strict unmapped-member
  handling, lower-camel property names, lower-camel enum values, and a canonical
  single-LF output that is pinned by a golden fixture under
  `Shared/HackerOs.App.Abstractions/Schema/Fixtures/`.
- **Build-profile cross-reference, build-time asset-existence checks, and
  discovery-list generation are now implemented for the initial headless slice.**
  `P1-BLD-004` is represented by the first declarative profile model with
  serializer and validator coverage, while `P1-BLD-005` and `P1-BLD-006` now
  complete the validator work for unresolved references, publish-asset scoping,
  and deterministic discovery-list assembly. The validator additionally rejects
  dependency cycles among included apps and requires at least one selected
  boot-critical package before publish/compile so the build profile cannot enter
  a boot-recovery-invalid state. `P1-BLD-008` is covered by explicit
  regression cases for every build-profile validation error code, alongside the
  existing manifest-schema fixture suite.
## Task list

- [x] Expand `AppManifest` and its satellite records to cover every analysis
  section 7.2 field group.
- [x] Extend `AppManifestValidator` with semantic checks for every new field
  group (assets, presentation, localizations, resources, settings schema,
  intents, update, auto-start).
- [x] Fix every existing test-project manifest factory broken by the two new
  required members (`Presentation`, `Resources`).
- [x] Add the `JsonSchema.Net` package to `HackerOs.App.Abstractions.Tests`.
- [x] Author `Schema/manifest.schema.v1.json` (Draft 2020-12, stable `$id`,
  `additionalProperties: false`, app-kind conditionals).
- [x] Embed the schema as a resource and expose it via
  `ManifestSchemaResource`.
- [x] Add one valid fixture per app kind and nine invalid fixtures covering
  distinct structural violations.
- [x] Add `ManifestSchemaConformanceTests` validating every fixture against
  the schema.
- [x] Run `dotnet test HackerOs.sln --no-restore` with zero failures
  (334 tests).
- [x] Add a strict source-generated serializer plus canonical fixture tests for
  manifest round-tripping/fixture parity.
- [x] Add a strict source-generated build-profile serializer plus validator and
  fixture-driven tests for the initial profile contract.
- [x] Update `integration-task-list.md`, `implementation-status.md`, and this
  document.

## Validation

```powershell
dotnet test HackerOs.sln --no-restore
```

339 tests pass, including 12 manifest JSON Schema conformance tests (3 valid
fixtures, 9 invalid fixtures) and 68 app-manifest serializer tests inside
`HackerOs.App.Abstractions.Tests`.
