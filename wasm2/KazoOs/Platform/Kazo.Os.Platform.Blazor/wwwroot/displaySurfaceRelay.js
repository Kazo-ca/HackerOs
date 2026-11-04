// Loaded by displaySurfaces.js INTO each detached display surface window (ADR 0078), so this code
// belongs to the surface's realm, not the desktop's.
//
// A browser stamps every posted message with the window whose code sent it (`event.source`). The
// isolated page frames of the Browser (ADR 0075) trust only messages from their parent window,
// which for a page shown in a surface is the surface window, while the code answering the page
// lives in the desktop window. The desktop therefore posts through this function: the message then
// comes from the surface, exactly as if the page had never left its parent.

window.hackerosSurfaceRelay = Object.freeze({
    post(target, message) {
        target.postMessage(message, "*");
    },
});
