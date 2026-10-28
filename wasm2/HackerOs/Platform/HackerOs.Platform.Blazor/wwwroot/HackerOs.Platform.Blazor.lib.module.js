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

// WebAssembly standalone host (HackerOs.Ecosystem) and legacy initializers.
export function beforeStart() {
    installDocumentListenerRegistry();
}

// Blazor Web App hosts (test host, HackerOs.Server).
export function beforeWebStart() {
    installDocumentListenerRegistry();
}
