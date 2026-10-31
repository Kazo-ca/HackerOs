/**
 * Browser side of the hardware Back / Forward navigation (ADR 0220 §1).
 *
 * C# (HardwareNavigationService) decides where a gesture goes. This module only does what C# cannot do
 * inside a browser event: cancel the host browser's own Back / Forward, and report the direction and the
 * source.
 *
 * - Mouse: buttons 4 (event.button 3, Back) and 5 (event.button 4, Forward). Cancelling mousedown,
 *   mouseup and auxclick stops Chrome and Edge from moving the tab.
 * - Keyboard: Alt+← / Alt+→ and the Browser Back / Forward keys, except inside an editable field, where
 *   Alt+arrows edit text (macOS) or belong to the editor; if the host browser then goes back anyway, the
 *   history path below still catches it.
 * - History: a "sentinel" entry, pushed on the same address after a user gesture (Chrome skips entries
 *   pushed without one). Going back lands on the entry below it; the module pushes the sentinel again and
 *   reports Back with the source "SystemBack". This is the only way to see Android's Back button or
 *   gesture, the iOS edge swipe, and the host browser's own Back arrow.
 *
 * Listeners are installed once per document and stay; attach/detach only swap the .NET receiver, so a
 * shell switch (desktop ↔ mobile) never pushes a second sentinel.
 */

const SENTINEL_KEY = "hackerosHardwareBack";
const BACK_BUTTON = 3;
const FORWARD_BUTTON = 4;
/** A history Back this soon after a mouse or keyboard Back is the same gesture (Firefox, Safari). */
const DUPLICATE_WINDOW_MS = 500;

const state = {
    receiver: null,
    token: 0,
    installed: false,
    lastDirectBack: -Infinity,
};

function report(forward, source) {
    if (!forward) {
        state.lastDirectBack = performance.now();
    }

    state.receiver?.invokeMethodAsync("OnHardwareNavigationAsync", forward, source)
        .catch(error => console.error("hardware navigation:", error));
}

function isNavigationButton(event) {
    return event.button === BACK_BUTTON || event.button === FORWARD_BUTTON;
}

function cancel(event) {
    event.preventDefault();
    event.stopPropagation();
}

function onMouseDown(event) {
    if (state.receiver && isNavigationButton(event)) {
        cancel(event);
    }

    armSentinel();
}

function onMouseUp(event) {
    if (!state.receiver || !isNavigationButton(event)) {
        return;
    }

    cancel(event);
    report(event.button === FORWARD_BUTTON, "MouseButton");
}

function onAuxClick(event) {
    if (state.receiver && isNavigationButton(event)) {
        cancel(event);
    }
}

function isEditable(target) {
    return target instanceof Element
        && (target.isContentEditable || target.closest("input, textarea, select, [contenteditable='true']") !== null);
}

function keyDirection(event) {
    if (event.key === "BrowserBack") {
        return false;
    }

    if (event.key === "BrowserForward") {
        return true;
    }

    if (event.altKey && !event.ctrlKey && !event.shiftKey && !event.metaKey && !isEditable(event.target)) {
        if (event.key === "ArrowLeft") {
            return false;
        }

        if (event.key === "ArrowRight") {
            return true;
        }
    }

    return null;
}

function onKeyDown(event) {
    armSentinel();
    if (!state.receiver || event.defaultPrevented) {
        return;
    }

    const forward = keyDirection(event);
    if (forward === null) {
        return;
    }

    cancel(event);
    if (!event.repeat) {
        report(forward, "KeyboardShortcut");
    }
}

function hasSentinel(historyState) {
    return !!historyState && typeof historyState === "object" && historyState[SENTINEL_KEY] === true;
}

/** Pushes the sentinel on top of the current entry, keeping the state Blazor stores there. */
function armSentinel() {
    if (!state.receiver || hasSentinel(history.state)) {
        return;
    }

    const current = history.state && typeof history.state === "object" ? history.state : {};
    history.pushState({ ...current, [SENTINEL_KEY]: true }, "", location.href);
}

function onPopState(event) {
    if (!state.receiver || hasSentinel(event.state)) {
        // No shell signed in: the tab goes back as usual. Landing on the sentinel itself (host Forward)
        // is not a gesture either.
        return;
    }

    // Landed below the sentinel: the host browser went back. Put the sentinel back and report Back unless
    // the mouse or the keyboard already did. Listeners added after this one never see the event; the Blazor
    // router, added at boot, still does, but the address did not change, so it stays on the same page.
    event.stopImmediatePropagation();
    armSentinel();
    if (performance.now() - state.lastDirectBack > DUPLICATE_WINDOW_MS) {
        report(false, "SystemBack");
    }
}

function install() {
    if (state.installed) {
        return;
    }

    state.installed = true;
    window.addEventListener("mousedown", onMouseDown, true);
    window.addEventListener("mouseup", onMouseUp, true);
    window.addEventListener("auxclick", onAuxClick, true);
    window.addEventListener("keydown", onKeyDown, true);
    window.addEventListener("pointerdown", armSentinel, true);
    window.addEventListener("touchend", armSentinel, true);
    window.addEventListener("popstate", onPopState, true);
}

/**
 * Starts sending gestures to this .NET receiver (the shell's listener component). Returns a token for
 * detachHardwareNavigation: .NET object references are new JS wrappers on each call, so they can't be compared.
 */
export function attachHardwareNavigation(receiver) {
    state.receiver = receiver;
    state.token += 1;
    install();
    return state.token;
}

/** Stops sending gestures, unless another listener attached since. */
export function detachHardwareNavigation(token) {
    if (state.token === token) {
        state.receiver = null;
    }
}
