// The wwwroot/ files of apps installed by hackpkg (ADR 0206), the service worker half of
// installedAppAssets.js. A classic script, loaded with importScripts by both service-worker files of
// the host (service-worker.js in development, service-worker.published.js once published).
//
// The page copies each verified file into the 'hackeros-installed-apps' cache under the address a
// built-in app uses (_content/<assembly>/<path>). This script answers those addresses from it, and
// lets every other request through. The query of a request is ignored (a worker script carries
// ?hackeros-worker=N, ADR 0131 §4).

self.hackerOsInstalledAppAssets = (() => {
    const cacheName = 'hackeros-installed-apps';
    // Registered by the OS on a development host, where index.html registers no worker: then only
    // installed apps are served, the release cache is never filled (installedAppAssets.js).
    const installedAppsOnly = new URL(self.location.href).searchParams.get('mode') === 'installed-apps';
    let pageIsolation = null;

    function contentPath() {
        return new URL('_content/', self.registration.scope).pathname;
    }

    /** Whether the request may be an installed app's file; synchronous, for respondWith. */
    function handles(request) {
        if (request.method !== 'GET') {
            return false;
        }

        const url = new URL(request.url);
        return url.origin === self.location.origin && url.pathname.startsWith(contentPath());
    }

    /** The cached file of an installed app, or null when the address is not one. */
    async function match(request) {
        const url = new URL(request.url);
        const cache = await caches.open(cacheName);
        return (await cache.match(url.origin + url.pathname)) || null;
    }

    // In "installed apps only" mode nothing else adds COOP/COEP: the files take the page's own, read
    // once from the server, so a worker started by an isolated page is allowed to run.
    async function readPageIsolation() {
        if (!pageIsolation) {
            try {
                const page = await fetch(self.registration.scope, { method: 'HEAD', cache: 'no-store' });
                pageIsolation = {
                    opener: page.headers.get('Cross-Origin-Opener-Policy'),
                    embedder: page.headers.get('Cross-Origin-Embedder-Policy')
                };
            } catch {
                pageIsolation = {};
            }
        }

        return pageIsolation;
    }

    async function withPageIsolation(response) {
        const isolation = await readPageIsolation();
        if (!isolation.opener && !isolation.embedder) {
            return response;
        }

        const headers = new Headers(response.headers);
        if (isolation.opener) headers.set('Cross-Origin-Opener-Policy', isolation.opener);
        if (isolation.embedder) headers.set('Cross-Origin-Embedder-Policy', isolation.embedder);
        return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
    }

    /** Answers a request handles() accepted: the cached file, otherwise the network. */
    async function respond(request) {
        const cached = await match(request);
        if (!cached) {
            return fetch(request);
        }

        return installedAppsOnly ? withPageIsolation(cached) : cached;
    }

    /**
     * ADR 0225: whether this worker may take control of a page that has none (a page loaded with
     * Ctrl+F5 bypasses the worker). Always in "installed apps only" mode and in development, where the
     * worker keeps no release cache. Otherwise only when the page runs the release this worker caches
     * (pageVersion is the version of the server's service-worker-assets.js): a page of another release
     * must not receive this worker's cached files.
     */
    function mayClaim(pageVersion) {
        const workerVersion = self.assetsManifest && self.assetsManifest.version;
        return installedAppsOnly || !workerVersion || pageVersion === workerVersion;
    }

    // ADR 0225: the OS asks for control before it copies an installed app's files (installedAppAssets.js).
    self.addEventListener('message', event => {
        if (!event.data || event.data.type !== 'HACKEROS_CLAIM_FOR_INSTALLED_APPS') {
            return;
        }

        const port = event.ports && event.ports[0];
        const workerVersion = (self.assetsManifest && self.assetsManifest.version) || null;
        if (!mayClaim(event.data.version)) {
            if (port) port.postMessage({ claimed: false, workerVersion });
            return;
        }

        event.waitUntil(self.clients.claim()
            .then(() => ({ claimed: true, workerVersion }))
            .catch(error => ({ claimed: false, workerVersion, message: String(error) }))
            .then(result => port && port.postMessage(result)));
    });

    return { cacheName, installedAppsOnly, handles, match, respond, mayClaim };
})();
