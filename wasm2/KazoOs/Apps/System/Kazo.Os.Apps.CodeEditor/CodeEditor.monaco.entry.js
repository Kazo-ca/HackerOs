// Import only the languages CodeEditorSyntaxMode actually offers instead of
// pulling in monaco-editor's full editor.main.js (which registers 80+
// languages and roughly doubles the bundle size for no benefit here). Each
// import's transitive CSS (codicons, widgets, ...) is bundled automatically
// by esbuild into the sibling monaco.bundle.css output.
import * as monaco from "monaco-editor/esm/vs/editor/edcore.main.js";
import "monaco-editor/esm/vs/language/css/monaco.contribution.js";
import "monaco-editor/esm/vs/language/html/monaco.contribution.js";
import "monaco-editor/esm/vs/language/json/monaco.contribution.js";
import "monaco-editor/esm/vs/language/typescript/monaco.contribution.js";
import "monaco-editor/esm/vs/basic-languages/markdown/markdown.contribution.js";
import "monaco-editor/esm/vs/basic-languages/csharp/csharp.contribution.js";

// Loaded once per module URL (the browser caches dynamic import()s), so this
// stylesheet injection and worker wiring only ever run a single time.
const stylesheetHref = new URL("./monaco.bundle.css", import.meta.url).href;
if (!document.querySelector(`link[href="${stylesheetHref}"]`)) {
  const link = document.createElement("link");
  link.rel = "stylesheet";
  link.href = stylesheetHref;
  document.head.appendChild(link);
}

// Monaco cancels in-flight language-service work (worker RPC requests, validation, ...) when a
// model or editor disposes mid-request. Its own errors.js explicitly excludes this exact error
// shape from onUnexpectedError (see isCancellationError there: name === "Canceled" && message
// === "Canceled"), but some internal call sites reject a bare promise with it instead of routing
// through that filter, so it can still reach the page as an unhandled rejection. Suppress only
// that precise, well-known shape; anything else still propagates normally.
window.addEventListener("unhandledrejection", (event) => {
  const reason = event.reason;
  if (reason instanceof Error && reason.name === "Canceled" && reason.message === "Canceled") {
    event.preventDefault();
  }
});

const WORKER_FILE_BY_LABEL = {
  json: "json.worker.js",
  css: "css.worker.js",
  scss: "css.worker.js",
  less: "css.worker.js",
  html: "html.worker.js",
  handlebars: "html.worker.js",
  razor: "html.worker.js",
  typescript: "ts.worker.js",
  javascript: "ts.worker.js"
};

self.MonacoEnvironment = {
  getWorkerUrl(_moduleId, label) {
    const workerFile = WORKER_FILE_BY_LABEL[label] ?? "editor.worker.js";
    return new URL(workerFile, import.meta.url).href;
  }
};

const LANGUAGE_BY_MODE = {
  cSharp: "csharp",
  javaScript: "javascript",
  typeScript: "typescript",
  html: "html",
  css: "css",
  json: "json",
  markdown: "markdown",
  plainText: "plaintext"
};

function languageFor(mode) {
  return LANGUAGE_BY_MODE[mode] ?? "plaintext";
}

// Kazo OS's own dark theme: the window's background and accent instead of vs-dark's grey, so
// the editor reads as part of the desktop. Token colours stay close to VS Code's Dark+.
monaco.editor.defineTheme("kazoos-dark", {
  base: "vs-dark",
  inherit: true,
  rules: [
    { token: "comment", foreground: "6b7b86", fontStyle: "italic" },
    { token: "keyword", foreground: "57c7ff" },
    { token: "string", foreground: "f3f99d" },
    { token: "number", foreground: "ff9f6b" },
    { token: "type", foreground: "42d392" },
    { token: "delimiter", foreground: "a8bac4" }
  ],
  colors: {
    "editor.background": "#0b1012",
    "editor.foreground": "#e6edf1",
    "editorLineNumber.foreground": "#3d4b52",
    "editorLineNumber.activeForeground": "#42d392",
    "editorCursor.foreground": "#42d392",
    "editor.lineHighlightBackground": "#12201c",
    "editor.lineHighlightBorder": "#00000000",
    "editor.selectionBackground": "#42d39240",
    "editor.inactiveSelectionBackground": "#42d39222",
    "editor.findMatchBackground": "#d5a84f66",
    "editor.findMatchHighlightBackground": "#d5a84f33",
    "editorIndentGuide.background1": "#1c2a2e",
    "editorIndentGuide.activeBackground1": "#2f4a43",
    "editorWidget.background": "#111a1d",
    "editorWidget.border": "#1f3a32",
    "input.background": "#0b1012",
    "focusBorder": "#42d39280",
    "minimap.background": "#0b1012",
    "scrollbarSlider.background": "#42d39226",
    "scrollbarSlider.hoverBackground": "#42d39244",
    "scrollbarSlider.activeBackground": "#42d39266"
  }
});

