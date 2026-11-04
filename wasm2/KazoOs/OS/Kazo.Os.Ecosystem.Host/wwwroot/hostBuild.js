// ADR 0162: what C# cannot read by itself to tell which build of the OS is running and which one
// the origin serves now. C# extracts and compares the build identity (HostBuildIdentity).

const buildElements = 'script[type="importmap"], link[rel="preload"], link[rel="modulepreload"], script[src]';

/** The import map and script references of the running page: they name the booted dotnet.js. */
export function runningBuildMarkup() {
    return Array.from(document.querySelectorAll(buildElements), element => element.outerHTML).join('\n');
}

/**
 * The page as the origin serves it now, past the HTTP cache, or null when it cannot be reached
 * (offline, server down): that is not an update.
 */
export async function servedBuildMarkup() {
    try {
        const response = await fetch(document.baseURI, { cache: 'no-store', credentials: 'same-origin' });
        return response.ok ? await response.text() : null;
    } catch {
        return null;
    }
}
