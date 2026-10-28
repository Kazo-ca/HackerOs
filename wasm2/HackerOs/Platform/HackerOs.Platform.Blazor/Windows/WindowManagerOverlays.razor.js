/**
 * Browser side of the window manager's keyboard commands (ADR 0095 §3).
 *
 * C# (WindowKeyboardCommands) decides everything: which chords are bound, what they do, when the
 * keyboard is grabbed. This module only does what C# cannot do synchronously in a key event:
 * catch a bound chord in the capture phase, before any app sees it, and cancel the browser's
 * default action; forward every key while the window manager holds the keyboard; and ask the host
 * for full screen and the keyboard lock (navigator.keyboard.lock, the only way to receive Alt+Tab,
 * Alt+F4 or Super inside a page). Keys pressed inside an iframe never reach the page and stay there.
 */

const MODIFIERS = new Set(["Alt", "AltGraph", "Control", "Shift", "Meta", "OS", "Super", "Hyper"]);

const state = {
    dotNet: null,
    bindings: [],
    grab: "none",
    switchPending: false,
};

/** Same rule as KeyChord.Matches in C#: modifiers exact, then the key, else the physical key. */
function matches(binding, event) {
    if (binding.ctrl !== event.ctrlKey || binding.alt !== event.altKey
        || binding.shift !== event.shiftKey || binding.meta !== event.metaKey) {
        return false;
    }

    if (binding.key !== null && binding.key !== undefined) {
        if (event.key.toLowerCase() === binding.key.toLowerCase()) {
            return true;
        }

        // macOS Option turns Alt+Q into "œ": fall back to the physical key for a printable chord.
        if (binding.key.length === 1 && event.key.length === 1 && event.key.charCodeAt(0) > 127) {
            return !!binding.code && event.code === binding.code;
        }

        return false;
    }

    return !!binding.code && event.code === binding.code;
}

function onKeyDown(event) {
    if (!state.dotNet) {
        return;
    }

    if (state.grab !== "none") {
        event.preventDefault();
        event.stopPropagation();
        if (!MODIFIERS.has(event.key)) {
            state.dotNet.invokeMethodAsync("OnGrabbedKey", event.key, event.code,
                event.ctrlKey, event.altKey, event.shiftKey, event.metaKey);
        }

        return;
    }

    const binding = state.bindings.find(candidate => matches(candidate, event));
    if (!binding) {
        return;
    }

    event.preventDefault();
    event.stopPropagation();
    if (event.repeat && !binding.holdsAlt) {
        return;
    }

    if (binding.holdsAlt) {
        // Alt may be released before C# opened the switcher: remember to report it.
        state.switchPending = true;
    }

    state.dotNet.invokeMethodAsync("OnShortcutAsync", binding.action);
}

function onKeyUp(event) {
    if (state.dotNet && (event.key === "Alt" || !event.altKey) && (state.switchPending || state.grab === "switching")) {
        state.switchPending = false;
        state.dotNet.invokeMethodAsync("OnAltReleased");
    }
}

function onBlur() {
    // The host took the focus (its own Alt+Tab): an open switcher switches, as on release.
    if (state.dotNet && (state.switchPending || state.grab === "switching")) {
        state.switchPending = false;
        state.dotNet.invokeMethodAsync("OnAltReleased");
    }
}

async function onFullScreenChange() {
    const fullScreen = !!document.fullscreenElement;
    let locked = false;
    if (fullScreen && navigator.keyboard && typeof navigator.keyboard.lock === "function") {
        try {
            await navigator.keyboard.lock();
            locked = true;
        } catch {
            locked = false;
        }
    } else if (!fullScreen && navigator.keyboard && typeof navigator.keyboard.unlock === "function") {
        navigator.keyboard.unlock();
    }

    state.dotNet?.invokeMethodAsync("OnHostKeyboardState", fullScreen, locked);
}

export function attachWindowKeyboard(dotNet, bindings) {
    detachWindowKeyboard();
    state.dotNet = dotNet;
    state.bindings = bindings ?? [];
    state.grab = "none";
    // On `document`, which the display surfaces mirror onto each secondary screen (ADR 0179 §2):
    // the shortcuts work in every screen's window.
    document.addEventListener("keydown", onKeyDown, true);
    document.addEventListener("keyup", onKeyUp, true);
    window.addEventListener("blur", onBlur);
    document.addEventListener("fullscreenchange", onFullScreenChange);
}

export function updateWindowKeyboard(bindings, grab) {
    state.bindings = bindings ?? [];
    state.grab = grab ?? "none";
    if (state.grab === "switching") {
        state.switchPending = false;
    }
}

export function detachWindowKeyboard() {
    document.removeEventListener("keydown", onKeyDown, true);
    document.removeEventListener("keyup", onKeyUp, true);
    window.removeEventListener("blur", onBlur);
    document.removeEventListener("fullscreenchange", onFullScreenChange);
    state.dotNet = null;
    state.bindings = [];
    state.grab = "none";
    state.switchPending = false;
}

export async function toggleFullScreen() {
    try {
        if (document.fullscreenElement) {
            await document.exitFullscreen();
        } else {
            await document.documentElement.requestFullscreen({ navigationUI: "hide" });
        }
    } catch {
        // Refused by the host (no user gesture, policy): the state does not change.
    }
}

/** Places the menu at the requested point, kept inside the viewport of the screen it is shown on. */
export function placeWindowMenu(menu, x, y) {
    if (!menu) {
        return;
    }

    const view = menu.ownerDocument?.defaultView ?? window;
    const rect = menu.getBoundingClientRect();
    const left = Math.max(4, Math.min(x, view.innerWidth - rect.width - 4));
    const top = Math.max(4, Math.min(y, view.innerHeight - rect.height - 4));
    menu.style.setProperty("--window-menu-x", `${left}px`);
    menu.style.setProperty("--window-menu-y", `${top}px`);
}
