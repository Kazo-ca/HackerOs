# ADR 0059: SDK boundary diagnostic codes

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 4: complete SDK and base apps, work item
  `rebuild/4-sdk-boundary` (ADR 0058).
- **Partially supersedes:** ADR 0055, sections 2 and 3, for the diagnostic
  numbers only. The rules of ADR 0055 do not change.

## Context

ADR 0055 assigns two codes to the new checks of the SDK
boundary:

- `HOSAPP0002` for a forbidden `ProjectReference` in an app project;
- `HOSAPP0003` for a forbidden injection, reported by the manifest
  generator.

These two numbers have already been taken since ADR 0047 and are emitted by the same
generator: `HOSAPP0002` reports a non-public `[AppManifest]` class,
`HOSAPP0003` an abstract `[AppManifest]` class (see
[`app-model.md`](../../framework/app-model.md#codes-derreur)). The same code would therefore have two
meanings, and the tests that look for `error HOSAPP0002` would no longer know which
rule they are verifying.

## Decision

The codes of ADR 0047 keep their meaning. The checks of ADR 0055
take the next free numbers, in a range reserved for the SDK
boundary (`HOSAPP0030` to `HOSAPP0039`):

| Code | Rule (ADR 0055) | Emitted by |
|---|---|---|
| `HOSAPP0030` | An app project references a project outside the allowed list (section 2). | MSBuild target `ValidateHackerOsSdkBoundary` |
| `HOSAPP0031` | An app project injects a service that is neither an SDK type, nor a MudBlazor service, nor `IJSRuntime` (section 3). | manifest generator |
| `HOSSDK0001` | An SDK library references a project other than an abstraction or another SDK library (section 2). Unchanged. | MSBuild target `ValidateHackerOsSdkBoundary` |

Everywhere that ADR 0055 and the work item table of ADR 0058 write
`HOSAPP0002` (reference) and `HOSAPP0003` (injection), read
`HOSAPP0030` and `HOSAPP0031`.

## Consequences

- No existing code changes: the projects and tests of ADR 0047
  keep working.
- The documentation of the codes ([`app-model.md`](../../framework/app-model.md#codes-derreur)
  and [`app-sdk.md`](../../framework/app-sdk.md)) lists the two new codes.

## Rejected alternatives

- **Renumbering the codes of ADR 0047** to free `HOSAPP0002` and
  `HOSAPP0003`: changes the meaning of codes already published, already tested and already
  described by an accepted ADR, for no benefit.

## References

- ADR 0047 (codes `HOSAPP0001` to `HOSAPP0099`), ADR 0055, ADR 0058.
- `Tools/HackerOs.Tools.ManifestGenerator/GeneratorDiagnostic.cs`,
  `build/HackerOs.Apps.targets`.
