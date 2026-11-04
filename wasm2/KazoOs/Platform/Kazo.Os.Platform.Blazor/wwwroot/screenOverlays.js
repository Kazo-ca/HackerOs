// The overlays follow the screen where the user acts (ADR 0179 §2,
// doc/en/wasm/dev/secondary-screen-desktop.md).
//
// The window menu, the OS context menu, the window switcher and the keyboard move banner are
// rendered once, in the main tab, inside one container. Before any of them reacts to a press, a
// right click or a key, this module moves that container into the overlay slot of the screen whose
// window received the event: screen 1 is the main tab, a secondary screen is a display surface
// whose content has a `[data-screen-overlay-slot]`. Other surfaces (a page detached by the OS
// browser) have no slot and leave the overlays where they are. The listeners sit on each window in
// the capture phase, so they run before the listeners every overlay puts on `document`.

const REGISTRY_KEY = Symbol.for("hackeros.displaySurfaces.documentListeners");
const SLOT = "[data-screen-overlay-slot]";
const FOLLOWED = ["pointerdown", "contextmenu", "keydown"];

const state = {
    container: null,
    anchor: null,
    unwatch: null,
    windows: new Map(),
};

function slotOf(doc) {
    if (doc === document) {
        return state.anchor;
    }

    try {
        return doc.querySelector(SLOT);
    } catch {
        // A window closing under us.
        return null;
    }
}

function moveTo(target) {
    const container = state.container;
    if (!container || !target || container.parentNode === target) {
        return;
    }

    target.appendChild(container);
}

function backHome() {
    if (state.container && state.anchor && state.anchor.isConnected) {
        moveTo(state.anchor);
    }
}

// A screen that closes gives the overlays back to screen 1: whether they are still in its window,
// or already back in the main tab with the screen's content (displaySurfaces.js gives it back).
function rescue(doc) {
    const parent = state.container?.parentNode;
    if (!parent || parent.ownerDocument === doc || (parent.ownerDocument === document && parent !== state.anchor)) {
        backHome();
    }
}

function listen(view, doc) {
    if (!view || state.windows.has(view)) {
        return;
    }

    const follow = () => moveTo(slotOf(doc));
    const leave = () => rescue(doc);
    for (const type of FOLLOWED) {
        view.addEventListener(type, follow, true);
    }

    view.addEventListener("pagehide", leave);
    state.windows.set(view, { follow, leave });
}

function forget(view) {
    const entry = state.windows.get(view);
    if (!entry) {
        return;
    }

    state.windows.delete(view);
    try {
        for (const type of FOLLOWED) {
            view.removeEventListener(type, entry.follow, true);
        }

        view.removeEventListener("pagehide", entry.leave);
    } catch {
        // The window is already gone.
    }
}

function onSurface(change, doc) {
    const view = doc?.defaultView;
    if (change === "attach") {
        listen(view, doc);
        return;
    }

    rescue(doc);
    if (view) {
        forget(view);
    }
}

/** Starts moving the overlay container to the screen of each input event; called once by the shell. */
export function attachScreenOverlays(container) {
    detachScreenOverlays();
    if (!container) {
        return;
    }

    state.container = container;
    state.anchor = container.parentNode;
    listen(window, document);
    state.unwatch = window[REGISTRY_KEY]?.watch(onSurface) ?? null;
}

/** Puts the overlays back on screen 1 and stops following the screens. */
export function detachScreenOverlays() {
    backHome();
    state.unwatch?.();
    state.unwatch = null;
    for (const view of [...state.windows.keys()]) {
        forget(view);
    }

    state.container = null;
    state.anchor = null;
}

/** Brings the main tab (screen 1) to the front, from a click in a secondary screen. */
export function focusPrimary() {
    try {
        window.focus();
    } catch {
        // The host may refuse: the message on the screen still says where to go.
    }
}
