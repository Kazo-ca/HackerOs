/**
 * Input-latency probe of the "not responding" detection (ADR 0127 §2).
 *
 * An app that holds the browser's main thread also holds the event loop: the user's clicks and key
 * presses wait in the browser's queue until the thread is given back. `event.timeStamp` says when
 * the input really happened, so `performance.now() - event.timeStamp` at dispatch is how long it
 * waited. This module only measures that wait and tells C#, which decides everything else
 * (WindowResponsivenessMonitor).
 */

const state = {
    dotNet: null,
    floor: 50,
    watched: new Set(),
    listener: null,
};

function onInput(event) {
    if (!state.dotNet || !(event.target instanceof Element)) {
        return;
    }

    // Inputs on the window's own content only: the chrome and the "not responding" dialog itself
    // are the window manager's, never the frozen app's.
    const content = event.target.closest(".window-content");
    if (!content || event.target.closest(".window-overlay")) {
        return;
    }

    const host = content.closest("[data-window-id]");
    const windowId = host?.getAttribute("data-window-id");
    if (!windowId) {
        return;
    }

    const latency = Math.max(0, performance.now() - event.timeStamp);
    if (latency < state.floor && !state.watched.has(windowId)) {
        return;
    }

    state.dotNet.invokeMethodAsync("ReportInputAsync", windowId, latency).catch(() => { });
}

/** Starts listening, in the capture phase so an app stopping propagation cannot hide its inputs. */
export function attachInputProbe(dotNet, floorMilliseconds) {
    detachInputProbe();
    state.dotNet = dotNet;
    state.floor = floorMilliseconds;
    state.listener = onInput;
    document.addEventListener("pointerdown", onInput, true);
    document.addEventListener("keydown", onInput, true);
}

/** Lists the windows marked as not responding: their fast inputs are reported too. */
export function setWatchedWindows(windowIds) {
    state.watched = new Set(windowIds ?? []);
}

/** Stops listening. */
export function detachInputProbe() {
    if (state.listener) {
        document.removeEventListener("pointerdown", state.listener, true);
        document.removeEventListener("keydown", state.listener, true);
    }

    state.listener = null;
    state.dotNet = null;
    state.watched = new Set();
}
