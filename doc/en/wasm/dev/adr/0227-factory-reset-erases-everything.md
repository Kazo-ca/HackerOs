> [Version française](../../../../fr/wasm/dev/adr/0227-factory-reset-erases-everything.md)

# ADR 0227: the factory reset erases everything HackerOS keeps in the browser

## Status

Accepted on 2026-11-02 (asked by Benoit, branch
`login/factory-reset-erase-everything`).

- **Complements:** [ADR 0056](0056-sdk-apis-for-system-apps.md) §11 and
  [ADR 0226](0226-forgot-password-factory-reset.md). Replaces ADR 0226's
  consequence "only the OS database is deleted".

## Context

Benoit (2026-11-02, after ADR 0226 was merged): "Check that when resetting,
all the data kept in the browser is deleted. Nothing must remain." Then, for
web site databases and the installed apps' cache: "They must be cleaned too."

Until then, `BrowserSystemPowerDevice.EraseStorageAsync` only deleted the
`hackeros` IndexedDB database. Left in the browser were: web site databases
(`hackweb.*`, ADR 0088), mounted computer folders (`hackeros-host-folders`),
the installed apps' cache (`hackeros-installed-apps`, ADR 0206), the
cross-origin isolation cache, `localStorage` (backup restore, sites),
`sessionStorage`, OPFS and the service worker.

## Decision

1. Resetting (Settings ▸ System or "Forgot password?") erases, for the OS's
   origin: **every** IndexedDB database (`indexedDB.databases()`), **every**
   Cache Storage cache, `localStorage`, `sessionStorage`, the origin private
   file system (OPFS), the cookies readable by script, and unregisters the
   service worker. The page then reloads.
2. The work is done by the `hostFactoryReset.js` module (`eraseEverything`)
   of `HackerOs.Ecosystem.Host`, called by `BrowserSystemPowerDevice` after
   deleting the OS database (through the module that holds its connections).
   Each step is independent: a failure is written to the console and does not
   stop the others.
3. A database still open elsewhere (another tab) blocks its deletion: the
   module waits at most 5 seconds, the request stays queued and completes
   when the connection closes or at the reload.

## Consequences

- After the reload, only what the boot recreates exists: an empty `hackeros`
  database (first-run setup), the service worker and the OS release cache
  downloaded again, empty caches. Checked in Chromium (HTTPS, active service
  worker) with markers in every storage.
- Since the OS release cache is erased, a reset made offline cannot restart
  the OS until the network is back.
- Passkeys stay in the device's authenticator (out of a web page's reach).
