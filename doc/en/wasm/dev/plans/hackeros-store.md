> [Version française](../../../../fr/wasm/dev/plans/hackeros-store.md)

# Plan: the Hack Store

Plan **approved on 2026-10-29** by Benoit, in the "Plan the Hack Store"
thread. No work item has started: this document lists what to do, in order.
It replaces the note of 2026-10-28 ("HackerOs Store"), which announced this
separate plan for the app store of
[`ecosystem-rebuild-plan.md`](ecosystem-rebuild-plan.md) (section 9) and
[`wasm-v3-completion-plan.md`](wasm-v3-completion-plan.md) (question 1).

## Goal

A **Hack Store** system app, in the style of the Play Store or Microsoft Store,
pinned by default in the Start menu, that lists the apps that can be installed
in HackerOS, shows their page, and installs, updates or removes them in one
click. Apps come from a reviewed catalog (`Kazo-ca/HackerOs-AppMarketPlace`),
where each version is built by us from its source code. In v2, the Store also
offers theme packs.

## Decisions

| ADR | Topic |
| --- | --- |
| [0188](../adr/0188-hack-store-and-app-catalog.md) | The Hack Store app (v1), its default pin, updates, the catalog repository format (one folder per version), the GitHub actions that check and publish, the action for app authors |
| [0189](../adr/0189-app-sources-trust-and-per-user-install.md) | Trusted sources (built into the OS or added by an administrator), per-user install without an administrator, block list, two-step validation of system installs; revises ADR 0182 Q4 |
| [0190](../adr/0190-theme-packs.md) | v2: `.hacktheme` packs (themes, wallpapers, icons, sounds, cursors) and the OS systems to build first |

Already in place: [ADR 0176](../adr/0176-app-repository-template.md) (app
repository template) and [ADR 0182](../adr/0182-install-apps-from-github.md)
(`.hackapp` packages, `hackpkg`).

## What to do, in order

Each line is a work item, with its branch and PR.

1. **`hackeros-solution` template**: ADR 0176 tasks, plus the workflow that
   publishes to the catalog (ADR 0188 §7). Catalog apps start from this
   template. **Done** (branch `todo/1-hackstore-solution-template`,
   [ADR 0207](../adr/0207-solution-template-implementation.md),
   [`app-repository.md`](../../framework/app-repository.md)); the template
   repository is regenerated at the next release.
2. **`wwwroot/` assets of installed apps** (done,
   [ADR 0206](../adr/0206-serve-installed-app-assets.md), issue #247): served at
   `_content/<assembly>/` by the service worker from a cache filled at the app's
   first launch, Web Workers included. Needed for any app that wraps a
   JavaScript project.
3. **Trust and per-user install** (ADR 0189): `app-sources.config`
   (`[catalogs]`, `[updates]`, values set at build, sources added by an
   administrator), `app-policy.config`, install into
   `~/.local/share/hackeros/apps`, catalog built when the session opens,
   two-step validation window, `hackpkg --user/--system`, `sources`, `policy`,
   Settings ▸ Apps ▸ Sources. **Done** (issue #248,
   [ADR 0208](../adr/0208-hack-store-sources-trust-build.md): catalog loaded at
   startup and hidden per session).
4. **`HackerOs-AppMarketPlace` repository** (ADR 0188 §4 to §6):
   `categories.json`, PR template, `validate.yml` (format, manifest, commit,
   build, API check, CodeQL, capabilities comment), `publish.yml`, `packages`
   branch, `catalog.json`, 3 versions kept.
5. **Hack Store app v1** (ADR 0188 §1 to §3): home by category, search, app
   pages, Install / Open / Update / Uninstall, Installed tab, "Restart to
   finish", update checks (startup then every 6 hours), badge and
   notification, default pin and migration of existing profiles, desktop and
   mobile. `hackpkg install <appId>` from catalogs. **Done** (branch
   `todo/5-hackstore-app`, issue #249,
   [ADR 0212](../adr/0212-hack-store-app-build.md)).
6. **First catalog app**: a third-party app, in its own repository and made
   from the template, that tests the whole chain (app repository → PR to the
   catalog → checks → publishing → install by the Store).
7. **Later**: adding an app to the catalog without a restart (ADR 0182, Q5 B);
   package signing; transactional install and data migration between versions
   (question 1 of `wasm-v3-completion-plan.md`).

### v2: theme packs (ADR 0190)

8. `.hacktheme` format, checks, install, Store Themes tab, with every part
   (themes, wallpapers, icons, sounds, cursors). **Done** (branch
   `todo/18-theme-packs`, issue #253,
   [ADR 0213](../adr/0213-theme-packs-build.md)); catalog CI:
   [AppMarketPlace #4](https://github.com/Kazo-ca/HackerOs-AppMarketPlace/issues/4).
9. One work item per new OS system, each with its ADR: image wallpapers, icon
   packs, event sounds, cursor themes. The Store accepts each part of a pack
   once its system exists. **Done** (ADRs 0202 to 0205).

## Rules

- This repository holds the code of no catalog app: each app lives in its own
  repository. **No game in this repository**
  ([ADR 0135](../adr/0135-no-game-in-this-repository.md)).
- No automatic merge to `main`; Benoit decides.
- Each work item updates the documentation indexes (French and English).
