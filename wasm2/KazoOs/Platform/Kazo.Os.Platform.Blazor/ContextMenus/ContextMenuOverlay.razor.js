/**
 * Browser side of the OS context menu (ADR 0150 §5, doc/en/wasm/framework/context-menu.md).
 *
 * C# decides everything about a menu: which one an element opens, what it contains, how keys move
 * in it. This module only does what C# cannot: catch the requests the browser raises (right click,
 * a finger's long press, the Menu key, Shift+F10) on an element carrying `data-hack-menu-token`,
 * cancel the browser's own menu there, measure the viewport to keep the menu on screen, dismiss the
 * menu on an outside press, a scroll or the loss of focus, give focus back to the element the menu
 * was opened from, and report the tokens of elements that left the page so C# can forget the
 * objects attached to them. The attached object itself never reaches JavaScript: only its token.
 */

const TOKEN = "data-hack-menu-token";
const SUPPRESS = "data-hack-menu-suppress";
const LONG_PRESS_MS = 500;
const LONG_PRESS_SLOP = 10;
const DUPLICATE_WINDOW_MS = 800;
const RELEASE_DELAY_MS = 1000;

const state = {
    dotNet: null,
    layer: null,
    open: false,
    restoreTarget: null,
    pendingTarget: null,
    lastOpenAt: 0,
    lastOrigin: "",
    press: null,
    suppressClickUntil: 0,
    observer: null,
    pendingRelease: new Map(),
    releaseTimer: 0,
};

function isEditable(element) {
    return !!element && (element.isContentEditable
        || element.tagName === "INPUT" || element.tagName === "TEXTAREA" || element.tagName === "SELECT");
}

/**
 * The element whose menu a request opens: the nearest ancestor with a token. A text field inside it
 * keeps the browser's own menu (copy, paste), unless the field itself carries a token.
 */
function menuTarget(start) {
    if (!(start instanceof Element)) {
        return null;
    }

    const target = start.closest(`[${TOKEN}]`);
    if (!target) {
        return null;
    }

    const field = start.closest("input, textarea, select, [contenteditable='true'], [contenteditable='']");
    if (field && field !== target && target.contains(field) && !field.hasAttribute(TOKEN)) {
        return null;
    }

    return target;
}

/**
 * Whether the OS menu is drawn for a document: the desktop's, or a secondary screen's (ADR 0179 §2),
 * where the overlays move when the user acts there. A page detached by the OS browser (ADR 0078)
 * keeps the browser's own menu.
 */
function isScreen(doc) {
    if (!doc) {
        return false;
    }

    if (doc === document) {
        return true;
    }

    try {
        return doc.querySelector("[data-screen-overlay-slot]") !== null;
    } catch {
        return false;
    }
}

function insideMenu(node) {
    return !!state.layer && node instanceof Node && state.layer.contains(node);
}

function request(target, x, y, origin) {
    if (!state.dotNet) {
        return;
    }

    state.lastOpenAt = Date.now();
    state.lastOrigin = origin;
    state.pendingTarget = target;
    state.dotNet.invokeMethodAsync("OnRequestedAsync", target.getAttribute(TOKEN), x, y, origin).catch(() => { });
}

function onContextMenu(event) {
    // Content moved to a detached window (ADR 0078) keeps the browser's menu: the OS menu is drawn
    // on the desktop's document and on the secondary screens only.
    if (!state.dotNet || event.defaultPrevented || !isScreen(event.target?.ownerDocument)) {
        return;
    }

    if (insideMenu(event.target)) {
        event.preventDefault();
        return;
    }

    const target = menuTarget(event.target);
    if (!target) {
        if (event.target instanceof Element && event.target.closest(`[${SUPPRESS}]`)) {
            event.preventDefault();
        }

        return;
    }

    event.preventDefault();
    cancelPress();
    const touch = event.pointerType === "touch" || event.sourceCapabilities?.firesTouchEvents === true;
    // The Menu key and a long press also raise this event once the request already went to C#;
    // a real right click (button 2 of a mouse) always opens.
    if (Date.now() - state.lastOpenAt < DUPLICATE_WINDOW_MS
        && state.lastOrigin !== "pointer"
        && (touch || event.button !== 2)) {
        return;
    }

    request(target, event.clientX, event.clientY, touch ? "touch" : "pointer");
}

