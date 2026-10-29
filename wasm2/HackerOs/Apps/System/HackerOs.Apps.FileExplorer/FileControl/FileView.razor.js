// Drag & drop DataTransfer read/write for FileView (FV-006, ADR 0058 section 1). Blazor's
// DragEventArgs/DataTransfer expose only read-only metadata (Types, EffectAllowed, ...): there is no
// getData/setData marshaled across the interop boundary, and the File objects of a drop from the host
// system are not reachable from C#. So the actual payload transfer happens here.
//
// setDragData relies on `window.event` still being the dragstart being dispatched (HTML5 requires
// setData synchronously within dragstart; Blazor WebAssembly runs the C# handler on the same call
// stack up to its first real await).
//
// Drops do not depend on that timing: attachDropZone installs native listeners on a FileView root,
// breadcrumb or drop zone. Its capture-phase `drop` listener runs before Blazor's delegated handler and
// stashes the HackerOS payload and the host File objects; the C# handler then reads the stash
// (getDragData / takeDroppedFiles). Host files pasted with Ctrl+V go through the same stash
// (attachPasteTarget). Its `dragover` listener sets dropEffect so the cursor shows a move,
// or a copy when Ctrl is held or the files come from the host, and highlights the folder under it.

const hostFileStore = new Map();
let nextToken = 1;
let lastDrop = null;

// An app dragged from the Start menu (ADR 0160 §6): dropped on a folder, it becomes a .desktop shortcut.
const appMimeType = 'application/vnd.hackeros.app+json';
// A desktop icon moved on its own desktop (ADR 0160 §3): the desktop places it, nothing is transferred.
const desktopItemMimeType = 'application/vnd.hackeros.desktop-item+json';

export function setDragData(mimeType, json, downloadKey, hint) {
    const event = window.event;
    if (!event || !event.dataTransfer) {
        return false;
    }

    event.dataTransfer.effectAllowed = 'copyMove';
    event.dataTransfer.setData(mimeType, json);
    addExternalDrag(event, downloadKey, hint);
    return true;
}

// Several payloads in one call, while dragstart is still being dispatched: a desktop drag carries the
// files (for other windows) and the icon keys (for its own desktop).
export function setDragDataMany(data, downloadKey, hint) {
    const event = window.event;
    if (!event || !event.dataTransfer) {
        return false;
    }

    event.dataTransfer.effectAllowed = 'copyMove';
    for (const [mimeType, json] of Object.entries(data)) {
        event.dataTransfer.setData(mimeType, json);
    }

    addExternalDrag(event, downloadKey, hint);
    return true;
}

// Drag out of the browser to download (ADR 0185 §4-5). Chrome and Edge save a drop outside the page
// from a `DownloadURL` entry ("type:name:url"), which must be written during dragstart: the content is
// prepared beforehand, when the mouse button goes down on an item (attachExternalDrag reports the press,
// FileView reads or compresses the items, prepareDownload keeps them as a blob URL). One prepared
// download at a time; the previous blob is released when the next one is prepared.
let preparedDownload = null;

export function prepareDownload(key, name, mimeType, bytes) {
    clearPreparedDownload();
    const blob = new Blob([bytes], { type: mimeType });
    preparedDownload = { key, name, mimeType, url: URL.createObjectURL(blob) };
    return true;
}

export function clearPreparedDownload() {
    if (preparedDownload) {
        URL.revokeObjectURL(preparedDownload.url);
        preparedDownload = null;
    }
}

// Adds the prepared download to a starting drag when it is for the same items, or shows the bubble
// that points to Download when they are too large.
function addExternalDrag(event, downloadKey, hint) {
    if (downloadKey && preparedDownload && preparedDownload.key === downloadKey) {
        const name = preparedDownload.name.replace(/:/g, '_');
        event.dataTransfer.setData('DownloadURL', `${preparedDownload.mimeType}:${name}:${preparedDownload.url}`);
    }

    if (hint) {
        showDragHint(event, hint);
    }
}

