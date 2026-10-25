> [English version](../../../../en/wasm/dev/adr/0011-settings-scope-layout.md)

# ADR 0011: Settings Scope Keys and Projection Paths

## Status

Accepted on 2026-08-01 with the Linux-like `.config` format amendment.

## Context

HackerOS supports app/user, app/device, app/roaming-user, and protected OS/admin
settings. Settings are canonical typed documents and are projected into the
virtual filesystem for Linux-like inspection and editing. Paths must not become
the database key or create duplicate records.

Apps may request scopes in their manifest, but app kind or first-party status
must not grant broader scope or authority. Future sync needs an explicit boundary
between local-only, device-only, roaming, and protected policy records.

## Decision

### Canonical document key

Every settings record uses a structured `SettingsDocumentKey` containing:

- scope;
- stable document ID;
- app ID when app-owned;
- user ID when user-owned; and
- installation ID when device-owned.

The key validates exactly which fields are required or forbidden for its scope.
It is the repository identity and future sync partition. Filesystem paths are
deterministic projections derived from the key, never parsed as the only source
of scope/ownership authority.

IDs are normalized and compared ordinally. App IDs follow the canonical manifest
rules. User, installation, and document IDs must be non-empty path-safe identifiers
that cannot contain `/`, `\`, control characters, `.` or `..` segments.

### Scope layout

| Scope | Structured partition | Projection path | Default sync |
| --- | --- | --- | --- |
| App + user | app ID + user ID + document ID | `/home/{userId}/.config/apps/{appId}/{documentId}.config` | Local only |
| App + device | app ID + installation ID + document ID | `/var/lib/hackeros/devices/{installationId}/apps/{appId}/{documentId}.config` | Never |
| App + roaming user | app ID + user ID + document ID | `/home/{userId}/.config/apps/{appId}/roaming/{documentId}.config` | Eligible |
| OS global/admin | OS namespace + installation ID + document ID | `/etc/hackeros/{documentId}.config` or a registered protected subpath | Protected policy only |

The default document ID is `settings`. File associations retain their existing
registered path `/etc/hackeros/file-associations.json`. Protected app policy uses
registered paths under `/etc/hackeros/apps/`.

User-level HackerOS preferences that are not owned by an app use a registered OS
user definition under `/home/{userId}/.config/hackeros/`; they do not impersonate
an app scope.

### Linux-like `.config` format

Ordinary scoped settings projections use a UTF-8, line-oriented `.config`
format with this deliberately small initial grammar:

```text
# Comment line
schemaVersion=1
key=value

[GroupName]
grouped-key=value
```

- `#` begins a comment when it is the first non-whitespace character on a line.
- `[GroupName]` selects an optional group for following keys. Group names and
  keys are stable, case-sensitive identifiers declared by the settings schema.
- Ungrouped keys before the first section belong to the document root.
- `key=value` splits on the first unescaped `=`. Whitespace around keys and
  section names is ignored; value whitespace is preserved.
- Blank lines and comment lines are valid. Duplicate root/group key pairs,
  duplicate sections, malformed headers, unknown keys, and invalid typed values
  reject the complete write atomically.
- `\#`, `\=`, `\\`, `\n`, `\r`, and `\t` are supported value escapes.
  Multiline values and inline trailing comments are deferred.
- Filesystem edits preserve accepted source comments and ordering. Schema-driven
  UI writes emit deterministic section/key ordering and retain comments associated
  with known sections/keys when parsed source is available.

The settings service owns parsing and serialization. Apps receive typed values
and never parse protected settings themselves.

Registered documents may use another schema-owned format when the product
contract requires it. `/etc/hackeros/file-associations.json` remains strict JSON
because its exact path and whole-document JSON schema are already approved and
implemented. This exception does not make JSON the default app settings format.

### Declarations and authorization

Each manifest setting declaration names a stable key, data type, default value,
scope, constraints, sensitivity, and schema/migration version. Runtime code may
access only declared keys and cannot request a broader scope than the manifest.

Policy grants are explicit per app and scope:

- app/user is the default app preference scope;
- app/device requires a manifest declaration and trusted scope grant;
- roaming requires declaration, sync eligibility, and exact settings/sync
  capabilities when sync exists; and
- OS/admin requires protected policy, exact system-settings capability, and
  Administrator or explicit audited System authority.

App kind alone grants nothing. A system app operated by a normal User retains
User authority.

Direct settings and filesystem projection operations use the same key,
authorization, validator, revision, and audit path.

### Documents and revisions

One canonical document contains all declared settings for the same app, scope,
owner partition, and document ID. Writes replace the complete validated document
atomically using an expected revision. Successful writes increment the canonical
revision once and publish one change/audit event.

Derived indexes and filesystem metadata are rebuildable. They do not own an
independent revision.

### Schema migration

Every document contains `schemaVersion`. App manifests declare the current
migration version and ordered migration identifiers. Migrations:

1. run through the settings service under an explicit trusted operation context;
2. process one version step at a time;
3. validate the complete candidate after each step;
4. commit the final document and schema version atomically with an expected
   revision; and
5. leave the prior document unchanged on failure or cancellation.

Downgrade is unsupported unless an explicit reverse migration exists. Browser
persistence later supplies recoverable migration snapshots according to its
storage ADR.

### Sensitivity and redaction

Settings declare one of these sensitivity classes:

- `public`: safe in projections, diagnostics, export, and eligible sync;
- `private`: visible to the owning authorized user/app but redacted from ordinary
  logs and diagnostics;
- `secretReference`: projection contains an opaque reference or fixed redacted
  marker, never secret bytes; and
- `restricted`: excluded from filesystem projection/export/sync unless a later
  security decision explicitly permits it.

Writing a redacted marker never erases or replaces an existing secret reference.
The browser app does not store server credentials or private keys in ordinary
settings documents.

### Sync eligibility

Only app/roaming-user records are sync-eligible by default. App/user and
OS-user preferences remain local unless moved to a declared roaming scope.
App/device never roams. OS/admin policy does not use client last-writer-wins and
requires the Phase 5 policy conflict decision before synchronization.

## Consequences

- Repository identity remains stable if projection paths evolve.
- Scope ownership and sync eligibility are explicit and testable.
- One revision serves settings UI, text editors, and filesystem access.
- Human-edited app settings support comments and optional grouped sections
  without adding a second storage representation.
- Device preferences cannot leak into roaming data accidentally.
- Sensitive values have defined projection/log/export behavior.
- The current single-path settings contracts must expand to structured keys and
  schema declarations.

## References

- ADR 0002: Authority Comes from Trusted Policy
- ADR 0003: Exact Capability Matching
- ADR 0004: Settings Files Are Canonical Projections
- `docs/settings-system.md`
- `doc/fr/wasm/dev/plans/wasm-v3-migration-analyse.md` section 10