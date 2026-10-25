/**
 * HackerOS platform-environment probe interop. Reads raw browser signals only — no HackerOS
 * domain logic here (see doc/en/wasm/dev/adr/0015-browser-storage-and-indexeddb-adapter.md's JS-isolation
 * precedent). The decision of which platform these signals suggest is made in C#
 * (PlatformEnvironmentPolicy) so it stays unit-testable without a browser.
 */

function readSignals() {
    return {
        logicalWidth: Math.round(window.innerWidth || 0),
        logicalHeight: Math.round(window.innerHeight || 0),
        pointerIsCoarse: !!(window.matchMedia && window.matchMedia('(pointer: coarse)').matches),
        hasHover: !!(window.matchMedia && window.matchMedia('(hover: hover)').matches),
        maxTouchPoints: navigator.maxTouchPoints || 0,
        isStandalone: !!(window.matchMedia && window.matchMedia('(display-mode: standalone)').matches)
    };
}

export function readCurrentSignals() {
    return readSignals();
}

export function observeEnvironmentChanges(dotNetHelper) {
    const notify = () => dotNetHelper.invokeMethodAsync('OnEnvironmentChanged', readSignals());

    let resizeTimeout = null;
    const debouncedNotify = () => {
        if (resizeTimeout) {
            clearTimeout(resizeTimeout);
        }
        resizeTimeout = setTimeout(notify, 150);
    };

    window.addEventListener('resize', debouncedNotify);

    const pointerQuery = window.matchMedia ? window.matchMedia('(pointer: coarse)') : null;
    const hoverQuery = window.matchMedia ? window.matchMedia('(hover: hover)') : null;
    const standaloneQuery = window.matchMedia ? window.matchMedia('(display-mode: standalone)') : null;
    [pointerQuery, hoverQuery, standaloneQuery]
        .filter(query => query && query.addEventListener)
        .forEach(query => query.addEventListener('change', notify));

    return {
        dispose: () => {
            window.removeEventListener('resize', debouncedNotify);
            [pointerQuery, hoverQuery, standaloneQuery]
                .filter(query => query && query.removeEventListener)
                .forEach(query => query.removeEventListener('change', notify));
            if (resizeTimeout) {
                clearTimeout(resizeTimeout);
            }
        }
    };
}