function onKeyDown(event) {
    if (!state.dotNet || event.defaultPrevented || insideMenu(event.target)) {
        return;
    }

    const isMenuKey = event.key === "ContextMenu" || (event.key === "F10" && event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey);
    if (!isMenuKey) {
        return;
    }

    const focused = (event.target?.ownerDocument ?? document).activeElement;
    const target = menuTarget(focused);
    if (!target) {
        return;
    }

    event.preventDefault();
    // The keyboard opens the menu at the element's corner, as Windows does.
    const rect = target.getBoundingClientRect();
    request(target, rect.left + Math.min(16, rect.width / 2), rect.top + Math.min(rect.height, 24), "keyboard");
}

function cancelPress() {
    if (state.press) {
        clearTimeout(state.press.timer);
        state.press = null;
    }
}

function onPointerDown(event) {
    if (state.open && !insideMenu(event.target)) {
        // An outside press closes the menu and goes on to its target, as it does in Windows.
        state.restoreTarget = null;
        state.dotNet?.invokeMethodAsync("OnDismissed").catch(() => { });
    }

    if ((event.pointerType !== "touch" && event.pointerType !== "pen") || !isScreen(event.target?.ownerDocument)) {
        return;
    }

    const target = menuTarget(event.target);
    if (!target || insideMenu(event.target)) {
        return;
    }

    cancelPress();
    const press = { x: event.clientX, y: event.clientY, id: event.pointerId, timer: 0 };
    press.timer = setTimeout(() => {
        state.press = null;
        // The browser's own long-press menu event came first and already opened it.
        if (Date.now() - state.lastOpenAt < DUPLICATE_WINDOW_MS && state.lastOrigin === "touch") {
            return;
        }

        // The finger is lifted after the menu shows: its click must not also activate the element.
        state.suppressClickUntil = Date.now() + DUPLICATE_WINDOW_MS + LONG_PRESS_MS;
        request(target, press.x, press.y, "touch");
    }, LONG_PRESS_MS);
    state.press = press;
}

function onPointerMove(event) {
    const press = state.press;
    if (press && event.pointerId === press.id
        && Math.hypot(event.clientX - press.x, event.clientY - press.y) > LONG_PRESS_SLOP) {
        cancelPress();
    }
}

function onPointerEnd(event) {
    if (state.press && event.pointerId === state.press.id) {
        cancelPress();
    }
}

function onClickCapture(event) {
    if (Date.now() < state.suppressClickUntil && !insideMenu(event.target)) {
        state.suppressClickUntil = 0;
        event.preventDefault();
        event.stopPropagation();
    }
}

function onDismissingEvent(event) {
    if (state.open && !(event?.type === "scroll" && insideMenu(event.target))) {
        state.restoreTarget = null;
        state.dotNet?.invokeMethodAsync("OnDismissed").catch(() => { });
    }
}

function collectTokens(node, into) {
    if (!(node instanceof Element)) {
        return;
    }

    if (node.hasAttribute(TOKEN)) {
        into.push([node.getAttribute(TOKEN), node]);
    }

    for (const child of node.querySelectorAll(`[${TOKEN}]`)) {
        into.push([child.getAttribute(TOKEN), child]);
    }
}

/** Elements carrying a token were removed or re-tokened: check them again a little later. */
function onMutations(records) {
    const found = [];
    for (const record of records) {
        if (record.type === "attributes") {
            if (record.oldValue) {
                found.push([record.oldValue, null]);
            }

            continue;
        }

        for (const node of record.removedNodes) {
            collectTokens(node, found);
        }
    }

    if (found.length === 0) {
        return;
    }

    for (const [token, element] of found) {
        let elements = state.pendingRelease.get(token);
        if (!elements) {
            elements = [];
            state.pendingRelease.set(token, elements);
        }

        if (element) {
            elements.push(element);
        }
    }

    if (!state.releaseTimer) {
        state.releaseTimer = setTimeout(flushReleases, RELEASE_DELAY_MS);
    }
}

function flushReleases() {
    state.releaseTimer = 0;
    const released = [];
    for (const [token, elements] of state.pendingRelease) {
        // Still on the page (re-rendered, or moved to a detached window): keep it.
        const stillShown = elements.some((element) => element.isConnected)
            || document.querySelector(`[${TOKEN}="${CSS.escape(token)}"]`) !== null;
        if (!stillShown) {
            released.push(token);
        }
    }

    state.pendingRelease.clear();
    if (released.length > 0) {
        state.dotNet?.invokeMethodAsync("OnTokensReleased", released).catch(() => { });
    }
}

