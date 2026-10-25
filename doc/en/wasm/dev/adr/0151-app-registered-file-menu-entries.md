> [Version française](../../../../fr/wasm/dev/adr/0151-app-registered-file-menu-entries.md)

# ADR 0151: file menu entries registered by apps

## Status

Accepted on 2026-10-19.

- **Benoit's decision:** plan [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)
  approved on 2026-10-19, questions Q4 and Q4 bis (registration at install time,
  result read at startup, like the Windows registry), Q7 (`types` filter
  and item count), Q8 (`after` and `afterGroup`), Q9 (installation
  order), Q10 (submenus through `parent`), Q11 (`selection`), Q12
  (common group), Q13 (`target`), Q15 (capability and setting), Q16
  (short identifiers), Q17 ("Open With" as a submenu), Q18 (`Desktop`
  scope), Q19 (title = resource key).
- **Builds on:** ADR 0150 (menu model), ADR 0096 (intents defined
  by apps), ADR 0121 (per-user associations), ADR 0104
  (manifest localization).

## Context

The "UnZip Here" and "Publish as a web site..." entries are coded into the
explorer. Another app cannot add anything to a file's menu. Benoit
wants the Windows model: each app declares its entries, the OS arranges them
in the menus of the file control (file, folder, empty, desktop), without
running the app's code to build a menu.

## Decision

### 1. Default menus and identifiers

