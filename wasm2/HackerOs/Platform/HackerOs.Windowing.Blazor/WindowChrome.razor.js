import { clearDropPreview, screenUnderPointer, showDropPreview, trackScreens } from "./windowScreenDrop.js";

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
