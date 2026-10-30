// The wwwroot/ files of apps installed by hackpkg (ADR 0207), the service worker half of
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

    return { cacheName, installedAppsOnly, handles, match, respond };
})();
