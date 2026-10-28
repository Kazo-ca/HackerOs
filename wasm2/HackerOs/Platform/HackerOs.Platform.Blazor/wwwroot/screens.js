// Host side of the secondary screens (ADR 0178 §4, ADR 0180, doc/en/wasm/dev/screen-service.md).
//
// A secondary screen is a display surface (displaySurfaces.js) the screen service opens on another
// monitor. Only what C# cannot reach lives here: the Window Management API of Chrome and Edge
// (permission "window-management", getScreenDetails, screenschange), placing a surface's window on a
// free monitor, following where the user moved it (fallback), full screen, and whether the main tab
// is hidden. Every decision (numbers, what to remember, when to close) is made by ScreenService.

const POLL_MS = 1000;
const tracked = new Map();
let dotnet = null;
let details = null;
let detailsPromise = null;
let started = false;

function call(method, ...args) {
    dotnet?.invokeMethodAsync(method, ...args).catch(() => { });
}

function hasApi() {
    return typeof window.getScreenDetails === "function";
}

async function permissionState() {
    if (!hasApi()) {
        return "unsupported";
    }
    try {
        const status = await navigator.permissions.query({ name: "window-management" });
        return status.state;
    } catch {
        // Older Chromium names the permission "window-placement", or cannot query it: ask on use.
        return "prompt";
    }
}

function reportPermission() {
    permissionState().then((state) => call("OnPermission", state));
}

function onScreensChange() {
    // A monitor that was unplugged takes its screen with it (ADR 0178 §4).
    for (const [id, entry] of tracked) {
        if (entry.monitor && !details.screens.includes(entry.monitor)) {
            entry.monitor = null;
            call("OnScreenMonitorLost", id);
        }
    }
}

// Asks for the monitors once; the browser prompts for "window-management" the first time, which
// needs a user gesture: callers start it before the gesture is used up by window.open.
function requestDetails() {
    if (!hasApi()) {
        return Promise.resolve(null);
    }
    if (!detailsPromise) {
        detailsPromise = window.getScreenDetails().then((value) => {
            details = value;
            details.addEventListener("screenschange", onScreensChange);
            reportPermission();
            return details;
        }).catch(() => {
            detailsPromise = null;
            reportPermission();
            return null;
        });
    }
    return detailsPromise;
}

function monitorOf(win) {
    if (!details) {
        return null;
    }
    try {
        const x = win.screenX + win.outerWidth / 2;
        const y = win.screenY + win.outerHeight / 2;
        return details.screens.find((screen) =>
            x >= screen.left && x < screen.left + screen.width && y >= screen.top && y < screen.top + screen.height) ?? null;
    } catch {
        return null;
    }
}

// A monitor that shows neither the main tab nor another screen, left to right.
function freeMonitor() {
    if (!details) {
        return null;
    }
    const taken = new Set([details.currentScreen]);
    for (const entry of tracked.values()) {
        const monitor = entry.monitor ?? monitorOf(entry.window);
        if (monitor) {
            taken.add(monitor);
        }
    }
    return [...details.screens]
        .filter((screen) => !taken.has(screen))
        .sort((first, second) => first.left - second.left || first.top - second.top)[0] ?? null;
}

function rectOf(monitor) {
    return { left: monitor.availLeft, top: monitor.availTop, width: monitor.availWidth, height: monitor.availHeight };
}

function geometryOf(win) {
    return {
        left: Math.round(win.screenX),
        top: Math.round(win.screenY),
        width: Math.round(win.innerWidth),
        height: Math.round(win.innerHeight),
    };
}

function sameGeometry(first, second) {
    return first && second
        && first.left === second.left && first.top === second.top
        && first.width === second.width && first.height === second.height;
}

/** Starts following the main tab's visibility and the permission; called once by ScreenService. */
export async function start(ref) {
    dotnet = ref;
    const permission = await permissionState();
    if (!started) {
        started = true;
        document.addEventListener("visibilitychange", () => call("OnPrimaryVisibility", document.visibilityState === "hidden"));
        try {
            const status = await navigator.permissions.query({ name: "window-management" });
            status.addEventListener("change", reportPermission);
        } catch {
            // No permission events: the state is read again after each request.
        }
        if (permission === "granted") {
            // Already allowed: no prompt, and the next screen opens directly on a free monitor.
            requestDetails();
        }
    }
    return { permission, hidden: document.visibilityState === "hidden" };
}

/**
 * Called by displaySurfaces.open before window.open: where the new screen's window goes.
 * @param {{ remembered: { left: number, top: number, width: number, height: number } | null }} placement
 */
export async function prepare(placement) {
    const plan = { features: placement?.remembered ?? null, monitor: null, pending: null };
    if (!hasApi()) {
        return plan;
    }
    if (!details) {
        const state = await permissionState();
        if (state === "granted") {
            await requestDetails();
        } else if (state === "prompt") {
            // The prompt answers later: the window opens at its remembered place and moves then.
            plan.pending = requestDetails();
            return plan;
        } else {
            return plan;
        }
    }
    const monitor = freeMonitor();
    if (monitor) {
        plan.monitor = monitor;
        plan.features = rectOf(monitor);
    }
    return plan;
}

/** Called by displaySurfaces.open once the window is open; returns the monitor's label, or null. */
export function settle(id, win, plan) {
    const entry = { window: win, monitor: plan.monitor, last: null, fullScreen: false, timer: 0 };
    tracked.set(id, entry);
    entry.timer = setInterval(() => poll(id, entry), POLL_MS);
    win.document.addEventListener("fullscreenchange", () => {
        entry.fullScreen = Boolean(win.document.fullscreenElement);
        call("OnScreenFullScreen", id, entry.fullScreen);
    });
    if (!plan.monitor && plan.pending) {
        plan.pending.then((value) => {
            if (!value || tracked.get(id) !== entry || win.closed) {
                return;
            }
            const monitor = freeMonitor();
            if (!monitor) {
                return;
            }
            try {
                win.moveTo(monitor.availLeft, monitor.availTop);
                win.resizeTo(monitor.availWidth, monitor.availHeight);
            } catch {
                // The browser refused the move: the user places the window.
                return;
            }
            entry.monitor = monitor;
            call("OnScreenPlaced", id, monitor.label || "");
        });
    }
    return plan.monitor ? (plan.monitor.label || "") : null;
}

// The browser has no "window moved" event: the place of a fallback window is read every second.
function poll(id, entry) {
    const win = entry.window;
    if (win.closed || entry.fullScreen) {
        return;
    }
    let geometry;
    try {
        geometry = geometryOf(win);
    } catch {
        return;
    }
    if (!sameGeometry(geometry, entry.last)) {
        entry.last = geometry;
        call("OnScreenGeometry", id, geometry.left, geometry.top, geometry.width, geometry.height);
    }
}

/** Called by displaySurfaces.js when a surface ends. */
export function forget(id) {
    const entry = tracked.get(id);
    if (entry) {
        clearInterval(entry.timer);
        tracked.delete(id);
    }
}

/**
 * Enters or leaves full screen in a screen's window. Called from a click in that window: the
 * browser needs the gesture in the window that goes full screen.
 */
export async function toggleFullScreen(id) {
    const entry = tracked.get(id);
    if (!entry) {
        return false;
    }
    const doc = entry.window.document;
    try {
        if (doc.fullscreenElement) {
            await doc.exitFullscreen();
        } else {
            await doc.documentElement.requestFullscreen();
        }
        return true;
    } catch {
        return false;
    }
}
