> [Version française](../../../../fr/wasm/dev/plans/hackeros-store.md)

# Note: the HackerOs Store app

Note of 2026-10-28, to pick up **once apps are published** in their
repositories. No work item opened. This is the "separate plan" announced for the
app store by [`ecosystem-rebuild-plan.md`](ecosystem-rebuild-plan.md)
(section 9) and [`wasm-v3-completion-plan.md`](wasm-v3-completion-plan.md)
(question 1).

## Idea

A **HackerOs Store** system app, in the style of the Play Store or Microsoft
Store, lists the apps that can be installed in HackerOS, shows their page and
installs, updates or removes them in one click.

## What already exists or is proposed

- The [`Kazo-ca/HackerOs-AppMarketPlace`](https://github.com/Kazo-ca/HackerOs-AppMarketPlace)
  repository exists (empty on 2026-10-28): it will hold the list of app
  repositories.
- The [`Kazo-ca/HackerOs-AppTemplate`](https://github.com/Kazo-ca/HackerOs-AppTemplate)
  repository exists (empty on 2026-10-28): starting point of an app,
  [ADR 0176](../adr/0176-app-repository-template.md).
- Installing from a GitHub repository at the terminal (`hackpkg`), its
  `.hackapp` packages and its trusted sources: [ADR 0182](../adr/0182-install-apps-from-github.md).
  The Store goes through the same installation service; it only adds the
  interface and the list.

## To decide when the time comes

- Format of the list in `HackerOs-AppMarketPlace` (for example an `apps.json`:
  repository, category, short description, screenshots), read through
  `raw.githubusercontent.com` like the packages.
- Who adds an app to the list (pull request reviewed by Benoit?) and whether a
  listed app becomes a trusted source.
- Package signing, transactional installation and data migration from one
  version to the next (question 1 of `wasm-v3-completion-plan.md`).
- An app's page: icon and screenshots from the package or the repository.
- Updates: checked at startup, next to the announcement of a new OS version
  ([ADR 0174](../adr/0174-announce-a-new-version-to-open-sessions.md)).
