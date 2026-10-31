// The wwwroot/ files of apps installed by hackpkg (ADR 0206), the page half of
// Apps/BrowserInstalledAppAssets.cs, imported by that class (never by a host, AGENTS.md).
//
// The OS copies each verified file into the Cache Storage cache below, under the address a
// built-in app uses (_content/<assembly>/<path>); the service worker (installedAppAssets.sw.js,
// imported by both service-worker files of the host) answers those addresses from it. An <img>, a
// <script src>, an import(), a CSS url(), a new Worker(...) and the importScripts of that worker
// then all find the file, with no change to the app.

const cacheName = 'hackeros-installed-apps';
// index.html registers no service worker on these hosts (development); the OS then registers one
// that only serves installed apps (no release cache), recognised by this query.
const installedAppsOnlyQuery = 'mode=installed-apps';

function isDevelopmentHost() {
    const host = window.location.hostname;
    return host.includes('localhost') || host.includes('127.0.0.1');
}

function absolute(url) {
    return new URL(url, document.baseURI).href;
}

/**
 * Makes sure a service worker controls the page and returns whether one does. On a development
 * host, registers the "installed apps only" worker first. Waits at most timeoutMs for it to take
 * control (clients.claim on activation).
 */
export async function prepare(timeoutMs) {
    if (!('serviceWorker' in navigator) || !('caches' in window)) {
        return false;
    }

    const container = navigator.serviceWorker;
    if (container.controller) {
        return true;
    }

    const controlled = new Promise(resolve => {
        const timer = setTimeout(() => resolve(!!container.controller), timeoutMs);
        container.addEventListener('controllerchange', () => { clearTimeout(timer); resolve(true); }, { once: true });
    });
    if (isDevelopmentHost()) {
        try {
            await container.register(`service-worker.js?${installedAppsOnlyQuery}`, { updateViaCache: 'none' });
        } catch (error) {
            console.warn('HackerOS: the service worker of installed apps could not be registered', error);
            return false;
        }
    }

    return await controlled;
}

/** Lists the cached addresses, relative to the page's base (for example _content/Kazo.Sample/x.js). */
export async function list() {
    const cache = await caches.open(cacheName);
    const base = new URL('.', document.baseURI).href;
    return JSON.stringify((await cache.keys())
        .map(request => request.url)
        .filter(url => url.startsWith(base))
        .map(url => url.substring(base.length)));
}

/** Stores one file under its address, with its content type. */
export async function put(url, bytes, contentType) {
    const cache = await caches.open(cacheName);
    await cache.put(absolute(url), new Response(new Uint8Array(bytes), {
        headers: {
            'Content-Type': contentType,
            // Same-origin files may be embedded, so a worker or frame under COEP require-corp loads them.
            'Cross-Origin-Resource-Policy': 'same-origin',
            'Cache-Control': 'no-cache'
        }
    }));
}

/** Removes one address; true when it was cached. */
export async function remove(url) {
    const cache = await caches.open(cacheName);
    return await cache.delete(absolute(url));
}

/**
 * Links a style sheet the cache serves (an installed app's scoped CSS, ADR 0216 §2) at the end of the
 * page's <head>, as Blazor's own styles.css links a built-in app's. Once per address: false when it
 * was already linked.
 */
export function linkStyleSheet(url) {
    const href = absolute(url);
    const linked = [...document.head.querySelectorAll('link[data-hackeros-installed-app]')];
    if (linked.some(link => link.href === href)) {
        return false;
    }

    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    link.dataset.hackerosInstalledApp = '';
    document.head.appendChild(link);
    return true;
}
