# ADR 0044: Settings as files, layered `/etc` then `~/.config`

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 1, Files ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 5.2, decisions D16 to D18).
- **Replaces:** ADR 0004 entirely. ADR 0011 in part: replaced are the
  canonical document outside files, the `SettingsDocumentKey` key as
  identity, the table of scopes and paths, and the atomic rejection of an invalid
  write. The `.config` grammar is kept. ADR 0029 entirely: the "Settings"
  synchronization domain is removed.
- **Complements:** ADR 0030 (exclusion of `~/.local/state/`).
- **Default values retained** (plan, section 11): administrator lock through a
  `[locked]` section.

## Context

Today, settings live in the `settings` IndexedDB store
(`IndexedDbSettingsDocumentService`). The `/etc/hackeros` mount that was meant to project them as files
(`SettingsFileSystemProvider`, `SettingsFileProjection`)
is never wired into the real OS: `FileSystemMountRouter` is created with the
IndexedDB provider alone (`OS/HackerOs.Ecosystem/EcosystemServiceCollectionExtensions.cs`).
`nano /etc/hackeros/...` therefore sees nothing. Security comes from the settings
service (`settings.*` capabilities, minimum authority per document), not from file
permissions.

The plan wants the opposite (D16): the file is the only source of truth, and its
permissions decide access.

## Decision

### 1. The file is the only source of truth

- A setting is a line in an ordinary file, stored by the file system's
  IndexedDB provider like any other file. There is no special
  mount and no projection.
- The `settings` store, `ISettingsDocumentService`,
  `IndexedDbSettingsDocumentService`, `InMemorySettingsDocumentService`,
  `SettingsFileProjection`, `SettingsFileSystemProvider`, `IAppSettingsGateway`
  (and `IAppExecutionContext.Settings`), `SettingsDocumentDefinition` and
  `SettingsDocumentKey` are **removed** from the execution path. Only exception:
  the migration (section 8) still reads the store.
- **Access is controlled only by file permissions**, through the file
  gateway: `filesystem.*` capabilities depending on the path, authority, mode
  bits, elevation tickets of ADR 0042. The `settings.read`,
  `settings.write`, `settings.system.read` and `settings.system.write` capabilities are no longer
  evaluated anywhere. They stay in the `AppCapabilities` catalog until
  the phase 2 ADR on dangerous permissions, which will decide on their removal.

### 2. Locations

`<appId>` is the canonical manifest identifier (for example
`org.hackeros.text-editor`). `hackeros` is reserved for the OS. `<document>` is
`settings` by default.

| Layer | Path | Owner and mode |
|---|---|---|
| System (app) | `/etc/<appId>/<document>.config` | folder `system:system 0755`, file `system:system 0644` |
| System (OS) | `/etc/hackeros/<document>.config` | same |
| User (app) | `~/.config/<appId>/<document>.config` | folder `<user>:<group> 0755`, file `0644` (umask of ADR 0043, protected by `~` at `0700`) |
| User (OS) | `~/.config/hackeros/<document>.config` | same |
| Local state, not synchronized | `~/.local/state/hackeros/...` | same |

Consequences for capability resolution in `FileSystemService.ResolveCapability`
(`Platform/HackerOs.Platform.Core/FileSystem/FileSystemService.cs`):

- `~/.config/<appId>/` (read and write) corresponds to
  `filesystem.private.read/write` of that app. It replaces the current
  `~/.config/apps/<appId>/`, which is moved by the migration.
- `/etc/<appId>/` for **reading** corresponds to `filesystem.private.read` of that
  app: an app reads its own system layer without the broad
  `filesystem.system.read` capability. **Writing** remains `filesystem.system.write`, with
  Administrator authority and mode bits. In practice, it requires `sudo` or an
  approval ticket from ADR 0042, since the file belongs to `system`.
- Other paths do not change.

`HackSettings` **never** creates a file in `/etc`. A missing system file is an
empty layer. The `~/.config/<appId>/` folder and the user file are created on the first write.

### 3. Layers and lock (D18)

The effective value of a key = the first one found in this order:

1. key **locked** in the system layer → system value (or default
   value if it has no system value); the user layer is ignored
   for this key;
2. user layer `~/.config/<appId>/<document>.config`;
3. system layer `/etc/<appId>/<document>.config`;
4. default value declared by the app.

**Lock syntax (value retained, plan section 11):** a reserved section
`[locked]` in the **system** file. Each line there names a qualified key
(`key` or `Group.key`, like `SettingFieldDeclaration.QualifiedKey`) with the
value `true`:

```text
# /etc/org.hackeros.text-editor/settings.config
theme=dark

[Editor]
tab-size=4

[locked]
theme=true
Editor.tab-size=true
```

- `false` or the absence of the key: no lock. Any other value is
  invalid: ignored, with a warning.
- A `[locked]` section in a **user** file is ignored, with a
  warning.
- `locked` becomes a reserved group name. An app schema that declares it is
  rejected by `AppManifestValidator`.

