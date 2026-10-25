# ADR 0020: Code Editor Architecture, Framework Selection, and Script Execution Sandbox Policy

## Status
**ACCEPTED** (DECISION: D-014)

## Context
Wave 3 introduces code editing, typed clipboard handling, drag-and-drop payloads, and terminal editing (`nano`) into HackerOS WebAssembly.
We must evaluate editor framework options (Monaco vs CodeMirror) based on payload size, accessibility, offline PWA cache footprint, language modes, worker cleanup, and licensing. Furthermore, we must strictly isolate code editing from code execution.

## Decision Drivers
1. **Payload & Performance:** WASM bundle footprint, memory consumption, and startup latency.
2. **Accessibility & Keyboard Trapping:** Compliance with WCAG 2.2 AA and keyboard navigation.
3. **Execution Isolation:** Code editing DOES NOT imply script execution permissions. User-script execution requires explicit capability grants (`user-scripts.execute`).
4. **Collocated Interop:** Scoped Blazor component interop without global `window` contamination.

## Framework Comparison Matrix

| Metric | Monaco Editor | CodeMirror 6 | Choice & Rationale |
|---|---|---|---|
| **Bundle Size** | ~3.2 MB JS + Worker DLLs | ~350 KB modular JS | **CodeMirror** for PWA footprint |
| **Offline Cache** | Complex worker script mapping | Single collocated JS module | **CodeMirror** for PWA simplicity |
| **Language Support** | Full language services | Modular syntax parsers | **Both Supported** via Abstraction Layer |
| **Keyboard Accessibility** | Complex DOM trapping | Standard ARIA contenteditable | **CodeMirror / Custom Textarea** |

## Decision (DECISION: D-014)
1. **Editor Architecture:** We adopt a modular C# Code Editor component (`org.hackeros.code-editor`) with collocated syntax highlighting, file tab management, and VFS integration.
2. **Script Sandbox Policy:**
   - Editing a file or script in Code Editor or Nano is a read/write operation (`vfs.read`, `vfs.write`).
   - Executing a user script or code snippet is strictly controlled by the `user-scripts.execute` capability.
   - Script execution runs within an isolated web-worker sandbox without access to host DOM or sensitive OS APIs.
3. **Typed Clipboard Gateway:**
   - Platform Core exposes `IAppClipboardGateway` to safely handle typed text and virtual file references without raw browser DOM leakage.
4. **Typed Drag & Drop Payloads:**
   - Internal drag-and-drop operations utilize strongly typed records (`VirtualFileDragPayload`, `VirtualFolderDragPayload`).

## Consequences
- The checked-in minified CodeMirror bundle is 585,094 bytes. It remains absent
  from the initial PWA download only after the separately reopened build-known
  lazy-loading design is integrated and verified; no smaller initial-payload
  claim is made before that evidence exists.
- Security is preserved: malicious user scripts edited in the OS cannot compromise host state.
- Automated unit tests verify editor manifests, VFS open/save contracts, and permission bounds.

## Implementation evidence (2026-08-03)

The editor uses exact packages pinned by
`Apps/System/HackerOs.Apps.CodeEditor/package-lock.json` and a checked-in local
bundle produced by esbuild. `CodeEditorDocument`, `CodeEditorSession`, and
`CodeEditorFileService` keep documents, tabs, permissions, VFS persistence, and
recovery data in C#. The collocated JavaScript boundary owns only CodeMirror DOM
creation, language reconfiguration, focus, content callbacks, and disposal.
See `docs/code-editor.md` for passed evidence and remaining host integration.

## Amendment (2026-08-21): CodeMirror replaced by Monaco

The Framework Comparison Matrix above chose CodeMirror for its smaller
payload; that choice is now superseded. The Code Editor was reworked to use
Monaco Editor instead, matching the legacy JavaScript version's editor,
alongside two additive features: an "Open Folder" tree view (reusing the
shared `HackerOs.AppSdk.FileView` control) and an embedded terminal panel
(the Terminal app's session/shell/rendering logic was extracted into a new
shared `HackerOs.AppSdk.Terminal` library — `TerminalPanel` — so both the
standalone Terminal app and the Code Editor's embedded terminal use the same
component).

The build pipeline, collocated-JS-boundary, and C#-owns-everything-else
decisions above still hold verbatim — only the editor library and its bundle
size changed. `docs/code-editor.md` describes the current Monaco-based
architecture, build pipeline, and evidence; do not treat the 585,094-byte
CodeMirror figure or the Bundle Size/Offline Cache rows above as current.
