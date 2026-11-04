// Drag of an app from the Start menu (ADR 0160 §6). The payload has to be written while the browser
// dispatches dragstart, so the rows (marked data-app-drag-id) are handled here rather than in C#. While
// a row is dragged, the menu lets the pointer through, so the desktop under it receives the drop.

const appMimeType = 'application/vnd.hackeros.app+json';

export function attachAppDrag(overlay, callbacks) {
    if (!overlay || overlay.__hackerOsAppDrag) {
        return;
    }

    overlay.addEventListener('dragstart', e => {
        const row = e.target instanceof Element ? e.target.closest('[data-app-drag-id]') : null;
        if (!row || !e.dataTransfer) {
            return;
        }

        e.dataTransfer.effectAllowed = 'copy';
        e.dataTransfer.setData(appMimeType, JSON.stringify({ appId: row.getAttribute('data-app-drag-id') }));
        e.dataTransfer.setData('text/plain', row.getAttribute('data-app-drag-id'));
        // After the browser took its drag image: then the menu fades and lets drops through.
        requestAnimationFrame(() => overlay.classList.add('is-dragging'));
    });

    overlay.addEventListener('dragend', e => {
        overlay.classList.remove('is-dragging');
        const dropped = !!e.dataTransfer && e.dataTransfer.dropEffect !== 'none';
        callbacks.invokeMethodAsync('OnAppDragEnded', dropped);
    });

    overlay.__hackerOsAppDrag = true;
}
