/**
 * Window previews of the taskbar (ADR 0199 §1, §4). C# decides when a preview opens and closes;
 * this module only does what C# cannot reach: it places the preview above its button and fills it
 * with a reduced live copy of the window's element, refreshed while the preview stays open.
 *
 * The copy is a clone of the window's DOM, scaled down with CSS. It never duplicates an HTML id
 * nor stays interactive: ids, names and id references are removed, and the copy lives in an inert,
 * aria-hidden layer. A window that shows an iframe, a video or a plugin keeps the icon and title
 * that C# rendered under the copy: those cannot be cloned.
 */

/** How often the copy is taken again while the preview stays open. */
const refreshIntervalMs = 500;

/** Elements whose content a DOM clone cannot show: the preview keeps its icon and title. */
const uncloneableSelector = "iframe, video, embed, object";

/** Attributes that point at another element by id: in a copy, they would point at the original. */
const idReferenceAttributes = [
    "for", "form", "list", "aria-labelledby", "aria-describedby", "aria-controls",
    "aria-activedescendant", "aria-owns", "aria-details", "aria-errormessage", "aria-flowto"
];

/** Attributes that would make the copy act like the original (radio groups, focus, popovers). */
const behaviorAttributes = ["id", "name", "tabindex", "autofocus", "accesskey", "contenteditable", "popover"];

const previews = new WeakMap();

/**
 * Places a preview above the button of its window and fills it with the window's live copy.
 * Called each time the preview shows another window; replaces what the preview showed before.
 */
export function showPreview(preview, windowId, liveCopySelector) {
    if (!preview || !preview.isConnected) {
        return;
    }

    stopRefresh(preview);
    placeAboveButton(preview, windowId);

    const live = preview.querySelector(".taskbar-preview-live");
    if (!live) {
        return;
    }

    const refresh = () => {
        if (!preview.isConnected) {
            stopRefresh(preview);
            return;
        }
        const source = liveCopySelector ? preview.ownerDocument.querySelector(liveCopySelector) : null;
        const copied = source ? copyWindow(source, live) : false;
        preview.toggleAttribute("data-live-copy", copied);
    };

    refresh();
    previews.set(preview, preview.ownerDocument.defaultView.setInterval(refresh, refreshIntervalMs));
}

function stopRefresh(preview) {
    const timer = previews.get(preview);
    if (timer !== undefined) {
        preview.ownerDocument.defaultView?.clearInterval(timer);
        previews.delete(preview);
    }
}

/** Centers the preview above its button, kept inside the taskbar's width. */
function placeAboveButton(preview, windowId) {
    const taskbar = preview.offsetParent ?? preview.parentElement;
    const button = taskbar?.querySelector(`[data-taskbar-window-id="${CSS.escape(windowId)}"]`);
    if (!taskbar || !button) {
        return;
    }

    const bar = taskbar.getBoundingClientRect();
    const anchor = button.getBoundingClientRect();
    const width = preview.offsetWidth;
    const centered = anchor.left - bar.left + anchor.width / 2 - width / 2;
    const left = Math.max(4, Math.min(centered, bar.width - width - 4));
    preview.style.setProperty("--taskbar-preview-left", `${left}px`);
}

/**
 * Replaces the content of the live layer with a reduced copy of the window's element.
 * Returns false when the window cannot be copied, so the icon and title stay visible.
 */
function copyWindow(source, live) {
    if (source.querySelector(uncloneableSelector)) {
        live.replaceChildren();
        return false;
    }

    const size = windowSize(source);
    if (!size) {
        live.replaceChildren();
        return false;
    }

    const copy = source.cloneNode(true);
    sanitize(copy);
    copyCanvases(source, copy);

    const scale = Math.min(live.clientWidth / size.width, live.clientHeight / size.height, 1);
    copy.style.setProperty("position", "absolute");
    copy.style.setProperty("inset", "0 auto auto 0");
    copy.style.setProperty("width", `${size.width}px`);
    copy.style.setProperty("height", `${size.height}px`);
    copy.style.setProperty("min-width", "0");
    copy.style.setProperty("min-height", "0");
    copy.style.setProperty("margin", "0");
    copy.style.setProperty("translate", "none");
    copy.style.setProperty("transform-origin", "0 0");
    copy.style.setProperty("transform", `scale(${scale})`);
    copy.style.setProperty("left", `${(live.clientWidth - size.width * scale) / 2}px`);
    copy.style.setProperty("top", `${(live.clientHeight - size.height * scale) / 2}px`);

    live.replaceChildren(copy);
    copyScrollPositions(source, copy);
    return true;
}

/** The window's size on screen, or its normal size from its geometry when it is minimized. */
function windowSize(source) {
    const rect = source.getBoundingClientRect();
    if (rect.width > 0 && rect.height > 0) {
        return { width: rect.width, height: rect.height };
    }
    const style = getComputedStyle(source);
    const width = parseFloat(style.getPropertyValue("--window-width"));
    const height = parseFloat(style.getPropertyValue("--window-height"));
    return width > 0 && height > 0 ? { width, height } : null;
}

/** Removes from the copy every id, name and id reference, and everything that takes the focus. */
function sanitize(copy) {
    for (const element of [copy, ...copy.querySelectorAll("*")]) {
        for (const name of behaviorAttributes) {
            element.removeAttribute(name);
        }
        for (const name of idReferenceAttributes) {
            element.removeAttribute(name);
        }
        for (const attribute of [...element.attributes]) {
            if (attribute.name.startsWith("data-window-")) {
                element.removeAttribute(attribute.name);
            }
        }
    }
    copy.removeAttribute("hidden");
    copy.setAttribute("role", "presentation");
    copy.removeAttribute("aria-label");
    copy.removeAttribute("aria-modal");
}

/** A cloned canvas is blank: draws the pixels of each original canvas into its copy. */
function copyCanvases(source, copy) {
    const originals = source.querySelectorAll("canvas");
    const copies = copy.querySelectorAll("canvas");
    originals.forEach((original, index) => {
        const target = copies[index];
        try {
            target?.getContext("2d")?.drawImage(original, 0, 0);
        } catch {
            // A canvas the page cannot read stays blank in the copy.
        }
    });
}

/** A clone starts scrolled to the top: scrolls each copied element where its original is. */
function copyScrollPositions(source, copy) {
    const originals = source.querySelectorAll("*");
    const copies = copy.querySelectorAll("*");
    originals.forEach((original, index) => {
        if (original.scrollTop || original.scrollLeft) {
            const target = copies[index];
            if (target) {
                target.scrollTop = original.scrollTop;
                target.scrollLeft = original.scrollLeft;
            }
        }
    });
}
