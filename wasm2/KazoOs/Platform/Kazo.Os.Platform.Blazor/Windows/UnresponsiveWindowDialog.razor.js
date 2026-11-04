/**
 * Focus trap of the "not responding" dialog (ADR 0127 §3, ADR 0105): Tab and Shift+Tab cycle
 * between its buttons instead of leaving for the frozen window's content. Everything else (texts,
 * Escape, the answers) is decided in C#.
 */

const traps = new WeakMap();

/** Keeps Tab inside the focusable elements of `root`. */
export function trapFocus(root) {
    if (!root || traps.has(root)) {
        return;
    }

    const onKeyDown = (event) => {
        if (event.key !== "Tab") {
            return;
        }

        const focusable = Array.from(root.querySelectorAll("button:not([disabled])"));
        if (focusable.length === 0) {
            return;
        }

        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        const active = document.activeElement;
        if (event.shiftKey && (active === first || !root.contains(active))) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && (active === last || !root.contains(active))) {
            event.preventDefault();
            first.focus();
        }
    };

    root.addEventListener("keydown", onKeyDown);
    traps.set(root, onKeyDown);
}

/** Removes the trap installed by `trapFocus`. */
export function releaseFocus(root) {
    const onKeyDown = root ? traps.get(root) : undefined;
    if (onKeyDown) {
        root.removeEventListener("keydown", onKeyDown);
        traps.delete(root);
    }
}
