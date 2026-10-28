// Dragging a window from one screen to another (ADR 0183, doc/en/wasm/dev/drag-between-screens.md).
// Kept in this module, which tests load on its own from a data: URL (no relative import).
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
function trackScreens() {
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
function screenUnderPointer(sourceDocument, event) {
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
function showDropPreview(state, target, sourceDocument, width, height) {
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
function clearDropPreview(state) {
    try {
        state.preview?.remove();
    } catch {
        // The screen's window is already gone.
    }

    state.preview = null;
}

const registrations = new WeakMap();

export function attachWindowGestures(root, dotNetReference) {
    detachWindowGestures(root);
    trackScreens();
    let active = null;
    let disposed = false;

    const onPointerDown = event => {
        if (active || event.button !== 0 || event.target.closest("button")) return;
        const target = event.target.closest("[data-window-gesture]");
        const host = root.closest("[data-window-id]");
        if (!target || !root.contains(target) || !host || host.dataset.windowState !== "Normal") return;

        const mode = target.dataset.windowGesture;
        const edge = target.dataset.resizeEdge ?? null;
        const origin = [host.dataset.windowX, host.dataset.windowY,
            host.dataset.windowWidth, host.dataset.windowHeight].map(Number);
        // A localized decimal or an invalid pointer must never become JSON null at JS interop.
        if (!origin.every(Number.isFinite) || !Number.isFinite(event.clientX) || !Number.isFinite(event.clientY)) return;

        const pointerId = event.pointerId;
        const startX = event.clientX;
        const startY = event.clientY;
        let delta = [0, 0];
        let frame = 0;
        let pending = null;
        let ending = false;
        let queue = dotNetReference.invokeMethodAsync("ReportGestureStartedAsync");
        // Drag to another screen (ADR 0183): the screen the window is on, where the title bar was
        // grabbed, and the screen under the pointer when it differs.
        const sourceDocument = host.ownerDocument;
        const currentDisplay = Number(host.closest(".window-layer[data-display]")?.dataset.display ?? 1);
        const hostRect = host.getBoundingClientRect();
        const grab = [startX - hostRect.left, startY - hostRect.top];
        const dropState = { preview: null };
        let drop = null;

        const clearPreview = () => {
            host.style.removeProperty("--window-drag-translation");
            clearDropPreview(dropState);
        };
        const trackDrop = moveEvent => {
            const found = screenUnderPointer(sourceDocument, moveEvent);
            drop = found && found.display !== currentDisplay
                ? { ...found, x: found.x - grab[0], y: Math.max(0, found.y - grab[1]) }
                : null;
            showDropPreview(dropState, drop, sourceDocument, origin[2], origin[3]);
        };
        const cleanup = () => {
            cancelAnimationFrame(frame);
            target.removeEventListener("pointermove", onPointerMove);
            target.removeEventListener("pointerup", finish);
            target.removeEventListener("pointercancel", finish);
            target.removeEventListener("lostpointercapture", finish);
            if (target.hasPointerCapture(pointerId)) target.releasePointerCapture(pointerId);
        };
        const abort = error => {
            ending = true;
            cleanup();
            clearPreview();
            active = null;
            if (!disposed) console.error("[WindowChrome] Pointer gesture failed:", error);
        };
        const report = values => dotNetReference.invokeMethodAsync(
            "ReportPointerDeltaAsync", mode, edge, ...values, ...origin);

        // Resize keeps C# constraints live. Keep only the newest sample while interop is busy,
        // instead of accumulating one promise and one desktop render for every pointer event.
        const flushResize = () => {
            frame = 0;
            if (!pending || ending || disposed) return;
            const values = pending;
            pending = null;
            queue = queue.then(() => !disposed && report(values));
            queue.then(() => {
                if (pending && !ending && !disposed && !frame) frame = requestAnimationFrame(flushResize);
            }).catch(abort);
        };

        const onPointerMove = moveEvent => {
            if (moveEvent.pointerId !== pointerId || ending) return;
            const values = [moveEvent.clientX - startX, moveEvent.clientY - startY];
            if (!values.every(Number.isFinite)) return;
            delta = values;
            if (mode === "move") {
                trackDrop(moveEvent);
                // Only a temporary compositor translation: no authoritative geometry or storage.
                if (!frame) frame = requestAnimationFrame(() => {
                    frame = 0;
                    // Anchor the preview to an absolute destination, subtracting the live C# base.
                    // A geometry render can precede the interop reply: adding a fixed delta here
                    // would otherwise draw the displacement twice for one or more painted frames.
                    host.style.setProperty("--window-drag-translation",
                        `calc(${origin[0] + delta[0]}px - var(--window-x, 0px)) calc(${origin[1] + delta[1]}px - var(--window-y, 0px))`);
                });
            } else {
                const wasPending = pending !== null;
                pending = values;
                if (!wasPending && !frame) {
                    queue.then(() => {
                        if (!ending && !disposed && !frame) frame = requestAnimationFrame(flushResize);
                    }).catch(abort);
                }
            }
        };

        const finish = finishEvent => {
            if (finishEvent.pointerId !== pointerId || ending) return;
            // pointercancel/lost capture often carry zero coordinates: keep the last real sample.
            if (finishEvent.type === "pointerup") onPointerMove(finishEvent);
            ending = true;
            cleanup();
            queue = queue.then(async () => {
                if (disposed) return;
                if (mode === "move" && drop) {
                    await dotNetReference.invokeMethodAsync("ReportScreenDropAsync", drop.display, drop.x, drop.y);
                } else if (mode === "move" || pending) {
                    await report(delta);
                }
                if (!disposed) await dotNetReference.invokeMethodAsync("ReportGestureCompletedAsync", mode);
            });
            queue.then(() => {
                clearPreview();
                active = null;
            }).catch(abort);
        };

        active = () => {
            ending = true;
            cleanup();
            clearPreview();
        };
        queue.catch(abort);
        target.addEventListener("pointermove", onPointerMove);
        target.addEventListener("pointerup", finish);
        target.addEventListener("pointercancel", finish);
        target.addEventListener("lostpointercapture", finish);
        try {
            target.setPointerCapture(pointerId);
        } catch (error) {
            abort(error);
            return;
        }
        event.preventDefault();
    };

    root.addEventListener("pointerdown", onPointerDown);
    registrations.set(root, () => {
        disposed = true;
        root.removeEventListener("pointerdown", onPointerDown);
        active?.();
    });
}

export function detachWindowGestures(root) {
    registrations.get(root)?.();
    registrations.delete(root);
}

/**
 * Projects the final C# position and removes the preview in the same browser task. A .NET
 * command acknowledgement alone is not a guarantee that WindowHost's projection has run.
 */
export function completeWindowMove(root, x, y) {
    const host = root.closest("[data-window-id]");
    if (!host || !Number.isFinite(x) || !Number.isFinite(y)) return;
    host.style.setProperty("--window-x", `${x}px`);
    host.style.setProperty("--window-y", `${y}px`);
    host.style.removeProperty("--window-drag-translation");
}