### 4. Format

The `.config` grammar of ADR 0011 is kept as is (`#`
comment, `[Group]`, `key=value`, escapes `\#`, `\=`, `\\`, `\n`, `\r`,
`\t`), as is the `ConfigDocumentFormat` parser
(`Shared/HackerOs.Simulation.Abstractions/Settings/`). `schemaVersion` remains an
optional root key.

### 5. Invalid file (D17)

- **Writing is always accepted**, whether through `nano`, the Settings app
  or `HackFile`: it is an ordinary file write. There is no longer any
  validation on write.
- **On read**, each problem is handled line by line, and never by
  rejecting the whole document:
  - malformed line: ignored;
  - undeclared key: ignored, but kept in the file;
  - value that does not respect its type or its allowed values: ignored, and the
    next layer applies;
  - duplicate key: the last one wins.
- Each problem produces **one** warning per file and per revision, in
  `/var/log/apps/<appId>.log` (or `/var/log/syslog` for `hackeros`), according to
  ADR 0045, in the format:
  `settings: /etc/x/settings.config:12: invalid value for 'tab-size' (expected int64), using default`.

### 6. Common merge engine

A pure function, shared by the SDK and the platform, in
`Shared/HackerOs.Simulation.Abstractions/Settings/LayeredConfigResolver.cs`:

```csharp
public static class LayeredConfigResolver
{
    public static LayeredConfigSnapshot Resolve(
        SettingsSchema schema,
        string? systemContent,   // null = missing file
        string? userContent,
        string systemPath,
        string userPath);
}

public sealed record LayeredConfigSnapshot(
    IReadOnlyDictionary<string, string> Values,          // qualified key -> serialized effective value
    IReadOnlyDictionary<string, SettingLayer> Sources,
    IReadOnlySet<string> LockedKeys,
    IReadOnlyList<LayeredConfigWarning> Warnings);

public enum SettingLayer { Default, System, User }
public sealed record LayeredConfigWarning(string Path, int LineNumber, string QualifiedKey, string Message);
```

The existing `SettingsSchema` and `SettingFieldDeclaration` remain the schema
model (types `String`, `Integer`, `Boolean`, `Enum`).

### 7. Typed `HackSettings` API (SDK)

Folder `Shared/HackerOs.AppSdk/Settings/`, public namespace
**`HackerOs.AppSdk.Settings`**. It builds on the `Hack*` API of ADR 0043 and
on the ambient context, so it goes through the file gateway. The schema
comes from `AppManifest.Settings` in phase 1, then from the `[AppManifest]` class in
phase 2, with no API change.

```csharp
public static class HackSettings
{
    // Opens the calling app's document (schema = manifest).
    public static ValueTask<HackSettingsDocument> OpenAsync(
        string documentId = "settings", CancellationToken ct = default);

    // Shortcuts on the default document.
    public static ValueTask<T> GetAsync<T>(string qualifiedKey, CancellationToken ct = default);
    public static ValueTask SetAsync<T>(string qualifiedKey, T value, CancellationToken ct = default);
}

public sealed class HackSettingsDocument : IAsyncDisposable
{
    public string SystemPath { get; }          // /etc/<appId>/<doc>.config
    public string UserPath { get; }            // /home/<user>/.config/<appId>/<doc>.config
    public T Get<T>(string qualifiedKey);      // string, long, int, bool, C# enum
    public SettingLayer GetSource(string qualifiedKey);
    public bool IsLocked(string qualifiedKey);
    public IReadOnlyList<LayeredConfigWarning> Warnings { get; }
    public ValueTask SetAsync<T>(string qualifiedKey, T value,
        SettingLayer target = SettingLayer.User, CancellationToken ct = default);
    public ValueTask ResetAsync(string qualifiedKey,
        SettingLayer target = SettingLayer.User, CancellationToken ct = default);
    public event EventHandler<HackSettingsChangedEventArgs>? Changed;
}

public sealed class HackSettingLockedException : InvalidOperationException;
```

- A key **not declared** in the schema throws `ArgumentException`: only declared
  keys are typed, documented and displayable by the Settings app.
- `SetAsync` to the user layer of a locked key throws
  `HackSettingLockedException`. To the system layer, the caller must have the right to
  write `/etc/<appId>/`; otherwise `HackUnauthorizedAccessException`, which
  triggers the administrator approval prompt of ADR 0042.
- Writing modifies the line while preserving comments, order and unknown
  keys (`ConfigDocumentFormat`). It goes through `HackFile` with the single-retry
  rule on conflict (ADR 0043).
- `Changed` is fed by two `HackFileSystemWatcher`s, one on each file.
  A `nano /etc/...` or a change from the Settings app is therefore seen by
  the running app.

### 8. OS settings

Platform services use the same engine through
`IOsSettingsService` (`Platform/HackerOs.Platform.Core/Settings/OsSettingsService.cs`),
which reads and writes the `hackeros` files with the context of the
session user:

