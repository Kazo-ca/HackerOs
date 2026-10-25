# ADR 0010: Canonical Manifest JSON and Schema

## Status

Accepted on 2026-08-01.

## Context

Every HackerOS app requires machine-readable identity, compatibility, entry
point, presentation, lifecycle, resource, capability, settings, intent, file,
terminal, dependency, asset, localization, and update metadata. The current C#
record covers the initial identity, compatibility, dependency, file-handler, and
terminal subset but has no canonical serializer or JSON Schema.

The manifest must be validated before assembly loading, work with trimming, be
hashable for packages and PWA profiles, and remain the only metadata developers
maintain. Generated assembly metadata cannot become a second source of truth.

## Decision

### Source and location

Each independently versioned app owns one `app.manifest.json` file at its project
and package root. That JSON file is the canonical author-maintained metadata.
Build tooling embeds or generates a compiled representation from it; developers
never edit both JSON and C# metadata.

The build fails when the canonical file is absent, invalid, incompatible, or
does not match generated/embedded output.

### JSON conventions

- Encoding is UTF-8.
- Property names use exact lower camel case.
- Enum values use exact lower camel case strings, including `window`, `terminal`,
  and `service`.
- Semantic versions are strings in canonical Semantic Version 2.0.0 form.
- Durations and byte sizes use explicit integer fields with unit-bearing names;
  ambiguous free-form units are forbidden.
- Paths use package-relative `/` separators and reject absolute paths, `.`/`..`,
  backslashes, query strings, fragments, and external URLs.
- Optional collections serialize as arrays, not null. Optional singleton objects
  may be absent; explicit null is rejected unless the schema names null as a
  meaningful value.
- Comments, trailing commas, `NaN`, `Infinity`, and duplicate object properties
  are rejected.

`schemaVersion` is a required positive integer and currently equals `1`. Readers
accept only schema versions they implement. Adding, removing, renaming, or
changing the meaning of a field requires a schema-version decision because strict
older readers reject unknown fields.

### Strict validation

Unknown properties are rejected recursively in both JSON Schema and
`System.Text.Json` deserialization. Duplicate object properties are rejected by
a pre-deserialization JSON structure check so last-property-wins behavior cannot
hide conflicting security metadata.

Validation occurs in this order:

1. UTF-8 and JSON structure, including duplicate-property rejection;
2. schema version and versioned JSON Schema;
3. source-generated deserialization;
4. semantic `AppManifestValidator` rules; and
5. build-profile/package cross-reference validation.

Failures use stable machine-readable codes and JSON property paths. First-party
builds and package installation reject the complete manifest; no partial app
descriptor is produced.

### Schema and serialization

Versioned schemas live under
`Shared/HackerOs.App.Abstractions/Schema/` with a stable `$id` and JSON Schema
Draft 2020-12 declaration. Schema fixtures include one complete example for each
app kind and invalid examples for every diagnostic family.

A `System.Text.Json` source-generation context owns runtime serialization. It
uses strict unmapped-member handling and string enums. Reflection-based serializer
fallback is not part of the App SDK contract.

Canonical serialization is compact JSON with properties in the declared schema
order. Collections whose order has no meaning are sorted ordinally by their
stable key before writing, including capabilities, dependencies, settings keys,
intent IDs, asset paths, and localization cultures. Collections with declared
priority retain explicit numeric priority and use stable keys as tie-breakers.
Canonical output ends with one LF byte. This representation is used for hashes,
fixtures, generated resources, and package comparison.

### Presentation and localization

`name` and `description` remain required invariant fallback text. Presentation
also declares category, launch visibility, and icon asset references.

Localization entries map a normalized culture name to a package-relative JSON
resource file. Resource files contain translated manifest labels/help only; they
do not replace the canonical manifest, add capabilities, change entry points, or
alter lifecycle/policy metadata. Missing cultures or keys fall back to the
manifest values.

### Assets

Assets declare package-relative path, kind, SHA-256 integrity value, and optional
logical role. Kinds cover images/icons, collocated CSS, collocated JavaScript
modules, localization resources, and approved data assets. Build validation
ensures every declared file exists, every owned static asset is declared, hashes
match, and excluded app assets do not enter publish output.

Collocated Razor CSS/JavaScript rules remain enforced independently. A manifest
asset declaration never permits inline CSS/JavaScript or arbitrary external URLs.

### Settings, intents, lifecycle, resources, and updates

The versioned manifest model expands to include the areas approved in migration
analysis section 7.2:

- singleton/multi-instance and service auto-start lifecycle policy;
- deterministic resource profile declarations;
- settings keys, types, defaults, scopes, constraints, sensitivity, and migration
  version;
- supported typed intent IDs and payload schema references;
- complete file-handler actions and priorities;
- terminal help, aliases, and stream behavior;
- required and optional dependencies;
- optional OS compatibility bounds; and
- data migration identifiers and package upgrade rules.

App authority and permission grants are never manifest fields. Manifests request
known exact capabilities; trusted build/install/runtime policy grants them.

## Consequences

- One JSON file remains the auditable source of app metadata.
- Strict unknown and duplicate handling prevents silent capability or lifecycle
  ambiguity.
- Source generation supports trimming and avoids unrestricted reflection.
- Canonical bytes support reproducible fixtures, package hashes, and profile
  validation.
- Schema evolution is explicit rather than relying on permissive readers.
- Localization can change user-facing text but not trusted behavior.
- The initial C# record must expand before first-slice app manifests are complete.

## References

- `docs/app-contracts.md`
- `doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md` section 7.2
- JSON Schema Draft 2020-12
- Semantic Versioning 2.0.0