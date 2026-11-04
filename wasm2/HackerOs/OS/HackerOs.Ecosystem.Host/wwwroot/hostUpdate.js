// ADR 0223: « Redémarrer pour mettre à jour » lands on the new version. The previous service worker
// serves every file of its own version (page included) and a reload keeps it in control, so the OS
// downloads the new version into a waiting worker, activates it once the session has ended, then
// reloads. The decisions stay in C# (HostUpdateInstaller); this module only reaches the browser's
// service worker API, which C# cannot call.

const installTimeoutMs = 180000;
const activateTimeoutMs = 15000;

// A page that had no controller at load (first visit, Ctrl+F5) is claimed by the worker without an
// update: index.html then reloads it as before.
const hadController = !!(navigator.serviceWorker && navigator.serviceWorker.controller);
let activating = null;
let dotnet = null;

/**
 * Called by index.html's controllerchange listener. Returns true when the OS handles the change,
 * so index.html does not reload the page abruptly.
 * @returns {boolean}
 */
function onControllerChange() {
    if (activating) {
        // This tab activated the new worker: the C# restart reloads once the session has ended.
        const resolve = activating;
        activating = null;
        resolve(true);
        return true;
    }

    // ADR 0225: a Ctrl+F5 page the worker took later to serve an installed app counts as controlled.
    if ((hadController || window.hackerOsClaimForInstalledApps === 'done') && dotnet) {
        // Another tab activated it: restart cleanly (ADR 0223, question 3).
        dotnet.invokeMethodAsync('OnActivatedElsewhere').catch(() => window.location.reload());
        return true;
    }

    return false;
}

/**
 * Starts handling controller changes for the OS.
 * @param {object} reference DotNetObjectReference with an OnActivatedElsewhere method.
 */
export function listen(reference) {
    dotnet = reference;
    window.hackerOsControllerChangeHandler = onControllerChange;
}

// The site's registration, whether or not it controls this page. A page loaded with Ctrl+F5 is
// not controlled, yet the next reload goes to the registration's active worker: when another
// KazoOS tab or window keeps the previous worker active, the new one waits, and only
// SKIP_WAITING lets the restart land on the new version (ADR 0223).
async function registration() {
    if (!('serviceWorker' in navigator)) {
        return null;
    }

    const found = await navigator.serviceWorker.getRegistration();
    return found && (found.active || found.waiting) ? found : null;
}

// Resolves 'ready' once a new worker has installed (its cache is filled and integrity-checked),
// 'up-to-date' when it became active meanwhile, 'unavailable' when it failed or took too long.
function waitInstalled(found, worker, timeoutMs) {
    return new Promise(resolve => {
        const timer = setTimeout(() => finish('unavailable'), timeoutMs);
        let current = null;
        function finish(result) {
            clearTimeout(timer);
            if (current) current.removeEventListener('statechange', check);
            resolve(result);
        }
        function follow(next) {
            if (current) current.removeEventListener('statechange', check);
            current = next;
            current.addEventListener('statechange', check);
            check();
        }
        function check() {
            if (found.waiting) return finish('ready');
            if (current.state === 'activating' || current.state === 'activated') return finish('up-to-date');
            if (current.state === 'redundant') {
                // Failed install, or replaced by an even newer worker: follow that one.
                return found.installing ? follow(found.installing) : finish('unavailable');
            }
        }
        follow(worker);
    });
}

/**
 * Asks the browser to check the served worker script and waits for a new version to download.
 * @returns {Promise<string>} 'no-worker', 'up-to-date', 'ready' or 'unavailable'.
 */
export async function download() {
    try {
        const found = await registration();
        if (!found) {
            return 'no-worker';
        }

        if (!found.waiting) {
            try {
                await found.update();
            } catch {
                // Offline, or the worker script could not be fetched.
                return 'unavailable';
            }
        }

        if (found.waiting) {
            return 'ready';
        }

        return found.installing ? await waitInstalled(found, found.installing, installTimeoutMs) : 'up-to-date';
    } catch {
        return 'unavailable';
    }
}

/**
 * Activates the waiting worker and waits until it controls this page. Does not reload.
 * @returns {Promise<boolean>}
 */
export async function activate() {
    try {
        const found = await registration();
        const waiting = found && found.waiting;
        if (!waiting) {
            return false;
        }

        const changed = new Promise(resolve => { activating = resolve; });
        waiting.postMessage({ type: 'SKIP_WAITING' });
        const timeout = new Promise(resolve => setTimeout(() => resolve(false), activateTimeoutMs));
        const result = await Promise.race([changed, timeout]);
        activating = null;
        return result;
    } catch {
        activating = null;
        return false;
    }
}
