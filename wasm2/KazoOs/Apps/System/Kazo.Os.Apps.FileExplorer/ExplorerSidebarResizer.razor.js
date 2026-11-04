// Pointer resize of the File Explorer's Places panel (ADR 0185 §1). Blazor has no pointer capture, so the
// drag runs here: the panel's width follows the pointer live, clamped to [min, max], and the final width
// goes back to C# once (OnResized), which saves it.

export function attach(handle, panelSelector, callbacks, min, max) {
    if (!handle || handle.__hackerOsResize) {
        return;
    }

    const onPointerDown = e => {
        if (e.button !== 0) {
            return;
        }

        const panel = handle.parentElement?.querySelector(panelSelector);
        if (!panel) {
            return;
        }

        e.preventDefault();
        handle.setPointerCapture(e.pointerId);
        handle.classList.add('dragging');
        const startX = e.clientX;
        const startWidth = panel.offsetWidth;
        let width = startWidth;

        const onMove = ev => {
            width = Math.min(max, Math.max(min, Math.round(startWidth + ev.clientX - startX)));
            panel.style.width = `${width}px`;
        };
        const onEnd = ev => {
            handle.removeEventListener('pointermove', onMove);
            handle.removeEventListener('pointerup', onEnd);
            handle.removeEventListener('pointercancel', onEnd);
            handle.classList.remove('dragging');
            if (handle.hasPointerCapture?.(ev.pointerId)) {
                handle.releasePointerCapture(ev.pointerId);
            }
            if (width !== startWidth) {
                callbacks.invokeMethodAsync('OnResized', width);
            }
        };

        handle.addEventListener('pointermove', onMove);
        handle.addEventListener('pointerup', onEnd);
        handle.addEventListener('pointercancel', onEnd);
    };

    handle.addEventListener('pointerdown', onPointerDown);
    handle.__hackerOsResize = onPointerDown;
}

export function setWidth(handle, panelSelector, width) {
    const panel = handle?.parentElement?.querySelector(panelSelector);
    if (panel) {
        panel.style.width = `${width}px`;
    }
}

export function detach(handle) {
    if (handle?.__hackerOsResize) {
        handle.removeEventListener('pointerdown', handle.__hackerOsResize);
        delete handle.__hackerOsResize;
    }
}
