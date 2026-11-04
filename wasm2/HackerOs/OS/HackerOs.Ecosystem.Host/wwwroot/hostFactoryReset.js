// ADR 0227: a factory reset (Settings ▸ System, or « Forgot password? » on the sign-in screen,
// ADR 0226) leaves nothing of KazoOS in the browser. Called by BrowserSystemPowerDevice once the
// session has ended and the logs were flushed; the C# side reloads the page right after.

const deleteWaitMs = 5000;

/**
 * Erases everything this origin keeps in the browser: every IndexedDB database, every Cache Storage
 * cache, localStorage, sessionStorage, the origin private file system, the cookies readable by
 * script and the service worker registrations. Each step is independent: one that fails does not
 * stop the others.
 * @returns {Promise<string[]>} The steps that failed (empty when everything was erased).
 */
export async function eraseEverything() {
    const failed = [];
    const step = async (name, action) => {
        try { await action(); } catch (error) { failed.push(`${name}: ${error && error.message ? error.message : error}`); }
    };

    await step('indexedDB', deleteAllDatabases);
    await step('caches', deleteAllCaches);
    await step('localStorage', () => localStorage.clear());
    await step('sessionStorage', () => sessionStorage.clear());
    await step('opfs', clearOriginPrivateFileSystem);
    await step('cookies', expireCookies);
    await step('serviceWorker', unregisterServiceWorkers);
    return failed;
}

async function deleteAllDatabases() {
    if (!self.indexedDB || typeof indexedDB.databases !== 'function') {
        return;
    }

    const databases = await indexedDB.databases();
    await Promise.all(databases.filter(database => database.name).map(database => deleteDatabase(database.name)));
}

// A connection still open elsewhere (another tab) blocks the deletion until it closes; the request
// stays queued and completes then, or when this page reloads. Waiting for it here could last forever.
function deleteDatabase(name) {
    return new Promise((resolve, reject) => {
        const request = indexedDB.deleteDatabase(name);
        const timer = setTimeout(resolve, deleteWaitMs);
        request.onsuccess = () => { clearTimeout(timer); resolve(); };
        request.onerror = () => { clearTimeout(timer); reject(request.error); };
    });
}

async function deleteAllCaches() {
    if (!self.caches) {
        return;
    }

    const keys = await caches.keys();
    await Promise.all(keys.map(key => caches.delete(key)));
}

async function clearOriginPrivateFileSystem() {
    if (!navigator.storage || typeof navigator.storage.getDirectory !== 'function') {
        return;
    }

    const root = await navigator.storage.getDirectory();
    const names = [];
    for await (const name of root.keys()) {
        names.push(name);
    }

    await Promise.all(names.map(name => root.removeEntry(name, { recursive: true })));
}

function expireCookies() {
    for (const cookie of document.cookie.split(';')) {
        const name = cookie.split('=')[0].trim();
        if (name) {
            document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/`;
            document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 GMT`;
        }
    }
}

async function unregisterServiceWorkers() {
    if (!navigator.serviceWorker || typeof navigator.serviceWorker.getRegistrations !== 'function') {
        return;
    }

    const registrations = await navigator.serviceWorker.getRegistrations();
    await Promise.all(registrations.map(registration => registration.unregister()));
}
