> [Version française](../../../../fr/wasm/dev/adr/0212-hack-store-app-build.md)

# ADR 0212: the Hack Store v1 app, implementation adjustments

## Status

Accepted on 2026-10-31, work item `todo/5-hackstore-app` (`TODO` issue
[#249](https://github.com/Kazo-ca/HackerOs/issues/249), Hack Store 5).
Implements [ADR 0188](0188-hack-store-and-app-catalog.md) §1 to §3 and §8, on
top of the sources and trust of [ADR 0208](0208-hack-store-sources-trust-build.md).
Changes no decision of ADR 0188; settles what it left open. Four questions were
asked to Benoit (Q1 to Q4 below); the recommendations are applied while waiting
for his answers.

## Context

ADR 0188 decides the Hack Store app, its default pin, its updates and how
`hackpkg` reads the catalogs. The `catalog.json` format is the one of branch
`catalog/1-initial-catalog` of `Kazo-ca/HackerOs-AppMarketPlace`
(`schemaVersion` 1, paths relative to the root of the `packages` branch, next
to `catalog.json`). Still open: how an install from a catalog is written in
`[installed]`, who may "Restart to finish", where the badge shows, which
catalog wins when several list the same app, and how to record that a profile
already got the default pin.

## Decision

1. **Source of a catalog app (Q2 A).** An app installed from a catalog is
   written `catalog:<name>/<appId>@<version>` in `[installed]` (for example
   `catalog:official/org.example.notes@1.2.0`), next to ADR 0182's
   `github:owner/repo@version` form. Its trust is that of the `catalog:<name>`
   key of `[trusted]`; the official catalog is **Verified** (ADR 0208 §1). The
   update reads the same catalog again. The installer turns the catalog entry
   into a package index (version, path, SHA-256), so installing, the checks
   (SHA-256, API, SDK) and updating keep one code path.
2. **Reading the catalogs.** The OS downloads `catalog.json` itself (8 MiB at
   most) and keeps it 5 minutes; the Store's Refresh button reads it again. A
   catalog that cannot be read is shown with its error without blocking the
   others; an invalid app or version (ID, path leaving the catalog, SHA-256,
   size) is skipped with a warning in `/var/log/syslog`. A `kind` other than
   `app` is skipped until theme packs (ADR 0190). The `description.<lang>.md`
   description (64 KiB at most) is read in the session language, then the
   language without region, then English. The Store draws it itself
   (headings, paragraphs, lists, code, bold, italic, `https` links only): no
   HTML is ever produced from an author's text.
3. **"Restart to finish" without an administrator (Q1 A).** The button
   restarts HackerOS like Start ▸ Restart, without authentication, and only
   when an installed, updated or removed app waits for the restart (otherwise
   `apps.package.nothing-pending`).
4. **An app ID alone (Q4 A).** `hackpkg install <appId>[@version]`,
   `hackpkg info <appId>` and the Store look the app up in the official
   catalog first, then in the other catalogs by name; the first that lists it
   wins (`apps.package.not-in-catalog` otherwise). An installed app keeps the
   page of the catalog it came from.
5. **Update check and badge (Q3 A).** A session service `AppUpdateMonitor`
   checks at sign-in then every `[updates] interval-hours` hours (6 by
   default). It counts the updates of the system apps and of the user's
   personal apps, shows that number as a badge on the Hack Store icon in the
   Start menu, on the mobile home screen and on its taskbar button, and posts
   a notification (one per set of updates) whose action opens the Installed
   tab (`--page=installed`). It stops and clears the badge at sign-out. No
   automatic update.
6. **Default pin.** `start-menu.json` gets, per profile, an optional
   `defaultPinsApplied` list of the apps already pinned by default. At the
   first load of a profile (new or existing), each app of the default list
   (`org.hackeros.store`) missing from `defaultPinsApplied` is added after the
   user's pins and recorded; an app unpinned afterwards does not come back.
7. **SDK.** `HackAppPackages.GetCatalogsAsync`, `GetCatalogDescriptionAsync`,
   `GetUpdatesAsync`, `RestartToFinishAsync`; `HackAppCatalog`,
   `HackCatalogApp`, `HackCatalogAppVersion`, `HackCatalogCategory`,
   `HackPackageUpdate`. The new `IAppManagementGateway` members have a default
   implementation that throws `NotSupportedException`. `InstallAsync` and
   `GetInfoAsync` accept `catalog:<name>/<appId>` and an app ID alone.
8. **The app.** `HackerOs.Apps.Store`, a system app (`IsSystemApp="true"`,
   cannot be uninstalled), capabilities `apps.manage` and `apps.launch`, one
   instance per user, the same screen on desktop and mobile (container query).

## Consequences

- The catalog repository must keep its paths relative to `catalog.json`; a
  `schemaVersion` change is refused by OSes already released
  (`apps.catalog.unsupported`).
- An app installed by the Store or by `hackpkg install <appId>` still appears
  after a restart (ADR 0182 Q5 C); hot add stays issue #251.
- The host template (`HackerOs.Templates`) references `HackerOs.Apps.Store`,
  published with the next version.

## Questions asked to Benoit

| Question | Choices | Recommendation applied |
|---|---|---|
| Q1 "Restart to finish" | A without administrator; B with administrator | A |
| Q2 Source written | A `catalog:<name>/<appId>`; B the app's GitHub repository | A |
| Q3 Badge | A Start menu and taskbar; B Start menu only | A |
| Q4 Several catalogs | A official then by name; B ask | A |

## Tasks

- [x] `catalog:` source, reading `catalog.json`, install and update from a catalog.
- [x] `hackpkg install <appId>` and `hackpkg info <appId>`.
- [x] SDK: catalogs, descriptions, updates, restart.
- [x] `AppUpdateMonitor`, badge (Start menu, mobile, taskbar), notification.
- [x] Default pin and profile migration (`defaultPinsApplied`).
- [x] Hack Store app: home, search, app page, Installed, Restart to finish, desktop and mobile.
- [x] Tests and documentation (`app-installation.md`, `linux-commands.md`).
