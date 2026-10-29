// JS initializer of HackerOs.Platform.Blazor (Blazor picks up "<Library>.lib.module.js" from every
// referenced Razor library, in all three hosts: no host references it by hand).
//
// Detached display surfaces (ADR 0078, doc/en/wasm/dev/detached-display-windows.md) show Blazor content in a
// second, same-origin window. Blazor delegates every DOM event through listeners it adds to the
// main `document`; an event in another window's document never reaches them. This initializer runs
// before Blazor starts, so it can remember each listener added to `document` and let the display
// surface module (displaySurfaces.js) add the same listeners to every surface's document: a click
// in a detached window then reaches the renderer exactly as a click on the desktop does.
//
// Blazor also finds the element behind an ElementReference with `document.querySelector` on its
// capture attribute (`[_bl_<id>]`): an element moved to a surface is then looked up in the surface
// documents too, so @ref keeps working for content shown there.
//
// Nothing else changes: listeners still go to `document` first, in the same order, and any other
// query answers exactly as before.
//
// It also installs the stray drop guard (installStrayDropGuard, below).

const REGISTRY_KEY = Symbol.for("hackeros.displaySurfaces.documentListeners");

function installDocumentListenerRegistry() {
    if (window[REGISTRY_KEY]) {
        return;
    }

    const nativeAdd = EventTarget.prototype.addEventListener;
    const nativeRemove = EventTarget.prototype.removeEventListener;
    const listeners = [];
    const documents = new Set();
    const watchers = new Set();
    const tell = (change, target) => {
        for (const watcher of watchers) {
            try {
                watcher(change, target);
            } catch {
                // One watcher failing must not stop the others.
            }
        }
    };

    const sameCapture = (a, b) => !!(typeof a === "object" ? a?.capture : a) === !!(typeof b === "object" ? b?.capture : b);

    document.addEventListener = function (type, listener, options) {
        nativeAdd.call(document, type, listener, options);
        if (!listener || listeners.some((entry) => entry.type === type && entry.listener === listener && sameCapture(entry.options, options))) {
            return;
        }
        listeners.push({ type, listener, options });
        for (const target of documents) {
            nativeAdd.call(target, type, listener, options);
        }
    };

    document.removeEventListener = function (type, listener, options) {
        nativeRemove.call(document, type, listener, options);
        const index = listeners.findIndex((entry) => entry.type === type && entry.listener === listener && sameCapture(entry.options, options));
        if (index >= 0) {
            listeners.splice(index, 1);
        }
        for (const target of documents) {
            nativeRemove.call(target, type, listener, options);
        }
    };

    const nativeQuerySelector = Document.prototype.querySelector;
    const elementCapture = /^\[_bl_[A-Za-z0-9-]+\]$/;
    document.querySelector = function (selectors) {
        const found = nativeQuerySelector.call(document, selectors);
        if (found || documents.size === 0 || typeof selectors !== "string" || !elementCapture.test(selectors)) {
            return found;
        }
        for (const target of documents) {
            try {
                const inSurface = nativeQuerySelector.call(target, selectors);
                if (inSurface) {
                    return inSurface;
                }
            } catch {
                // A window closing under us.
            }
        }
        return null;
    };

    window[REGISTRY_KEY] = {
        // Starts mirroring onto a surface's document: every known listener now, later ones as added.
        attach(target) {
            documents.add(target);
            for (const entry of listeners) {
                nativeAdd.call(target, entry.type, entry.listener, entry.options);
            }
            tell("attach", target);
        },
        // Stops mirroring onto a surface's document (closed, lost).
        detach(target) {
            if (!documents.delete(target)) {
                return;
            }
            for (const entry of listeners) {
                try {
                    nativeRemove.call(target, entry.type, entry.listener, entry.options);
                } catch {
                    // The window is already gone.
                }
            }
            tell("detach", target);
        },
        // Tells a watcher about every surface document, now and later ("attach", then "detach"),
        // for modules that listen on a surface's window rather than on its document (ADR 0178 §4).
        // Returns the function that stops watching.
        watch(watcher) {
            watchers.add(watcher);
            for (const target of documents) {
                try {
                    watcher("attach", target);
                } catch {
                    // A window closing under us.
                }
            }
            return () => watchers.delete(watcher);
        },
    };
}

// ADR 0185 §3: a drop that lands outside every drop zone does nothing, in every window. Without this
// the browser opens a host file dropped next to a zone (a text file replaced the whole OS). The zones
// (FileView, the desktop, the Places entries, apps) cancel `dragover` themselves, through Blazor or their
// own listener; these window listeners run last (bubble phase on the window) and only act when nobody
// did: they refuse the drop (dropEffect "none", the "not allowed" cursor) and cancel the default action.
// Text fields keep the browser's own text drop.
const DROP_GUARD_KEY = Symbol.for("hackeros.strayDropGuard");

function isEditableTarget(target) {
    const element = target instanceof Node && target.nodeType !== Node.ELEMENT_NODE ? target.parentElement : target;
    return !!(element && typeof element.closest === "function" && element.closest("input, textarea, [contenteditable]:not([contenteditable='false'])"));
}

export function installStrayDropGuard(target) {
    if (!target || target[DROP_GUARD_KEY]) {
        return false;
    }

    target[DROP_GUARD_KEY] = true;
    target.addEventListener("dragover", (e) => {
        if (e.defaultPrevented || isEditableTarget(e.target)) {
            return;
        }
        e.preventDefault();
        if (e.dataTransfer) {
            e.dataTransfer.dropEffect = "none";
        }
    });
    target.addEventListener("drop", (e) => {
        if (!e.defaultPrevented && !isEditableTarget(e.target)) {
            e.preventDefault();
        }
    });
    return true;
}

function installStrayDropGuards() {
    installStrayDropGuard(window);
    // Detached display windows (ADR 0078) get the same guard.
    window[REGISTRY_KEY]?.watch((change, surfaceDocument) => {
        if (change === "attach") {
            installStrayDropGuard(surfaceDocument.defaultView);
        }
    });
}

// WebAssembly standalone host (HackerOs.Ecosystem) and legacy initializers.
export function beforeStart() {
    installDocumentListenerRegistry();
    installStrayDropGuards();
}

// Blazor Web App hosts (test host, HackerOs.Server).
export function beforeWebStart() {
    installDocumentListenerRegistry();
    installStrayDropGuards();
}
