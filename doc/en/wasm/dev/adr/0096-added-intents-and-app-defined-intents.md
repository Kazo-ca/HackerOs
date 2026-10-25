> [Version française](../../../../fr/wasm/dev/adr/0096-added-intents-and-app-defined-intents.md)

# ADR 0096: added intents (reveal, edit, open a URI, app settings) and app-defined intents

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work item `v3/4-intents`).

- **Complements:** ADR 0010 and 0047 (`Intents` declaration of the manifest),
  [ADR 0039](0039-directory-open-file-association.md) (folder
  association), ADR 0050 §7 (`ExecuteCommandIntent`), ADR 0056 (SDK facades).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  line `v3/4-intents`; gap R6.

## Context

`AppIntentDispatcher` (`Platform/HackerOs.Platform.Core/Intents/AppIntentDispatcher.cs:160-169`)
handles `LaunchApp`, `OpenFile` and `ExecuteCommand`. Two intents exist but do
nothing: `RevealFileIntent` (line 166) and `ShowAppSettingsIntent` (line 297,
which validates the target and answers "sent"). There is no intent to edit a
file or to open a URI; `HackBrowser.OpenAsync` launches the HackerOS browser
directly. `IAppIntentGateway` only exposes `LaunchAsync` and
`OpenFileAsync`. An app can declare intents (`AppDefinition.Intents`,
`intents` field of the manifest) but the dispatcher always answers
`intent.unknown` to an identifier it does not know. There is no
`xdg-open` command.

## Decision

### 1. Reveal a file (`RevealFileIntent`)

- The dispatcher launches the default folder handler (type
  `inode/directory`, ADR 0039) on the **parent folder**, with the argument
  `--select=<path>`. This is the contract of `org.freedesktop.FileManager1.ShowItems`
  reduced to a launch argument.
- The file explorer opens the folder and selects the item, scrolling
  to it. Another folder handler that ignores
  `--select` simply opens the folder.
- Same access rules as `OpenFile`: the caller must be able to read the parent
  folder, otherwise `intent.reveal-file.denied`; missing path:
  `intent.reveal-file.not-found`.

### 2. Edit a file (`EditFileIntent`)

- New intent `org.hackeros.intent.edit-file.v1`, record
  `EditFileIntent(VirtualPath Path, string? MediaType)`.
- It chooses among the handlers whose `FileHandlerManifest.Actions` contains
  `edit`, with the same priority and default-choice rules as `OpenFile`.
  Without an `edit` handler, it falls back to `open` (like `xdg-open`, which has no
  "edit" verb), and the result reports it (`FellBackToOpen`).
- `IAppIntentGateway.EditFileAsync` and `HackApps.EditFileAsync` expose it.

### 3. Open a URI (`OpenUriIntent`) and `xdg-open`

- New intent `org.hackeros.intent.open-uri.v1`, record `OpenUriIntent(Uri Uri)`.
- As under freedesktop, a URI scheme is a media type
  `x-scheme-handler/<scheme>`: an app declares it in its `FileHandlers`
  (`MediaType = "x-scheme-handler/https"`, action `open`) and the default choice
  lives in `/etc/hackeros/file-associations.json`, editable by the
  administrator and overridable by the user, like the other
  associations. The HackerOS browser declares `http` and `https` and is the
  default handler for them.
- `file://` becomes an `OpenFileIntent` on the path.
- Scheme without a handler: `intent.open-uri.no-handler`.
- `IAppIntentGateway.OpenUriAsync` and `HackApps.OpenUriAsync` expose it;
  `HackBrowser.OpenAsync` remains, for an app that specifically wants the
  HackerOS browser.
- New command `xdg-open <path|uri>` in `HackerOs.Commands`: a
  path gives `OpenFile` (a folder opens in its handler, as on
  Linux), a URI gives `OpenUri`. Exit code 0 if
  the intent is sent, 3 without a handler, 4 if the file does not exist (codes
  of `xdg-open`).

### 4. App settings (`ShowAppSettingsIntent`)

- If the target app itself declares the intent `org.hackeros.intent.show-app-settings.v1`
  in `Intents`, it is the one launched (or activated) with
  `--show-settings`: it has its own settings page.
- Otherwise, Settings opens on the app's page (`--app=<id>`): granted
  capabilities, associations, service state and start mode for an app of
  kind `Service`, uninstallation.

### 5. App-defined intents

- An app that declares an identifier in `Intents` **receives it**: the
  dispatcher no longer answers `intent.unknown` to an identifier declared by at
  least one installed app.
- New generic intent `CustomIntent(string IntentId, string? PayloadJson)`,
  sent by `HackApps.SendIntentAsync(intentId, payload)`. Free but
  well-formed identifier (`reversed.domain.name.vN`), like an Android action.
- Routing: a single declaring app, it is launched or activated; several,
  the user chooses in the same picker as "Open with", with the "always use"
  checkbox saved in `~/.config/hackeros/intents.config`.
- Reception: `IAppExecutionContext.Intent` (identifier and payload) at
  launch; for an already open app with `SingleInstancePerUser`, an
  `OnIntentReceived` hook of `WindowAppBase` and equivalent `TerminalApp`/`ServiceApp` ones.
- The payload is a JSON object of at most 64 KiB, checked to be well-formed.
  `PayloadSchemaAssetPath` remains documentation for callers: no
  schema validation in the dispatcher (no JSON Schema library in the
  OS).
- The caller must have `apps.launch`, as for `LaunchApp` to another app.

## Consequences

- `AppIntentIds` gains `edit-file`, `open-uri` and the free prefix; the manifest
  schema does not change (the declarations already exist).
- `v3/4` adds the intents, `xdg-open`, `--select` in the explorer, the app
  page in Settings, and dispatcher tests for each intent and each
  error code.
- `sdk-reference.md`, `app-model.md`, `file-explorer.md`, `linux-commands.md`
  and `shell-commands.md` are updated.
