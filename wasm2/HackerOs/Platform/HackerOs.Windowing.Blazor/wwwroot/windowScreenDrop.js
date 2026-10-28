// Dragging a window from one screen to another (ADR 0183, doc/en/wasm/dev/drag-between-screens.md).
//
// A move keeps the pointer captured by the title bar, so the browser keeps sending its events to the
// window where the drag started, even when the pointer is over another screen's window. The other
// screens are found by the pointer's position on the physical desktop (screenX/screenY): each
// screen's window knows where its page starts on the desktop from the last pointer event it saw
// (screenX - clientX), else from its position and borders. A screen is a window layer with a
// `data-display` number: screen 1 in the main tab, the others in their display surfaces.

const REGISTRY_KEY = Symbol.for("hackeros.displaySurfaces.documentListeners");
const LAYER = ".window-layer[data-display]";
const PREVIEW_CLASS = "window-drop-preview";
const LEARNED_FROM = ["pointermove", "pointerdown", "pointerover"];

const pageOrigins = new WeakMap();
const trackedViews = new WeakSet();
const surfaceDocuments = new Set();
let watching = false;

function learnOrigin(event) {
    const view = event.view ?? event.target?.ownerDocument?.defaultView;
    if (!view || !Number.isFinite(event.screenX) || !Number.isFinite(event.clientX)
        || !Number.isFinite(event.screenY) || !Number.isFinite(event.clientY)) {
        return;
    }

    pageOrigins.set(view, {
        x: event.screenX - event.clientX,
        y: event.screenY - event.clientY,
        left: view.screenX,
        top: view.screenY,
    });
}

function track(view) {
    if (!view || trackedViews.has(view)) {
        return;
    }

    trackedViews.add(view);
    for (const type of LEARNED_FROM) {
        view.addEventListener(type, learnOrigin, { capture: true, passive: true });
    }
}

/** Starts learning where each screen's page sits on the physical desktop; safe to call many times. */
export function trackScreens() {
    track(window);
    if (watching) {
        return;
    }

    const registry = window[REGISTRY_KEY];
    if (!registry) {
        return;
    }

    watching = true;
    registry.watch((change, doc) => {
        if (change === "attach") {
            surfaceDocuments.add(doc);
            track(doc?.defaultView);
        } else {
            surfaceDocuments.delete(doc);
        }
    });
}

/** Where the page of `view` starts on the physical desktop, in CSS pixels. */
function originOf(view) {
    const known = pageOrigins.get(view);
    if (known) {
        // The window may have moved since: follow its outer position.
        return { x: known.x + view.screenX - known.left, y: known.y + view.screenY - known.top };
    }

    const border = Math.max(0, (view.outerWidth - view.innerWidth) / 2);
    return {
        x: view.screenX + border,
        y: view.screenY + Math.max(0, view.outerHeight - view.innerHeight - border),
    };
}

function layerAt(doc, x, y) {
    let layers;
    try {
        layers = doc.querySelectorAll(LAYER);
    } catch {
        // A window closing under us.
        return null;
    }

    for (const layer of layers) {
        const rect = layer.getBoundingClientRect();
        const display = Number(layer.dataset.display);
        if (Number.isInteger(display) && display >= 1 && rect.width > 0 && rect.height > 0
            && x >= rect.left && x < rect.right && y >= rect.top && y < rect.bottom) {
            return { layer, display, x: x - rect.left, y: y - rect.top };
        }
    }

    return null;
}

function inside(view, x, y) {
    return x >= 0 && y >= 0 && x < view.innerWidth && y < view.innerHeight;
}

/**
 * Finds the screen under the pointer of `event`, a move in `sourceDocument`. Inside the window
 * where the drag started, that window's layers decide; outside it, the other screens' windows,
 * by the pointer's position on the desktop. Returns the screen number and the point in its
 * window layer, or null when the pointer is over no screen.
 */
export function screenUnderPointer(sourceDocument, event) {
    const sourceView = sourceDocument?.defaultView;
    if (!sourceView || ![event.clientX, event.clientY, event.screenX, event.screenY].every(Number.isFinite)) {
        return null;
    }

    if (inside(sourceView, event.clientX, event.clientY)) {
        return layerAt(sourceDocument, event.clientX, event.clientY);
    }

    for (const doc of [document, ...surfaceDocuments]) {
        const view = doc?.defaultView;
        if (doc === sourceDocument || !view || view.closed) {
            continue;
        }

        const origin = originOf(view);
        const x = event.screenX - origin.x;
        const y = event.screenY - origin.y;
        if (inside(view, x, y)) {
            const found = layerAt(doc, x, y);
            if (found) {
                return found;
            }
        }
    }

    return null;
}

/**
 * Shows where the window will land in another screen's window: an outline of its size. Only for a
 * screen in another window; in the same window, the dragged window itself shows it.
 */
export function showDropPreview(state, target, sourceDocument, width, height) {
    const layer = target && target.layer.ownerDocument !== sourceDocument ? target.layer : null;
    if (!layer) {
        clearDropPreview(state);
        return;
    }

    let preview = state.preview;
    if (!preview || preview.ownerDocument !== layer.ownerDocument) {
        clearDropPreview(state);
        preview = layer.ownerDocument.createElement("div");
        preview.className = PREVIEW_CLASS;
        preview.setAttribute("aria-hidden", "true");
        state.preview = preview;
    }

    if (preview.parentNode !== layer) {
        layer.appendChild(preview);
    }

    preview.style.setProperty("--window-drop-x", `${target.x}px`);
    preview.style.setProperty("--window-drop-y", `${target.y}px`);
    preview.style.setProperty("--window-drop-width", `${width}px`);
    preview.style.setProperty("--window-drop-height", `${height}px`);
}

/** Removes the drop outline, if any. */
export function clearDropPreview(state) {
    try {
        state.preview?.remove();
    } catch {
        // The screen's window is already gone.
    }

    state.preview = null;
}
