# Canonical Settings System

## Purpose

Provide Linux-like, human-editable settings files without creating two sources
of truth. The settings service owns canonical typed documents, while the virtual
filesystem projects those same records at paths such as
`/etc/hackeros/file-associations.json`.

## Architecture

`HackerOs.Simulation.Abstractions` defines settings documents, read/write
results, whole-document validators, optimistic revisions, the canonical service,
and filesystem projection interfaces.

`HackerOs.Platform.Core` currently provides:

- `InMemorySettingsDocumentService`, a headless reference implementation;
- `SettingsFileProjection`, which delegates file operations to the canonical
  service rather than copying settings; and
- strict validators/codecs for file associations, appearance, UI-platform
  preference, and Start-menu profiles.

`HackerOs.Infrastructure.Browser` supplies the production
`IndexedDbSettingsDocumentService` behind those same contracts without changing
authorization or projection semantics.

ADR 0011 defines structured settings keys for app/user, app/device,
app/user/device, app/roaming-user, and OS/admin documents. Filesystem paths remain deterministic
projections, while the structured key owns scope, app, user, installation, and
document identity.

Default app paths are:

```text
/home/{userId}/.config/apps/{appId}/settings.config
/home/{userId}/.config/apps/{appId}/roaming/settings.config
/var/lib/hackeros/devices/{installationId}/apps/{appId}/settings.config
/var/lib/hackeros/devices/{installationId}/users/{userId}/apps/{appId}/settings.config
```

Protected documents remain under `/etc/hackeros/`, including file associations,
schema-v2 appearance (`appearance.json`), and the device-local aggregate of
per-user ordered Start-menu pins (`start-menu.json`). See
[`theming.md`](theming.md) and [`start-menu.md`](start-menu.md) for their exact
schemas. Per ADR 0029, sync eligibility is an explicit
`SyncEligible` flag on `SettingsDocumentDefinition`, not inferred from scope —
`AppRoamingUser` remains the natural scope for a document a user expects to
follow them, but any document may opt in regardless of scope, and none does
so implicitly. See `docs/adr/0029-settings-sync.md` and
`docs/server-implementation-pass.md` for what is actually wired up today
(`AppearanceSettingsDocuments` is sync eligible; Start-menu pins deliberately
are not).

Ordinary `.config` documents accept blank lines, `#` comment lines, root
`key=value` pairs, and optional `[GroupName]` sections. The settings schema owns
valid groups, keys, types, defaults, and migrations. Malformed syntax, duplicate
sections or keys, unknown keys, and invalid values reject the complete write.
Registered `/etc/hackeros/*.json` documents remain explicit strict-JSON
exceptions.

## Authorization

Every read or write requires both:

1. the exact capability configured by the document definition; and
2. sufficient effective authority under `System > Administrator > User`.

Normal user-driven system-app UI uses the user's authority. Only explicit,
audited OS work sets `IsSystemOperation`, which raises effective authority to
System. System operations still require the exact capability and never bypass
policy.

For the file-association document:

- normal users may read only when granted `file-associations.read`;
- writes require `file-associations.write`; and
- writes additionally require Administrator or System authority.

Appearance remains an ordinary user preference: an authorized Settings app may
read/write it at User authority. The shell has a privileged read-only
`ThemePreferenceService` projection so rendering cannot mutate appearance.
The aggregate Start-menu document requires System authority for both directions;
`StartMenuPreferencesService` mediates access using the active opaque
`LocalUserId`, preventing one user from editing another profile.

## Filesystem usage

An editor first reads a `SettingsDocumentSnapshot`, including its revision. It
then writes a complete candidate document and the expected revision through
`ISettingsFileProjection.WriteFileAsync`.

The write is committed only when:

- capability and authority checks pass;
- the expected revision is current; and
- whole-document syntax and schema validation passes.

Invalid or conflicting writes leave the prior content and revision untouched. A
successful write increments the revision and emits an audited change event with
app ID, user ID, and effective authority.

## Key decisions

- Canonical settings are not duplicated into ordinary file records.
- File reads and writes use the same settings service as settings UI.
- Settings files are complete human-editable `.config` documents by default;
  registered protected schemas may define an explicit format such as association
  JSON.
- Optimistic revisions prevent silent overwrite of concurrent edits.
- The capability check is never bypassed, including for System authority.
- IndexedDB remains an infrastructure detail behind the canonical contract.
- Existing schema-v1 appearance JSON is decoded without data loss and every new
  write uses schema v2 with independent desktop/mobile theme IDs.
- Start-menu pins persist only stable app IDs and preserve unavailable IDs; live
  catalog filtering is a shell concern, not a settings migration.

## Task list

- [x] Define canonical document and projection contracts.
- [x] Enforce capability plus authority on reads and writes.
- [x] Validate initial settings at service construction.
- [x] Validate complete file-association documents before replacement.
- [x] Add revision conflict detection.
- [x] Emit audited change events after successful commit.
- [x] Test direct service and filesystem projection consistency.
- [x] Define app/user, app/device, and roaming path definitions.
- [x] Define local app/user/device scope for per-device user window geometry.
- [x] Add schema-driven app settings validators.
- [x] Add a persistent IndexedDB repository implementation (`IndexedDbSettingsDocumentService`).
- [x] Add appearance schema-v2 migration and the boot-time read projection.
- [x] Add a strict, ordered, per-user Start-menu pin document and mutation service.
- [ ] Add a full virtual filesystem router that mounts the settings projection.
