// Node harness of SharedRingTests.cs: exercises Infrastructure.Browser/wwwroot/sharedRing.js (ADR
// 0131, section 4) with real SharedArrayBuffers and Atomics, which Node offers without isolation.
// Usage: node sharedRing.test.mjs <path of sharedRing.js> <scenario>. Prints "ok" or throws.
import { Worker } from 'node:worker_threads';
import { pathToFileURL } from 'node:url';

const ringUrl = pathToFileURL(process.argv[2]).href;
const scenario = process.argv[3];
const ring = await import(ringUrl);

function assert(condition, message) {
    if (!condition) {
        throw new Error(message);
    }
}

function drained(reader) {
    const out = [];
    reader.drain(text => out.push(text));
    return out;
}

const scenarios = {
    // Messages come out in order, whatever their characters.
    order() {
        const buffer = ring.createRingBuffer(1024);
        let rings = 0;
        const writer = new ring.SharedRing(buffer, () => rings++);
        const reader = new ring.SharedRing(buffer, () => { });
        for (const text of ['one', 'deux', 'trois ✓', '{"t":"out","d":"é"}']) {
            assert(writer.write(text), `write ${text}`);
        }
        assert(rings === 1, `the doorbell rang ${rings} times before a drain`);
        const out = drained(reader);
        assert(JSON.stringify(out) === JSON.stringify(['one', 'deux', 'trois ✓', '{"t":"out","d":"é"}']), `order: ${out}`);
        writer.write('again');
        assert(rings === 2, 'the doorbell rings again after a drain');
    },

    // Frames that cross the end of the buffer come back whole.
    wrap() {
        const buffer = ring.createRingBuffer(64);
        const writer = new ring.SharedRing(buffer, () => { });
        const reader = new ring.SharedRing(buffer, () => { });
        const sent = [];
        const received = [];
        for (let i = 0; i < 500; i++) {
            const text = `message ${i} ${'x'.repeat(i % 23)}`;
            if (!writer.write(text)) {
                // Full: the reader catches up, then the frame fits.
                reader.drain(text => received.push(text));
                assert(writer.write(text), `write ${i} after a drain`);
            }
            sent.push(text);
        }
        reader.drain(text => received.push(text));
        assert(JSON.stringify(received) === JSON.stringify(sent), 'wrapped frames changed');
    },

    // A full ring refuses the write (the caller posts the message instead) and never blocks.
    full() {
        const buffer = ring.createRingBuffer(64);
        const writer = new ring.SharedRing(buffer, () => { });
        const reader = new ring.SharedRing(buffer, () => { });
        assert(writer.write('a'.repeat(40)), 'first write');
        assert(!writer.write('b'.repeat(40)), 'a write past the free space must fail');
        assert(!writer.write('c'.repeat(100)), 'a message larger than the ring must fail');
        assert(drained(reader)[0] === 'a'.repeat(40), 'the first message is intact');
        assert(writer.write('b'.repeat(40)), 'room again after the drain');
    },

    // A worker blocks in Atomics.wait until the page answers; a late answer is refused.
    async sync() {
        const buffer = ring.createSyncBuffer();
        const worker = new Worker(`
            const { parentPort, workerData } = require('node:worker_threads');
            import(workerData.ringUrl).then(ring => {
                parentPort.on('message', ({ id, timeout }) => {
                    const answer = ring.waitForSyncAnswer(workerData.buffer, id, () => parentPort.postMessage({ sent: id }), timeout);
                    parentPort.postMessage({ id, answer });
                });
            });`, { eval: true, workerData: { buffer, ringUrl } });
        const next = () => new Promise(resolve => worker.once('message', resolve));
        try {
            worker.postMessage({ id: 1, timeout: 5000 });
            assert((await next()).sent === 1, 'call 1 sent');
            assert(ring.writeSyncAnswer(buffer, 2, 'wrong call') === 'late', 'an answer to another call is refused');
            assert(ring.writeSyncAnswer(buffer, 1, '{"t":"result","i":1,"v":{"ps":[]}}') === 'ok', 'answer 1 written');
            const first = await next();
            assert(first.answer === '{"t":"result","i":1,"v":{"ps":[]}}', `answer 1 read: ${first.answer}`);

            worker.postMessage({ id: 2, timeout: 50 });
            assert((await next()).sent === 2, 'call 2 sent');
            const second = await next();
            assert(second.answer === null, 'nobody answered: null after the timeout');
            assert(ring.writeSyncAnswer(buffer, 2, 'too late') === 'late', 'a late answer finds the slot idle');

            worker.postMessage({ id: 3, timeout: 5000 });
            assert((await next()).sent === 3, 'call 3 sent');
            assert(ring.writeSyncAnswer(buffer, 3, 'x'.repeat(ring.SYNC_CAPACITY + 1)) === 'too-large', 'too large');
            assert(ring.writeSyncAnswer(buffer, 3, 'fits') === 'ok', 'answer 3 written');
            assert((await next()).answer === 'fits', 'answer 3 read');
        } finally {
            await worker.terminate();
        }
    }
};

const run = scenarios[scenario];
assert(run, `unknown scenario ${scenario}`);
await run();
console.log('ok');
