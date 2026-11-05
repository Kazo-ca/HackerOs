// Web Workers of Kazo OS (ADR 0070, 0071), the page half of Workers/BrowserWebWorkerRuntime.cs,
// imported by that class through JSHost.ImportAsync. Never referenced by a host (AGENTS.md).
//
// Each worker is a module worker running workerBootstrap.js, which starts its own .NET runtime from
// the same _framework/dotnet.js as the page (cold start 0.5 to 2 s) and hands every message to the
// worker host's exported entry point. Between the page and a worker, messages are plain strings (the
// JSON of the WorkerHost protocol); the only objects are the bootstrap's own control messages.
//
// The JavaScript does nothing else: placement, heartbeats and kills are decided in C#. Terminating a
// worker (worker.terminate()) is the only way to stop .NET code that never yields (ADR 0069, step 4).
//
// Cross-origin isolated page (ADR 0131, section 4): each worker also gets two shared rings and a
// synchronous call slot (sharedRing.js). Messages then travel through the rings in both directions;
// a message that arrives by postMessage is handed to .NET only after the ring is drained, so the
// order never changes. The worker's URL carries its id (?kazoos-worker=N) so that
// performance.measureUserAgentSpecificMemory() can attribute memory to it.

import { ringsAvailable, createRingBuffer, createSyncBuffer, SharedRing, writeSyncAnswer } from './sharedRing.js';

const workers = new Map();
let nextId = 0;

/** Resolves a path of the app through the page's import map (fingerprinted file names), if any. */
function resolveAsset(path) {
    const plain = new URL(path, document.baseURI).href;
    try {
        return import.meta.resolve(plain);
    } catch {
        return plain;
    }
}

/** navigator.hardwareConcurrency: the ceiling of the worker count (ADR 0071, section 4). */
export function hardwareConcurrency() {
    return navigator.hardwareConcurrency || 2;
}

/** Resolves once the browser is idle (requestIdleCallback), or after timeoutMs at the latest. */
export function whenIdle(timeoutMs) {
    return new Promise(resolve => {
        if (typeof globalThis.requestIdleCallback === 'function') {
            globalThis.requestIdleCallback(() => resolve(), { timeout: timeoutMs });
        } else {
            setTimeout(resolve, Math.min(timeoutMs, 50));
        }
    });
}

/**
 * Starts one worker and returns its id. onEvent(kind, payload) is called with:
 * "ready" once its runtime is up, "message" for every message it posts, "error" if it fails.
 */
export function startWorker(name, entryAssembly, entryType, onEvent) {
    const id = ++nextId;
    const bootstrapUrl = new URL(resolveAsset('_content/Kazo.Os.Infrastructure.Browser/workerBootstrap.js'));
    bootstrapUrl.searchParams.set('kazoos-worker', String(id));
    const worker = new Worker(bootstrapUrl.href, { type: 'module', name });
    // toWorker is used only once the worker said it reads its ring (its "ready" message).
    const entry = { worker, toWorker: null, fromWorker: null, sync: null, ringToWorker: null };
    let rings = null;
    if (ringsAvailable()) {
        rings = { toOs: createRingBuffer(), fromOs: createRingBuffer(), sync: createSyncBuffer() };
        entry.fromWorker = new SharedRing(rings.toOs, () => { });
        entry.ringToWorker = new SharedRing(rings.fromOs, () => worker.postMessage({ __kazoos: 'ring' }));
        entry.sync = rings.sync;
    }
    workers.set(id, entry);
    const deliver = message => onEvent('message', message);
    worker.onmessage = event => {
        const data = event.data;
        if (typeof data === 'string') {
            entry.fromWorker?.drain(deliver);
            deliver(data);
        } else if (data && data.__kazoos === 'ring') {
            entry.fromWorker?.drain(deliver);
        } else if (data && data.__kazoos === 'ready') {
            entry.toWorker = data.rings === true ? entry.ringToWorker : null;
            onEvent('ready', '');
        } else if (data && data.__kazoos === 'error') {
            onEvent('error', String(data.message));
        }
    };
    worker.onerror = event => {
        event.preventDefault();
        onEvent('error', event.message || 'worker script error');
    };
    worker.postMessage({
        __kazoos: 'boot',
        dotnetUrl: resolveAsset('_framework/dotnet.js'),
        ringUrl: resolveAsset('_content/Kazo.Os.Infrastructure.Browser/sharedRing.js'),
        rings,
        entryAssembly,
        entryType
    });
    return id;
}

/** Posts one protocol message to a worker: through its ring when it fits, else by postMessage. */
export function post(id, message) {
    const entry = workers.get(id);
    if (entry && !(entry.toWorker && entry.toWorker.write(message))) {
        entry.worker.postMessage(message);
    }
}

/** Whether the workers of this page use shared rings and may call the OS synchronously. */
export function sharedChannels() {
    return ringsAvailable();
}

/**
 * Answers synchronous call `callId` of a worker waiting in Atomics.wait: "ok", "late" (it gave up
 * or the worker is gone) or "too-large".
 */
export function answerSync(id, callId, message) {
    const entry = workers.get(id);
    return entry && entry.sync ? writeSyncAnswer(entry.sync, callId, message) : 'late';
}

/**
 * Memory of each worker in bytes, as JSON {"id": bytes}, from
 * performance.measureUserAgentSpecificMemory() (ADR 0131, section 4); "" when the browser does not
 * offer it here (not isolated, not Chromium). The browser may take several seconds to answer.
 */
export async function measureMemory() {
    if (globalThis.crossOriginIsolated !== true || typeof performance.measureUserAgentSpecificMemory !== 'function') {
        return '';
    }

    let result;
    try {
        result = await performance.measureUserAgentSpecificMemory();
    } catch {
        return '';
    }

    const perWorker = {};
    for (const entry of result.breakdown || []) {
        const attributions = entry.attribution || [];
        for (const attribution of attributions) {
            if (attribution.scope !== 'DedicatedWorkerGlobalScope' || !attribution.url) {
                continue;
            }
            const workerId = new URL(attribution.url).searchParams.get('kazoos-worker');
            if (workerId) {
                perWorker[workerId] = (perWorker[workerId] || 0) + entry.bytes / attributions.length;
            }
        }
    }
    return JSON.stringify(perWorker);
}

/** Stops a worker at once, whatever it is running. */
export function terminate(id) {
    const entry = workers.get(id);
    if (entry) {
        workers.delete(id);
        entry.worker.terminate();
    }
}