// ADR 0105: Kazo OS's high-contrast editor theme, over Monaco's own hc-black (black background,
// white text, yellow focus and cursor), matching the shell's high-contrast layer.
monaco.editor.defineTheme("kazoos-high-contrast", {
  base: "hc-black",
  inherit: true,
  rules: [
    { token: "comment", foreground: "d6d6d6", fontStyle: "italic" },
    { token: "keyword", foreground: "66ccff" },
    { token: "string", foreground: "ffff80" },
    { token: "number", foreground: "ffb380" },
    { token: "type", foreground: "00ff9c" }
  ],
  colors: {
    "editor.background": "#000000",
    "editor.foreground": "#ffffff",
    "editorCursor.foreground": "#ffff00",
    "editorLineNumber.foreground": "#d6d6d6",
    "editorLineNumber.activeForeground": "#ffff00",
    "focusBorder": "#ffff00",
    "contrastBorder": "#ffffff",
    "contrastActiveBorder": "#ffff00"
  }
});

/** The editor settings that depend on the session's accessibility preferences (ADR 0105). */
function accessibilityOptions(options) {
  const scale = options?.textScale > 0 ? options.textScale : 1;
  const reduced = !!options?.reducedMotion;
  return {
    theme: options?.highContrast ? "kazoos-high-contrast" : "kazoos-dark",
    fontSize: Math.round((options?.fontSize ?? 14) * scale),
    lineHeight: Math.round(21 * scale),
    smoothScrolling: !reduced,
    cursorBlinking: reduced ? "solid" : "smooth",
    cursorSmoothCaretAnimation: reduced ? "off" : "on"
  };
}

/**
 * Creates the editor in `host`. Each open document (tab) gets its own Monaco model, so undo
 * history, cursor and scroll position stay with the tab, as in VS Code. C# drives it with
 * showDocument / closeDocument / flush / markSaved and receives OnDirtyChanged(documentId,
 * dirty), OnEditorChanged(documentId, content) after a typing pause, and
 * OnCursorChanged(line, column, selectionLength).
 */
