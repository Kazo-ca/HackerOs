// Browser-side helpers for TextEditorWindow. Only what C# cannot reach lives here:
// textarea selection and default browser shortcut handling.

export function selectRange(textarea, start, end) {
    if (!textarea) {
        return;
    }

    textarea.focus({ preventScroll: true });
    textarea.setSelectionRange(start, end);

    const totalLength = textarea.value.length || 1;
    const scrollableHeight = textarea.scrollHeight - textarea.clientHeight;
    if (scrollableHeight > 0) {
        const ratio = start / totalLength;
        textarea.scrollTop = Math.max(0, (ratio * textarea.scrollHeight) - (textarea.clientHeight / 2));
    }
}

// Returns [selectionStart, selectionEnd] of the editor textarea, or null when it is gone.
export function getSelectionRange(textarea) {
    if (!textarea || typeof textarea.selectionStart !== "number") {
        return null;
    }

    return [textarea.selectionStart, textarea.selectionEnd];
}

// Editor shortcuts handled in C# (Save, Open, New, Find, Replace). Without this guard the
// browser would also act on them (Ctrl+H opens the history page, Ctrl+S saves the web page).
const guardedKeys = new Set(["s", "o", "n", "f", "h"]);

export function guardEditorShortcuts(container) {
    if (!container || container.__hosShortcutGuard) {
        return;
    }

    container.__hosShortcutGuard = true;
    container.addEventListener("keydown", event => {
        if (event.ctrlKey && !event.altKey && guardedKeys.has(event.key.toLowerCase())) {
            event.preventDefault();
        }
    });
}
