const registrations = new WeakMap();

// Pointer travel, in CSS pixels, before a drag on a maximized or snapped title bar restores the window:
// a plain click or a double click must not restore it (ADR 0177).
const restoreDragThreshold = 4;

export function attachWindowGestures(root, dotNetReference) {
    detachWindowGestures(root);
    let active = null;
    let disposed = false;

    const onPointerDown = event => {
        if (active || event.button !== 0 || event.target.closest("button")) return;
        const target = event.target.closest("[data-window-gesture]");
        const host = root.closest("[data-window-id]");
        if (!target || !root.contains(target) || !host) return;

        const mode = target.dataset.windowGesture;
        const edge = target.dataset.resizeEdge ?? null;
        // A maximized or snapped window is dragged by its title bar only, and first gets back its
        // geometry from before the maximize or the snap (ADR 0177).
        let restoreOnDrag = mode === "move"
            && (host.dataset.windowState === "Maximized" || host.dataset.windowSnapped === "true");
        if (host.dataset.windowState !== "Normal" && !restoreOnDrag) return;
        let origin = [host.dataset.windowX, host.dataset.windowY,
            host.dataset.windowWidth, host.dataset.windowHeight].map(Number);
        // A localized decimal or an invalid pointer must never become JSON null at JS interop.
        if (!origin.every(Number.isFinite) || !Number.isFinite(event.clientX) || !Number.isFinite(event.clientY)) return;

        const pointerId = event.pointerId;
        let startX = event.clientX;
        let startY = event.clientY;
        let restoring = false;
        let delta = [0, 0];
        let frame = 0;
        let pending = null;
        let ending = false;
        let queue = dotNetReference.invokeMethodAsync("ReportGestureStartedAsync");

        const clearPreview = () => {
            host.style.removeProperty("--window-drag-translation");
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

        const restore = moveEvent => {
            restoreOnDrag = false;
            restoring = true;
            const hostBox = host.getBoundingClientRect();
            const layerBox = host.offsetParent?.getBoundingClientRect() ?? { left: 0, top: 0 };
            const pointerX = moveEvent.clientX;
            const pointerY = moveEvent.clientY;
            const values = [pointerX - layerBox.left, pointerY - layerBox.top,
                hostBox.width > 0 ? (startX - hostBox.left) / hostBox.width : 0, startY - hostBox.top];
            if (!values.every(Number.isFinite)) return;
            queue = queue
                .then(() => !disposed && dotNetReference.invokeMethodAsync("ReportDragRestoreAsync", ...values))
                .then(bounds => {
                    restoring = false;
                    if (!Array.isArray(bounds) || !bounds.every(Number.isFinite)) return;
                    // The drag goes on from the restored geometry, under the pointer.
                    origin = bounds;
                    startX = pointerX;
                    startY = pointerY;
                    delta = [0, 0];
                });
            queue.catch(abort);
        };

        const onPointerMove = moveEvent => {
            if (moveEvent.pointerId !== pointerId || ending) return;
            const values = [moveEvent.clientX - startX, moveEvent.clientY - startY];
            if (!values.every(Number.isFinite)) return;
            if (restoreOnDrag) {
                if (Math.hypot(values[0], values[1]) >= restoreDragThreshold) restore(moveEvent);
                return;
            }
            if (restoring) return;
            delta = values;
            if (mode === "move") {
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
            if (restoreOnDrag) {
                // A click on a maximized or snapped title bar: nothing moved, nothing to report.
                active = null;
                return;
            }
            queue = queue.then(async () => {
                if (disposed) return;
                if (mode === "move" || pending) await report(delta);
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

    // A double click on the title bar, outside its buttons, toggles maximize (ADR 0177).
    const onDoubleClick = event => {
        if (event.button !== 0 || event.target.closest("button")) return;
        const header = event.target.closest(".window-chrome");
        if (!header || !root.contains(header)) return;
        event.preventDefault();
        dotNetReference.invokeMethodAsync("ReportTitleDoubleClickAsync").catch(error => {
            if (!disposed) console.error("[WindowChrome] Title bar double click failed:", error);
        });
    };

    root.addEventListener("pointerdown", onPointerDown);
    root.addEventListener("dblclick", onDoubleClick);
    registrations.set(root, () => {
        disposed = true;
        root.removeEventListener("pointerdown", onPointerDown);
        root.removeEventListener("dblclick", onDoubleClick);
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
