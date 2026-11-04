import { createCodeEditor } from "./monaco/monaco.bundle.js";

// The collocated boundary keeps Monaco private to this component and exposes only document,
// reveal, focus, and deterministic disposal operations to C#.
// options.ariaLabel is the accessible name in the process's language, chosen by C# (ADR 0104).
export function createEditor(host, dotNet, options) {
  const editor = createCodeEditor(host, dotNet, options);
  // Monaco's hidden input surface already carries role="textbox" but a generic accessible name
  // ("Editor content") — relabel it directly instead of the wrapping host div, which axe flags
  // as aria-prohibited-attr since a plain div has no role for aria-label to attach to. Browsers
  // with the EditContext API get a `div.native-edit-context`; others fall back to the classic
  // `textarea.inputarea`.
  const relabel = () => {
    const inputArea = host.querySelector(".native-edit-context, textarea.inputarea");
    if (inputArea) {
      inputArea.setAttribute("aria-label", options?.ariaLabel || "Source code editor");
    }
  };
  relabel();
  const observer = new MutationObserver(relabel);
  observer.observe(host, { childList: true, subtree: true });
  const destroy = editor.destroy;
  editor.destroy = () => {
    observer.disconnect();
    destroy();
  };
  return editor;
}

// Workbench shortcuts, VS Code's (Ctrl+key, or Alt+Z for word wrap). Handled in the capture phase so they win over the browser
// (Ctrl+S "save page", Ctrl+P "print", Ctrl+O "open file") and over Monaco, then forwarded to
// C# by name. Chrome keeps Ctrl+N, Ctrl+W and Ctrl+T for itself unless KazoOS runs as an
// installed app, so each command also has a button or menu entry.
const SHORTCUTS = [
  { ctrl: true, shift: false, key: "s", name: "save" },
  { ctrl: true, shift: true, key: "s", name: "saveAs" },
  { ctrl: true, shift: false, key: "o", name: "openFile" },
  { ctrl: true, shift: false, key: "n", name: "newFile" },
  { ctrl: true, shift: false, key: "w", name: "closeTab" },
  { ctrl: true, shift: false, key: "p", name: "quickOpen" },
  { ctrl: true, shift: true, key: "f", name: "search" },
  { ctrl: true, shift: true, key: "e", name: "explorer" },
  { ctrl: true, shift: false, key: "b", name: "toggleSidebar" },
  { ctrl: true, shift: false, key: "`", name: "toggleTerminal" },
  { ctrl: true, shift: false, key: "j", name: "togglePanel" },
  { ctrl: true, shift: false, key: "tab", name: "nextTab" },
  { ctrl: true, shift: true, key: "tab", name: "previousTab" },
  { alt: true, shift: false, key: "z", name: "wordWrap" }
];

export function attachWorkbench(root, dotNet) {
  const onKeyDown = (event) => {
    if (event.metaKey || event.ctrlKey === event.altKey) {
      return;
    }

    const key = event.key.toLowerCase();
    // The integrated terminal owns its keys (Ctrl+W deletes a word, Ctrl+P is history), except
    // the panel toggles.
    const inTerminal = event.target instanceof Element && event.target.closest(".dock-terminal");
    const shortcut = SHORTCUTS.find((item) =>
      item.key === key && item.shift === event.shiftKey && !!item.ctrl === event.ctrlKey && !!item.alt === event.altKey);
    if (!shortcut || (inTerminal && shortcut.name !== "toggleTerminal" && shortcut.name !== "togglePanel")) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    dotNet.invokeMethodAsync("OnShortcut", shortcut.name).catch(() => {});
  };

  root.addEventListener("keydown", onKeyDown, true);
  const detachSplitters = [
    attachSplitter(root, ".sidebar-splitter", "--sidebar-width", "x", 160, 640),
    attachSplitter(root, ".panel-splitter", "--panel-height", "y", 80, 2000)
  ];

  return {
    dispose() {
      root.removeEventListener("keydown", onKeyDown, true);
      for (const detach of detachSplitters) detach();
    }
  };
}

/**
 * Lets the user drag a splitter (the side bar's right edge, the terminal panel's top edge) to
 * resize it; the size is a CSS custom property on the workbench root, so the layout stays in
 * the scoped stylesheet. Delegated: the splitters may come and go with Blazor renders.
 */
function attachSplitter(root, selector, property, axis, min, max) {
  let drag = null;

  const onPointerDown = (event) => {
    const handle = event.target instanceof Element ? event.target.closest(selector) : null;
    if (!handle || event.button !== 0) return;
    const target = handle.parentElement?.querySelector(handle.dataset.resizes);
    if (!target) return;
    const rect = target.getBoundingClientRect();
    drag = {
      pointerId: event.pointerId,
      start: axis === "x" ? event.clientX : event.clientY,
      size: axis === "x" ? rect.width : rect.height
    };
    handle.setPointerCapture(event.pointerId);
    root.classList.add("is-resizing");
    event.preventDefault();
  };

  const onPointerMove = (event) => {
    if (!drag || event.pointerId !== drag.pointerId) return;
    const position = axis === "x" ? event.clientX : event.clientY;
    // The panel grows upwards: dragging its top edge up makes it taller.
    const delta = axis === "x" ? position - drag.start : drag.start - position;
    const limit = axis === "y" ? Math.min(max, root.clientHeight - 160) : max;
    const size = Math.round(Math.min(Math.max(drag.size + delta, min), Math.max(min, limit)));
    root.style.setProperty(property, `${size}px`);
  };

  const onPointerUp = (event) => {
    if (!drag || event.pointerId !== drag.pointerId) return;
    drag = null;
    root.classList.remove("is-resizing");
  };

  root.addEventListener("pointerdown", onPointerDown);
  root.addEventListener("pointermove", onPointerMove);
  root.addEventListener("pointerup", onPointerUp);
  root.addEventListener("pointercancel", onPointerUp);
  return () => {
    root.removeEventListener("pointerdown", onPointerDown);
    root.removeEventListener("pointermove", onPointerMove);
    root.removeEventListener("pointerup", onPointerUp);
    root.removeEventListener("pointercancel", onPointerUp);
  };
}