export function createCodeEditor(host, dotNet, options) {
  const models = new Map();
  let activeId = null;
  let suppressChange = false;
  let disposed = false;

  const accessibility = accessibilityOptions(options);
  const editor = monaco.editor.create(host, {
    model: null,
    theme: accessibility.theme,
    automaticLayout: true,
    minimap: { enabled: true, renderCharacters: false },
    fontFamily: options?.fontFamily || "'Cascadia Mono', 'Fira Code', Consolas, monospace",
    fontSize: accessibility.fontSize,
    lineHeight: accessibility.lineHeight,
    tabSize: 2,
    wordWrap: "off",
    scrollBeyondLastLine: true,
    smoothScrolling: accessibility.smoothScrolling,
    cursorBlinking: accessibility.cursorBlinking,
    cursorSmoothCaretAnimation: accessibility.cursorSmoothCaretAnimation,
    renderLineHighlight: "all",
    bracketPairColorization: { enabled: true },
    guides: { indentation: true, bracketPairs: true },
    padding: { top: 8 },
    fixedOverflowWidgets: true
  });

  // Typing must stay inside the browser: a keystroke only updates Monaco. C# is told at once
  // when a tab's "modified" state flips (OnDirtyChanged, compared against the model's version at
  // the last load or save), and receives the text itself once typing pauses (OnEditorChanged,
  // debounced) or when it asks for it before saving or searching (flush).
  const CONTENT_DELAY_MS = 400;
  const CURSOR_DELAY_MS = 60;
  let contentTimer = 0;
  let cursorTimer = 0;
  const pendingContent = new Set();

  function sendContent(id) {
    const entry = models.get(id);
    pendingContent.delete(id);
    if (!entry || disposed) return Promise.resolve();
    return dotNet.invokeMethodAsync("OnEditorChanged", id, entry.model.getValue()).catch(() => {});
  }

  function flushContent() {
    window.clearTimeout(contentTimer);
    contentTimer = 0;
    return Promise.all([...pendingContent].map(sendContent));
  }

  const changeSubscription = editor.onDidChangeModelContent(() => {
    const model = editor.getModel();
    if (suppressChange || disposed || !model || !activeId) return;
    const id = activeId;
    const entry = models.get(id);
    const dirty = model.getAlternativeVersionId() !== entry.savedVersion;
    if (dirty !== entry.dirty) {
      entry.dirty = dirty;
      void dotNet.invokeMethodAsync("OnDirtyChanged", id, dirty).catch(() => {});
    }

    pendingContent.add(id);
    window.clearTimeout(contentTimer);
    contentTimer = window.setTimeout(() => void flushContent(), CONTENT_DELAY_MS);
  });

  const cursorSubscription = editor.onDidChangeCursorSelection((event) => {
    if (disposed) return;
    window.clearTimeout(cursorTimer);
    cursorTimer = window.setTimeout(() => {
      const model = editor.getModel();
      const selection = editor.getSelection() ?? event.selection;
      const selected = model ? model.getValueLengthInRange(selection) : 0;
      void dotNet
        .invokeMethodAsync("OnCursorChanged", selection.positionLineNumber, selection.positionColumn, selected)
        .catch(() => {});
    }, CURSOR_DELAY_MS);
  });

  function replaceContent(model, value) {
    suppressChange = true;
    try {
      model.pushEditOperations([], [{ range: model.getFullModelRange(), text: value }], () => null);
    } finally {
      suppressChange = false;
    }
  }

  return {
    /** Shows a tab's document, creating its model the first time. */
    showDocument(id, content, mode, dirty) {
      if (disposed) return;
      const value = content ?? "";
      let entry = models.get(id);
      if (!entry) {
        const model = monaco.editor.createModel(value, languageFor(mode));
        // A tab restored with unsaved changes has no clean version in this model.
        entry = { model, viewState: null, dirty: !!dirty, savedVersion: dirty ? -1 : model.getAlternativeVersionId() };
        models.set(id, entry);
      } else {
        // Only text C# changed itself (reload, rejected edit); never text still waiting in pendingContent.
        if (!pendingContent.has(id) && entry.model.getValue() !== value) replaceContent(entry.model, value);
        monaco.editor.setModelLanguage(entry.model, languageFor(mode));
      }

      if (activeId === id && editor.getModel() === entry.model) return;
      const current = activeId ? models.get(activeId) : null;
      if (current) current.viewState = editor.saveViewState();
      activeId = id;
      editor.setModel(entry.model);
      if (entry.viewState) editor.restoreViewState(entry.viewState);
    },
    /** Sends every tab's text still waiting for the typing pause; awaited before save, search, close. */
    flush() {
      return disposed ? Promise.resolve() : flushContent();
    },
    /** Records the current text of a tab as saved: its "modified" state is measured from here. */
    markSaved(id, savedContent) {
      const entry = models.get(id);
      if (!entry || disposed) return;
      // Typed while the save ran: those characters are not on disk, the tab stays modified.
      if (entry.model.getValue() !== savedContent) {
        entry.savedVersion = -1;
        return;
      }

      entry.savedVersion = entry.model.getAlternativeVersionId();
      entry.dirty = false;
    },
    /** Forgets a closed tab's model. */
    closeDocument(id) {
      const entry = models.get(id);
      pendingContent.delete(id);
      if (!entry) return;
      if (activeId === id) {
        activeId = null;
        editor.setModel(null);
      }
      entry.model.dispose();
      models.delete(id);
    },
    setLanguage(nextMode) {
      const model = editor.getModel();
      if (!disposed && model) monaco.editor.setModelLanguage(model, languageFor(nextMode));
    },
    /** Selects `length` characters at line/column (1-based) and scrolls them to the centre. */
    reveal(line, column, length) {
      if (disposed || !editor.getModel()) return;
      const range = new monaco.Range(line, column, line, column + Math.max(0, length));
      editor.setSelection(range);
      editor.revealRangeInCenter(range, monaco.editor.ScrollType.Smooth);
      editor.focus();
    },
    /** Runs a built-in editor action (e.g. "actions.find", "editor.action.formatDocument"). */
    runAction(actionId) {
      if (disposed) return;
      editor.focus();
      const action = editor.getAction(actionId);
      if (action) void action.run();
    },
    /** The selected text, used to seed the Search view (empty when nothing is selected). */
    getSelectedText() {
      const model = editor.getModel();
      const selection = editor.getSelection();
      return !disposed && model && selection ? model.getValueInRange(selection) : "";
    },
    /** Changes view options (minimap, wordWrap) from the View menu. */
    setOptions(next) {
      if (disposed || !next) return;
      const options = {};
      if (typeof next.minimap === "boolean") options.minimap = { enabled: next.minimap, renderCharacters: false };
      if (typeof next.wordWrap === "boolean") options.wordWrap = next.wordWrap ? "on" : "off";
      editor.updateOptions(options);
    },
    /** Applies new accessibility preferences (ADR 0105): theme, font size, cursor and scrolling. */
    setAccessibility(preferences) {
      if (disposed) return;
      const next = accessibilityOptions({ ...preferences, fontSize: options?.fontSize });
      monaco.editor.setTheme(next.theme);
      const { theme, ...rest } = next;
      editor.updateOptions(rest);
    },
    layout() {
      if (!disposed) editor.layout();
    },
    focus() {
      if (!disposed) editor.focus();
    },
    destroy() {
      if (disposed) return;
      disposed = true;
      window.clearTimeout(contentTimer);
      window.clearTimeout(cursorTimer);
      changeSubscription.dispose();
      cursorSubscription.dispose();
      editor.dispose();
      for (const entry of models.values()) entry.model.dispose();
      models.clear();
    }
  };
}
