> [Version française](../../../../fr/wasm/dev/adr/0215-hack-store-answers-notification-and-ambiguous-app-id.md)

# ADR 0215: Hack Store, updates by notification only and ambiguous appId refused

## Status

Accepted on 2026-10-31, work item `todo/5b-hackstore-no-badge` (`todo/base`
series, `TODO` issue [#249](https://github.com/Kazo-ca/HackerOs/issues/249)).
Replaces [ADR 0212](0212-hack-store-app-build.md) §4 and §5, and the badge of
[ADR 0188](0188-hack-store-and-app-catalog.md) §3 ("it puts a badge on its
icon"). The rest of ADRs 0188 and 0212 does not change.

## Context

ADR 0212 asked Benoit four questions and applied the recommendations while
waiting. On 2026-10-31 Benoit confirmed **Q1 A** ("Restart to finish" without a
password, like the Start menu) and **Q2 A** (`catalog:official/<appId>`
source), but chose something else for the other two:

- **Q3 C, "Notification only"**: no badge on the Hack Store's icon.
- **Q4 B, "Refuse and ask"**: a bare appId listed by several catalogs is not
  installed; the user chooses the catalog.

## Decision

1. **Updates: notification only (replaces ADR 0212 §5).** `AppUpdateMonitor`
   still checks at sign-in then every `[updates] interval-hours` hours, and
   posts a notification (once per set of updates) whose action opens the
   Installed tab (`--page=installed`). No badge is drawn: not in the Start menu,
   not on the mobile home screen, not on the taskbar button.
   `AppUpdateMonitor.GetBadgeCount` and `TaskbarWindowItem.BadgeCount` go away
   (they were never shipped). The Store's own check
   (`HackAppPackages.GetUpdatesAsync`) still goes through the monitor, so an
   update the Store already shows is not notified a second time.
2. **Bare appId in several catalogs: refused (replaces ADR 0212 §4).**
   `hackpkg install <appId>[@version]`, `hackpkg info <appId>` and the SDK's
   install by appId read every catalog of `[catalogs]`. One catalog lists the
   app: it is used. Several list it: the operation fails with
   `apps.package.ambiguous` and a message naming every possible source, for
   example "org.example.notes is in several catalogs; choose one:
   catalog:official/org.example.notes, catalog:mirror/org.example.notes."
   (`hackpkg` prints it and exits with code 1). None: still
   `apps.package.not-in-catalog`. The Store, which shows each catalog, always
   installs with the `catalog:<name>/<appId>` form and is not affected.

## Consequences

- The OS never picks a catalog for the user; an added catalog that repeats an
  app of the official one cannot replace it by surprise.
- Scripts running `hackpkg install <appId>` keep working while one catalog is
  configured (the default).
- The Start menu, the mobile screen and the taskbar go back to how they were
  drawn before ADR 0212.

## Tasks

- [x] Remove the badge (Start menu, mobile, taskbar) and `GetBadgeCount`.
- [x] `apps.package.ambiguous` for a bare appId listed by several catalogs, with a test.
- [x] Docs (`app-installation.md`, `linux-commands.md`, `features.md`, Hack Store plan, memory) and change entry.
