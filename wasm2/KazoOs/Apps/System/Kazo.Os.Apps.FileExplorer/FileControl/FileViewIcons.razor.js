// Marquee (rubber-band) multi-select support for FileViewIcons.razor. C# has no access to
// getBoundingClientRect, so hit-testing which tiles a dragged rectangle intersects happens here.

export function getContainerRect(containerEl) {
    const rect = containerEl.getBoundingClientRect();
    return { left: rect.left, top: rect.top };
}

export function getIntersectingPaths(containerEl, left, top, right, bottom) {
    const containerRect = containerEl.getBoundingClientRect();
    const tiles = containerEl.querySelectorAll('[data-item-path]');
    const results = [];
    for (const tile of tiles) {
        const rect = tile.getBoundingClientRect();
        const tileLeft = rect.left - containerRect.left;
        const tileTop = rect.top - containerRect.top;
        const tileRight = tileLeft + rect.width;
        const tileBottom = tileTop + rect.height;
        if (tileLeft < right && tileRight > left && tileTop < bottom && tileBottom > top) {
            results.push(tile.getAttribute('data-item-path'));
        }
    }

    return results;
}
