// Shared message rings between the page and a KazoOS Web Worker (ADR 0131, section 4), used by
// workerHost.js (page) and workerBootstrap.js (worker) when the page is cross-origin isolated.
//
// A ring is a SharedArrayBuffer holding a single-producer, single-consumer byte queue of framed
// UTF-8 messages, one ring per direction. It replaces postMessage for the protocol's strings
// (standard output, acknowledgements, calls and their results), like a pipe in shared memory: the
// writer copies a message into the ring and rings a doorbell once (a tiny postMessage) when the
// ring was idle; the reader drains every message in order.
//
// Order with postMessage is kept by one rule on both sides: before handling any message that
// arrived by postMessage, the reader drains its ring. A message too big for the free space is
// posted as before, and since everything written to the ring earlier is drained first, the order
// of the protocol never changes. Neither side ever blocks on a ring: the credit of the output
// streams (OutputAck, ADR 0070) stays the flow control, so a full ring only falls back to messages.
//
// Layout of the buffer: Int32 header [writeIndex, readIndex, doorbell, unused], then `capacity`
// bytes of data. Indices count bytes since creation and wrap at 2^32 (two's complement arithmetic);
// a frame is a 4-byte little-endian length followed by the message bytes, possibly wrapping.

const HEADER_BYTES = 16;
const WRITE = 0;
const READ = 1;
const DOORBELL = 2;

/** Capacity of each ring: 1 MiB, a power of two so positions are a mask. */
export const RING_CAPACITY = 1 << 20;

const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** Whether this realm may use shared rings: cross-origin isolated with SharedArrayBuffer and Atomics. */
export function ringsAvailable() {
    return globalThis.crossOriginIsolated === true
        && typeof globalThis.SharedArrayBuffer === 'function'
        && typeof globalThis.Atomics === 'object';
}

/** Creates the SharedArrayBuffer of one ring. */
export function createRingBuffer(capacity = RING_CAPACITY) {
    return new SharedArrayBuffer(HEADER_BYTES + capacity);
}

/**
 * One end of a ring. `doorbell` is called (at most once until the reader drains) when a write
 * finds the ring idle, so the reader schedules a drain.
 */
export class SharedRing {
    constructor(buffer, doorbell) {
        this.header = new Int32Array(buffer, 0, 4);
        this.data = new Uint8Array(buffer, HEADER_BYTES);
        this.capacity = this.data.length;
        this.mask = this.capacity - 1;
        this.doorbell = doorbell;
    }

    /** Writes one message; false when it does not fit in the free space (post it instead). */
    write(text) {
        const bytes = encoder.encode(text);
        const needed = 4 + bytes.length;
        const write = Atomics.load(this.header, WRITE);
        const used = (write - Atomics.load(this.header, READ)) | 0;
        if (needed > this.capacity - used) {
            return false;
        }

        const length = new Uint8Array(4);
        new DataView(length.buffer).setUint32(0, bytes.length, true);
        this.#copyIn(write, length);
        this.#copyIn((write + 4) | 0, bytes);
        Atomics.store(this.header, WRITE, (write + needed) | 0);
        if (Atomics.compareExchange(this.header, DOORBELL, 0, 1) === 0) {
            this.doorbell();
        }
        return true;
    }

    /** Hands every message in the ring to `deliver`, in order. */
    drain(deliver) {
        // Cleared first: a write that lands after the last read below rings again.
        Atomics.store(this.header, DOORBELL, 0);
        let read = Atomics.load(this.header, READ);
        for (;;) {
            const write = Atomics.load(this.header, WRITE);
            if (read === write) {
                return;
            }

            const lengthBytes = this.#copyOut(read, 4);
            const length = new DataView(lengthBytes.buffer).getUint32(0, true);
            // TextDecoder does not accept a view of shared memory: the bytes are copied out first.
            const text = decoder.decode(this.#copyOut((read + 4) | 0, length));
            read = (read + 4 + length) | 0;
            Atomics.store(this.header, READ, read);
            deliver(text);
        }
    }

    #copyIn(index, bytes) {
        const start = index & this.mask;
        const first = Math.min(bytes.length, this.capacity - start);
        this.data.set(bytes.subarray(0, first), start);
        if (first < bytes.length) {
            this.data.set(bytes.subarray(first), 0);
        }
    }

    #copyOut(index, length) {
        const out = new Uint8Array(length);
        const start = index & this.mask;
        const first = Math.min(length, this.capacity - start);
        out.set(this.data.subarray(start, start + first), 0);
        if (first < length) {
            out.set(this.data.subarray(0, length - first), first);
        }
        return out;
    }
}

// ---------------------------------------------------------------------------------------------
// Synchronous calls (ADR 0131, section 4): a worker waits with Atomics.wait for the OS's answer to
// one call; the page never waits. The slot is a SharedArrayBuffer: Int32 header [state, awaited
// call id, length, answered call id] then the answer's bytes. States: 0 idle, 1 waiting, 2 answered.
// ---------------------------------------------------------------------------------------------

const STATE = 0;
const CALL = 1;
const LENGTH = 2;
const ANSWER = 3;
const IDLE = 0;
const WAITING = 1;
const ANSWERED = 2;

/** Room for one synchronous answer (a process list, a process record). */
export const SYNC_CAPACITY = 1 << 20;

/** Creates the SharedArrayBuffer of a worker's synchronous call slot. */
export function createSyncBuffer() {
    return new SharedArrayBuffer(HEADER_BYTES + SYNC_CAPACITY);
}

/** The worker's side: marks call `id` as waited for, sends it with `send`, waits for the answer. */
export function waitForSyncAnswer(buffer, id, send, timeoutMs) {
    const header = new Int32Array(buffer, 0, 4);
    Atomics.store(header, CALL, id);
    Atomics.store(header, STATE, WAITING);
    send();
    const deadline = performance.now() + timeoutMs;
    for (;;) {
        const outcome = Atomics.wait(header, STATE, WAITING, Math.max(0, deadline - performance.now()));
        if (outcome === 'timed-out' && Atomics.compareExchange(header, STATE, WAITING, IDLE) === WAITING) {
            return null; // Nobody answered in time; a late answer finds the slot idle and is dropped.
        }
        if (Atomics.load(header, ANSWER) === id) {
            break;
        }
        // The answer of an earlier, abandoned call slipped in: wait on for this one.
        Atomics.store(header, STATE, WAITING);
    }

    const length = Atomics.load(header, LENGTH);
    const text = decoder.decode(new Uint8Array(buffer, HEADER_BYTES, length).slice());
    Atomics.store(header, STATE, IDLE);
    return text;
}

/**
 * The page's side: writes the answer of call `id` and wakes the worker. Returns "ok", "late" when
 * the worker no longer waits for this call, or "too-large" when the answer does not fit.
 */
export function writeSyncAnswer(buffer, id, text) {
    const header = new Int32Array(buffer, 0, 4);
    if (Atomics.load(header, STATE) !== WAITING || Atomics.load(header, CALL) !== id) {
        return 'late';
    }

    const bytes = encoder.encode(text);
    if (bytes.length > buffer.byteLength - HEADER_BYTES) {
        return 'too-large';
    }

    new Uint8Array(buffer, HEADER_BYTES, bytes.length).set(bytes);
    Atomics.store(header, LENGTH, bytes.length);
    Atomics.store(header, ANSWER, id);
    if (Atomics.compareExchange(header, STATE, WAITING, ANSWERED) !== WAITING) {
        return 'late';
    }
    Atomics.notify(header, STATE);
    return 'ok';
}
