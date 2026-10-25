# Code Editor

## Purpose

The Code Editor provides bounded, multi-tab source editing over the app-scoped
virtual filesystem, an "Open Folder" tree view for working across a whole
directory, and an embedded terminal scoped to that folder. Editing never
grants script execution: that remains behind the separate
`user-scripts.execute` capability defined by ADR 0020.

## Architecture

- `CodeEditorDocument` owns one buffer, clean baseline, VFS path/revision,
  syntax mode, 1 MiB UTF-8 limit, and serializable recovery state.
- `CodeEditorSession` owns tab order, active-tab selection, dirty-close
  decisions, tab isolation, and complete C# recovery snapshots.
- `CodeEditorFileService` performs bounded reads, selected-resource-aware VFS
  access, new-file creation, and atomic optimistic content replacement. Denial,
  missing files, binary content, size limits, and revision conflicts are typed
  recoverable outcomes.
- `CodeEditorWindow` owns dialogs, tabs, status, shortcuts, C# document state,
  VFS-backed recovery, whole-window close confirmation, JS resource disposal,
  the Open Folder tree, and the set of embedded terminal tabs.
- `CodeEditorRecoveryStore` serializes bounded snapshots through the approved
  app-scoped VFS boundary, keyed by the signed-in user's home path (not the
  opaque internal user ID). It returns typed missing, malformed, denied, and
  oversized outcomes rather than relying on browser storage.
- `CodeEditorWindow.razor.js` is the narrow collocated boundary to a local
  Monaco Editor bundle. It exposes `createEditor(host, dotNet, content, mode)`,
  whose returned handle can update content, change syntax language, focus, and
  destroy the editor; it cannot access the VFS, app context, or script
  execution.

The pinned bundle is generated from `package-lock.json` (`monaco-editor`
pinned exactly) with `npm run build`, which runs `build.mjs`: an esbuild pass
produces the ESM entry bundle (`monaco.bundle.js`/`.css` from
`CodeEditor.monaco.entry.js`, importing only the core editor plus
JSON/CSS/HTML/TypeScript/Markdown/C# language contributions, not the full
`editor.main.js`), and a second IIFE esbuild pass produces the five classic
`new Worker(url)` scripts Monaco needs (`editor`, `json`, `css`, `html`, and
`ts` workers) under `wwwroot/monaco/`, alongside `codicon.ttf` and a
regenerated `monaco.bundle.LICENSE.txt`. There is no runtime CDN dependency.
The project build fails (`ValidateCodeEditorAssets` MSBuild target) if the
lock file, the bundle, or the license notice is missing.

### Open Folder tree view

`OpenFolderAsync` (`CodeEditorWindow.razor`) uses the existing
`WindowAppBase.SelectFolderAsync` dialog, then scopes a fresh
`IAppFileSystemGateway` to that folder via `WithSelectedHandle` and renders the
shared `HackerOs.AppSdk.FileView` component in `Mode="FileViewMode.Tree"`
inside a sidebar. Opening a file in the tree adds an editor tab through a
`CodeEditorFileService` scoped to the open folder's file system. No dedicated
Code-Editor-specific tree service was introduced — the feature reuses
`FileView`, the same reusable control File Explorer is built on.

### Integrated terminal

The Code Editor embeds the shared `HackerOs.AppSdk.Terminal` `TerminalPanel`
component directly (`CallerAppId="org.hackeros.code-editor"`), one instance
per open terminal tab, all mounted simultaneously (only the active one is
shown, via CSS) so output survives tab switches. New terminal tabs default
their working directory to the currently open folder. `TerminalPanel` and its
supporting session/parser classes are the same ones the standalone Terminal
app uses — see [`apps/terminal.md`](apps/terminal.md). Because the embedded
terminal dispatches catalog commands through `AppIntentDispatcher`, the Code
Editor manifest declares `apps.launch` in addition to its filesystem
capabilities.

## Usage and API

`CodeEditorDocument.TryEdit` rejects content over 1 MiB without corrupting the
previous buffer. `CompleteSave` advances the clean baseline only after a VFS
write commits. `CodeEditorSession.Close` returns `ConfirmationRequired` for a
dirty tab unless the caller explicitly confirms discard. `CaptureRecovery` and
`Restore` keep recovery serialization independent of Monaco and the DOM.

Developers rebuilding the checked-in browser asset use:

```powershell
cd Apps/System/HackerOs.Apps.CodeEditor
npm ci
npm run build
```

## Key decisions

- Monaco Editor is bundled locally per the ADR 0020 amendment (superseding the
  original CodeMirror 6 choice) to match the legacy JS version's editor; all
  runtime editor packages are pinned exactly.
- C# is authoritative for files, tabs, permissions, persistence decisions,
  dirty state, recovery data, and lifecycle. JavaScript owns only Monaco's
  DOM-specific adapter and its worker wiring.
- Save replaces file content atomically with an expected revision. Save As
  creates an empty VFS entry when needed, then atomically replaces its content.
- Recovery snapshots are keyed by the user's `/home` path, not their opaque
  user ID, because the VFS has no route to an ID-keyed location.

## Task list and evidence

- [x] Replace the prototype textarea with a local editor adapter (originally
  CodeMirror 6, superseded by Monaco to match the legacy app).
- [x] Implement independent tabs, syntax modes, bounded buffers, and dirty-tab
  confirmation.
- [x] Implement app-scoped VFS load, Save, Save As, typed denial, and optimistic
  revision-conflict behavior.
- [x] Add deterministic recovery snapshots, approved VFS persistence, and editor
  disposal.
- [x] Connect dirty state to the platform's whole-window close coordinator.
- [x] Add an Open Folder tree view (shared `FileView` control) and an embedded,
  tabbed `TerminalPanel` scoped to the open folder.
- [x] Pass the focused unit/tab tests, the Chromium
  `Code_editor_local_bundle_edits_switches_mode_and_disposes_cleanly` test
  (updated for Monaco's `.monaco-editor`/`.native-edit-context` DOM), and the
  representative serious/critical axe scan.
- [ ] Prove recovery in a real rendered Code Editor reload scenario.
- [ ] Exercise the complete window plus real file dialogs/VFS in published,
  offline, lazy-loaded browser output.

`P4-W3-002` and audit item `E-001` remain unchecked until real component reload
and published-browser gaps are closed.