/** Starts listening on the document; called once by the shell's overlay. */
export function attachContextMenus(dotNet, layer) {
    detachContextMenus();
    state.dotNet = dotNet;
    state.layer = layer;
    document.addEventListener("contextmenu", onContextMenu);
    document.addEventListener("keydown", onKeyDown);
    document.addEventListener("pointerdown", onPointerDown, true);
    document.addEventListener("pointermove", onPointerMove, true);
    document.addEventListener("pointerup", onPointerEnd, true);
    document.addEventListener("pointercancel", onPointerEnd, true);
    document.addEventListener("click", onClickCapture, true);
    document.addEventListener("scroll", onDismissingEvent, true);
    window.addEventListener("blur", onDismissingEvent);
    window.addEventListener("resize", onDismissingEvent);
    if (typeof MutationObserver === "function" && document.body) {
        state.observer = new MutationObserver(onMutations);
        state.observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: [TOKEN],
            attributeOldValue: true,
        });
    }
}

export function detachContextMenus() {
    document.removeEventListener("contextmenu", onContextMenu);
    document.removeEventListener("keydown", onKeyDown);
    document.removeEventListener("pointerdown", onPointerDown, true);
    document.removeEventListener("pointermove", onPointerMove, true);
    document.removeEventListener("pointerup", onPointerEnd, true);
    document.removeEventListener("pointercancel", onPointerEnd, true);
    document.removeEventListener("click", onClickCapture, true);
    document.removeEventListener("scroll", onDismissingEvent, true);
    window.removeEventListener("blur", onDismissingEvent);
    window.removeEventListener("resize", onDismissingEvent);
    state.observer?.disconnect();
    state.observer = null;
    cancelPress();
    clearTimeout(state.releaseTimer);
    state.releaseTimer = 0;
    state.pendingRelease.clear();
    state.dotNet = null;
    state.layer = null;
    state.open = false;
}

/** A menu is shown: remember where focus was (a menu opened from code has no target yet). */
export function menuOpened() {
    if (state.pendingTarget) {
        state.restoreTarget = state.pendingTarget;
    } else if (!state.open) {
        const active = (state.layer?.ownerDocument ?? document).activeElement;
        state.restoreTarget = active && active !== document.body && !insideMenu(active) ? active : null;
    }

    state.pendingTarget = null;
    state.open = true;
}

/** The menu closed: give focus back to its element, unless an outside press moved it on purpose. */
export function menuClosed(restoreFocus) {
    const target = state.restoreTarget;
    state.restoreTarget = null;
    state.open = false;
    if (restoreFocus && target && target.isConnected && typeof target.focus === "function") {
        target.focus({ preventScroll: true });
    }
}

/**
 * Places each panel of the layer: the menu at the point, opening left or up when the viewport is
 * short of room; a submenu beside its parent row, on the free side.
 */
export function placeMenu(layer, x, y) {
    if (!layer) {
        return;
    }

    // The viewport of the screen the menu is shown on (ADR 0179 §2).
    const doc = layer.ownerDocument ?? document;
    const view = doc.defaultView ?? window;
    const margin = 4;
    const width = view.innerWidth;
    const height = view.innerHeight;
    for (const panel of layer.querySelectorAll("[data-menu-level]")) {
        const rect = panel.getBoundingClientRect();
        let left;
        let top;
        const parentId = panel.getAttribute("data-parent-row");
        const parent = parentId ? doc.getElementById(parentId) : null;
        if (!parent) {
            left = x + rect.width + margin > width ? x - rect.width : x;
            top = y + rect.height + margin > height ? y - rect.height : y;
        } else {
            const anchor = parent.getBoundingClientRect();
            left = anchor.right + rect.width + margin > width ? anchor.left - rect.width : anchor.right;
            top = anchor.top;
        }

        left = Math.max(margin, Math.min(left, width - rect.width - margin));
        top = Math.max(margin, Math.min(top, height - rect.height - margin));
        panel.style.setProperty("--hack-menu-x", `${Math.round(left)}px`);
        panel.style.setProperty("--hack-menu-y", `${Math.round(top)}px`);
        panel.classList.add("is-placed");
    }
}