function showDragHint(event, text) {
    const target = event.target instanceof Element ? event.target : null;
    const root = target ? target.closest('.file-view') : null;
    if (!root) {
        return;
    }

    const bubble = root.ownerDocument.createElement('div');
    bubble.className = 'file-view-tooltip file-view-drag-hint';
    bubble.setAttribute('role', 'status');
    bubble.textContent = text;
    root.appendChild(bubble);

    const rect = root.getBoundingClientRect();
    const scaleX = root.offsetWidth ? rect.width / root.offsetWidth : 1;
    const scaleY = root.offsetHeight ? rect.height / root.offsetHeight : 1;
    const left = Math.min((event.clientX - rect.left) / scaleX + 12, root.clientWidth - bubble.offsetWidth - 4);
    const top = Math.min((event.clientY - rect.top) / scaleY + 18, root.clientHeight - bubble.offsetHeight - 4);
    bubble.style.left = `${Math.max(4, left) + root.scrollLeft}px`;
    bubble.style.top = `${Math.max(4, top) + root.scrollTop}px`;

    let removed = false;
    const remove = () => {
        if (!removed) {
            removed = true;
            bubble.remove();
            root.ownerDocument.removeEventListener('dragend', remove, true);
        }
    };
    root.ownerDocument.addEventListener('dragend', remove, true);
    setTimeout(remove, 4000);
}

// Reports a press of the main mouse button on an item, with the items selected at that moment, so
// FileView prepares what a drag would carry before the drag starts. Touch has no drag out of the page.
export function attachExternalDrag(root, callbacks) {
    if (!root || root.__hackerOsExternalDrag) {
        return false;
    }

    root.__hackerOsExternalDrag = true;
    root.addEventListener('pointerdown', e => {
        if (e.pointerType !== 'mouse' || e.button !== 0) {
            return;
        }

        const item = e.target instanceof Element ? e.target.closest('[data-item-path]') : null;
        if (!item || !root.contains(item) || item.querySelector('input')) {
            return;
        }

        const selected = [];
        root.querySelectorAll('[aria-selected="true"], .selected').forEach(element => {
            const path = element.dataset.itemPath
                ?? element.querySelector('[data-item-path]')?.dataset.itemPath;
            if (path && !selected.includes(path)) {
                selected.push(path);
            }
        });

        callbacks.invokeMethodAsync('PrepareExternalDragAsync', item.dataset.itemPath, selected)
            .catch(() => { /* The view is gone. */ });
    }, true);
    return true;
}

export function getDragData(mimeType) {
    if (lastDrop && lastDrop.data && performance.now() - lastDrop.at < 5000 && mimeType in lastDrop.data) {
        return lastDrop.data[mimeType];
    }

    const event = window.event;
    if (!event || !event.dataTransfer) {
        return null;
    }

    const data = event.dataTransfer.getData(mimeType);
    return data ? data : null;
}

// Returns { token, files: [{ name, size, type }] } for the host files of the last drop, keeping the
// File objects under `token` until releaseDroppedFiles(token).
export function takeDroppedFiles() {
    const files = lastDrop && performance.now() - lastDrop.at < 5000 ? lastDrop.files : [];
    lastDrop = null;
    if (files.length === 0) {
        return { token: 0, files: [] };
    }

    const token = nextToken++;
    hostFileStore.set(token, files);
    return { token, files: files.map(f => ({ name: f.name, size: f.size, type: f.type || '' })) };
}

// The File (a Blob) comes back to .NET as an IJSStreamReference.
export function openDroppedFile(token, index) {
    const files = hostFileStore.get(token);
    if (!files || index < 0 || index >= files.length) {
        throw new Error('The dropped file is no longer available.');
    }

    return files[index];
}

export function releaseDroppedFiles(token) {
    hostFileStore.delete(token);
}

export function attachDropZone(element, mimeType) {
    if (!element || element.__hackerOsDropZone) {
        return false;
    }

    let highlighted = null;
    const clearHighlight = () => {
        if (highlighted) {
            highlighted.classList.remove('file-view-drop-hover');
            highlighted = null;
        }
    };

    const onDragOver = e => {
        const transfer = e.dataTransfer;
        if (!transfer) {
            return;
        }

        const types = Array.from(transfer.types || []);
        if (types.includes(desktopItemMimeType)) {
            transfer.dropEffect = 'move';
        } else if (types.includes(mimeType)) {
            transfer.dropEffect = e.ctrlKey ? 'copy' : 'move';
        } else if (types.includes('Files') || types.includes(appMimeType)) {
            transfer.dropEffect = 'copy';
        } else {
            return;
        }

        const target = e.target instanceof Element ? e.target.closest('[data-drop-target]') : null;
        if (target !== highlighted) {
            clearHighlight();
            if (target && element.contains(target)) {
                target.classList.add('file-view-drop-hover');
                highlighted = target;
            }
        }
    };

    const onDrop = e => {
        clearHighlight();
        const transfer = e.dataTransfer;
        if (!transfer) {
            lastDrop = null;
            return;
        }

        const data = {};
        for (const type of [mimeType, appMimeType, desktopItemMimeType]) {
            const value = transfer.getData(type);
            data[type] = value ? value : null;
        }

        lastDrop = {
            at: performance.now(),
            data,
            files: Array.from(transfer.files || [])
        };
    };

    element.addEventListener('dragover', onDragOver);
    element.addEventListener('dragleave', e => {
        if (!element.contains(e.relatedTarget)) {
            clearHighlight();
        }
    });
    element.addEventListener('drop', onDrop, true);
    element.__hackerOsDropZone = true;
    return true;
}

