> [Version française](../../../fr/wasm/framework/settings-files.md)

# Settings as files

Mobile home uses `~/Desktop/.mobile/layout.conf` (text sections and keys,
ADR 0166; read migration from the old JSON) for pages
and the dock. The layered OS document `mobile.config` exposes `mouseHelpers`
(default `true`) to Settings through `HackSettings.OpenSystemAsync(AppContext, "mobile")`.
See [mobile-home-screen.md](../dev/mobile-home-screen.md) and ADR 0165/0166.
`OsSettingsFile` can now use `OsSettingsLocation.UserHome` to
resolve a path under the session's home folder; access remains
that of this user, as for `UserConfig` and `UserState`.

Implementation of section 5.2 of the [rebuild plan](../dev/plans/ecosystem-rebuild-plan.md)
following [ADR 0044](../dev/adr/0044-settings-as-files.md). This document describes the code
delivered; the ADR remains the reference for decisions.

## Goal

App and OS settings are real text files of the simulated file
system. The Settings app, a third-party app and `nano /etc/...` read and
modify the same files, and only the file's permissions decide who can
write. The `settings` IndexedDB store is only used by the
migration, read-only.

## Locations

| Content | System layer | User layer |
| --- | --- | --- |
| An app's settings | `/etc/<appId>/<document>.config` | `~/.config/<appId>/<document>.config` |
| OS settings (appearance, interface, Start menu) | `/etc/hackeros/<document>.config` | `~/.config/hackeros/<document>.config` |
| Policy (`policy.config`) | `/etc/hackeros/policy.config` | none (`SystemOnly` document) |
| Dangerous capability policy (`security.config`, ADR 0049) | `/etc/hackeros/security.config` | none (`SystemOnly` document) |
| OOM killer (`[oom-killer]`: `enabled=false`, `min-free-percent=5`, ADR 0126, see [`oom-killer.md`](../dev/oom-killer.md)) | `/etc/hackeros/memory.config` | none (system layer only) |
| Provider of the file control and file dialogs (`[providers]`, key = contract, value = app; absent = explorer; ADR 0156 and 0158, see [`embedded-components.md`](embedded-components.md)) | `/etc/hackeros/components.config` (read line by line, no schema) | none |
| Entries that apps add to file menus (one JSON per app, written by the OS at startup and on each install change; ADR 0151, 0159) | `/etc/hackeros/context-menus.d/<appId>.json` | none |
| File menu entries hidden by the user (`[hidden]`, `<appId>.<name>=true`; Settings ▸ File menus; ADR 0159 §8) | `~/.config/hackeros/context-menus.config` (read line by line each time a menu opens) | none |
| Dangerous capability decisions (ADR 0049) | `/etc/hackeros/app-permissions.config` (`allow`/`deny`) | `~/.config/hackeros/app-permissions.config` (`deny` only) |
| Accessibility (`highContrast`, `textScale`, `reducedMotion`, ADR 0105, see [`accessibility.md`](../dev/accessibility.md)) | `/etc/hackeros/accessibility.config` (also read by the sign-in screen) | `~/.config/hackeros/accessibility.config` |
| Date and time (`timeZone`: `auto` or an IANA zone, ADR 0193, see [`time-zone.md`](time-zone.md)) | `/etc/hackeros/datetime.config` (also read by the sign-in screen) | `~/.config/hackeros/datetime.config` |
| File associations | `/etc/hackeros/file-associations.json` | `~/.config/hackeros/file-associations.json` (same schema, read before the machine file, ADR 0121, [`user-file-associations.md`](../dev/user-file-associations.md)) |
| Window manager shortcuts (`[linux]`, `[fallback]`, ADR 0095, see [`window-model.md`](window-model.md)) | `/etc/hackeros/keybindings.config` | `~/.config/hackeros/keybindings.config` |
| "Not responding" box (`[unresponsive] dialog`, ADR 0127 §3, see [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md)) | `/etc/hackeros/window-manager.config` | `~/.config/hackeros/window-manager.config` |
| Window size (`width`, `height`, `maximized`, ADR 0116 and 0122, see [`window-geometry.md`](../dev/window-geometry.md)) | none | `~/.local/state/hackeros/windows/<appId>.config` |
| Uninstalled apps (ADR 0048, see [`app-installation.md`](../dev/app-installation.md)) | `/etc/hackeros/apps.config` | `~/.config/hackeros/apps.config` |
| Clipboard sharing with the host (key `sharing`, ADR 0065, see [`clipboard.md`](../dev/clipboard.md)) | `/etc/hackeros/clipboard.config` | `~/.config/hackeros/clipboard.config` |
| Periodic sync (ADR 0101 §4, see [`synchronization.md`](../dev/synchronization.md)) | `/etc/hackeros/sync.config` (`intervalMinutes`, read only here) | `~/.config/hackeros/sync.config` (`paused`) |
| Pending sync conflicts (ADR 0101 §2) | none | `~/.local/state/hackeros/sync-conflicts.json` |
| Desktop (`[view]` size, rearrangement, sort, system icons; `[positions]` cell of each icon; read and written by `HackDesktopSettings`, no schema; ADR 0152, 0160, see [`desktop.md`](../dev/desktop.md)) | none | `~/.config/hackeros/desktop.config` |

