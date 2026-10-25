> [Version française](../../../fr/wasm/dev/settings-app.md)

# Settings App (`org.hackeros.settings`)

## Purpose

Describes the system app `Apps/System/HackerOs.Apps.Settings`: the OS control
panel. It owns no data: each tab reads and writes OS files (settings as files,
ADR 0044) or calls an SDK `Hack*` facade. The decisions that shaped it are
ADR 0047 (manifest), ADR 0048 (system app), ADR 0096 (`--app=` arguments),
ADR 0105, 0120, 0121, 0160 and sections 8 to 11 of ADR 0056.

Key files: `SettingsApp.cs` (the `[AppManifest]` class, identifier,
capabilities, resource weights), `SettingsWindow.razor` and
`SettingsWindow.razor.cs` (the window, a `MudTabs`), `SettingsWindow.Mobile.cs`
(mobile preferences), one `*Panel.razor` panel per tab, `Strings.cs` and
`Resources/` (English and French texts). The `manifest.json` is generated at
build time from `SettingsApp.cs` (do not edit it). Tests:
`Tests/HackerOs.Apps.Settings.Tests`.

## Tabs

Order matters: the `--page=` arguments and the index hooks rely on it.

| Tab | Panel | Main content |
|---|---|---|
| Appearance | (in `SettingsWindow.razor`) | Write layer (user or system), desktop and mobile theme, accent, animations, user themes (`/etc/hackeros/themes` and `~/.config/hackeros/themes`, `UserThemeLibrary`); writes `appearance.config`. |
| Desktop | `DesktopPanel` | Desktop options and layout (`desktop.config`, ADR 0160). |
| Accessibility | `AccessibilityPanel` | High contrast, text size, reduced motion (ADR 0105). |
| Language | `LanguagePanel` | Interface language. |
| File associations | `FileAssociationsPanel` | Default applications, system or user layer (`HackFileAssociationLayer`, ADR 0121). |
| Granted permissions | (in the window) | Capabilities granted to the user. |
| Permissions | `AppPermissionsPanel` | Per-app decisions and approvals of dangerous capabilities (ADR 0049). |
| Installed applications | `InstalledAppsPanel` | List of apps and their commands, uninstall and reinstall (ADR 0048). |
| OS components | `OsComponentsPanel` | Provider chosen for each contract (`HackComponentProviders.GetOverviewAsync`, `SetProviderAsync`). |
| File menus | `FileMenuEntriesPanel` | Menu entries registered by apps (ADR 0151). |
| Server connection | `ServerRelayPolicyPanel` (+ the window) | Optional server, network relay (ADR 0102, 0134). Visible in all cases, the state comes from `HackServerConnection`. |
| Synchronization | (in the window) | Sync state and conflicts (ADR 0101). |
| Users | `UsersPanel` (+ `PasskeysPanel`) | Accounts, passwords, adding and deleting accounts by an administrator, session passkeys (ADR 0120, 0168, 0169; see [Users page](#users-page)). |
| Memory | `MemoryPanel` | File `/etc/hackeros/memory.config`, section `[oom-killer]` (ADR 0126). |
| Backup | `BackupPanel` | Present only if `HackSystemBackup.IsSupportedFor` allows it (ADR 0099). |
| About | (in the window) | Authority, reboot (`HackSystem.RebootAsync`), danger zone: factory reset (`HackSystem.FactoryResetAsync`), confirmed by typing `RESET`. |
| Application | `AppDetailsPanel` | Added only with `--app=<id>` (ADR 0096): details and settings of an app. |

## Launch arguments

- `--page=appearance` or `--page=desktop`: opens that tab (the desktop's
  "Change background" and "Desktop settings" use it, ADR 0160 §8).
- `--app=<id>`: opens the "Application" tab of that app; an empty identifier
  is ignored, the last one wins.

An unknown `--page=` value is ignored.

## Data access

| Data | Access | Capability |
|---|---|---|
| `appearance.config`, `mobile` (system documents) | `HackSettings.OpenSystemAsync` | `filesystem.system.*` to write `/etc` |
| Installed apps, default handlers | `HackApps` (`GetInstalledAsync`, `GetDefaultHandlersAsync`, `SetDefaultHandlerAsync`, `ResetDefaultHandlerAsync`), `HackAppInstallInfo` | `apps.launch`, `apps.manage`, `file-associations.read` / `.write` |
| Server | `HackServerConnection` | `server.connection.manage` |
| Processes and services | `HackServiceController`, process list | `process.list`, `services.manage` |
| Backup | `HackSystemBackup` | `system.backup` |
| Reboot, reset | `HackSystem` | `system.power` |
| Permissions, users | `HackPermissionAdministration`, `HackUserAccounts` | `permissions.manage`, `users.manage` |

The two administration operations (reboot, reset) have the OS ask an
administrator for confirmation in its own window; the app never sees the
password ([`admin-authentication-agent.md`](admin-authentication-agent.md)).

## Users page

`UsersPanel` (ADR 0120, 0169) and `PasskeysPanel` (ADR 0168), from top to
bottom:

- **Account list**: login name, display name, badges "Administrator", "You",
  number of passkeys, state `P`/`NP`/`L`. For an administrator, a "Delete" link
  on each account other than their own opens a confirmation with the checkbox
  "Also delete its home folder `/home/<login>`", unchecked by default
  (`HackUserAccounts.DeleteAsync(login, removeHome)`).
- **Add an account** (administrator only): login name, display name, standard
  or administrator type, initial password and confirmation, checked by
  `HackUserAccounts.ValidateNewAccount` before sending
  (`HackUserAccounts.CreateAsync`).
- **My passkeys** (everyone): name, date added and date of last use, "keychain"
  badge if the key opens the keychain (PRF), "Remove" button, and a "Name"
  field + "Add a passkey" that starts the browser prompt. Without WebAuthn
  (insecure page), a note replaces the form. See [`passkeys.md`](passkeys.md).
- **My password**, then for an administrator the **reset** of another
  account's password (ADR 0120).

Adding, deleting and resetting require `users.manage` and the confirmation of
an administrator in the OS window (password or passkey). Managing one's own
passkeys requires no capability: the browser asks for the user's presence. The
window navigation is not affected by these panels.

## What the app does not do

- It has no store of its own: everything it displays comes from the OS.
- It is single-instance per user (`SingleInstancePerUser`) and has no
  `AutoStart`.
- It is a system app (`IsSystemApp`, ADR 0048): it cannot be uninstalled.

## Links

- Settings as files and layers: [`settings-files.md`](../framework/settings-files.md), [`sensitive-settings.md`](sensitive-settings.md).
- Associations: [`user-file-associations.md`](user-file-associations.md).
- Desktop: [`desktop.md`](desktop.md). Accessibility: [`accessibility.md`](accessibility.md).
- Themes: [`theme-editor.md`](theme-editor.md). Backup: [`system-backup.md`](system-backup.md).
- Synchronization: [`synchronization.md`](synchronization.md). Memory: [`oom-killer.md`](oom-killer.md).
- App installation: [`app-installation.md`](app-installation.md).
