// Sound card of the OS (ADR 0204 §5, doc/en/wasm/dev/system-sounds.md).
//
// Only what C# cannot reach lives here: the Web Audio API. OsSoundService (C#) decides which file
// plays, when and how loud; this module decodes the bytes it gets once, keeps the decoded buffer by
// key (the file path and its revision) and plays it through a gain node.
//
// Browsers keep an AudioContext suspended until the page gets a click or a key: the first sounds
// before that (an automatic guest sign-in) are dropped, which is what a real machine without a user
// in front of it would sound like anyway.

const MAX_CACHED = 32;
const buffers = new Map();
let context = null;

function getContext() {
    if (context) {
        return context;
    }
    const AudioContextType = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextType) {
        return null;
    }
    context = new AudioContextType();
    // Resume on the first user gesture; later sounds then play at once.
    const resume = () => {
        context.resume().catch(() => { });
        if (context.state === "running") {
            window.removeEventListener("pointerdown", resume, true);
            window.removeEventListener("keydown", resume, true);
        }
    };
    window.addEventListener("pointerdown", resume, true);
    window.addEventListener("keydown", resume, true);
    return context;
}

function start(audio, buffer, volume) {
    const gain = audio.createGain();
    gain.gain.value = Math.max(0, Math.min(1, volume));
    const source = audio.createBufferSource();
    source.buffer = buffer;
    source.connect(gain).connect(audio.destination);
    source.start();
}

async function ready() {
    const audio = getContext();
    if (!audio) {
        return null;
    }
    if (audio.state === "suspended") {
        try {
            await audio.resume();
        } catch {
            // Still waiting for a user gesture.
        }
    }
    return audio.state === "running" ? audio : null;
}

/** Plays a buffer already decoded under this key. Returns "played", "missing" or "muted". */
export async function playCached(key, volume) {
    const buffer = buffers.get(key);
    if (!buffer) {
        return "missing";
    }
    const audio = await ready();
    if (!audio) {
        return "muted";
    }
    start(audio, buffer, volume);
    return "played";
}

/** Decodes the bytes of a sound file, keeps them under this key and plays them. */
export async function play(key, bytes, volume) {
    const audio = getContext();
    if (!audio) {
        return false;
    }
    let buffer = buffers.get(key);
    if (!buffer) {
        // Blazor hands a byte[] over as a Uint8Array; decodeAudioData wants its own ArrayBuffer.
        const data = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
        try {
            buffer = await audio.decodeAudioData(data);
        } catch {
            return false;
        }
        if (buffers.size >= MAX_CACHED) {
            buffers.delete(buffers.keys().next().value);
        }
        buffers.set(key, buffer);
    }
    if (!(await ready())) {
        return false;
    }
    start(audio, buffer, volume);
    return true;
}
