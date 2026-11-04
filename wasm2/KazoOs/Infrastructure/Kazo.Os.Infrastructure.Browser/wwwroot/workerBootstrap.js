// Bootstrap of one Kazo OS Web Worker (ADR 0070, section 4), started by workerHost.js.
//
// It loads the page's own .NET runtime (_framework/dotnet.js) into this worker, then forwards every
// message to the exported entry point of Kazo.Os.Platform.WorkerHost (BrowserWorkerEntryPoint), and
// gives .NET a `post` function back. Messages that arrive while the runtime loads are queued.
//
// `dotnetSidecar`: without it, dotnet.js takes any worker for one of its own WebAssembly threads
// (a pthread) and waits forever for a main thread that never comes; with it, the worker runs a
// complete, separate runtime.
//
// Cross-origin isolated page (ADR 0131, section 4): the boot message also carries two shared rings
// (sharedRing.js) and a synchronous call slot. Messages then travel through the rings, a message
// that arrives by postMessage is handled only after the ring is drained (same order as before),
// and .NET may wait for one answer of the OS with Atomics.wait (`callSync`), in this worker only.

let entryPoint = null;
const pending = [];
let toOs = null;      // ring this worker writes
let fromOs = null;    // ring the page writes
let syncBuffer = null;
let waitForSyncAnswer = null;

function deliver(message) {
    if (entryPoint) {
        entryPoint.Receive(message);
    } else {
        pending.push(message);
    }
}

self.onmessage = event => {
    const data = event.data;
    if (typeof data === 'string') {
        fromOs?.drain(deliver);
        deliver(data);
    } else if (data && data.__hackeros === 'ring') {
        fromOs?.drain(deliver);
    } else if (data && typeof data === 'object' && data.__hackeros === 'boot') {
        boot(data);
    }
};

/** Sends one protocol message to the OS: through the ring when it fits, else by postMessage. */
function post(message) {
    if (!toOs || !toOs.write(message)) {
        self.postMessage(message);
    }
}

async function boot({ dotnetUrl, entryAssembly, entryType, ringUrl, rings }) {
    try {
        if (rings && ringUrl && globalThis.crossOriginIsolated === true) {
            try {
                const ring = await import(ringUrl);
                toOs = new ring.SharedRing(rings.toOs, () => self.postMessage({ __hackeros: 'ring' }));
                fromOs = new ring.SharedRing(rings.fromOs, () => { });
                syncBuffer = rings.sync;
                waitForSyncAnswer = ring.waitForSyncAnswer;
            } catch (error) {
                // The worker still runs, with messages only; its "ready" tells the page so.
                console.warn('Kazo OS worker: shared rings unavailable, messages only', error);
            }
        }

        globalThis.dotnetSidecar = true;
        const { dotnet } = await import(dotnetUrl);
        const runtime = await dotnet.create();
        runtime.setModuleImports('hackeros-worker', {
            post,
            // ADR 0131, section 4: true when callSync may block this worker for an answer.
            syncAvailable: () => syncBuffer !== null,
            // Sends call `id` (its JSON text) and waits for the OS's answer; null after the timeout.
            callSync: (id, message, timeoutMs) => waitForSyncAnswer(syncBuffer, id, () => post(message), timeoutMs)
        });
        const exports = await runtime.getAssemblyExports(entryAssembly);
        entryPoint = entryType.split('.').reduce((scope, part) => scope?.[part], exports);
        if (!entryPoint) {
            throw new Error(`${entryType} is not exported by ${entryAssembly}.`);
        }

        entryPoint.Start();
        self.postMessage({ __hackeros: 'ready', rings: fromOs !== null });
        // What arrived during the boot first, in its order, then what is still in the ring.
        for (const message of pending.splice(0)) {
            entryPoint.Receive(message);
        }
        fromOs?.drain(deliver);
    } catch (error) {
        self.postMessage({ __hackeros: 'error', message: String(error?.stack ?? error) });
    }
}
