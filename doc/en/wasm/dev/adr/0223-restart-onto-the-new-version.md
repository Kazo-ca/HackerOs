> [Version française](../../../../fr/wasm/dev/adr/0223-restart-onto-the-new-version.md)

# ADR 0223: restart onto the new version, not the old one

## Status

Proposed on 2026-11-01. Questions asked to Benoit in the thread "Corriger le
redémarrage vers une ancienne version"; nothing is coded before his answers.

- **Complements:** [ADR 0017](0017-pwa-cache-and-offline-strategy.md) (PWA
  cache), [ADR 0162](0162-restart-after-a-host-build-change.md) (restart
  banner), [ADR 0174](0174-announce-a-new-version-to-open-sessions.md)
  (`/version.json`), [ADR 0195](0195-start-menu-power-button.md) (Start menu
  "Restart").

## Context

Benoit (2026-11-01): when a new version is offered and he clicks "Restart", he
lands on an **even older** version, without having installed the app. He
suggests the classic version number in the URL (`my-script.js?v=1101`), but
with no number to change on every deployment.

### What already works: file fingerprints

The .NET 10 build already does what `?v=` does, automatically and better: every
`_framework/` file carries a hash of its content in its name
(`dotnet.h8uf4fadjg.js`), the import map of `index.html` links them, CSS uses
`#[.{fingerprint}]`, and every resource is checked by its SHA-256 (SRI). A
changed file therefore changes name on every build, with nothing done by hand.
Adding `?v=` would change nothing: the file that stays old is `index.html`
itself, and it is not the HTTP cache that keeps it.

### The cause: the service worker keeps the old version

Checked in the code of `main` and on `https://hackos.kazo.ca` on 2026-11-01:

1. **The worker is installed even without installing the app.** `index.html`
   registers `service-worker.js` in every tab that is not `localhost`.
   Installing the PWA changes nothing about the cache.
2. **It serves everything from its cache, `index.html` included** (cache first,
   `service-worker.published.js`, `fetchShellOrAsset`). The cache is named after
   the version (`hackeros-cache-<manifest version>`).
3. **The new worker waits forever.** After a deployment the browser downloads
   the new worker, which fills its cache then stays "waiting" as long as a tab
   is controlled by the old one. The worker can receive `SKIP_WAITING`, but
   **nothing sends it**, and the `hackeros-pwa-update-available` event of
   `index.html` **has no listener** in the OS.
4. **"Restart" is a plain `location.reload()`** (`BrowserSystemPowerDevice`). A
   reload does not release the old worker: the reloaded page still belongs to
   it, and it serves the page from its old cache.
5. **Why "even older":** the open page can be newer than the worker's cache. A
   Ctrl+F5 (which the project memory gives as the fix) bypasses the worker and
   loads the latest version from the network; the Start menu "Restart"
   (ADR 0195) empties the cache when the old worker can no longer download its
   version, and the next page comes from the network. On a normal reload the
   old worker takes over again with its version's cache, which can be several
   deployments old since it is never replaced while a tab stays open.
6. **The GitHub Pages HTTP cache matters little.** Pages sends
   `Cache-Control: max-age=600` for `index.html` and `version.json`,
   `max-age=14400` for `service-worker.js` and `service-worker-assets.js`. The
   worker is registered with `updateViaCache: 'none'`, its install downloads
   with `cache: 'no-cache'`, and `version.json` is requested with `?t=` and
   `no-store`: these lifetimes do not delay the update, except for the page
   served by the network when it is not in the worker's cache (up to 10
   minutes).

The diagnosis comes from reading the code and the site's headers; it was not
reproduced in a browser.

## Options

### A. Activate the new worker on restart (recommended)

The standard service worker update flow, missing today:

1. When `/version.json` announces a version (ADR 0174) or the build changed
   (ADR 0162), the OS calls `registration.update()`: the new worker downloads
   the new version into its own cache, checked by SRI.
2. The "Restart to update" button ends the session cleanly (ADR 0162 §2), sends
   `SKIP_WAITING` to the waiting worker, and the page reloads on
   `controllerchange` (already written in `index.html`). The old cache is
   deleted on activation (already written).
3. Offline or failed download: the old version is kept and the banner says so;
   nothing breaks.

Keeps offline use and the isolation headers the worker adds on a static host
(ADR 0131). No number by hand: the cache name already comes from the manifest
generated at build time. A small JS module in `HackerOs.Ecosystem.Host`
(browser API C# cannot call), the logic stays in C#.

### B. Network first for the page

The worker asks the network for `index.html` and uses its cache only offline.
Simpler, but the new page loads files the old cache does not have: they come
from the network without being kept, and the OS no longer starts offline before
the new worker is active. Mixes two versions.

### C. No service worker without installation

Register the worker only in the installed PWA; an ordinary tab follows the HTTP
cache and the fingerprints. Fixes Benoit's case, but the tab loses offline use
**and** cross-origin isolation on GitHub Pages (ADR 0131: the worker adds
COOP/COEP, Pages cannot send headers), hence processes in shared-memory Web
Workers. Does not fix the installed PWA, which has the same defect.

### D. Version number in URLs (starting idea)

A script reads `version.json` and rewrites `data-v-src` into
`src?v=<version>`. Redundant with the .NET fingerprints, does not touch the
`index.html` served by the worker, and breaks the integrity check and the
import map if applied to `_framework/` files. Rejected.

## Questions asked to Benoit

1. **Method:** A (recommended), B or C.
2. **When to download:** (a) in the background as soon as it is announced, the
   button restarts at once (recommended); (b) only on click, with a wait.
3. **Other tabs** open on HackerOS: (a) they do the same clean restart before
   reloading (recommended); (b) they reload without ending the session; (c) no
   update while they are open.
4. **Start menu "Restart"** (ADR 0195): (a) takes the new version if there is
   one, with the same flow (recommended); (b) stays as today.

## Decision

To be written after Benoit's answers.
