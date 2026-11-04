export async function downloadFileFromStream(fileName, contentStreamReference) {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer]);
    const url = URL.createObjectURL(blob);
    const anchorElement = document.createElement('a');
    anchorElement.href = url;
    anchorElement.download = fileName ?? '';
    // The anchor must be attached to the DOM before .click() — a detached anchor's
    // synthetic click is not reliably treated as a download trigger by Chromium,
    // especially under headless/automated browsers (Playwright never observes it).
    document.body.appendChild(anchorElement);
    anchorElement.click();
    anchorElement.remove();
    URL.revokeObjectURL(url);
}
