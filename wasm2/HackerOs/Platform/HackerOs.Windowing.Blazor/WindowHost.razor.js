/**
 * Projects C#-authoritative geometry through CSS custom properties without
 * placing a dynamic style attribute in Razor source.
 */
export function projectWindowGeometry(element, x, y, width, height, zOrder) {
    if (!element || !element.style) {
        return;
    }
    element.style.setProperty("--window-x", `${x}px`);
    element.style.setProperty("--window-y", `${y}px`);
    element.style.setProperty("--window-width", `${width}px`);
    element.style.setProperty("--window-height", `${height}px`);
    element.style.setProperty("--window-z", `${zOrder}`);
}

const focusableSelector = [
    "a[href]", "button:not([disabled])", "input:not([disabled]):not([type=hidden])",
    "select:not([disabled])", "textarea:not([disabled])", "[contenteditable=true]",
    "[tabindex]:not([tabindex='-1'])"
].join(",");

/**
 * Remembers, per window, the last element inside it that held the keyboard focus, the way a
 * desktop window manager gives a window its focused control back when the window is raised.
 */
export function trackFocus(element) {
    if (!element || element.__hosTracksFocus) {
        return;
    }
    element.__hosTracksFocus = true;
    element.addEventListener("focusin", event => {
        element.__hosLastFocus = event.target;
    });
}

/**
 * Moves the keyboard focus into a window that just became the focused one (switcher, Alt+Q,
 * taskbar): its last focused control, else its first focusable content, else the window itself.
 * Does nothing when the focus is already inside it (a click, or an app that focused itself).
 */
export function focusWindow(element) {
    if (!element || element.hidden || element.contains(element.ownerDocument.activeElement)) {
        return;
    }
    const last = element.__hosLastFocus;
    const content = element.querySelector(".window-content");
    const target = last && last.isConnected && element.contains(last)
        ? last
        : content?.querySelector(focusableSelector) ?? element;
    target.focus({ preventScroll: true });
}