`~/.local/state/` holds device-specific state: file system
synchronization ignores it (`IFileSystemSyncService.IsDeviceLocal`).

On first startup, `/etc/hackeros/` receives commented default files
(mode `0644`, owner `root`). Since these files list all the
keys, the displayed origin of an OS value is "system" and not
"built-in" as long as nobody has changed anything.

### Sensitive keys (ADR 0100)

A document that declares non-public keys (`SettingSensitivity` `Private`,
`SecretReference` or `Restricted`) puts these keys in a companion file
`<doc>.private.config` in mode `0600`, next to `<doc>.config`, in each
layer. When the caller cannot read the private file, the key is returned
"masked" instead of making the read fail. Secrets go in the sign-in
keyring. OS documents only accept public keys. Details: [`sensitive-settings.md`](../dev/sensitive-settings.md).

## Architecture

| Layer | Type | Role |
| --- | --- | --- |
| Simulation.Abstractions | `ConfigDocumentLines`, `ConfigDocumentEditor` | Line-by-line reading of the `.config` format (ADR 0011) and writing that only touches one line, keeping comments, empty lines and unknown keys. |
| Simulation.Abstractions | `LayeredConfigResolver`, `LayeredConfigSnapshot` | Layer merging, locks, `settings: <path>:<line>: <message>` warnings. |
| Simulation.Abstractions | `SettingsTextFile`, `SettingsFilesWatcher` | Reading, writing (creating missing folders) and watching the two files of a document through a file gateway. |
| Simulation.Abstractions | `ILegacySettingsStore`, `LegacySettingsRecord` | Contract for reading the old store, for the migration. |
| AppSdk | `HackSettings`, `HackSettingsDocument` | Typed API for apps. |
| AppSdk.Blazor | `SettingOriginBadge` | Reusable badge "yours", "system default", "built-in", "locked by admin". |
| Platform.Core | `IOsSettingsService`, `OsSettingsService` | OS documents (`OsSettingsDocument`) and whole files (`OsSettingsFile`), for the current session. |
| Platform.Core | `SettingsStoreToFilesMigration` | One-time migration from IndexedDB, then default files. |
| Infrastructure.Browser | `IndexedDbLegacySettingsStore` | `getAll` read of the `settings` store, without changing the schema version. |
| Platform.Blazor | `WindowGeometryPersistence`, `WindowGeometryRecorder` | Reads and writes window size and maximized state (ADR 0116, ADR 0122): written by the recorder on `WindowRuntime` facts, reread by `WindowLaunchCoordinator` on opening. |

### Layer order

For each declared key:

1. key locked in the `[locked]` section of the system file: system value,
   otherwise default value;
2. otherwise valid user value;
3. otherwise valid system value;
4. otherwise the schema's default value.

`[locked]` is only read in the system file; in a user file
the section is ignored with a warning. `locked` is therefore a reserved group
name, refused by the schema and the manifest validator.

### Tolerant parsing

A malformed line, an unknown key or an invalid value is ignored; the
next layer applies. Each problem gives a warning sent to
`IAppLoggingGateway` (for an app) or to `ILogger` (for the OS), only once
per file revision. Writing to `/var/log` belongs to the logs work item
(ADR 0045).

### Permissions

| Operation | App capability | File permissions |
| --- | --- | --- |
| Read `~/.config/<appId>/` | `filesystem.private.read` | those of the user |
| Write `~/.config/<appId>/` | `filesystem.private.write` | those of the user |
| Read `/etc/<appId>/` | `filesystem.private.read` | `0644`: readable by all |
| Write `/etc/...` | `filesystem.system.write` | Administrator authority, mode bits, ADR 0042 approval ticket consumed on each call |