// Ctrl+V on the view (ADR 0065). The browser's `paste` event, not the keydown, drives the paste: it
// fires after the host clipboard bridge (hostClipboard.js of HackerOs.Infrastructure.Browser, listening
// on the document in the capture phase) updated the OS clipboard, and it carries the host files of a
// paste from the real machine. The bridge marks the event with `hackerOsHostFiles` (whether those files
// are new host content allowed by clipboard.config); without the bridge, any pasted file counts.
// Some browsers (Safari) fire no paste on a non-editable element: a Ctrl+V keydown not followed by a
// paste within 250 ms still pastes the OS clipboard.
function isEditable(target) {
    return target instanceof Element
        && (target.isContentEditable || target.closest('input, textarea, select, [contenteditable="true"]') !== null);
}

export function attachPasteTarget(element, callbacks) {
    if (!element || element.__hackerOsPasteTarget) {
        return false;
    }

    let pending = null;
    const cancelPending = () => {
        if (pending !== null) {
            clearTimeout(pending);
            pending = null;
        }
    };

    element.addEventListener('keydown', e => {
        if (!(e.ctrlKey || e.metaKey) || e.altKey || e.key.toLowerCase() !== 'v' || isEditable(e.target)) {
            return;
        }

        cancelPending();
        pending = setTimeout(() => {
            pending = null;
            callbacks.invokeMethodAsync('OnPasteShortcutAsync', false);
        }, 250);
    });

    element.addEventListener('paste', e => {
        if (isEditable(e.target)) {
            return;
        }

        cancelPending();
        e.preventDefault();
        const files = Array.from((e.clipboardData && e.clipboardData.files) || []);
        const hostFiles = files.length > 0 && e.hackerOsHostFiles !== false;
        if (hostFiles) {
            lastDrop = { at: performance.now(), data: {}, files };
        }

        callbacks.invokeMethodAsync('OnPasteShortcutAsync', hostFiles);
    });

    element.__hackerOsPasteTarget = true;
    return true;
}

// Reveal (ADR 0096, section 1): scrolls the row/tile/node for `path` to the middle of its scroller,
// then focuses it without scrolling again, so "Show in folder" lands on a visible, selected item.
export function revealItem(containerEl, path) {
    for (const el of containerEl.querySelectorAll('[data-item-path]')) {
        if (el.getAttribute('data-item-path') === path) {
            el.scrollIntoView({ block: 'center', inline: 'nearest' });
            el.focus({ preventScroll: true });
            return true;
        }
    }

    return false;
}

// Roving-tabindex keyboard navigation (FV-011): moves real DOM focus to the row/tile/node for `path`
// after an arrow key changes the active item. Every renderer tags its row's root element with
// data-item-path, so this one lookup works across Details/Icons/Tree without knowing which mode is active.
export function focusItem(containerEl, path) {
    for (const el of containerEl.querySelectorAll('[data-item-path]')) {
        if (el.getAttribute('data-item-path') === path) {
            el.focus();
            return true;
        }
    }

    return false;
}

