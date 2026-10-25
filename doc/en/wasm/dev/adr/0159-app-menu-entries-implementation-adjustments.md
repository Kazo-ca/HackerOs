# ADR 0159: app menu entries, implementation adjustments

## Status

Accepted on 2026-10-20.

- **Work item:** `desk/4-app-menu-entries` (plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §12,
  work item 4).
- **Adjusts:** ADR 0151 (§2 titles, §3 registration, §4.4 `afterGroup`, §5
  checks, §6 setting, §8 last point). Its other decisions remain
  true and are delivered as is.

## Context

ADR 0151 decides how an app adds entries to file menus:
a `[RegisterFileContextMenu]` method, run at build time for built-in
apps and at install time for the others, a
`/etc/hackeros/context-menus.d` folder read at startup, placement by `first`,
`after`, `afterGroup` and `parent`, the `shell.context-menu` capability and a
setting to hide entries. While implementing it, a few points had to be
clarified or corrected. This ADR records them; the usage guide is
[`context-menu.md`](../../framework/context-menu.md) (section "App entries").

## Decision

### 1. A single registration: at build time, for all apps

All the ecosystem's apps are compiled into the host
(`HackerOs.App.Build`): there is no package installation at
run time where the OS would run the method (ADR 0151 §3.2). The manifest
generator therefore runs it for **each** app, at build time, and writes the entries
in `manifest.json` (`contextMenus`, omitted without entries) then in
`BuiltInApplications.json`. An app installed later through a package will follow
the same path: its `manifest.json` already carries its entries, the OS never
runs its code for a menu.

The generator:

- looks for the method **declared on the `[AppManifest]` class itself**
  (not an inherited method), by the full name of the attribute; at most one is
  allowed, `static void M(RegisterFileContextMenuContext context)`;
- runs it **twice** and compares the two results: a method that
  depends on the time, a file or a service is refused;
- refuses the whole registration if the method throws an exception (nothing
  partial, ADR 0151 §2).

### 2. The `/etc/hackeros/context-menus.d` folder

At startup and at each change of installed apps
(`AppInstallationService.Changed`), `FileContextMenuRegistry` aligns the
folder with the manifests, then rereads it:

- one `<appId>.json` file per app that has entries: `schemaVersion`,
  `appId`, `appVersion`, `rank`, `source` (SHA-256 fingerprint of the entries) and
  `entries`;
- the file is rewritten only if the fingerprint or the app's version changes;
  it then keeps its **rank**. A new app takes the next rank, in
  the catalog's activation order; this is the "installation order" of
  ADR 0151 §4.6;
- the file of an app uninstalled for the whole machine, or unknown to the
  catalog, is removed; an app disabled or uninstalled for the
  user keeps its file, its entries are only hidden;
- a file that is unreadable, names another app, whose app does not request
  `shell.context-menu`, or that violates a rule of ADR 0151 §5, is
  ignored and reported in `/var/log/syslog` (there is no installation to
  refuse, ADR 0151 §5 last sentence);
- each write is an audited system operation of the OS.

### 3. Titles: `Resources/AppManifest.resx` in English

`title` is a resource key. The English text lives in the **neutral** file
`Resources/AppManifest.resx`, the translations in
`Resources/AppManifest.<culture>.resx` (ADR 0104). The generator copies each
title into the manifest, per culture (`titles: [{culture, text}]`, English
required). In a project that declares several apps, the key is prefixed
with the app's identifier, like the other manifest keys. These keys
are not localized manifest strings in the sense of ADR 0104: the
localization generator ignores them. The menu chooses the title of the session's
culture, then of its parent culture, then English.

### 4. The menu applied by the OS, whatever the provider