Another app has no access to an app's private folder `~/.config/<appId>/`.

## Usage

### In an app

The schema comes from `AppManifest.Settings` (keys, types, default values,
groups). Each method exists with the app's context as a parameter, and without
it: it then uses the ambient context of ADR 0043
(`HackExecutionContext.Current`), like the `Hack*` file API.

```csharp
await using HackSettingsDocument settings = await HackSettings.OpenAsync(AppContext);
EditorTheme theme = settings.Get<EditorTheme>("theme");      // enum : "high-contrast" <-> HighContrast
bool locked = settings.IsLocked("autosave");
SettingLayer source = settings.GetSource("theme");          // Default, System ou User
settings.Changed += (_, e) => Refresh(e.ChangedKeys);       // édition externe (nano, autre fenêtre)
await settings.SetAsync("Editor.tab-size", 2);              // couche utilisateur
await settings.ResetAsync("theme");                         // retire la valeur utilisateur

int size = await HackSettings.GetAsync<int>(AppContext, "Editor.tab-size"); // lecture ponctuelle
int same = await HackSettings.GetAsync<int>("Editor.tab-size");             // contexte ambiant
```

- An undeclared key or an incompatible type throws `ArgumentException`.
- Writing a locked key throws `HackSettingLockedException` (with the path
  of the system file).
- A file error is translated into `UnauthorizedAccessException`,
  `FileNotFoundException` or `IOException`.
- `SetAsync(key, value, SettingLayer.System)` writes `/etc/<appId>/` if the app has
  the capability and the user has the file's permissions.

Several keys of the same layer are written in a single file edit
with `SetManyAsync(values, layer)` (a single approval for `/etc`).

#### OS documents (`HackSettings.OpenSystemAsync`, ADR 0056 §8)

```csharp
await using HackSettingsDocument appearance = await HackSettings.OpenSystemAsync(AppContext, "appearance");
string accent = appearance.Get<string>("accent");
await appearance.SetAsync("accent", "cyan");                       // ~/.config/hackeros/appearance.config
await appearance.SetManyAsync(values, SettingLayer.System);        // /etc/hackeros : sudo ou approbation
```

- Documents opened: `appearance`, `accessibility` (ADR 0105), `ui-platform-preference`, `security`,
  `policy`, `clipboard` (ADR 0065), `screens` (ADR 0179 §7, Settings ▸ Screens) (`OsSettingsDocumentRegistry`, Platform Core). Any other id throws
  `ArgumentException`.
- In `appearance`, `desktopThemeId` and `mobileThemeId` are free strings
  since `v3/11` (ADR 0103): the id of a built-in theme or of a `.theme`
  file in `/etc/hackeros/themes/` or `~/.config/hackeros/themes/`; an unknown
  id gives the default theme (see [`theme-editor.md`](../dev/theme-editor.md)).
- In `appearance`, `wallpaper`, `wallpaperFit` and `wallpaperScreen2` to
  `wallpaperScreen9` choose the wallpaper (ADR 0202, see
  [`wallpapers.md`](../dev/wallpapers.md)).
- No capability: file permissions decide (D16). The user layer
  is written by the OS on behalf of the session's user; the system
  layer with the file permissions of the calling app, hence `sudo`
  or the approval of ADR 0042.
- `Changed` follows `DocumentChanged` of `IOsSettingsService`, in the app's
  ambient context.

`IAppExecutionContext.Settings` (the old `IAppSettingsGateway` gateway)
is removed; `IAppExecutionContext.HomeDirectory` gives the home folder.

### In the OS

`IOsSettingsService` reads an `OsSettingsDocument` (`ReadAsync` gives a
`LayeredConfigSnapshot`), writes one or several keys in a single edit
(`SetAsync`, `SetManyAsync`), removes a key (`ResetAsync`), reads and writes whole
files (`ReadFileAsync`, `ReadValidatedFileAsync`, `WriteFileAsync`)
and publishes `DocumentChanged`. A system write passes the calling app's `writer`,
so that its permissions apply. OS documents are
declared in `AppearanceSettingsDocuments`, `UiPlatformPreferenceSettingsDocuments`,
`StartMenuSettingsDocuments`, `PolicySettingsDocuments` and
`FileAssociationSettingsDocuments`.

### Settings app

