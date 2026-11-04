// Host clipboard bridge of Kazo OS (ADR 0065), the page half of HostClipboardBridge (Platform Core),
// imported by Interop/BrowserHostClipboardChannel.cs. Like the VirtualBox Guest Additions, it shares
// plain text between the Kazo OS clipboard ("guest") and the real machine's clipboard ("host"):
//
// - guest to host: syncGuest(text, true) calls navigator.clipboard.writeText while the user gesture
//   that copied (Ctrl+C, a click) is still active, so the browser asks for no permission;
// - host to guest: a `paste` event (capture phase, on the document, before any app) whose text or
//   files differ from what Kazo OS itself last wrote is reported to .NET (OnHostPaste) and becomes the
//   current entry. navigator.clipboard.readText is never called: it would prompt for permission;
// - native copies inside Kazo OS (a text field, the terminal) are reported too (OnNativeCopy).
//
// The directions come from /etc/hackeros/clipboard.config (setDirections). They are decided here,
// synchronously inside the event, because a paste or copy can only be changed while it is dispatched:
// - host to guest refused, or the host text is stale: a paste into a Kazo OS text field inserts the
//   Kazo OS clipboard text instead of the host's;
// - guest to host refused: a native copy keeps the host clipboard untouched.
// Only text is ever handed to the host; the other formats of the Kazo OS clipboard never leave .NET.

let callbacks = null;
let attached = false;
let guestToHost = true;
let hostToGuest = true;
// Text of the Kazo OS clipboard as last told by .NET or seen here, or null.
let guestText = null;
// What the host clipboard is believed to hold: the last text written there or received from there.
let lastHostText = null;
let lastHostFilesKey = null;
// Whether the Kazo OS clipboard currently stands for host files (a later paste imports them again).
let hostFilesCurrent = false;

function isEditable(target) {
    return target instanceof Element
        && (target.isContentEditable || target.closest('input, textarea, [contenteditable="true"]') !== null);
}

function selectedText() {
    const active = document.activeElement;
    if (active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement) {
        const start = active.selectionStart;
        const end = active.selectionEnd;
        return typeof start === 'number' && typeof end === 'number' ? active.value.substring(start, end) : '';
    }

    const selection = document.getSelection();
    return selection ? selection.toString() : '';
}

function filesKey(files) {
    return files.map(f => `${f.name}\u0000${f.size}\u0000${f.lastModified}`).join('\u0001');
}

function onPaste(e) {
    const data = e.clipboardData;
    if (!data) {
        return;
    }

    const text = data.getData('text/plain') || '';
    const files = Array.from(data.files || []);
    const key = files.length > 0 ? filesKey(files) : null;
    const newText = text !== '' && text !== lastHostText;
    const newFiles = key !== null && key !== lastHostFilesKey;

    if (hostToGuest && (newText || newFiles)) {
        lastHostText = text;
        lastHostFilesKey = key;
        hostFilesCurrent = key !== null;
        guestText = text !== '' ? text : null;
        if (callbacks) {
            callbacks.invokeMethodAsync('OnHostPaste', text !== '' ? text : null, files.map(f => f.name));
        }
    }

    // Tells a FileView (FileView.razor.js) whether to import the pasted files.
    e.hackerOsHostFiles = hostToGuest && key !== null && hostFilesCurrent && key === lastHostFilesKey;

    if (isEditable(e.target) && text !== (guestText ?? '')) {
        // The field would receive host text Kazo OS does not hold (sharing refused, or stale host
        // content): it receives the Kazo OS clipboard instead. Stopping the event here also keeps a
        // component's own paste handler (xterm.js) from reading the host text; insertText raises the
        // input event those components listen to.
        e.preventDefault();
        e.stopImmediatePropagation();
        if (guestText) {
            document.execCommand('insertText', false, guestText);
        }
    }
}

function onCopy(e) {
    // Runs in the bubble phase on window, after the page's own handlers: a component that wrote the
    // data itself (xterm.js) left it in clipboardData and cancelled the event.
    const text = e.defaultPrevented && e.clipboardData ? e.clipboardData.getData('text/plain') : selectedText();
    if (!text) {
        return;
    }

    if (guestToHost) {
        lastHostText = text;
        lastHostFilesKey = null;
    } else if (e.defaultPrevented) {
        e.clipboardData.clearData();
    } else {
        e.preventDefault();
        if (e.type === 'cut' && isEditable(e.target)) {
            document.execCommand('delete');
        }
    }

    guestText = text;
    hostFilesCurrent = false;
    if (callbacks) {
        callbacks.invokeMethodAsync('OnNativeCopy', text);
    }
}

// Installs the listeners once per page; a later call (a new Blazor circuit) only swaps the callbacks.
export function attach(dotNetCallbacks) {
    callbacks = dotNetCallbacks;
    if (attached) {
        return;
    }

    document.addEventListener('paste', onPaste, true);
    window.addEventListener('copy', onCopy);
    window.addEventListener('cut', onCopy);
    attached = true;
}

export function detach(dotNetCallbacks) {
    if (callbacks === dotNetCallbacks) {
        callbacks = null;
    }
}

export function setDirections(toHost, toGuest) {
    guestToHost = !!toHost;
    hostToGuest = !!toGuest;
}

// An app changed the Kazo OS clipboard (or it was emptied): remember its text and, when allowed,
// copy it to the host. Throws when the browser refuses the write (no focus, no user gesture).
export async function syncGuest(text, writeToHost) {
    guestText = text;
    hostFilesCurrent = false;
    if (writeToHost && text !== null && navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text);
        lastHostText = text;
        lastHostFilesKey = null;
    }
}
