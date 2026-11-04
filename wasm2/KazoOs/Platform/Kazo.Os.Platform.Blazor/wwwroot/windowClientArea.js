/**
 * Measures the client area of a desktop window (ADR 0095 §1). Raw layout reading only: when to
 * measure and what to tell the app is decided in C# (WindowAppEventDispatcher).
 */

const nextLayout = () => new Promise(resolve =>
    requestAnimationFrame(() => requestAnimationFrame(resolve)));

/**
 * Waits for the window's new geometry to be laid out, then returns the size of its content box,
 * or null when the window is not on screen.
 */
export async function measureWindowClient(windowId) {
    await nextLayout();
    const host = document.querySelector(`[data-window-id="${CSS.escape(windowId)}"]`);
    const content = host?.querySelector(":scope > .window-content");
    if (!content || host.hidden) {
        return null;
    }

    const rect = content.getBoundingClientRect();
    return { width: rect.width, height: rect.height };
}
