const registrations = new WeakMap();
const sizeObservers = new WeakMap();
const drawerFocus = new WeakMap();

// Browser-only pointer capture and click suppression. C# interprets distances and owns all state.
export function attachMobileGestures(root, reference, attribute, callback, longPressCallback = null) {
    detachMobileGestures(root);
    let active = null;
    let suppressClickUntil = 0;
    let suppressClickTarget = null;
    const onClick = event => {
        if (performance.now() < suppressClickUntil
            && (event.target === root || suppressClickTarget?.contains(event.target))) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    };
    const onDown = event => {
        if (active || event.button !== 0 || event.isPrimary === false) return;
        const surface = event.target.closest(`[${attribute}]`);
        if (!surface || !root.contains(surface) || event.target.closest('input, textarea, select')) return;
        if (event.target.closest('[draggable="true"]')) return;
        const id = event.pointerId;
        const startX = event.clientX;
        const startY = event.clientY;
        let x = startX;
        let y = startY;
        let moved = false;
        let held = false;
        const app = longPressCallback && event.target.closest('[data-home-app]');
        const timer = app ? setTimeout(() => {
            held = true;
            suppressClickTarget = surface;
            suppressClickUntil = performance.now() + 800;
            reference.invokeMethodAsync(longPressCallback, app.dataset.homeApp).catch(console.error);
        }, 550) : 0;
        const cleanup = () => {
            clearTimeout(timer);
            root.removeEventListener('pointermove', onMove);
            window.removeEventListener('pointerup', finish, true);
            window.removeEventListener('pointercancel', cancel, true);
            root.removeEventListener('lostpointercapture', lostCapture);
            if (root.hasPointerCapture(id)) root.releasePointerCapture(id);
            active = null;
        };
        const onMove = move => {
            if (move.pointerId !== id) return;
            x = move.clientX;
            y = move.clientY;
            const dx = x - startX;
            const dy = y - startY;
            if (Math.hypot(dx, dy) > 10) {
                clearTimeout(timer);
                // Page swipes must not steal the drawer's vertical scroll or a normal tap.
                if (surface.getAttribute(attribute) === 'pages' && Math.abs(dy) > Math.abs(dx)) return;
                moved = true;
                if (!root.hasPointerCapture(id)) {
                    try { root.setPointerCapture(id); }
                    catch (error) { if (error.name !== 'NotFoundError') throw error; }
                }
                if (move.cancelable) move.preventDefault();
            }
        };
        const finish = up => {
            if (up.pointerId !== id) return;
            x = up.clientX;
            y = up.clientY;
            const dx = x - startX;
            const dy = y - startY;
            cleanup();
            if (moved || held) {
                suppressClickTarget = surface;
                suppressClickUntil = performance.now() + 500;
            }
            if (moved && !held && Number.isFinite(dx) && Number.isFinite(dy)) {
                reference.invokeMethodAsync(callback, surface.getAttribute(attribute), dx, dy).catch(console.error);
            }
        };
        const cancel = event => { if (event.pointerId === id) cleanup(); };
        // Touch starts with implicit capture on the pressed child. Transferring it to root
        // emits a bubbling lostpointercapture from that child, which must not cancel our drag.
        const lostCapture = event => { if (event.target === root) cancel(event); };
        active = cleanup;
        root.addEventListener('pointermove', onMove, { passive: false });
        window.addEventListener('pointerup', finish, true);
        window.addEventListener('pointercancel', cancel, true);
        root.addEventListener('lostpointercapture', lostCapture);
        // Do not prevent pointerdown: that suppresses the trusted mouse click on the shade handle.
    };
    root.addEventListener('pointerdown', onDown);
    root.addEventListener('click', onClick, true);
    const shade = root.querySelector('[data-shell-gesture="shade"]');
    if (shade) shade.dataset.swipeGestureAttached = 'true';
    registrations.set(root, () => {
        active?.();
        root.removeEventListener('pointerdown', onDown);
        root.removeEventListener('click', onClick, true);
    });
}

export function detachMobileGestures(root) {
    drawerFocus.get(root)?.cleanup();
    drawerFocus.delete(root);
    sizeObservers.get(root)?.disconnect();
    sizeObservers.delete(root);
    registrations.get(root)?.();
    registrations.delete(root);
}

export function observeMobileWorkspace(root, reference) {
    const workspace = root.querySelector('.mobile-shell-workspace');
    if (!workspace) return;
    const observer = new ResizeObserver(entries => {
        const { width, height } = entries[0].contentRect;
        if (width > 0 && height > 0) reference.invokeMethodAsync('ReportMobileWorkspaceSize', width, height).catch(console.error);
    });
    sizeObservers.get(root)?.disconnect();
    sizeObservers.set(root, observer);
    observer.observe(workspace);
}

// DOM focus belongs in the browser; C# still owns whether the drawer is open.
export function setMobileDrawerOpen(root, open) {
    const previous = drawerFocus.get(root);
    previous?.cleanup();
    drawerFocus.delete(root);
    if (!open) {
        if (previous?.trigger?.isConnected && previous.trigger.getClientRects().length)
            previous.trigger.focus({ preventScroll: true });
        return;
    }
    const drawer = root.querySelector('.mobile-app-drawer');
    if (!drawer) return;
    const trigger = document.activeElement;
    const trap = event => {
        if (event.key !== 'Tab') return;
        const controls = [...drawer.querySelectorAll('button:not(:disabled), input:not(:disabled), [tabindex="0"]')]
            .filter(control => control.getClientRects().length);
        const first = controls[0];
        const last = controls.at(-1);
        if (event.shiftKey && (document.activeElement === first || !drawer.contains(document.activeElement))) {
            event.preventDefault();
            last?.focus();
        } else if (!event.shiftKey && (document.activeElement === last || !drawer.contains(document.activeElement))) {
            event.preventDefault();
            first?.focus();
        }
    };
    root.addEventListener('keydown', trap);
    drawerFocus.set(root, { trigger, cleanup: () => root.removeEventListener('keydown', trap) });
    drawer.querySelector('.mobile-drawer-search')?.focus({ preventScroll: true });
}
