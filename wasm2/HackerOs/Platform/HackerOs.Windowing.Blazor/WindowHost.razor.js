/**
 * Projects C#-authoritative geometry through CSS custom properties without
 * placing a dynamic style attribute in Razor source, then plays the window animation of this
 * render (ADR 0198): "open", "minimize", "restore", "geometry" (maximize, restore the size, snap)
 * or "none". The promise of "minimize" settles when the window may be hidden.
 */
export function projectWindowGeometry(element, x, y, width, height, zOrder, transition = "none", windowId = null) {
    if (!element || !element.style) {
        return undefined;
    }
    // The box before this render: the drag preview when a drag just ended (a snap), else the box
    // of the previous render. Read before the new geometry is written.
    const before = element.__hosPreviewBox ?? element.__hosLayoutBox ?? null;
    delete element.__hosPreviewBox;
    // Drawn transparent by the first render until its open animation starts (or is skipped).
    element.classList.remove("is-opening");
    element.style.setProperty("--window-x", `${x}px`);
    element.style.setProperty("--window-y", `${y}px`);
    element.style.setProperty("--window-width", `${width}px`);
    element.style.setProperty("--window-height", `${height}px`);
    element.style.setProperty("--window-z", `${zOrder}`);

    let settled;
    switch (transition) {
        case "open":
            playOpen(element);
            break;
        case "minimize":
            settled = playMinimize(element, windowId);
            break;
        case "restore":
            playRestore(element, windowId);
            break;
        case "geometry":
            playGeometryChange(element, before);
            break;
        default:
            break;
    }
    rememberLayout(element);
    return settled;
}

/**
 * Plays the close animation of a window that is going away (ADR 0198 §4). Like a compositor that
 * keeps the last frame of a destroyed window, it fades a frozen copy of the window: the real
 * element belongs to Blazor and is removed at once.
 */
export function playClose(element) {
    if (!element) {
        return;
    }
    const layer = element.isConnected ? element.closest(".window-layer") : element.__hosLayer;
    // Hidden or shrinking into the taskbar: nothing to fade.
    if (!layer || !layer.isConnected || element.hidden || !element.__hosLayoutBox || element.__hosMinimizing) {
        return;
    }
    const motion = motionOf(layer);
    if (!motion) {
        return;
    }
    const ghost = frozenCopy(element);
    layer.appendChild(ghost);
    const animation = animate(ghost, [
        { opacity: 1, transform: "none" },
        { opacity: 0, transform: `scale(${motion.scale})` }
    ], motion, "forwards");
    const remove = () => ghost.remove();
    if (!animation) {
        remove();
        return;
    }
    settle(animation, motion).then(remove);
}

// Window animations (ADR 0198). Duration, curve and scale come from theme variables; a duration of
// 0 (the "Reduce motion" setting zeroes it, doc/en/wasm/dev/window-animations.md) means no
// animation at all, so the window manager behaves exactly as before.
const defaultDurationMs = 180;
const defaultEasing = "cubic-bezier(0.2, 0, 0, 1)";
const defaultScale = 0.95;
const reducedMotionSelector = ["reduced", "disabled", "off"]
    .flatMap(mode => [`[data-motion="${mode}"]`, `[data-hos-motion="${mode}"]`])
    .join(",");

function motionOf(element) {
    if (!element || !element.isConnected || typeof element.animate !== "function"
        || element.closest(reducedMotionSelector)) {
        return null;
    }
    const view = element.ownerDocument.defaultView;
    const style = view.getComputedStyle(element);
    const [durationText, ...curve] = style.getPropertyValue("--hos-window-anim-duration").trim().split(/\s+/);
    // No theme sheet (a sample host): the default, unless the browser asks for less motion.
    if (!durationText && view.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
        return null;
    }
    const duration = durationText ? parseDuration(durationText) : defaultDurationMs;
    if (!(duration > 0)) {
        return null;
    }
    const easing = curve.join(" ") || style.getPropertyValue("--hos-window-anim-easing").trim() || defaultEasing;
    const scale = Number.parseFloat(style.getPropertyValue("--hos-window-anim-scale"));
    return { duration, easing, scale: scale > 0 && scale <= 1 ? scale : defaultScale };
}

