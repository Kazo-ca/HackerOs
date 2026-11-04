export function selectText(element) {
    if (element && typeof element.select === 'function') {
        element.select();
    }
}
