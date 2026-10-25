# ADR 0017: Progressive Web App Caching, Offline Strategy, and Version Migration

* **Status:** Accepted (DECISION: D-011)
* **Date:** 2026-08-03
* **Context:** HackerOS is packaged as a standalone Progressive Web App (PWA) running entirely client-side in Blazor WebAssembly. To ensure reliable offline operation, instant startup, atomic updates, and data persistence consistency across browser reloads, a clear caching and compatibility strategy is required.

---

## Context and Problem Statement

When deploying a Blazor WASM PWA, browsers download static web assets (`.dll`, `.wasm`, `.html`, `.json`, `.css`). Without a strict caching policy:
1. Browser HTTP caching can serve mismatched asset versions during updates, leading to runtime failures.
2. Serviceworkers can mix old and new release assets if cache keys overlap or if waiting workers activate abruptly.
3. Offline reload can fail if the navigation shell (`index.html`) is not cached or matched.

## Decision Drivers

* **Offline Independence:** HackerOS must launch, initialize IndexedDB, run applications, and edit files without an active internet connection after initial installation.
* **Asset Integrity:** All cached assets must verify against `service-worker-assets.js` hashes. Disabling integrity checks to suppress build errors is strictly prohibited (`P2-PWA-003`).
* **Atomic Versioning:** Cache keys must be unique per release (`hackeros-cache-${version}`). Upgrades must prune prior cache keys atomically on activation (`P2-PWA-005`).
* **No Cache Stale HTTP Headers:** Service worker registration must specify `updateViaCache: 'none'` (`P2-PWA-002`).

---

## Considered Options

1. **Network-First Strategy:** Try network for every asset, fallback to cache offline. (Rejected: Slow startup, depends on server availability for shell loading).
2. **Stale-While-Revalidate Strategy:** Serve cached version, update in background. (Rejected: Risk of running mismatched assemblies during WASM startup).
3. **Cache-First Atomic Shell Strategy:** Serve verified release assets directly from the versioned cache; fetch network only when un-cached or requested by optional external APIs. (Selected).

---

## Decision Outcome

**Selected Option:** Cache-First Atomic Shell Strategy (DECISION: D-011).

### Architectural Rules

1. **Service Worker Registration:**
   The published host registers `service-worker.js` with `{ updateViaCache: 'none' }` in `index.html`.

2. **Asset Manifest & Integrity:**
   All release assets are matched against `service-worker-assets.js`. Integrity hashes (`integrity: asset.hash`) are checked on cache populate.

3. **Cache Key Strategy:**
   Caches are named `hackeros-cache-${version}`. The `activate` event deletes any cache key starting with `hackeros-cache-` that does not match the active version.

4. **Update Flow:**
   - When a new service worker version is detected (`installingWorker.onstatechange === 'installed'`), `index.html` dispatches `hackeros-pwa-update-available`.
   - Update activation triggers `SKIP_WAITING`, followed by `controllerchange` page reload.

5. **Historical Compatibility Window:**
   IndexedDB schemas (`v1` to `v2`+) and local settings support backwards compatibility for all versions within major `v1.x`.

---

## Positive Consequences

* Instant offline boot for returning users.
* Zero risk of mixing old and new `.dll`/`.wasm` assemblies during updates.
* Clean separation between client-side simulation storage (IndexedDB) and PWA asset caching.