// Hover bubble (ADR 0185 §16): after `delay` ms of the mouse resting on an item ([data-item-path]), asks C#
// for its lines (GetItemTooltipAsync) and shows them in a bubble placed under the pointer, inside the view.
// Mouse only: a touch never shows one (on a phone the long press opens the menu). Any click, key, wheel,
// drag or leaving the item hides it.
export function attachTooltips(root, callbacks, delay) {
    if (!root || root.__hackerOsTooltips) {
        return false;
    }

    let timer = 0;
    let current = null;
    let bubble = null;
    let token = 0;
    let pointer = { x: 0, y: 0 };

    const hide = () => {
        clearTimeout(timer);
        timer = 0;
        current = null;
        token++;
        if (bubble) {
            bubble.remove();
            bubble = null;
        }
    };

    const show = (lines) => {
        bubble = root.ownerDocument.createElement('div');
        bubble.className = 'file-view-tooltip';
        bubble.setAttribute('role', 'tooltip');
        lines.forEach((line, index) => {
            const row = root.ownerDocument.createElement('div');
            row.className = index === 0 ? 'file-view-tooltip-title' : 'file-view-tooltip-line';
            row.textContent = line;
            bubble.appendChild(row);
        });
        root.appendChild(bubble);

        // Placed in the view's own coordinates (it scrolls, and windows may be transformed), kept inside it.
        const rect = root.getBoundingClientRect();
        const scaleX = root.offsetWidth ? rect.width / root.offsetWidth : 1;
        const scaleY = root.offsetHeight ? rect.height / root.offsetHeight : 1;
        let left = (pointer.x - rect.left) / scaleX + 12;
        let top = (pointer.y - rect.top) / scaleY + 18;
        const maxLeft = root.clientWidth - bubble.offsetWidth - 4;
        const maxTop = root.clientHeight - bubble.offsetHeight - 4;
        if (top > maxTop) {
            top = Math.max(4, (pointer.y - rect.top) / scaleY - bubble.offsetHeight - 8);
        }
        bubble.style.left = `${Math.max(4, Math.min(left, maxLeft)) + root.scrollLeft}px`;
        bubble.style.top = `${Math.max(4, Math.min(top, Math.max(4, maxTop))) + root.scrollTop}px`;
    };

    const onPointerOver = e => {
        if (e.pointerType !== 'mouse') {
            return;
        }

        const item = e.target instanceof Element ? e.target.closest('[data-item-path]') : null;
        if (item === current) {
            return;
        }

        hide();
        if (!item || !root.contains(item) || item.querySelector('input')) {
            return;
        }

        current = item;
        const mine = token;
        pointer = { x: e.clientX, y: e.clientY };
        timer = setTimeout(async () => {
            let lines = null;
            try {
                lines = await callbacks.invokeMethodAsync('GetItemTooltipAsync', item.getAttribute('data-item-path'));
            } catch {
                return;
            }
            if (mine === token && current === item && Array.isArray(lines) && lines.length > 0 && item.isConnected) {
                show(lines);
            }
        }, delay);
    };

    root.addEventListener('pointerover', onPointerOver);
    root.addEventListener('pointermove', e => {
        if (!bubble) {
            pointer = { x: e.clientX, y: e.clientY };
        }
    });
    root.addEventListener('pointerleave', hide);
    root.addEventListener('pointerdown', hide, true);
    root.addEventListener('keydown', hide, true);
    root.addEventListener('wheel', hide, { passive: true });
    root.addEventListener('dragstart', hide, true);
    root.addEventListener('contextmenu', hide, true);
    root.__hackerOsTooltips = true;
    return true;
}

// ADR 0185 §14: tells .NET which folder tiles of the Icons mode came on screen, so their content is
// read for the preview only when it can be seen. Tiles mark themselves with data-folder-preview; new
// tiles (a navigation, a refresh, a mode change) are found by a mutation observer.
export function attachFolderPreviews(root, callbacks) {
    if (!root || root.__hackerOsFolderPreviews || typeof IntersectionObserver === 'undefined') {
        return false;
    }

    const pending = new Set();
    let timer = 0;
    const flush = () => {
        timer = 0;
        const paths = [...pending];
        pending.clear();
        if (paths.length > 0) {
            callbacks.invokeMethodAsync('LoadFolderPreviewsAsync', paths).catch(() => { });
        }
    };

    const visible = new IntersectionObserver((entries) => {
        for (const entry of entries) {
            const path = entry.target.getAttribute('data-folder-preview');
            if (entry.isIntersecting && path) {
                pending.add(path);
            }
        }

        if (pending.size > 0 && !timer) {
            timer = setTimeout(flush, 60);
        }
    });

    const observed = new WeakSet();
    const scan = () => {
        for (const element of root.querySelectorAll('[data-folder-preview]')) {
            if (!observed.has(element)) {
                observed.add(element);
                visible.observe(element);
            }
        }
    };

    // A tile kept by Blazor whose attribute changed (another folder) is observed again.
    const changes = new MutationObserver((records) => {
        for (const record of records) {
            if (record.type === 'attributes' && record.target.hasAttribute('data-folder-preview')) {
                visible.unobserve(record.target);
                visible.observe(record.target);
            }
        }

        scan();
    });
    changes.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-folder-preview'] });
    scan();
    root.__hackerOsFolderPreviews = true;
    return true;
}
