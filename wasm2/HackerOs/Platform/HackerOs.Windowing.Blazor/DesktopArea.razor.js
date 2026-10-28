/**
 * HackerOS DesktopArea Resize Observer Interop
 *
 * The area of a secondary screen (ADR 0178 §1) is moved into another window's document right after
 * its first render. A ResizeObserver only follows layout in its own window, so the area is observed
 * by an observer of the window that holds it, made again when the area changes document.
 */
const REBIND_INTERVAL_MS = 200;
const REBIND_ATTEMPTS = 50;

export function observeDesktopSize(element, dotNetHelper) {
    if (!element) {
        return;
    }

    let observer = null;
    let observedDocument = null;
    let lastSize = "";

    const report = (width, height) => {
        width = Math.round(width);
        height = Math.round(height);
        const size = `${width}x${height}`;
        if (width > 0 && height > 0 && size !== lastSize) {
            lastSize = size;
            dotNetHelper.invokeMethodAsync('OnDesktopResized', width, height).catch(() => { });
        }
    };

    const bind = () => {
        const doc = element.ownerDocument;
        if (!element.isConnected || doc === observedDocument) {
            return;
        }

        const Observer = doc.defaultView?.ResizeObserver ?? window.ResizeObserver;
        if (!Observer) {
            return;
        }

        observer?.disconnect();
        observedDocument = doc;
        observer = new Observer(entries => {
            for (const entry of entries) {
                report(entry.contentRect.width, entry.contentRect.height);
            }
        });
        observer.observe(element);
    };

    bind();
    // Watch for the move to a screen's window for a while after the first render.
    let attempts = 0;
    const timer = setInterval(() => {
        bind();
        if (++attempts >= REBIND_ATTEMPTS || observedDocument !== document) {
            clearInterval(timer);
        }
    }, REBIND_INTERVAL_MS);
}
