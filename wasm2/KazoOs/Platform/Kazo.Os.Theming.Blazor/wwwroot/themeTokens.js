// Applies the verified tokens of a user theme (ADR 0103 §2.3) to a theme root element.
// The C# side (ThemeTokenApplier) validates every name and value against ThemeTokenCatalog first;
// this module only calls style.setProperty/removeProperty on custom properties, so no markup carries
// an inline style attribute (ADR 0007) and nothing but "--hos-*" properties can be touched.

/**
 * Sets and removes custom properties on one element.
 * @param {HTMLElement} element Theme root (ThemeScope or ThemePreview).
 * @param {Record<string, string>} set Properties to set, by name.
 * @param {string[]} remove Properties to remove (set by a previous call, absent now).
 */
export function applyTokens(element, set, remove) {
    if (!element || !element.style) {
        return;
    }

    for (const name of remove ?? []) {
        if (name.startsWith("--hos-")) {
            element.style.removeProperty(name);
        }
    }

    for (const [name, value] of Object.entries(set ?? {})) {
        if (name.startsWith("--hos-")) {
            element.style.setProperty(name, value);
        }
    }
}
