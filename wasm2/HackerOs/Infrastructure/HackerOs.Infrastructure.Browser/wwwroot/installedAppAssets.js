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
 *
 * A page loaded with Ctrl+F5 is not controlled even though a worker is active (the browser bypasses
 * it for that load). The page then asks that worker to take control (ADR 0225), which it does only
 * when it caches the release the page runs.
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
    let registration = null;
    if (isDevelopmentHost()) {
        try {
            registration = await container.register(`service-worker.js?${installedAppsOnlyQuery}`, { updateViaCache: 'none' });
        } catch (error) {
            console.warn('HackerOS: the service worker of installed apps could not be registered', error);
            return false;
        }
    } else {
        registration = await container.getRegistration().catch(() => null);
    }

    // An active worker never activates again, so nothing else would make it claim this page.
    if (registration && registration.active && !container.controller) {
        const answer = await askToClaim(registration.active, isDevelopmentHost() ? null : await serverReleaseVersion(), timeoutMs);
        if (!answer.claimed) {
            console.warn('HackerOS: the service worker does not serve this page, installed apps keep their wwwroot/ files until the next restart', answer);
            return !!container.controller;
        }
    }

    return await controlled;
}

/** The version of the release the server delivers now, the one a page loaded with Ctrl+F5 runs (ADR 0225). */
async function serverReleaseVersion() {
    try {
        const response = await fetch('service-worker-assets.js', { cache: 'no-store' });
        const match = response.ok ? /"version"\s*:\s*"([^"]+)"/.exec(await response.text()) : null;
        return match ? match[1] : null;
    } catch {
        return null;
    }
}

/** Asks a worker to take control of this page; resolves to its answer, { claimed: false } on silence. */
function askToClaim(worker, version, timeoutMs) {
    return new Promise(resolve => {
        const channel = new MessageChannel();
        const timer = setTimeout(() => resolve({ claimed: false, reason: 'no-answer' }), timeoutMs);
        channel.port1.onmessage = event => {
            clearTimeout(timer);
            const answer = event.data || { claimed: false };
            if (!answer.claimed && window.hackerOsClaimForInstalledApps === 'pending') {
                window.hackerOsClaimForInstalledApps = undefined;
            }
            resolve(answer);
        };
        // index.html reloads a page whose controller changes; this change is expected (same release).
        window.hackerOsClaimForInstalledApps = 'pending';
        worker.postMessage({ type: 'HACKEROS_CLAIM_FOR_INSTALLED_APPS', version }, [channel.port2]);
    });
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
 * Links a style sheet the cache serves (an installed app's scoped CSS, ADR 0221 §2) at the end of the
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
