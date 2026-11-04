// Draws a cursor theme (ADR 0205 §4) on the whole document, as a real OS swaps the bitmaps behind
// the cursor names an app asks for. The C# side (CursorThemeSurface) validated the theme: every
// image is a checked PNG (a data: URL) or a built-in file of this library. This module only:
//  1. rewrites every same-origin style rule (imported sheets included) whose `cursor` is a single themed name
//     (`cursor: pointer` -> `cursor: url(...) 11 1, pointer`), remembering the original value;
//  2. adds one small style element for what no rule names: the document's default arrow, links,
//     and text fields (which would otherwise inherit the arrow instead of the I-beam);
//  3. watches for style sheets added later (lazy-loaded apps) and rewrites them too.
// An empty theme restores every rule and removes the style element. Cross-origin sheets, inline
// style attributes and iframes keep the host's cursors.

const styleElementId = "hos-cursor-theme";
const rewritten = []; // { rule, value, priority }
let cursors = null; // name -> CSS value
let observer = null;

/**
 * Applies a theme; an empty object restores the host's cursors.
 * @param {Record<string, {url: string, x: number, y: number}>} theme Images by cursor name.
 */
export function applyCursorTheme(theme) {
    restoreRules();
    cursors = toCssValues(theme ?? {});
    if (Object.keys(cursors).length === 0) {
        cursors = null;
        document.getElementById(styleElementId)?.remove();
        observer?.disconnect();
        observer = null;
        return;
    }

    writeBaseStyle();
    for (const sheet of Array.from(document.styleSheets)) {
        rewriteSheet(sheet);
    }

    watchNewSheets();
}

function toCssValues(theme) {
    const values = {};
    for (const [name, image] of Object.entries(theme)) {
        const url = safeUrl(image?.url);
        if (url && /^[a-z-]+$/.test(name)) {
            values[name] = `url("${url}") ${Math.trunc(image.x) || 0} ${Math.trunc(image.y) || 0}, ${name}`;
        }
    }

    return values;
}

// A cursor rewritten inside another sheet resolves relative URLs against that sheet: built-in
// files are made absolute. Only PNG data: URLs and same-origin files are accepted.
function safeUrl(url) {
    if (typeof url !== "string") {
        return null;
    }

    if (url.startsWith("data:image/png;base64,")) {
        return /^[A-Za-z0-9+/=]*$/.test(url.slice(22)) ? url : null;
    }

    const absolute = new URL(url, document.baseURI);
    return absolute.origin === location.origin ? absolute.href.replaceAll('"', "%22") : null;
}

function writeBaseStyle() {
    let element = document.getElementById(styleElementId);
    if (!element) {
        element = document.createElement("style");
        element.id = styleElementId;
        document.head.appendChild(element);
    }

    const rules = [];
    if (cursors.default) {
        rules.push(`html { cursor: ${cursors.default}; }`);
    }

    if (cursors.pointer) {
        rules.push(`:where(a[href]) { cursor: ${cursors.pointer}; }`);
    }

    if (cursors.text) {
        rules.push(":where(textarea, [contenteditable]:not([contenteditable=\"false\"]), "
            + "input:not([type=button], [type=submit], [type=reset], [type=checkbox], [type=radio], "
            + `[type=range], [type=color], [type=file], [type=image], [type=hidden])) { cursor: ${cursors.text}; }`);
    }

    element.textContent = rules.join("\n");
}

function rewriteSheet(sheet) {
    if (!cursors || sheet.ownerNode?.id === styleElementId) {
        return;
    }

    let rules;
    try {
        rules = sheet.cssRules;
    } catch {
        return; // Cross-origin sheet: its rules cannot be read.
    }

    rewriteRules(rules);
}

function rewriteRules(rules) {
    for (const rule of Array.from(rules ?? [])) {
        const value = rule.style?.getPropertyValue("cursor")?.trim();
        if (value && cursors[value]) {
            const priority = rule.style.getPropertyPriority("cursor");
            rewritten.push({ rule, value, priority });
            rule.style.setProperty("cursor", cursors[value], priority);
        }

        if (rule.styleSheet) {
            rewriteSheet(rule.styleSheet); // @import: the bundles of the component libraries.
        }

        if (rule.cssRules) {
            rewriteRules(rule.cssRules); // @media, @supports, @layer, nesting.
        }
    }
}

function restoreRules() {
    for (const { rule, value, priority } of rewritten.splice(0)) {
        rule.style?.setProperty("cursor", value, priority);
    }
}

function watchNewSheets() {
    if (observer) {
        return;
    }

    observer = new MutationObserver(records => {
        for (const record of records) {
            for (const node of record.addedNodes) {
                if (node instanceof HTMLStyleElement) {
                    rewriteSheet(node.sheet);
                } else if (node instanceof HTMLLinkElement && node.rel === "stylesheet") {
                    if (node.sheet) {
                        rewriteSheet(node.sheet);
                    } else {
                        node.addEventListener("load", () => rewriteSheet(node.sheet), { once: true });
                    }
                }
            }
        }
    });
    observer.observe(document.head, { childList: true, subtree: true });
    observer.observe(document.body, { childList: true });
}
