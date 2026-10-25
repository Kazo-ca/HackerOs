# ADR 0121: per-user file and scheme associations

## Status

Accepted on 2026-10-17 (work item `v3/4b`, follow-up of `v3/4-intents`). Benoit
chose "Now" on the card of the `v3/4` thread ("Add per-user file and
scheme associations?"). One question remains open (§8); what follows applies the
recommended answer while awaiting it.

- **Implements:** the user layer announced by ADR 0096 §3 ("editable
  by the administrator and overridable by the user, like the other
  associations") and by ADR 0044 §8 ("a user layer may come").
- **Complements:** [ADR 0039](0039-directory-open-file-association.md) (folders),
  [ADR 0056](0056-sdk-apis-for-system-apps.md) §2 (`HackApps`, "Open with"),
  [ADR 0096](0096-added-intents-and-app-defined-intents.md) (`xdg-open`).
- **Document:** [`app-intents.md`](../../framework/app-intents.md),
  [`settings-files.md`](../../framework/settings-files.md).

## Context

The choice of the app that opens a file type lives in
`/etc/hackeros/file-associations.json`, editable by an administrator
only. ADR 0096 assumes a user can override this choice, but
no user layer exists: a user who prefers the code editor
for `.md`, or another browser for `https`, has no way to say so.
The "File associations" tab of Settings only lists the types
declared by the apps. On Linux, freedesktop meets this need through
`~/.config/mimeapps.list`, read before `/etc/xdg/mimeapps.list`, and through
`xdg-mime default`.

## Decision

### 1. User file

- `~/.config/hackeros/file-associations.json`, **same schema** as the system
  file (`schemaVersion` 1, `associations` array of entries `extension` or
  `mediaType`, `appId`, `actions`) and same validator.
- Created as `0644` in `~/.config/hackeros` (`0755`) when the user makes their
  first choice, like `mimeapps.list`. It is synchronized with the rest of the
  home folder (it is not in `~/.local/state`).
- Missing or invalid, it does not count: resolution moves on to the system
  layer. A write never replaces an invalid file (a typo made with
  `nano` is not lost).
- The association index is now organized by **target and action**: two
  entries can name different apps for `open` and `edit` of the same
  type, in both files.

### 2. Resolution order

1. The app explicitly requested by the intent.
2. The default of **the user running the intent** (the principal, not
   necessarily the session user: `sudo -u bob xdg-open` reads
   `bob`'s file).
3. The system default.
4. The single candidate app, otherwise the picker.

An entry that names an unknown or uninstalled app is ignored, layer by
layer: an outdated user choice falls back to the administrator's.
The order applies to everything that resolves an association: `OpenFile`, `EditFile`,
`OpenUri` (`x-scheme-handler/<scheme>`), `RevealFile` (`inode/directory`) and the
"Open with" list (the default first).

### 3. SDK API (minor, `Unshipped`)

- `IAppCatalogGateway.GetDefaultHandlersAsync` and
  `SetUserDefaultHandlerAsync(target, action, appId?)`, default members (an
  older gateway throws `NotSupportedException`).
- `HackApps.GetDefaultHandlersAsync()`, `SetDefaultHandlerAsync(target, appId,
  action = "open")`, `ResetDefaultHandlerAsync(target, action = "open")`.
- `HackFileAssociation(Target, Action, AppId, Layer)`: the user's rows
  first, then the system's; the first row for a target
  and action is the one the OS uses.
- `HackDefaultHandlerStatus`: `Changed`, `InvalidTarget`, `InvalidAction`,
  `AppNotFound`, `AppCannotHandle`, `FileInvalid`, `WriteFailed`.
- A target is an extension with its dot (`.md`) or a media type
  `type/subtype` (`text/markdown`, `x-scheme-handler/https`), lowercased.
  The app must be an installed window app that **declares** the target
  and the action in its manifest; removing a choice is always allowed.
- **Capabilities:** reading requires `file-associations.read`; changing one's own
  choice additionally requires `filesystem.user-home.write`, since it is a file of the
  home folder that the app could write anyway.
  `file-associations.write` (dangerous) remains that of the machine file.
- Worker apps go through two operations, `cat.defaults` and
  `cat.setdef`.

### 4. Settings and "Open with"

- The "File associations" tab becomes "Default applications":
  one row per target and action declared by the installed apps, with a
  selector ("System default (X)", "Only app (X)" or "Ask every
  time", then the candidate apps), the origin badge (yours, system default,
  built-in) and a filter. The machine file is shown, never written here.
- The explorer's "Open with" box offers "Always use this
  app for .ext files" when the chosen app declares the extension. The explorer
  receives `file-associations.read` for that.

### 5. `xdg-mime` command

- `xdg-mime query default <type>`: the app the OS uses to open this type
  for this user (nothing if there is no default).
- `xdg-mime default <app> <type>...`: writes the user's choice; a trailing
  `.desktop` is ignored, as freedesktop names its apps.
- `xdg-mime query filetype <file>`: `inode/directory` for a folder, otherwise
  the media type that an installed app declares for the extension, otherwise
  the extension itself, otherwise `application/octet-stream`. HackerOS has no
  `shared-mime-info` database.
- Exit codes of `xdg-mime`: 0, 1 command line, 2 missing file, 4
  action denied. Only the `open` action can be set here, as under freedesktop;
  `edit` is set in Settings or through the SDK.

### 6. What does not change

- Intents defined by apps keep `~/.config/hackeros/intents.config`
  (ADR 0096 §5).
- The `FileAssociations` synchronization (ADR 0033) remains that of the machine
  file.

### 7. Rejected options

- **`mimeapps.list` format (INI)**: more faithful to Linux, but two formats for
  the same content; the same JSON keeps a single validator and a single index.
- **New "user associations" capability**: it protects nothing
  that `filesystem.user-home.write` does not already allow, and adds one more line
  to grant.
- **Writing the machine file from Settings**: would remain an administrator
  gesture with approval; outside the stated need.

### 8. Question asked to Benoit: can an administrator lock a default?

The other settings have a `[locked]` section (ADR 0044) that prevents the
user from overriding a key. Nothing like it exists here: a user
can always choose their app.

- **No lock (recommended, applied)**: as with freedesktop, one user's choice
  only affects that user.
- **Per-entry lock** (`"locked": true` in the machine file): the user
  layer is ignored for that entry and Settings greys out the
  row; requires a new ADR and a schema field.

## Consequences

- 70 commands (`xdg-mime` added, after `passwd`), 87 built-in apps.
- A file `~/.config/hackeros/file-associations.json` appears at the first
  choice; deleting it hands control back to the system default.
- `FileAssociationResolver` reads the file system under the user's identity;
  its old signatures remain (system layer only).
