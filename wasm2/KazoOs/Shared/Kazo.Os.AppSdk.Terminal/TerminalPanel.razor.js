import { createTerminal } from "./xterm/xterm.bundle.js";

/**
 * Mounts the panel's terminal emulator (xterm.js, ADR 0061) in `host`. The returned object is
 * the only surface C# sees: write(bytes), focus(), dimensions() and dispose(). Keyboard input
 * comes back to the panel through its OnTerminalData method, the cell grid size through
 * UpdateTerminalViewport.
 */
export function mountTerminal(host, dotNetReference, options) {
    return createTerminal(host, dotNetReference, options);
}
