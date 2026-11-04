// Host file picker: the opening counterpart of download.js (ADR 0099, section 1). The browser only
// lets a page read a file the user chose in its own dialog, so this is the one way into the host's
// files. Opening the dialog needs a recent user gesture (a click, or Enter in a terminal).

// Opens the dialog and resolves with the chosen File, or null when the user closed it ("cancel"
// event, Chromium 113+ and Firefox 91+).
// Throws "pickFile: user activation required" when the page has no recent user gesture: Chromium
// would silently ignore the click and the promise would never settle.
export function pickFile(accept) {
    if (navigator.userActivation && !navigator.userActivation.isActive) {
        throw new Error('pickFile: user activation required');
    }

    return new Promise((resolve) => {
        const input = document.createElement('input');
        input.type = 'file';
        if (accept) {
            input.accept = accept;
        }

        input.hidden = true;
        let settled = false;
        const finish = (file) => {
            if (!settled) {
                settled = true;
                input.remove();
                resolve(file ?? null);
            }
        };

        input.addEventListener('change', () => finish(input.files && input.files.length > 0 ? input.files[0] : null));
        input.addEventListener('cancel', () => finish(null));
        // Like download.js: an attached element is what Chromium reliably treats as user-driven.
        document.body.appendChild(input);
        input.click();
    });
}

// Name of a picked file, as the host shows it.
export function fileName(file) {
    return file.name;
}

// Whole content of a picked file; a Uint8Array arrives in .NET as byte[].
export async function readFile(file) {
    return new Uint8Array(await file.arrayBuffer());
}
