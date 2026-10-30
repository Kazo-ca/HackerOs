// ADR 0202 §5: thumbnails of the wallpaper files of the library. The bytes read by the panel become
// blob addresses, released with their images when the panel closes.

const addresses = new WeakMap();

export function showThumbnail(image, content, mediaType) {
    if (!image || !content) {
        return;
    }

    release(image);
    const address = URL.createObjectURL(new Blob([content], { type: mediaType }));
    addresses.set(image, address);
    image.src = address;
}

export function releaseAll(images) {
    for (const image of images || []) {
        release(image);
    }
}

function release(image) {
    const address = image ? addresses.get(image) : undefined;
    if (address) {
        URL.revokeObjectURL(address);
        addresses.delete(image);
    }
}
