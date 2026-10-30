// HackerOS Progressive Web App Service Worker (Published / Production Mode)
// Implements Cache-First offline shell strategy, atomic cache versioning,
// asset integrity checking, and safe update activation (P2-PWA-002 through P2-PWA-005),
// plus the corrupted-cache recovery of ADR 0099 §3 (RESET_CACHE message, see onMessage)
// and the cross-origin isolation headers of ADR 0131 §2 (see withIsolationHeaders), and the
// wwwroot/ files of installed apps of ADR 0206 (see installedAppAssets.sw.js).

self.importScripts('./service-worker-assets.js');
self.importScripts('./_content/HackerOs.Infrastructure.Browser/installedAppAssets.sw.js');
const installedAppAssets = self.hackerOsInstalledAppAssets;

self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => {
    // ADR 0206: registered by the OS on a development host, this worker only serves installed apps.
    if (installedAppAssets.installedAppsOnly) {
        if (installedAppAssets.handles(event.request)) event.respondWith(installedAppAssets.respond(event.request));
        return;
    }

    const url = new URL(event.request.url);
    const legacyPath = new URL('legacy/', self.registration.scope).pathname;
    // Pages also hosts the original application. Let its requests reach the network:
    // the WASM navigation fallback must never replace legacy HTML with index.html.
    if (url.origin === self.location.origin
        && (url.pathname === legacyPath.slice(0, -1) || url.pathname.startsWith(legacyPath))) return;
    event.respondWith(onFetch(event));
});
self.addEventListener('message', event => onMessage(event));

const cacheNamePrefix = 'hackeros-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
// Fonts (.ttf/.woff2) are listed so lazy-loaded apps keep their icons offline: the Code
// Editor ships Monaco's codicon.ttf next to its bundle (v3/11, ADR 0103 §3).
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff2?$/, /\.ttf$/, /\.svg$/, /\.webp$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/ ];
// hackeros-host.json is never cached (ADR 0137): HackerOs.Server answers that path itself
// ("a server delivered this OS"), so its bytes differ from the static file's integrity hash.
const offlineAssetsExclude = [ /^service-worker\.js$/, /^hackeros-host\.json$/ ];

// Base URL for relative asset resolution
const base = "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

// ADR 0131 §2: the worker adds COOP/COEP to the same-origin responses it serves, navigation
// included, so the PWA is cross-origin isolated offline and on a static host that cannot set
// headers. HackerOs:CrossOriginIsolation in appsettings.json decides: "off" adds nothing, "on"
// always adds them, "auto" adds them when the audit passed for this release. The audit result is
// the constant CrossOriginIsolationPolicy.AuditPassed (Platform.Core); a unit test keeps both equal.
const crossOriginIsolationAuditPassed = true;
// The page asks once for require-corp when the browser ignored credentialless (COI_STRICT); the
// choice is kept in this cache, under a synthetic key, across worker restarts.
const isolationConfigCache = 'hackeros-isolation';
const isolationConfigKey = new URL('__hackeros/isolation-config', baseUrl).href;
let isolationState = null;

// Reads the setting from the cached appsettings.json (the network when it is not cached yet) and
// the stored COEP choice. Called on every navigation, so a changed setting applies at the next load.
async function loadIsolationState() {
    let mode = 'auto';
    try {
        const cache = await caches.open(cacheName);
        const response = await cache.match('appsettings.json') || await fetch('appsettings.json', { cache: 'no-store' });
        if (response && response.ok) {
            const settings = JSON.parse((await response.text()).replace(/^\uFEFF/, ''));
            const value = settings && settings.HackerOs && settings.HackerOs.CrossOriginIsolation;
            if (typeof value === 'string' && /^(on|off|auto)$/i.test(value.trim())) {
                mode = value.trim().toLowerCase();
            }
        }
    } catch (error) {
        console.warn('HackerOS Service Worker: appsettings.json unreadable, cross-origin isolation stays "auto"', error);
    }

    let embedderPolicy = 'credentialless';
    try {
        const stored = await (await caches.open(isolationConfigCache)).match(isolationConfigKey);
        if (stored && (await stored.json()).embedderPolicy === 'require-corp') {
            embedderPolicy = 'require-corp';
        }
    } catch {
        // No stored choice: credentialless.
    }

    const sends = mode === 'on' || (mode === 'auto' && crossOriginIsolationAuditPassed);
    isolationState = { mode, sends, embedderPolicy };
    return isolationState;
}

// Copies a same-origin response with the two headers. Opaque (cross-origin no-cors) and error
// responses cannot be rebuilt and pass through untouched; COEP credentialless accepts them.
async function withIsolationHeaders(response, refresh) {
    const state = refresh || !isolationState ? await loadIsolationState() : isolationState;
    if (!state.sends || !response || response.type === 'opaque' || response.type === 'opaqueredirect' || response.status === 0) {
        return response;
    }

    const headers = new Headers(response.headers);
    headers.set('Cross-Origin-Opener-Policy', 'same-origin');
    headers.set('Cross-Origin-Embedder-Policy', state.embedderPolicy);
    // Same-origin resources declare themselves embeddable, so a frame or worker under require-corp loads them.
    headers.set('Cross-Origin-Resource-Policy', 'same-origin');
    return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
}

async function onInstall(event) {
    if (installedAppAssets.installedAppsOnly) {
        // No release cache on a development host: take over at once, the OS waits for it.
        await self.skipWaiting();
        return;
    }

    console.info('HackerOS Service Worker: Installing release asset cache', cacheName);

    const cache = await caches.open(cacheName);
    await cache.addAll(buildAssetRequests());
}

