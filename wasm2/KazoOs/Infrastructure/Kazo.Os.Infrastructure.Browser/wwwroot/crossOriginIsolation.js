// Cross-origin isolation of the page (ADR 0131), the browser half of
// Isolation/BrowserCrossOriginIsolation.cs, imported by that class (never by a host, AGENTS.md).
//
// The OS decides on crossOriginIsolated in the page, never on its setting alone: probe() reports
// what the browser really did, and asks the PWA's service worker (service-worker.published.js) what
// it sent, so a missing header (no service worker yet, a proxy) is told apart from a browser that
// ignored it. When the browser ignored COEP "credentialless", the page asks the worker for
// "require-corp" and reloads once (ADR 0131, section 2).

const strictGuardKey = 'hackeros.isolationStrictReload';

/** Asks the controlling service worker one question on a MessageChannel; null after timeoutMs. */
function askServiceWorker(type, timeoutMs) {
    const worker = navigator.serviceWorker && navigator.serviceWorker.controller;
    if (!worker) {
        return Promise.resolve(null);
    }

    return new Promise(resolve => {
        const channel = new MessageChannel();
        const timer = setTimeout(() => resolve(null), timeoutMs);
        channel.port1.onmessage = event => { clearTimeout(timer); resolve(event.data || null); };
        worker.postMessage({ type }, [channel.port2]);
    });
}

/**
 * The smallest step of performance.now() seen in a short busy loop, in microseconds: 5 µs when the
 * page is isolated, 100 µs otherwise (clamped by the browser against timing attacks).
 */
function timerResolutionMicroseconds() {
    let smallest = Infinity;
    let last = performance.now();
    const end = last + 3;
    while (true) {
        const now = performance.now();
        if (now !== last) {
            smallest = Math.min(smallest, now - last);
            last = now;
        }
        if (now >= end) {
            break;
        }
    }
    return Number.isFinite(smallest) ? Math.round(smallest * 1000 * 100) / 100 : 0;
}

function readGuard() {
    try { return !!sessionStorage.getItem(strictGuardKey); } catch { return true; }
}

function setGuard() {
    try { sessionStorage.setItem(strictGuardKey, String(Date.now())); } catch { }
}

/**
 * Returns the JSON text of what the page found. wanted is whether the host's setting asks for
 * isolation. "reloading": true means the page is about to reload once with require-corp.
 */
export async function probe(wanted) {
    const isolated = globalThis.crossOriginIsolated === true;
    const worker = await askServiceWorker('COI_STATE', 1500);
    const result = {
        isolated,
        secureContext: globalThis.isSecureContext === true,
        controlled: !!(navigator.serviceWorker && navigator.serviceWorker.controller),
        workerSends: worker ? worker.sends === true : null,
        workerEmbedderPolicy: worker ? worker.embedderPolicy || null : null,
        sharedArrayBuffer: typeof globalThis.SharedArrayBuffer === 'function' && isolated,
        measureMemory: isolated && typeof performance.measureUserAgentSpecificMemory === 'function',
        timerResolutionUs: timerResolutionMicroseconds(),
        reloading: false
    };

    if (wanted && !isolated && worker && worker.sends && worker.embedderPolicy === 'credentialless' && !readGuard()) {
        setGuard();
        const strict = await askServiceWorker('COI_STRICT', 3000);
        if (strict && strict.embedderPolicy === 'require-corp') {
            result.reloading = true;
            setTimeout(() => location.reload(), 0);
        }
    }

    return JSON.stringify(result);
}
