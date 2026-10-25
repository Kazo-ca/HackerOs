# Recovery from a corrupted PWA cache

Implementation of section 3 of
[ADR 0099](adr/0099-os-backup-and-low-storage.md) (work item
`v3/7-storage-backup`), which completes
[ADR 0017](adr/0017-pwa-cache-and-offline-strategy.md) (versioned "cache
first" cache, integrity checked at installation).

## Purpose

An entry in the service worker cache can become damaged after installation
(disk, interrupted write, extension, development tool). The service
worker serves it as is: it does not recheck integrity on every request.
Before this work item, Blazor refused the resource, startup stopped on the
loading ring and the "An unhandled error has occurred" bar, and
reloading changed nothing. Now:

- **online**, the page asks the service worker to empty its cache and to
  fetch it again from the server, then reloads **only once**; the OS starts;
- **offline**, the startup error screen explains that the cache is damaged
  and that you must reconnect once; the damaged cache is not touched, and
  the "Retry" button repairs it once the connection is back.

OS data (IndexedDB) is never affected: only the application files cache
is rebuilt.

## Architecture

| Piece | File | Role |
|---|---|---|
| Integrity check | Blazor (`dotnet.js`, SHA-256 fingerprints of the startup configuration) | Each startup resource is loaded with `integrity`: damaged bytes make the load fail ("SRI's integrity checks failed"), then startup fail ("Failed to start platform. Reason: … download '…' failed"). |
| Startup guard | `OS/HackerOs.Ecosystem/wwwroot/index.html` (inline script, host bootstrap boundary) | Launches Blazor (`autostart="false"`, then `Blazor.start()`), detects the failure, decides on the repair, displays the error screen. |
| Repair | `OS/HackerOs.Ecosystem/wwwroot/service-worker.published.js`, `RESET_CACHE` message | Empties the version's cache and fetches it again from the server, or keeps it intact if the server is unreachable. |
| Error screen | `index.html` (`#hackeros-boot-error`) and `OS/HackerOs.Ecosystem.Host/wwwroot/css/app.css` (served under `_content/HackerOs.Ecosystem.Host/css/app.css`) | Three states (`data-state`): `repairing`, `offline`, `failed`. Dark console style; Blazor's loading ring and error bar are hidden (`html.hackeros-boot-failed`). |

### Flow

1. `Blazor.start()` fails. In .NET 10, the "Failed to start platform" error
   comes out as an unhandled promise rejection and the promise of `Blazor.start()` stays
   pending: the guard therefore listens to `unhandledrejection` **and** the rejection of
   `Blazor.start()`. If `blazor.webassembly.js` itself failed to load
   (`window.Blazor` absent), that is also a load failure.
2. Only an integrity or load error (`integrity`, `SRI`,
   `download '`, `Failed to fetch`, `Failed to load`, …) triggers the
   repair, and only if a service worker controls the page. Any other
   error displays the `failed` state.
3. If the guard `sessionStorage['hackeros.bootRecovery']` is already set, the
   page has already repaired and reloaded in this tab: no loop, the `failed`
   screen (or `offline` if `navigator.onLine` is false) is displayed.
4. Otherwise the `repairing` screen is displayed and the page sends `RESET_CACHE` to the
   service worker over a `MessageChannel` (maximum delay 120 s). The service
   worker:
   - probes the server (`service-worker-assets.js`, `cache: 'no-store'`); if it
     is unreachable, it answers `{ ok: false, reason: 'unreachable' }` without erasing
     anything, and the page displays the `offline` state;
   - otherwise it downloads all the manifest files again, with their
     `integrity`, into a transit cache `hackeros-cache-<version>-repair`,
     empties the damaged cache, copies the transit cache into it, then deletes the transit cache;
   - if the server cannot provide a verified copy of this version
     (file damaged on the server, newer version deployed), it empties
     the damaged cache anyway: resources then come from the server.
5. On `{ ok: true }`, the page sets the guard and reloads. A successful startup
   clears the guard: a later corruption in the same tab will be
   repaired again.

The transit cache starts with `hackeros-cache-`: if it were left half
written, the next activation of a version deletes it like any stale
cache (ADR 0017).

## Decisions

- **Repair requested by the page, not by the service worker**: only Blazor
  knows that a resource was refused; checking integrity on every request
  in the service worker would cost one SHA-256 per file at each startup.
- **Offline, nothing is erased**: emptying the cache without being able to fetch it again
  would also remove `index.html`, hence the screen that explains what to do.
- **A single reload per tab** (guard in `sessionStorage`): if the fresh
  copy fails too, the user sees an error instead of a loop of reloads.
- **Inline script in `index.html`**: the guard must run before and
  during Blazor startup, without depending on a resource that could
  itself be damaged; no `<script src>` is added to the host.
- In development mode (`localhost`, `127.0.0.1`), no service worker is
  registered: a startup failure directly displays the `failed` state.

## Tests

`Tests/HackerOs.Pwa.E2E.Tests/PublishedPwaTests.cs`, against a Release
publication served statically (the test that checked that corrupted bytes
were served as is has been replaced):

- `Corrupted_boot_resource_in_the_cache_is_downloaded_again_and_the_app_boots_after_one_reload`:
  `System.Private.CoreLib.*.wasm` overwritten in the cache, reload; the OS
  starts after exactly one recovery reload, the cache contains
  the real file again and the guard is cleared.
- `Corrupted_cache_offline_asks_to_reconnect_once_and_recovers_when_back_online`:
  same corruption, browser offline and server stopped (Playwright's offline
  emulation does not reach the service worker's requests); the `offline` screen
  is displayed, the damaged cache is intact; server restarted, "Retry"
  repairs it and the OS starts.

Without IPv6 (cloud container), the harness binds to `127.0.0.2` instead of
`[::1]`: loopback address, secure context, and not excluded by the
`localhost`/`127.0.0.1` filter of `index.html`.

## Tasks

- [x] Launch Blazor manually and detect the startup failure (unhandled rejection in .NET 10).
- [x] Service worker `RESET_CACHE` message: probe, verified transit cache, replacement.
- [x] Single-reload guard in `sessionStorage`, cleared by a successful startup.
- [x] Startup error screen (states `repairing`, `offline`, `failed`).
- [x] Online and offline recovery E2E tests; harness fallback to `127.0.0.2`.
- [x] Manual verification (Playwright Node, Release publication served by `python3 -m http.server`): online, offline then reconnection, file damaged on the server (a single reload, then `failed` state).
