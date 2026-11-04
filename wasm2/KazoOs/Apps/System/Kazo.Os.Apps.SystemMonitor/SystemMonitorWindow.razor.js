// Browser-side helper of SystemMonitorWindow. The only thing C# cannot see: whether the window is
// actually visible. A minimized window is hidden by the desktop (display: none), and a hidden
// browser tab is not painted; in both cases the task manager stops reading /proc every second.

const watchers = new WeakMap();

export function watchVisibility(element, dotNetRef) {
    if (!element || watchers.has(element)) {
        return;
    }

    let inView = true;
    let last = null;
    const report = () => {
        const visible = inView && document.visibilityState === "visible";
        if (visible !== last) {
            last = visible;
            dotNetRef.invokeMethodAsync("SetWindowVisible", visible).catch(() => { });
        }
    };

    // A display:none ancestor (minimized window) makes the element stop intersecting the viewport.
    const observer = new IntersectionObserver(entries => {
        inView = entries.some(entry => entry.isIntersecting);
        report();
    });
    observer.observe(element);

    const onVisibilityChange = () => report();
    document.addEventListener("visibilitychange", onVisibilityChange);

    // The Processes table handles Ctrl+A itself; keep the browser from selecting the whole page.
    const onKeyDown = event => {
        if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === "a"
            && event.target.closest && event.target.closest("[data-sysmon-selectable]")) {
            event.preventDefault();
        }
    };
    element.addEventListener("keydown", onKeyDown);

    watchers.set(element, { observer, onVisibilityChange, onKeyDown });
}

export function unwatchVisibility(element) {
    const watcher = element && watchers.get(element);
    if (!watcher) {
        return;
    }

    watcher.observer.disconnect();
    document.removeEventListener("visibilitychange", watcher.onVisibilityChange);
    element.removeEventListener("keydown", watcher.onKeyDown);
    watchers.delete(element);
}