The app offers two targets: "Only me" (user layer) and "All
users (system default)". Each setting displays its origin; a locked
setting is disabled for the user target. Writing the system layer
requires administrator approval; a refusal displays a message instead of an
error. Appearance goes through `HackSettings.OpenSystemAsync`
(work item `4-sdk-system-admin`). "Synchronize" pushes and pulls the file
system, pulls permissions, pushes and pulls associations, then rereads
appearance. The "Synchronization" tab (ADR 0101) shows the state of
periodic sync, suspends it for the user (`paused` in
`~/.config/hackeros/sync.config`), launches a run and lists pending
conflicts with a button that opens the conflict dialog. The "Installed Apps" tab writes the `apps.config` files
(see [`app-installation.md`](../dev/app-installation.md)).

The "Language" tab (ADR 0104, `LanguagePanel`) does not use `HackSettings`:
the locale is a systemd file. It shows the language, `LANG`, the
`LC_MESSAGES`/`LC_ALL` overrides and an example of the session's formats (read
from the Settings process environment), then offers "Follow the system
language" or one of the `HackLocale.AvailableLocales`. Choosing a locale writes
`LANG=<locale>` in `~/.config/locale.conf` (`HackLocale.FormatLocaleConf`,
`~/.config` created if needed); "Follow the system language" removes `LANG` and
deletes the file if it contains nothing else (a `LC_TIME` is kept).
The change applies at the next sign-in, which the panel displays.
`/etc/locale.conf` is shown read-only with the
`sudo localectl set-locale` hint. The app declares for this
`filesystem.user-home.read` and `filesystem.user-home.write`. There is no
"Sign out" button: the SDK does not expose sign-out to apps.
Tests: `Tests/HackerOs.Apps.Settings.Tests/LanguagePanelTests.cs`.

## Migration

`SettingsStoreToFilesMigration` reads the old store only once:

- at startup, the OS documents (`OsAdmin`) become files in
  `/etc/hackeros/`. An invalid document is replaced by the default
  value. The marker `/var/lib/hackeros/migrations/settings-to-files` prevents
  a second copy, so the administrator's changes survive
  restarts;
- at sign-in, only the signed-in user's values are copied:
  app settings to `~/.config/<appId>/`, Start menu pins to
  `~/.config/hackeros/start-menu.json`, geometry to `~/.local/state/`. The
  old `~/.config/apps/<appId>` folders are moved to
  `~/.config/<appId>`. The marker
  `~/.local/state/hackeros/migrations/settings-to-files` avoids overwriting what
  the user has changed since.

A store read error is logged and prevents neither startup
nor sign-in. The IndexedDB schema does not change.

## Implementation decisions

- A multi-key write (`SetManyAsync`) is done in a single file
  edit, so that an observer never sees a half-written state.
- An app can create `~/.config/<appId>/` without read permission on
  `~/.config`: if inspection of a parent folder is refused, it is assumed to
  exist and the creation of the final folder decides.
- Settings synchronization (`ISettingsSyncService`) disappears: the
  files are synchronized with the rest of the file system (ADR 0044
  replaces ADR 0029).
- Cost of a write: settings go through the file system like
  any file. Since ADR 0090, each path is looked up in one request to
  IndexedDB; saving the Mobile/Desktop choice costs 51 transactions
  (235 ms) instead of 604 (845 ms). See
  [`filesystem-path-lookup.md`](../dev/filesystem-path-lookup.md).

## Contract tests

In `Tests/HackerOs.Platform.Core.Tests/Settings/`:

- `LayeredConfigResolverTests`: layers, locks, tolerant parsing, warning
  text, reserved group;
- `ConfigDocumentEditorTests`: writing that preserves human text;
- `HackSettingsTests`: SDK API on the real file system, owner
  and mode, lock, reset, warnings, permissions on `/etc`,
  isolation between apps, change notification;
- `SettingsStoreToFilesMigrationTests`: default files, one-time copy,
  per-user copy, unreadable store.
- `SensitiveSettingsTests`: private files, masking, misplaced keys,
  `Restricted`, secrets in the keyring (ADR 0100).

## Task list

- [x] Layer engine, locks and tolerant parsing
- [x] Typed `HackSettings` API in the SDK
- [x] `IOsSettingsService` service and OS documents as files
- [x] Window geometry in `~/.local/state`, excluded from synchronization
- [x] Migration from the IndexedDB store
- [x] Settings app: layer target, origin and lock per setting
- [x] Removal of `IAppSettingsGateway`, of the projection and of settings synchronization
- [x] Contract tests