function parseDuration(text) {
    const match = /^(\d+|\d*\.\d+)(ms|s)$/.exec(text);
    if (!match) {
        return defaultDurationMs;
    }
    return Number(match[1]) * (match[2] === "s" ? 1000 : 1);
}

// Starts one window animation, after cancelling the previous one: a restore that comes during a
// minimize starts from where the window is, and never leaves it transparent.
function animate(element, keyframes, motion, fill = "none") {
    element.__hosWindowAnimation?.cancel();
    element.__hosWindowAnimation = null;
    let animation;
    try {
        animation = element.animate(keyframes, { duration: motion.duration, easing: motion.easing, fill });
    } catch {
        // A theme curve the browser does not understand.
        animation = element.animate(keyframes, { duration: motion.duration, easing: defaultEasing, fill });
    }
    element.__hosWindowAnimation = animation;
    return animation;
}

function cancelAnimation(element) {
    element.__hosWindowAnimation?.cancel();
    element.__hosWindowAnimation = null;
    element.__hosMinimizing = false;
}

// Settles when the animation ends or is cancelled, or a little after its duration: a background
// tab may not paint the frame that finishes it.
function settle(animation, motion) {
    const finished = animation.finished.then(() => undefined, () => undefined);
    const timeout = new Promise(resolve => setTimeout(resolve, motion.duration + 100));
    return Promise.race([finished, timeout]);
}

function playOpen(element) {
    cancelAnimation(element);
    const motion = motionOf(element);
    if (!motion || element.hidden) {
        return;
    }
    animate(element, [
        { opacity: 0, transform: `scale(${motion.scale})` },
        { opacity: 1, transform: "none" }
    ], motion, "backwards");
}

// Minimize: the window shrinks into its button on the taskbar of its own screen, or towards the
// bottom of its screen when that taskbar has no button for it. It stays drawn at the end (fill
// "forwards") until C# hides it, so it never flashes back in between.
function playMinimize(element, windowId) {
    cancelAnimation(element);
    const motion = motionOf(element);
    if (!motion) {
        return Promise.resolve();
    }
    const keyframes = towardsTaskbar(element, windowId, motion);
    if (!keyframes) {
        return Promise.resolve();
    }
    element.__hosMinimizing = true;
    return settle(animate(element, keyframes, motion, "forwards"), motion);
}

// Restore from the taskbar: the minimize played backwards.
function playRestore(element, windowId) {
    cancelAnimation(element);
    const motion = motionOf(element);
    if (!motion || element.hidden) {
        return;
    }
    const keyframes = towardsTaskbar(element, windowId, motion);
    if (keyframes) {
        animate(element, keyframes.reverse(), motion);
    }
}

function towardsTaskbar(element, windowId, motion) {
    const box = element.getBoundingClientRect();
    if (box.width <= 0 || box.height <= 0) {
        return null;
    }
    const target = taskbarButtonBox(element, windowId) ?? screenBottomBox(element, box);
    const dx = target.left + target.width / 2 - (box.left + box.width / 2);
    const dy = target.top + target.height / 2 - (box.top + box.height / 2);
    const scale = Math.max(0.05, Math.min(motion.scale, target.width / box.width));
    return [
        { opacity: 1, transform: "none" },
        { opacity: 0, transform: `translate(${dx}px, ${dy}px) scale(${scale})` }
    ];
}

// The button of this window on the taskbar of the screen the window is on (ADR 0179: one taskbar
// per screen). Several screens can share a page: the button closest to the window's layer wins.
function taskbarButtonBox(element, windowId) {
    if (!windowId) {
        return null;
    }
    const layer = element.closest(".window-layer")?.getBoundingClientRect();
    let best = null;
    let bestDistance = Infinity;
    for (const button of element.ownerDocument.querySelectorAll(`[data-taskbar-window-id="${CSS.escape(String(windowId))}"]`)) {
        const box = button.getBoundingClientRect();
        if (box.width <= 0 || box.height <= 0) {
            continue;
        }
        const distance = layer
            ? Math.max(0, layer.left - box.right, box.left - layer.right) + Math.max(0, layer.top - box.bottom, box.top - layer.bottom)
            : 0;
        if (distance < bestDistance) {
            best = box;
            bestDistance = distance;
        }
    }
    return best;
}

