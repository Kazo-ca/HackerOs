> [Version française](../../../../fr/wasm/dev/adr/0224-retry-the-boot-after-a-server-error.md)

# ADR 0224: retry the boot after a server error

## Status

Accepted on 2026-11-01 (fix asked by Benoit, branch
`boot/retry-server-errors`).

- **Complements:** [ADR 0099](0099-os-backup-and-low-storage.md) §3 (boot
  guard, damaged PWA cache). Replaces its rule "a single reload per tab, then
  the error screen" for load errors the cache does not explain.
- **See also:** [ADR 0223](0223-restart-onto-the-new-version.md).

## Context

Benoit (2026-11-01 23:16Z), on `https://hackos.kazo.ca`, eight minutes after a
deployment: `503` on `_framework/System.a3z5tpzyvn.wasm`, then "Failed to find
a valid digest in the 'integrity' attribute" and the "HackerOS could not
start" screen.

Checked right after: the served file and the hash of the boot configuration
match (`sha256-Gs5b…`), in this deployment and in the previous one. The file
never changed: the body of the `503` response (GitHub Pages sends some for a
while after a deployment) is what fails the integrity check. The .NET loader
retries twice within a fraction of a second, then gives up.

The guard of ADR 0099 §3 can only repair the service worker's cache. A page
with no worker (first visit, Ctrl+F5), or whose cache was already rebuilt,
showed the `failed` state at once, when retrying a little later was enough.

## Decision

1. A load error (integrity, download) on a page **with no worker**, or after
   the cache was rebuilt, reloads the page by itself after 5, 15 then 30
   seconds (`retrying` state, with a countdown and the "Retry" button to retry
   at once). Beyond three attempts in the tab, the previous `failed` state.
2. Offline (`navigator.onLine === false`), no automatic retry: the `offline`
   state (page with a worker) or `failed`.
3. The counter lives in `sessionStorage['hackeros.bootRetries']`; a successful
   boot clears it with the `hackeros.bootRecovery` guard, and a click on
   "Retry" starts three new attempts.
4. The guard also listens to the "download '…' for … failed" rejection that
   escapes from the loader on its own, not only to "Failed to start platform".
5. Everything stays in the boot script of `index.html` (host bootstrap
   boundary, ADR 0099); no `<script src>` is added.

## Rejected options

- **Retry each file in `loadBootResource`**: no reload, but the loader's
  `fetch` (per-file cache mode, integrity) would have to be rewritten and
  followed at every .NET version.
- **Change nothing**: the user sees an error for an incident that goes away
  by itself within minutes.

## Consequences

- Right after a deployment, a boot can take up to about one more minute
  instead of failing.
- A file really damaged on the server shows `failed` after about 50 seconds
  instead of at once.
