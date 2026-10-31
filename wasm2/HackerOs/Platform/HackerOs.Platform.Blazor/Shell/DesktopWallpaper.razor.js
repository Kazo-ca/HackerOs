// ADR 0202 §4: draws a wallpaper on its element. A file's bytes become a blob address, released
// when the element shows another image or goes away; a built-in image is a static file address.
// The address is made absolute from this page, so a secondary screen (another document) loads it too.

const blobAddresses = new WeakMap();

export function show(element, content, mediaType, assetPath) {
    if (!element) {
        return;
    }

    release(element);
    let address;
    if (content) {
        address = URL.createObjectURL(new Blob([content], { type: mediaType }));
        blobAddresses.set(element, address);
    } else {
        address = new URL(assetPath, document.baseURI).href;
    }

    element.style.backgroundImage = `url("${address}")`;
}

export function release(element) {
    const address = element ? blobAddresses.get(element) : undefined;
    if (address) {
        URL.revokeObjectURL(address);
        blobAddresses.delete(element);
    }
}