| Current document (IndexedDB store) | New file(s) | Layers |
|---|---|---|
| `/etc/hackeros/appearance.json` | `/etc/hackeros/appearance.config` + `~/.config/hackeros/appearance.config` | system + user |
| `/etc/hackeros/ui-platform-preference.json` | `/etc/hackeros/ui-platform-preference.config` + `~/.config/hackeros/ui-platform-preference.config` | system + user |
| `/etc/hackeros/policy.config` (`PolicySettingsDocuments`) | `/etc/hackeros/policy.config` | system only (user layer ignored) |
| `/etc/hackeros/file-associations.json` | `/etc/hackeros/file-associations.json`, strict JSON, whole document | system only; a user layer may come with the phase 2 ADR on apps |
| `/etc/hackeros/start-menu.json` (one `profiles` per user) | `~/.config/hackeros/start-menu.json`, one per user, strict JSON | user only |
| window geometry (`WindowGeometrySettings`, scope `AppUserDevice`) | `~/.local/state/hackeros/windows/<appId>.config` | user only, not synchronized |

The registered JSON documents remain format exceptions, already accepted
by ADR 0011. They are not merged into layers. Their owning service
ignores an invalid file, keeps its default values and writes a
warning, as in section 5.

The `AppDevice`, `AppUserDevice` and `AppRoamingUser` scopes disappear: an
installation is one machine, and `/etc` is its layer.

### 9. Migration on first boot

Service `SettingsStoreToFilesMigration` (`Platform/HackerOs.Platform.Core/Settings/`):

1. **At boot**, after the file system is seeded and before any
   sign-in, with a system context: each `OsAdmin` record of the
   `settings` store is written to its system file (table in
   section 8). Flat JSON documents are converted to `.config`. Appearance
   and UI platform values go into the **system** layer, which
   keeps the current behavior, valid for the whole machine. Marker:
   `/var/lib/hackeros/migrations/settings-to-files` (empty file).
2. **When each user signs in**, after `FileSystemSeeder.SeedAsync`:
   their app documents (`AppUser`, `AppRoamingUser`, `AppUserDevice`) are written
   to `~/.config/<appId>/...`, their `start-menu` profile to
   `~/.config/hackeros/start-menu.json`, their window geometry to
   `~/.local/state/hackeros/windows/`, and the old `~/.config/apps/<appId>/` folder
   is moved to `~/.config/<appId>/`. Marker:
   `~/.local/state/hackeros/migrations/settings-to-files`.
3. A target file that already exists is never overwritten.
4. **The `settings` IndexedDB store is neither deleted nor emptied, and the
   IndexedDB schema version does not change** in this phase (see
   [`old/common-pitfalls.md`](../old/common-pitfalls.md) on IndexedDB
   versioning). Its removal will be handled in a later step.

`FileSystemSeeder` additionally creates `/etc/hackeros` (`0755`), `/var/lib/hackeros`
(`0755`), `~/.local`, `~/.local/state` and `~/.local/state/hackeros` (`0755`). It
no longer creates `~/.config/apps`.

### 10. Synchronization

- The "Settings" synchronization domain of ADR 0029
  (`SettingsSyncService`, `ISettingsSyncService`, `SyncEligible`) is removed.
- The user layer is under `/home/<user>`. It is therefore already
  synchronized by the "FileSystem" domain of ADR 0030, which covers
  `~/.config/hackeros/appearance.config`, the only document synchronized
  so far.
- `/etc` is not synchronized, like the administrator documents of ADR 0029.
- **Complement to ADR 0030:** the synchronization walk excludes
  `/home/<user>/.local/state/` (device-specific state, in the XDG sense).

### 11. Settings app

The Settings app (`Apps/System/HackerOs.Apps.Settings`) reads and writes through
`HackSettings` and `IOsSettingsService`. For each key it shows its
originating layer and a padlock if the key is locked. A system change goes through
the approval prompt of ADR 0042.

## Consequences

- `nano /etc/hackeros/appearance.config` and the Settings app modify the same
  file. No projection can get out of sync.
- A badly edited file no longer breaks anything: the app starts with its default
  values and the log says why. In exchange, a typo is only
  reported at read time.
- An administrator imposes a value on all users with `[locked]`.
- Tests that depend on `ISettingsDocumentService`,
  `SettingsFileSystemProvider` or `SettingsSyncService` are rewritten or
  deleted in the same phase.
- Documents that followed the "device" and "roaming" scopes lose
  that distinction. It had only one real user (window
  geometry), which moves to `~/.local/state`.

## References

- ADR 0004 (replaced), ADR 0011 (partially replaced), ADR 0029 (replaced), ADR 0030 (complemented).
- ADR 0042: administrator approval prompt.
- ADR 0043: `Hack*` API and ambient context.
- ADR 0045: logs as files.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 5.2 and 11.
- [`old/settings-system.md`](../old/settings-system.md): current system.
