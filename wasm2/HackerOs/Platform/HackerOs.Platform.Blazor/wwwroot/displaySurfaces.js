// Host side of the detached display surfaces (ADR 0078 §2, doc/wasm/dev/detached-display-windows.md).
//
// Only what C# cannot reach lives here: opening a same-origin window of the host browser on an
// empty document, building that document (style sheets mirrored from the desktop, a root for the
// content), moving rendered content in and out, and reporting the window's events (resize, focus,
// close) to DisplaySurfaceService. The window runs no .NET runtime: the main instance renders into
// it, and the JS initializer (HackerOs.Platform.Blazor.lib.module.js) mirrors Blazor's event
// listeners onto its document.

const REGISTRY_KEY = Symbol.for("hackeros.displaySurfaces.documentListeners");
const MIRRORED = "data-hackeros-mirrored";
const surfaces = new Map();
const anchors = new WeakMap();
let unloadHooked = false;

function report(surface, type) {
    const win = surface.window;
    let width = 0;
    let height = 0;
    try {
        width = Math.round(win.innerWidth || 0);
        height = Math.round(win.innerHeight || 0);
    } catch {
        // A closed window has no size.
    }
    surface.dotnet.invokeMethodAsync("OnSurfaceEvent", surface.id, type, width, height).catch(() => { });
}

// Copies the desktop's style sheets (head links and styles, and the <style> elements Blazor
// components render in the body, such as MudBlazor's theme) into the surface, and the root
// element's attributes, so the content looks the same in both windows.
function mirrorStyles(surface) {
    const doc = surface.window.document;
    doc.querySelectorAll(`[${MIRRORED}]`).forEach((node) => node.remove());
    const sources = document.querySelectorAll("link[rel~='stylesheet'], style");
    for (const source of sources) {
        if (source.closest(".hackeros-surface-root")) {
            continue;
        }
        const copy = doc.importNode(source, true);
        copy.setAttribute(MIRRORED, "");
        if (copy.tagName === "LINK") {
            copy.href = source.href;
        }
        doc.head.appendChild(copy);
    }
    for (const name of ["class", "data-theme", "lang", "dir"]) {
        const value = document.documentElement.getAttribute(name);
        if (value === null) {
            doc.documentElement.removeAttribute(name);
        } else {
            doc.documentElement.setAttribute(name, value);
        }
    }
}

function buildDocument(surface, title) {
    const doc = surface.window.document;
    doc.title = title;
    if (!doc.head.querySelector("meta[name='viewport']")) {
        const viewport = doc.createElement("meta");
        viewport.name = "viewport";
        viewport.content = "width=device-width, initial-scale=1";
        doc.head.appendChild(viewport);
    }
    // Relative URLs in the content (images, _content/ assets) resolve as on the desktop.
    const base = doc.createElement("base");
    base.href = document.baseURI;
    doc.head.prepend(base);

    // Runs in the surface's realm: lets the desktop post messages that come from the surface.
    const relay = doc.createElement("script");
    relay.src = new URL("_content/HackerOs.Platform.Blazor/displaySurfaceRelay.js", document.baseURI).href;
    surface.relayLoaded = new Promise((resolve) => {
        relay.addEventListener("load", resolve);
        relay.addEventListener("error", resolve);
        setTimeout(resolve, 3000);
    });
    doc.head.appendChild(relay);

    const own = doc.createElement("link");
    own.rel = "stylesheet";
    own.href = new URL("_content/HackerOs.Platform.Blazor/displaySurface.css", document.baseURI).href;
    doc.head.appendChild(own);
    mirrorStyles(surface);

    doc.body.className = "hackeros-surface";
    doc.body.setAttribute("data-surface-id", surface.id);
    // Created by the desktop's document, like everything Blazor renders: the content's elements
    // stay objects of the main window, which Blazor's event code expects.
    const root = document.createElement("div");
    root.className = "hackeros-surface-root";
    doc.body.appendChild(root);
    surface.root = root;
}

