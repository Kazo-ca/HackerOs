// Desktop mode of FileView (ADR 0160 §3). C# cannot measure an element nor hit-test a lasso, so this
// module reports the grid's size (a ResizeObserver, which also fires once at the start), gives its
// origin for pointer math, finds the icons a lasso crosses and moves focus to an icon by its key.

const observers = new WeakMap();

export function observeSize(element, callbacks) {
    if (!element || observers.has(element)) {
        return;
    }

    let frame = 0;
    const observer = new ResizeObserver(entries => {
        // One call per animation frame at most: a window being resized fires many entries.
        cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => {
            const rect = entries[entries.length - 1].contentRect;
            callbacks.invokeMethodAsync('OnDesktopResized', rect.width, rect.height);
        });
    });
    observer.observe(element);
    observers.set(element, observer);
}

export function unobserveSize(element) {
    const observer = element ? observers.get(element) : null;
    if (observer) {
        observer.disconnect();
        observers.delete(element);
    }
}

export function getOrigin(element) {
    const rect = element.getBoundingClientRect();
    return { left: rect.left, top: rect.top };
}

export function getIntersectingKeys(element, left, top, right, bottom) {
    const origin = element.getBoundingClientRect();
    const keys = [];
    for (const tile of element.querySelectorAll('[data-desktop-key]')) {
        const rect = tile.getBoundingClientRect();
        const tileLeft = rect.left - origin.left;
        const tileTop = rect.top - origin.top;
        if (tileLeft < right && tileLeft + rect.width > left && tileTop < bottom && tileTop + rect.height > top) {
            keys.push(tile.getAttribute('data-desktop-key'));
        }
    }

    return keys;
}

export function focusKey(element, key) {
    for (const tile of element.querySelectorAll('[data-desktop-key]')) {
        if (tile.getAttribute('data-desktop-key') === key) {
            tile.focus();
            return true;
        }
    }

    return false;
}
