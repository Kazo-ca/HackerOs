> [Version française](../../../fr/wasm/dev/user-file-associations.md)

# Per-user default applications

This document covers the implementation of per-user file associations. The formats of the two files, the resolution order, the `HackApps` API and the capabilities are in [`../framework/file-associations-api.md`](../framework/file-associations-api.md).

Work item `v3/4b`, follow-up to `v3/4-intents`, following
[ADR 0121](adr/0121-user-file-associations.md). Completes
[`app-intents.md`](../framework/app-intents.md) (intent resolution) and
[`settings-files.md`](../framework/settings-files.md) (file locations).

## Purpose

Each user chooses the app that opens a file type (`.md`, `text/plain`) or a URI
scheme (`https`) for themselves alone, without administrator rights. Their
choice takes precedence over the machine's, like `~/.config/mimeapps.list` takes
precedence over `/etc/xdg/mimeapps.list` on Linux.

## Architecture

```text
Paramètres / « Ouvrir avec » / xdg-mime
        │ HackApps.Get/Set/ResetDefaultHandlerAsync
        ▼
IAppCatalogGateway (AppCatalogGateway ; RemoteCatalogGateway dans un worker : cat.defaults, cat.setdef)
        │ capacités file-associations.read (+ filesystem.user-home.write pour écrire)
        ▼
FileAssociationResolver ──► UserFileAssociationStore (~/.config, sous l'identité de l'utilisateur)
        │                └─► FileAssociationSettingsDocuments (/etc, ou contenu intégré)
        ▼
AppIntentDispatcher (passe le principal à chaque résolution)
```

- **`UserFileAssociationStore`** (`Platform.Core/Intents`): reads the user's file
  (validated by `FileAssociationSettingsValidator`), writes it while keeping the
  other entries and the other actions of the same target. Creates
  `~/.config/hackeros` as `0755` and the file as `0644`.
- **`PrincipalTextFile`**: reading and writing a small text file in the home
  folder under the user's identity, shared with `IntentChoiceStore`
  (`intents.config`).
- **`FileAssociationIndex`**: keyed by target **and** action, in both layers.
- **`FileAssociationResolver`**: overloads that take the user
  (`ResolveAsync(intent, user)`, `ResolveSchemeAsync(scheme, user)`,
  `GetHandlerAppIdsAsync(path, user)`), `GetConfiguredDefaultsAsync(user)` and
  `SetUserDefaultAsync(user, target, action, appId?)`. The old signatures read
  the system layer only.
- **`AppCatalogGateway`** receives the principal and the app's capability
  checker.

## Interfaces

- **Settings > File associations** ("Default applications"): one row per target
  and action declared by the installed apps (extensions, then media types, then
  schemes shown as `https:`), a selector ("System default (X)", "Only app (X)"
  or "Ask every time", then the apps), the origin badge (yours, system default,
  built-in) and a filter. `FileAssociationsPanel` component.
- **Explorer > Open with**: checkbox "Always use this app for .ext files" when
  the chosen app declares the extension. The explorer declares
  `file-associations.read`.
- **`xdg-mime`** ([`linux-commands.md`](linux-commands.md)):
  `query default <type>`, `default <app> <type>...`, `query filetype <fichier>`.

## Decisions

- Same JSON format as the machine's file rather than `mimeapps.list`: a single
  validator, a single index.
- No administrator lock for now (question raised, ADR 0121 §8).
- The machine's file remains written by hand by an administrator.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Intents/UserFileAssociationTests.cs`:
  layer order, user by user, stale choice, separate `open` and `edit`, `0644`
  file and reset, invalid file kept, refusals (`InvalidTarget`, `InvalidAction`,
  `AppNotFound`, `AppCannotHandle`), scheme, "Open with", list of defaults.
- `AppIntentDispatcherTests.Open_uri_and_open_file_use_the_acting_users_own_default`.
- `AppCatalogGatewayTests`: capabilities and row order.
- `XdgMimeCommandTests`, `xdg-mime` parity in `CommandParityTests`,
  `WireCodecIntentTests.Default_handlers_keep_target_action_app_and_layer`.
- `HackerOs.Apps.Settings.Tests/FileAssociationsPanelTests.cs`: panel rows.

## Task list

- [x] ADR 0121.
- [x] `~/.config/hackeros/file-associations.json` layer read under the user's identity.
- [x] Index keyed by target and action.
- [x] User order, then machine, in all resolutions.
- [x] SDK API (`Unshipped`) and worker operations.
- [x] Settings panel.
- [x] "Always use" in "Open with".
- [x] `xdg-mime` command.
- [x] Tests and documentation.