function watch(surface) {
    const win = surface.window;
    let resizeTimer = null;
    win.addEventListener("resize", () => {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => report(surface, "resize"), 100);
    });
    win.addEventListener("focus", () => report(surface, "focus"));
    win.addEventListener("blur", () => report(surface, "blur"));

    // The page bridge of an isolated page frame (ADR 0075) posts to its parent window, which is
    // now the surface: hand its messages to the desktop window, where the frame's host listens.
    win.addEventListener("message", (event) => {
        window.dispatchEvent(new MessageEvent("message", {
            data: event.data,
            origin: event.origin,
            source: event.source,
            ports: [...event.ports],
        }));
    });

    // Closing the window, or navigating it away, unloads the surface's document.
    win.addEventListener("pagehide", () => {
        if (surface.ending) {
            return;
        }
        setTimeout(() => end(surface, win.closed ? "closed" : "lost"), 0);
    });
    // A window closed without pagehide (the host process died, some mobile browsers).
    surface.poll = setInterval(() => {
        if (win.closed) {
            end(surface, "closed");
        }
    }, 1000);

    surface.styleObserver = new MutationObserver(() => mirrorStyles(surface));
    surface.styleObserver.observe(document.head, { childList: true, subtree: true, characterData: true });
    surface.themeObserver = new MutationObserver(() => mirrorStyles(surface));
    surface.themeObserver.observe(document.documentElement, { attributes: true });
}

// Gives every mounted element back to its anchor on the desktop, so the renderer can remove it.
function giveBack(surface) {
    if (!surface.root) {
        return;
    }
    for (const element of [...surface.root.children]) {
        const anchor = anchors.get(element);
        if (anchor && anchor.isConnected) {
            anchor.appendChild(element);
        }
    }
}

function end(surface, reason) {
    if (surface.ending) {
        return;
    }
    surface.ending = true;
    surfaces.delete(surface.id);
    clearInterval(surface.poll);
    surface.styleObserver?.disconnect();
    surface.themeObserver?.disconnect();
    giveBack(surface);
    try {
        window[REGISTRY_KEY]?.detach(surface.window.document);
    } catch {
        // The window is gone.
    }
    if (reason === "lost" && !surface.window.closed) {
        // The window shows something else now: it is no longer ours to keep open.
        surface.window.close();
    }
    if (reason !== "closed-by-os") {
        report(surface, reason);
    }
}

function hookUnload() {
    if (unloadHooked) {
        return;
    }
    unloadHooked = true;
    // Closing or reloading the OS closes its surfaces (ADR 0078 §2).
    window.addEventListener("pagehide", () => {
        for (const surface of [...surfaces.values()]) {
            surface.ending = true;
            surface.window.close();
        }
        surfaces.clear();
    });
}

export async function open(dotnet, id, title, width, height) {
    let win = null;
    try {
        win = window.open("", `hackeros-${id}`, `popup=yes,width=${width},height=${height}`);
    } catch {
        win = null;
    }
    if (!win) {
        return { status: "blocked", width: 0, height: 0 };
    }

    const surface = { id, dotnet, window: win, root: null, ending: false };
    buildDocument(surface, title);
    const registry = window[REGISTRY_KEY];
    if (registry) {
        registry.attach(win.document);
    } else {
        console.warn("[HackerOS] Display surfaces: the event mirror is not installed; content in detached windows will not react to input.");
    }
    watch(surface);
    surfaces.set(id, surface);
    hookUnload();
    await surface.relayLoaded;
    return { status: "opened", width: Math.round(win.innerWidth || width), height: Math.round(win.innerHeight || height) };
}

// The content leaves the desktop's ancestors (a theme scope, a window's colours): it keeps their
// inherited look by copying the computed custom properties and text style onto the surface root.
function inheritLook(surface, anchor) {
    if (!(anchor instanceof Element)) {
        return;
    }
    const computed = getComputedStyle(anchor);
    for (let index = 0; index < computed.length; index++) {
        const name = computed[index];
        if (name.startsWith("--")) {
            surface.root.style.setProperty(name, computed.getPropertyValue(name));
        }
    }
    for (const name of ["color", "font-family", "font-size", "line-height"]) {
        surface.root.style.setProperty(name, computed.getPropertyValue(name));
    }
}

export function mount(id, element) {
    const surface = surfaces.get(id);
    if (!surface || !element) {
        return;
    }
    if (!anchors.has(element)) {
        anchors.set(element, element.parentNode);
    }
    inheritLook(surface, anchors.get(element));
    surface.root.appendChild(element);
}

export function unmount(id, element) {
    if (!element) {
        return;
    }
    const anchor = anchors.get(element);
    if (anchor && anchor.isConnected && element.parentNode !== anchor) {
        anchor.appendChild(element);
    }
    anchors.delete(element);
}

export function setTitle(id, title) {
    const surface = surfaces.get(id);
    if (surface) {
        surface.window.document.title = title;
    }
}

export function focus(id) {
    surfaces.get(id)?.window.focus();
}

export function close(id) {
    const surface = surfaces.get(id);
    if (!surface) {
        return;
    }
    end(surface, "closed-by-os");
    surface.window.close();
}
