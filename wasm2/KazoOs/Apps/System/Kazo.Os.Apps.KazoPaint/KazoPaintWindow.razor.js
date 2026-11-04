export function renderRgba(canvas, width, height, pixels) {
    if (!canvas) return;
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext("2d", { alpha: true });
    context.putImageData(new ImageData(new Uint8ClampedArray(pixels), width, height), 0, 0);
}

export function capturePointer(canvas, pointerId) { canvas?.setPointerCapture(pointerId); }
export function releasePointer(canvas, pointerId) { if (canvas?.hasPointerCapture(pointerId)) canvas.releasePointerCapture(pointerId); }
export function setViewport(canvas, x, y, scale) { if (canvas) canvas.style.transform = `translate(${x}px, ${y}px) scale(${scale})`; }

// ── Image files (ADR 0103 §1) ──────────────────────────────────────────────────────────────
// The C# side reads and writes the files through the OS; this module only turns file bytes into
// RGBA pixels and back, with the browser's own codecs. It never touches the filesystem.

/**
 * Decodes an encoded image (PNG, JPEG, BMP, GIF first frame, WebP) into RGBA pixels.
 * @param {Uint8Array} bytes File content, already checked for size by C#.
 * @param {string} mediaType Media type detected from the header.
 * @param {number} maxDimension Largest width or height accepted.
 * @returns {Promise<{width:number,height:number,pixels:Uint8Array}|{width?:number,height?:number,error:string}>}
 *   The error is a code the C# side turns into a sentence in the app's language:
 *   "decode-failed", or "too-large" with the image's own width and height.
 */
export async function decodeImage(bytes, mediaType, maxDimension) {
    let bitmap;
    try {
        bitmap = await createImageBitmap(new Blob([bytes], { type: mediaType }));
    } catch {
        return { error: "decode-failed" };
    }

    try {
        if (bitmap.width > maxDimension || bitmap.height > maxDimension) {
            return { width: bitmap.width, height: bitmap.height, error: "too-large" };
        }

        const canvas = document.createElement("canvas");
        canvas.width = bitmap.width;
        canvas.height = bitmap.height;
        const context = canvas.getContext("2d", { willReadFrequently: true });
        context.drawImage(bitmap, 0, 0);
        const data = context.getImageData(0, 0, bitmap.width, bitmap.height).data;
        return { width: bitmap.width, height: bitmap.height, pixels: new Uint8Array(data.buffer, data.byteOffset, data.byteLength) };
    } finally {
        bitmap.close();
    }
}

/**
 * Encodes RGBA pixels with the canvas encoder.
 * @param {number} width Image width.
 * @param {number} height Image height.
 * @param {Uint8Array} pixels RGBA pixels.
 * @param {string} mediaType "image/png" or "image/jpeg".
 * @param {number} quality JPEG quality between 0 and 1.
 * @returns {Promise<Uint8Array>} Encoded file content.
 */
export async function encodeImage(width, height, pixels, mediaType, quality) {
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext("2d");
    if (mediaType === "image/jpeg") {
        // JPEG has no alpha channel: flatten over white, as image editors do, instead of the
        // encoder's default black.
        const layer = document.createElement("canvas");
        layer.width = width;
        layer.height = height;
        layer.getContext("2d").putImageData(new ImageData(new Uint8ClampedArray(pixels), width, height), 0, 0);
        context.fillStyle = "#ffffff";
        context.fillRect(0, 0, width, height);
        context.drawImage(layer, 0, 0);
    } else {
        context.putImageData(new ImageData(new Uint8ClampedArray(pixels), width, height), 0, 0);
    }

    const blob = await new Promise((resolve, reject) =>
        canvas.toBlob(result => result ? resolve(result) : reject(new Error("encoding failed")), mediaType, quality));
    return new Uint8Array(await blob.arrayBuffer());
}

/**
 * Keeps the browser from taking Hack Paint's shortcuts (Ctrl+S "save page", Ctrl+O, Ctrl+N new
 * window) while the focus is inside the window. The C# keydown handler runs the command.
 * @param {HTMLElement} root The app's root element.
 */
export function guardShortcuts(root) {
    if (!root || root.dataset.shortcutsGuarded) return;
    root.dataset.shortcutsGuarded = "true";
    root.addEventListener("keydown", event => {
        if ((event.ctrlKey || event.metaKey) && ["s", "o", "n", "z", "y"].includes(event.key.toLowerCase())) {
            event.preventDefault();
        }
    });
}