function screenBottomBox(element, box) {
    const layer = element.closest(".window-layer")?.getBoundingClientRect();
    const left = layer ? layer.left + layer.width / 2 : box.left + box.width / 2;
    const bottom = layer ? layer.bottom : box.bottom;
    return { left: left - 24, top: bottom - 24, width: 48, height: 24 };
}

// Maximize, restore the size and snap: the window flies from its previous box to its new one
// ("FLIP": the new layout is drawn at once, a transform makes it start from the old box).
function playGeometryChange(element, before) {
    cancelAnimation(element);
    const motion = motionOf(element);
    const after = layoutBox(element);
    if (!motion || !before || !after || element.hidden
        || before.width <= 0 || before.height <= 0 || after.width <= 0 || after.height <= 0) {
        return;
    }
    const dx = before.left - after.left;
    const dy = before.top - after.top;
    const sx = before.width / after.width;
    const sy = before.height / after.height;
    if (Math.abs(dx) < 1 && Math.abs(dy) < 1 && Math.abs(sx - 1) < 0.01 && Math.abs(sy - 1) < 0.01) {
        return;
    }
    animate(element, [
        { transformOrigin: "0 0", transform: `translate(${dx}px, ${dy}px) scale(${sx}, ${sy})` },
        { transformOrigin: "0 0", transform: "none" }
    ], motion);
}

// The window's box in the page without any running animation or drag preview: the start of the
// next geometry animation, and what the close animation needs to know the window was drawn.
function layoutBox(element) {
    const parent = element.offsetParent;
    if (element.hidden || !parent) {
        return null;
    }
    const origin = parent.getBoundingClientRect();
    return {
        left: origin.left + element.offsetLeft,
        top: origin.top + element.offsetTop,
        width: element.offsetWidth,
        height: element.offsetHeight
    };
}

function rememberLayout(element) {
    element.__hosLayoutBox = layoutBox(element);
    element.__hosLayer = element.closest(".window-layer") ?? element.__hosLayer ?? null;
}

// A copy of the window for its close animation: a <div>, not an <article>, hidden from assistive
// technologies, out of the focus order and of the window queries (no data-window-*, no id), with
// no live content (no iframe, video or audio that would load or play again). Canvases keep their
// last picture. It keeps the class and the scoped-CSS attribute, so it is drawn like the window.
function frozenCopy(element) {
    const copy = element.cloneNode(true);
    const ghost = element.ownerDocument.createElement("div");
    for (const attribute of copy.attributes) {
        const name = attribute.name;
        if (name !== "id" && name !== "role" && name !== "tabindex" && name !== "hidden" && name !== "inert"
            && !name.startsWith("aria-") && !name.startsWith("data-window")) {
            ghost.setAttribute(name, attribute.value);
        }
    }
    ghost.append(...copy.childNodes);
    ghost.setAttribute("aria-hidden", "true");
    ghost.inert = true;
    ghost.classList.add("is-closing-ghost");
    for (const node of ghost.querySelectorAll("[id]")) {
        node.removeAttribute("id");
    }
    for (const node of ghost.querySelectorAll("iframe, video, audio, object, embed")) {
        node.replaceWith(ghost.ownerDocument.createElement("div"));
    }
    const sources = element.querySelectorAll("canvas");
    ghost.querySelectorAll("canvas").forEach((canvas, index) => {
        try {
            canvas.getContext("2d")?.drawImage(sources[index], 0, 0);
        } catch {
            // A WebGL or tainted canvas: an empty one is fine for a fade.
        }
    });
    return ghost;
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
