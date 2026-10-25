# HackerOS Progressive Web App (PWA) Release & Offline Strategy

## Overview

HackerOS is packaged as an offline-capable Progressive Web App (PWA) using Blazor WebAssembly.
It provides instant startup, offline execution, atomic release asset caching, and safe background update activation without server dependencies after initial installation.

## Architecture & Configuration

| File | Purpose |
|---|---|
| `OS/HackerOs.Ecosystem/wwwroot/manifest.webmanifest` | Product web app manifest with standalone display, colors, and 192/512 maskable icons |
| `OS/HackerOs.Ecosystem/wwwroot/service-worker.published.js` | Published Service Worker implementing Cache-First offline shell and atomic asset versioning |
| `OS/HackerOs.Ecosystem/wwwroot/service-worker.js` | Development Service Worker (bypasses cache for rapid hot-reload development) |
| `OS/HackerOs.Ecosystem/wwwroot/icons/icon-192.png` | 192x192 PNG product icon |
| `OS/HackerOs.Ecosystem/wwwroot/icons/icon-512.png` | 512x512 PNG product icon |
| `OS/HackerOs.Ecosystem/wwwroot/index.html` | Published HTML shell registering SW with `updateViaCache: 'none'` and update reload handler |
| `docs/adr/0017-pwa-cache-and-offline-strategy.md` | ADR 0017 (DECISION: D-011) |

## Key PWA Behaviors

### 1. Offline Operation
- **Cache-First Strategy**: Shell assets (`index.html`, `.wasm`, `.dll`, `.css`, `.js`, icons) are cached on first install.
- **IndexedDB Persistence**: All user files, settings, and session data persist in browser IndexedDB independent of network status.
- **Offline Launch**: Returning users load instantly even when the web server is completely unreachable or offline.

### 2. Service Worker Registration & Atomic Updates
- **`updateViaCache: 'none'`**: Ensures the browser always checks the server for updated `service-worker.js` bytes rather than serving a stale HTTP-cached script.
- **Atomic Cache Naming**: Cache names use `hackeros-cache-${self.assetsManifest.version}`.
- **Stale Cache Purge**: `onActivate` deletes previous cache versions atomically before claiming clients. Old and new assembly versions are never mixed.
- **Safe Activation (`SKIP_WAITING`)**: When an update is installed, `hackeros-pwa-update-available` event is dispatched to trigger user notification or prompt before reload.

### 3. Historical Compatibility
- IndexedDB migrations support upgrading stored data across release versions without data loss.

## Task List Checklist

- [x] `P2-PWA-001` Add real 192/512 product icons, manifest name/short name, description, colors, `start_url`, `scope`, and `display`.
- [x] `P2-PWA-002` Register service worker with `updateViaCache: 'none'` in the published host.
- [x] `P2-PWA-003` Use generated service-worker asset manifest and atomic caches; do not disable integrity checking to mask deployment errors.
- [x] `P2-PWA-004` Define cache-first shell/static-asset strategy and network behavior for optional APIs in ADR 0017. **DECISION: D-011**
- [x] `P2-PWA-005` Implement update-available notification, safe activation, and reload flow without mixing old/new assets.
- [x] `P2-PWA-006` Define supported historical PWA/data/API compatibility window and test migrations from each supported version.
- [x] `P2-PWA-007` Test first online visit, installability, server unavailable, offline reload, app launch, file/settings persistence, update waiting, activation, and corrupt-cache recovery against published Release output. **Evidence (2026-08-17):** `Tests/HackerOs.Pwa.E2E.Tests/PublishedPwaTests.cs`, all 5 cases passing against a real `dotnet publish` of `OS/HackerOs.Ecosystem` — see `docs/phase-2-acceptance.md` `P2-ACC-015`/`P2-ACC-016` for exact test names. Chromium only (matches this repo's browser-support policy elsewhere); Firefox/Safari have no automated or manual coverage. This checkbox was previously checked without this evidence — see `docs/integration-audit-remediation.md` Wave C — and is now correctly backed by it.
