> [Version française](../../../../fr/wasm/dev/adr/0225-serve-installed-app-files-to-every-page.md)

# ADR 0225: serve installed apps' files to every page

## Status

Accepted on 2026-11-02 (fix asked for by Benoit, branch
`installed-apps/serve-uncontrolled-page`).

- **Complements:** [ADR 0206](0206-serve-installed-app-assets.md) (installed
  apps' `wwwroot/` files served by the service worker). Replaces its rule "with
  no service worker, the files are not served for the session" and its rule for
  cleaning the cache at the first launch.
- **See also:** [ADR 0214](0214-hack-store-hot-add-signing-transactions.md) (adding an app without a
  restart), [ADR 0223](0223-restart-onto-the-new-version.md) (restart onto the
  new version).

## Context

Benoit (2026-11-02 00:46Z), on `https://hackos.kazo.ca`, after installing an
app "just for me" without restarting: "Failed to fetch dynamically imported
module: …/_content/Kazo.Chess/js/chess-rules.js". The file is declared in the
app's manifest; the server does not have it, the service worker must serve it
from the `hackeros-installed-apps` cache. Benoit saw the error after a Ctrl+F5
**and** in a session with no reload.

Two causes, found by reading the code and reproduced in Chromium for the first:

1. **Page loaded with Ctrl+F5.** The browser bypasses the service worker for
   that load: the page has no controller, even though a worker is active.
   `prepare()` waited 10 s for a `controllerchange` that never comes (a worker
   that is already active never activates again, so it never claims the page),
   then the OS kept "no service worker" **for the whole session**: no installed
   app had its files.
2. **Cache shared between tabs.** The cache is shared by every tab and window of
   the OS, but the cleaning at the first launch removed every address no app
   **of this page's catalog** declares. A tab opened before the install (another
   screen, another window) therefore erased, at the first launch of one of its
   installed apps, the files of the app the other tab had just installed; and
   that tab does not copy them again (once per session).

## Decision

1. **Take control of a page that has none.** When no worker controls the page
   but a worker is active, `installedAppAssets.js` sends it
   `HACKEROS_CLAIM_FOR_INSTALLED_APPS` with the version of the release the
   server delivers now (`version` of `service-worker-assets.js`, read with no
   cache: the one a page loaded with Ctrl+F5 runs). The worker
   (`installedAppAssets.sw.js`) calls `clients.claim()` only when that is the
   release it caches, or when it keeps no release cache (`installed-apps` mode,
   development). A page of another release never receives an old worker's
   cached files: it stays without a controller, as before, until the restart.
2. **The expected controller change does not reload the page.**
   `installedAppAssets.js` sets `window.hackerOsClaimForInstalledApps` to
   `pending` before asking; `index.html`'s `controllerchange` listener sets it to
   `done` and does not reload. `hostUpdate.js` then counts the page as
   controlled (clean restart when another tab activates a new release, ADR 0223).
3. **With no worker, the OS asks again at the next launch.** Only a success is
   kept for the session; an app launched with no worker is not marked
   "published".
4. **Cleaning follows the disk.** Before removing an address from the cache, the
   OS reads the apps installed on disk again
   (`InstalledAppsBoot.ReadInstalledOnDiskAsync`, leaving the catalog alone); an
   address stays when an app of this page **or** of the disk declares it.

## Rejected options

- **Activate a new worker or reload the page**: would cut the session and the
  other tabs (the reason for ADR 0223).
- **Claim the page without comparing versions**: after a Ctrl+F5 onto a new
  release, the old worker would serve its cached files to a newer page (mixed
  releases).
- **Per-app cleaning only** (at launch, remove the addresses of that app it no
  longer declares): would leave an uninstalled app's files forever.
- **Serve the files with no service worker** (`blob:` URLs): needs an SDK API
  and a change in every app (option B of ADR 0206, rejected).

## Consequences

- After a Ctrl+F5, an installed app finds its files from its first launch, when
  the page runs the worker's release.
- When the page runs another release than the worker (new release deployed,
  worker waiting), the files are not served before the restart; a line in the
  console and in `/var/log/syslog` says so.
- The first launch of an installed app reads the list of installed apps once
  more (a few IndexedDB reads).

Tests: `InstalledAppAssetPublisherTests`
(`A_launch_after_a_page_without_service_worker_asks_again_and_copies_the_files`,
`Files_of_an_app_another_tab_installed_are_kept`). Checked in Chromium
(Playwright, `service-worker.published.js` served on `127.0.0.2`): after a
forced reload, the page is claimed, does not reload and imports the cached
module; with another release on the server, the worker refuses.