The file control provider (ADR 0156, 0158) sets
`HackMenuDefinition.FileMenu` (`HackFileMenuContext`: scope, items,
displayed folder) on the menu it opens. `ContextMenuService` adds the
apps' entries **after** the menu's `Opening`, therefore after the host's
`ContextMenuOpening` (ADR 0151 §8, last point: the host adjusts
the default menu, not the apps' entries). A third-party provider that does not
set `FileMenu` does not get the apps' entries; the `hackeros.file-view`
contract requires it ([`embedded-components.md`](../../framework/embedded-components.md)).
An error while adding the entries is written to the log and the menu
opens with its default entries.

The identifiers of the entries in the menu are `app:<appId>/<name>`. On click,
the OS launches the target through `AppLifecycleOrchestrator`; the intent's sender
is the OS (`org.hackeros.shell`), not the app that shows the menu. In the empty
space (scopes `background` and `desktop`), the path passed is the displayed
folder.

### 5. Filters

- A file is accepted by the `file` scope and the types; a folder by
  the `directory` scope and, if types are declared, by `*` or
  `inode/directory`. A mixed selection requires both scopes.
- The desktop's empty-space menu is the empty-space menu plus that
  of the desktop (ADR 0152): a `background` entry also appears there, a `desktop`
  entry appears only there.
- A child entry registered without a scope takes its parent's; a
  child cannot appear where its parent does not appear.

### 6. `afterGroup`: one group per anchor

Several `afterGroup: "X"` entries (from the same app or from different apps)
share **a single** new group after X's, in rank order
then declaration order, rather than one group each. An entry anchored to
an entry of its own app declared further down is placed after it; an anchor
absent from the menu sends the entry to the common group (ADR 0151 §4.7).

### 7. Diagnostics

| Code | Case |
|---|---|
| `HOSAPP0026` | Malformed method, several methods, exception, two different runs, title missing from `AppManifest.resx`, rule of ADR 0151 §5 |
| `HOSAPP0027` | Entries without the `shell.context-menu` capability |
| `HOSAPP0028` | (host) `target` points to an app absent from the host or that does not declare the intent |

### 8. The setting: Settings ▸ File Menus

The setting of ADR 0151 §6 is a separate tab, **File Menus**,
rather than a section of Settings ▸ Applications: it lists the entries of
all apps, grouped by app, submenus under their parent, with a checkbox
per entry. It goes through a public SDK API, `HackFileMenuEntries`
(`GetEntriesAsync`, `SetHiddenAsync`), which requires `apps.manage`, like
`HackComponentProviders`; the choice is the session user's
(`~/.config/hackeros/context-menus.config`), no administrator is
requested. Hiding an unknown entry is refused; showing it again remains
possible to clean up a leftover line.

### 9. Migrated entries

- "Extract Here" (`extract-here`): `types: [".zip", "application/zip"]`,
  `after: "open"`, intent `org.hackeros.file-explorer.extract-here.v1`.
  The explorer opens on the archive's folder and extracts it into a
  folder with its name (`<name>.d` for an `application/zip` file without
  extension).
- "Publish as a web site..." (`publish-site`): scope `Directory`,
  `selection: Single`, intent `org.hackeros.web-server-manager.publish-site.v1`,
  received at launch or through `IntentReceived`.
- The entries that the explorer used to add itself in its
  `ContextMenuOpening` disappear.

## Consequences

- The `shell.context-menu` capability is a normal capability of the catalog
  ([`capabilities.md`](../../framework/capabilities.md)).
- The entries are the same in the explorer, the file dialogs,
  any app that displays `HackFileView` and, in work item 5, the desktop.
- The SDK gains `RegisterFileContextMenuAttribute`,
  `RegisterFileContextMenuContext`, `MenuSelection`, `HackFileMenuIntent`,
  `HackFileMenuContext`, `HackMenuDefinition.FileMenu` and
  `HackFileMenuEntries` (unshipped, for SDK 2.0 of work item 6).

## Alternatives rejected

- **Run the method at OS startup**: refused by ADR 0151.
- **One group per `afterGroup` entry**: chops the menu with separators for
  entries intended for the same place.
- **A section of Settings ▸ Applications**: would force opening each
  app to find an entry; a single list better answers "what
  is cluttering my menu?".

## References

- ADR 0104, 0150, 0151, 0152, 0153, 0154, 0155, 0156, 0158.
- [`context-menu.md`](../../framework/context-menu.md),
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md),
  [`app-intents.md`](../../framework/app-intents.md).
