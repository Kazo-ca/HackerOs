// ADR 0206 test package: starts a worker from this app's _content/ address and returns its answer.
export function run() {
    return new Promise((resolve, reject) => {
        const worker = new Worker(new URL('engine/worker.js', import.meta.url));
        worker.onmessage = event => { worker.terminate(); resolve(event.data); };
        worker.onerror = () => { worker.terminate(); reject(new Error('the worker did not start')); };
        worker.postMessage('ping');
    });
}