The file control builds four menus on the model of ADR 0150:
`File`, `Directory`, `Background` (the empty space) and `Desktop` (= `Background` plus
the desktop's entries, ADR 0152). Their content is that of plan §7. The
identifiers are short and stable, with constants in the SDK
(`HackFileMenuItems.Open` = `open`, `OpenWith` = `open-with`, `Cut`, `Copy`,
`Paste`, `PasteInto`, `Rename`, `Delete`, `DeletePermanently`, `NewFolder`,
`Refresh`, `Properties`, `View`, `Sort`, `Background`). They are part of the
SDK's SemVer promise (ADR 0114 §2, "observable by an app").

"Open With" is always a submenu: the apps that declare the type
(associations of ADR 0121, the user's default first), a separator,
then "Choose another application...", which opens the provided dialog
(ADR 0148 §6) with "Always use this app".

### 2. Declaration

A static method of the `[AppManifest]` class, marked
`[RegisterFileContextMenu]`, receives a `RegisterFileContextMenuContext` and
calls `Register(…)`:

| Parameter | Role | Default |
|---|---|---|
| `title` | App resource key (`Resources/AppManifest.<culture>.resx`, ADR 0104) | required |
| `name` | Unique identifier in the app | required |
| `target` | `"self"` or the identifier of another app that declares the intent | `"self"` |
| `intent` | Intent defined by the target app (ADR 0096); absent on a parent entry | none |
| `icon` | `HackIcons.FromImage(asset)` or `HackIcons.Vector(library, name)` | none |
| `scope` | `FileMenuScope.File`, `Directory`, `Background`, `Desktop`, combinable | `File` |
| `types` | Extensions (`.zip`), media types (`application/zip`, `image/*`), `*` | all |
| `selection` | `MenuSelection.All`, `Each`, `Single` | `All` |
| `first`, `after`, `afterGroup`, `parent` | Position (§4) | none |

The context **only records the calls**: it gives access to no
service, file or network. A method that throws an exception makes the
registration of all the app's entries fail (nothing partial).

### 3. Registration at install time

1. **Built-in apps**: the manifest generator
   (`HackerOs.Tools.ManifestGenerator`) calls the method **at build time** and
   writes the result in `manifest.json` (optional property
   `contextMenus`, omitted without entries) and `BuiltInApplications.json`. The
   schema, the allowlist of `AppManifestJsonSerializer`, the test canonical
   file and the validator change together (common-pitfalls §2).
2. **Apps installed later**: the OS runs the method **once at
   install time** (and at each update of the app), in the recording
   context.
3. The result is written to `/etc/hackeros/context-menus.d/<appId>.json`
   (`root`, `0644`): the entries, the EN and FR titles copied from the
   resources, and the **installation rank** (increasing integer). Built-in apps
   are written there at first startup and at each OS update, in the order of
   `BuiltInApplications.json`.
4. Startup reads this folder: **no app code runs to build
   a menu**. An unreadable file is ignored and reported in
   `/var/log/syslog`.
5. Uninstalling the app removes its file; a disabled app (ADR 0048) has
   its entries hidden without removing the file.

### 4. Placement

1. The default entries form their groups (§1, plan §7).
2. `first: true`: a **common group at the top** of the menu, followed by a
   separator; several apps share this group there.
3. `after: "X"`: right after X, **in X's group**, without a separator.
4. `afterGroup: "X"`: a **new group** right after X's group.
5. Without a position: the **common group of apps**, right before the
   "Properties" group.
6. Several entries at the same place: **installation order** of the apps, then
   declaration order in the app.
7. `X` designates a default identifier (§1) or the `name` of an entry of the
   same app. An anchor absent from the current menu (for example `paste-into` on
   a file) sends the entry to the common group.
8. `parent: "n"`: the entry goes into the submenu of the entry `n` of the same
   app; `n` has no intent; total depth 3 at most (ADR 0150 §2).
   `first`, `after` and `afterGroup` are refused on an entry that has a
   `parent`.
9. The filters (`scope`, `types`, `selection`), disabled apps and hidden
   entries apply **before** placement; separators then
   collapse (ADR 0150 §3).

### 5. Checks

The generator (at build time) and the OS (at install time) check: unique `name`
in the app; `parent` existing, from the same app and without intent;
depth ≤ 3; `after`/`afterGroup` targeting a default identifier or
an entry of the same app; a single position per entry; `target` equal to
`"self"` or to an app that declares the intent; `intent` present on every entry
without children; `title` present in the EN resources. An error is an
`HOSAPP` diagnostic at build time and a refusal to install the app.

### 6. Capability and setting

- The **`shell.context-menu`** capability (normal) is required to register
  entries. Without it, the generator produces a diagnostic and the OS ignores the
  entries.
- Settings ▸ Applications lists each app's entries and lets the user
  hide them, in `~/.config/hackeros/context-menus.config`
  (`[hidden]`, `<appId>.<name>=true`). Hiding a parent entry hides its
  children.

### 7. Execution

- `selection: All`: one launch with all the paths; `Each`: one
  launch per item; `Single`: the entry is hidden if several
  items are chosen.
- The target app receives the intent: `AppContext.Intent`, `IntentId` = the declared
  intent, JSON payload `{"paths":["…"],"scope":"File"}`. A single instance
  already open receives it through `IntentReceived` (ADR 0096).
- Without an intent declared by the target (`target: "self"` on an app that has
  none yet), the OS passes the launch arguments
  `["<intent>", "<path1>", "<path2>", …]`.
- `target` is rechecked on click: if the target app is no longer installed or no
  longer declares the intent, the entry is hidden.
- The app is launched **with its own rights**: the menu entry gives it
  access to no file that it would not otherwise have. An app that cannot read a
  received path gets the usual `HackFile` error.

### 8. Existing entries migrated

- The explorer registers "Extract Here" (`types: [".zip"]`,
  `target: "self"`, intent defined by the explorer).
- Web Server Manager (`org.hackeros.web-server-manager`) registers "Publish
  as a web site" (scope `Directory`, `target: "self"`, `selection:
  Single`, with an intent that it declares to publish a folder). Its current
  launch with the folder as first argument (ADR 0077 §3) remains
  accepted. The entry thus leaves the explorer for the app that does the work.
- They therefore also appear on the desktop. `ZipFileContextMenuProvider` and
  `PublishSiteContextMenuProvider` disappear.
- A host can still adjust its instance's menu through the
  `ContextMenuOpening` event of `IHackFileView`, with callbacks (ADR 0150 §4).

## Consequences

- Building a menu loads no app: the cost is reading a
  folder at startup.
- An app entry is visible everywhere the file control is displayed
  (explorer, desktop, dialogs, any host app).
- Work item 4 of the plan delivers the generator, `context-menus.d`, the placement,
  the capability, the Settings page and the migration of the two entries.
- [`capabilities.md`](../../framework/capabilities.md) gains `shell.context-menu`.

## Alternatives rejected

- **Run the app's code at startup**: loads all apps and slows
  startup; Benoit chose registration at install time.
- **One group per app without a position**: a menu chopped up by separators; Benoit
  chose a common group.

## References

- ADR 0048, 0096, 0104, 0114, 0121, 0148, 0150, 0152.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §7
  and §8, [`app-intents.md`](../../framework/app-intents.md),
  [`localization.md`](../../framework/localization.md).
