// Moves a window's element into the window layer of its screen (ADR 0178 §3,
// doc/en/wasm/dev/window-portal.md). Elements are kept by key, not by ElementReference: Blazor
// finds a referenced element with a query on the document, which misses an element that sits in a
// layer just removed from the page.
const portals = new Map();

export function register(key, element) {
    if (element) {
        portals.set(key, { element, anchor: element.parentNode });
    }
}

// Moves the element into `layer`, or back under its anchor when `layer` is null or gone. The
// keyboard focus inside the window follows it.
export function place(key, layer) {
    const portal = portals.get(key);
    if (!portal) {
        return;
    }
    const destination = layer && layer.isConnected ? layer : portal.anchor;
    const { element } = portal;
    if (!destination || element.parentNode === destination) {
        return;
    }
    const active = element.ownerDocument.activeElement;
    const hadFocus = active && element.contains(active);
    destination.appendChild(element);
    if (hadFocus && active.isConnected) {
        active.focus({ preventScroll: true });
    }
}

// Gives the element back to its anchor before the renderer removes the anchor.
export function release(key) {
    const portal = portals.get(key);
    portals.delete(key);
    if (!portal) {
        return;
    }
    if (portal.anchor && portal.anchor.isConnected) {
        portal.anchor.appendChild(portal.element);
    } else {
        portal.element.remove();
    }
}