// Fetch requests for every cacheable item of the assets manifest, with integrity verification (P2-PWA-003).
function buildAssetRequests() {
    return self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
}

async function onActivate(event) {
    console.info('HackerOS Service Worker: Activating cache version', cacheName);

    // Delete all stale/previous version caches atomically to avoid mixing old and new release assets (P2-PWA-005)
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));

    await self.clients.claim();
}

async function onFetch(event) {
    if (event.request.method !== 'GET') {
        return fetch(event.request);
    }

    const response = await fetchShellOrAsset(event);
    // Only this origin's responses: a cross-origin one keeps its own headers.
    return new URL(event.request.url).origin === self.origin
        ? withIsolationHeaders(response, event.request.mode === 'navigate')
        : response;
}

async function fetchShellOrAsset(event) {
    // Cache-First strategy for static assets and HTML shell (P2-PWA-004)
    const shouldServeIndexHtml = event.request.mode === 'navigate'
        && !manifestUrlList.some(url => url === event.request.url);

    // ADR 0206: an installed app's file, copied by the OS, comes before the release cache.
    if (!shouldServeIndexHtml && installedAppAssets.handles(event.request)) {
        const installed = await installedAppAssets.match(event.request);
        if (installed) {
            return installed;
        }
    }

    const request = shouldServeIndexHtml ? 'index.html' : event.request;
    const cache = await caches.open(cacheName);
    // A Web Worker's script carries its id in the query (?hackeros-worker=N, ADR 0131 §4, memory
    // attribution): it is the same cached file.
    const ignoreSearch = !shouldServeIndexHtml && new URL(event.request.url).searchParams.has('hackeros-worker');
    const cachedResponse = await cache.match(request, { ignoreSearch });

    if (cachedResponse) {
        return cachedResponse;
    }

    try {
        const networkResponse = await fetch(event.request);
        return networkResponse;
    } catch (error) {
        // Offline, the host discovery answers "no server reachable" instead of failing (ADR 0137).
        if (new URL(event.request.url).pathname.endsWith('/hackeros-host.json')) {
            return new Response('{"product":"HackerOS","isServer":false}',
                { headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' } });
        }
        // When network is unavailable and request is navigation, serve cached shell
        if (event.request.mode === 'navigate') {
            const fallbackShell = await cache.match('index.html');
            if (fallbackShell) {
                return fallbackShell;
            }
        }
        throw error;
    }
}

function onMessage(event) {
    if (event.data && event.data.type === 'COI_STATE') {
        // ADR 0131 §5: the page asks what this worker sends, to tell "missing headers" from "browser".
        const port = event.ports && event.ports[0];
        event.waitUntil(loadIsolationState().then(state => port && port.postMessage(state)));
    } else if (event.data && event.data.type === 'COI_STRICT') {
        // The browser ignored credentialless: require-corp from now on, for this origin.
        const port = event.ports && event.ports[0];
        event.waitUntil(caches.open(isolationConfigCache)
            .then(cache => cache.put(isolationConfigKey, new Response(JSON.stringify({ embedderPolicy: 'require-corp' }),
                { headers: { 'Content-Type': 'application/json' } })))
            .then(loadIsolationState)
            .then(state => port && port.postMessage(state)));
    } else if (event.data && event.data.type === 'SKIP_WAITING') {
        console.info('HackerOS Service Worker: Received SKIP_WAITING signal');
        self.skipWaiting();
    } else if (event.data && event.data.type === 'RESET_CACHE') {
        // ADR 0099 §3: the page's boot failed on an integrity or load error. Reply on the
        // MessageChannel port the page passed, so it knows whether to reload.
        const port = event.ports && event.ports[0];
        event.waitUntil(resetCache()
            .catch(error => ({ ok: false, reason: 'error', message: String(error) }))
            .then(result => port && port.postMessage(result)));
    }
}

// Empties the release cache and refills it from the server (ADR 0099 §3).
// - Server unreachable (offline): the damaged cache is kept as it is, so the shell can still
//   show the "reconnect once" screen offline; reply { ok: false, reason: 'unreachable' }.
// - Otherwise every asset is downloaded again, integrity-checked, into a staging cache that
//   then replaces the damaged one. If the server cannot give a verified copy of this release
//   (e.g. a newer release is deployed), the damaged cache is emptied anyway: resources then
//   come straight from the server, and the page's one-reload guard prevents any loop.
async function resetCache() {
    console.warn('HackerOS Service Worker: Rebuilding damaged cache', cacheName);
    try {
        await fetch(new Request('service-worker-assets.js', { cache: 'no-store' }));
    } catch {
        return { ok: false, reason: 'unreachable' };
    }

    // Starts with cacheNamePrefix, so onActivate also purges a staging cache left half-written.
    const stagingName = `${cacheName}-repair`;
    await caches.delete(stagingName);
    const staging = await caches.open(stagingName);
    let refilled = true;
    try {
        await staging.addAll(buildAssetRequests());
    } catch (error) {
        console.warn('HackerOS Service Worker: Could not download a verified copy of this release', error);
        refilled = false;
    }

    await caches.delete(cacheName);
    if (refilled) {
        const cache = await caches.open(cacheName);
        for (const request of await staging.keys()) {
            await cache.put(request, await staging.match(request));
        }
    }
    await caches.delete(stagingName);
    return { ok: true, refilled };
}
