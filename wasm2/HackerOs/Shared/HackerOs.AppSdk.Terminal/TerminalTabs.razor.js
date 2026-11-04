/**
 * Cancels the browser's own action for the console's tab shortcuts (ADR 0058, section 4):
 * Ctrl+Shift+T, Ctrl+Shift+W, Ctrl+PageUp and Ctrl+PageDown. The keys still reach the Blazor
 * handler of TerminalTabs, which opens, closes or switches tabs.
 *
 * A browser may reserve some of them for its own tabs (Chrome keeps Ctrl+Shift+W and
 * Ctrl+Shift+T unless KazoOS runs as an installed app); the "+" and "×" buttons always work.
 */
export function trapTabShortcuts(element) {
    const onKeyDown = (event) => {
        if (!event.ctrlKey || event.altKey || event.metaKey) {
            return;
        }

        const key = event.key.toLowerCase();
        const tabKey = event.shiftKey && (key === "t" || key === "w");
        const switchKey = !event.shiftKey && (event.key === "PageUp" || event.key === "PageDown");
        if (tabKey || switchKey) {
            event.preventDefault();
        }
    };

    element.addEventListener("keydown", onKeyDown);

    return {
        dispose() {
            element.removeEventListener("keydown", onKeyDown);
        }
    };
}
