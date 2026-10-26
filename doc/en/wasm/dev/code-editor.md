> [Version française](../../../fr/wasm/dev/code-editor.md)

# Code editor

`Apps/System/HackerOs.Apps.CodeEditor` (`org.hackeros.code-editor`): a
VS Code-style code editor, on Monaco 0.55 (ADR 0020) and laid out as a
workbench (ADR 0062), with a bottom dock (ADR 0063). It opens files and
folders of the virtual filesystem, searches across a whole folder, and embeds
the terminals of the Terminal app.

The old documentation (switch from CodeMirror to Monaco, August 2026 evidence)
is archived in [`old/code-editor.md`](old/code-editor.md).

## Features

| Area | What the user does |
| --- | --- |
| Folder | File › Open Folder… (or the empty explorer's button); the dialog's "New folder" button creates a new project's folder (ADR 0175); the folder tree appears in the sidebar; File › Close Folder. |
| Explorer | A click shows a file without leaving the tree (arrows, Delete, F2 stay with the tree); double-click or Enter opens it and gives the focus to the editor. "New File…", "New Folder…", "Refresh" buttons; the name is typed in place in the selected folder. The tree is the OS file control (`HackFileView` in tree mode, provided by the explorer, ADR 0155): its menu is the OS one (Open, Open with, Cut, Copy, Paste into folder, Rename, Move to trash, Delete permanently, Properties), to which the editor adds New File…, New Folder…, Find in Folder… and Open in Integrated Terminal through `ContextMenuOpening`. Drag and drop, clipboard, trash and errors (to the status bar) are those of the control, with the handle of the chosen folder (`SelectedResource`). |
| Tabs | Several open files, each with its undo history, selection and scroll. A "●" dot on a modified tab, a cross on hover; middle click to close; folder name added when two tabs have the same name. Ctrl+Tab and Ctrl+Shift+Tab switch tabs. |
| Search | Ctrl+Shift+F or the magnifier icon: search in all files of the folder, as you type. Options: case (Aa), whole word (ab), regular expression (.*), "files to include" and "files to exclude" (`*.cs`, `src/**`). Results grouped by file, collapsible; a click opens the file and selects the occurrence. The text selected in the editor serves as the initial search. |
| Quick open | Ctrl+P: lists the files of the folder, filtered as a fuzzy subsequence (`edwin` finds `EditorWindow.razor`), arrows and Enter. With no folder open, Ctrl+P opens the "Open File" box. |
| Editing | All of Monaco: find/replace in the file (Ctrl+F, Ctrl+H), go to line (Ctrl+G), comment (Ctrl+/), format, Monaco command palette (F1), multicursor, minimap. View › Word Wrap (Alt+Z), Show/Hide Minimap. |
| Saving | Ctrl+S, Ctrl+Shift+S (Save As), File › Save All. Files in the open folder go through the access granted to the folder. Unsaved content is saved for recovery and restored at the next launch. |
| Bottom dock | Ctrl+J shows or hides it (it is hidden, not destroyed: the shells keep running). **Status** tabs (folder, file, language, open and unsaved tabs, terminals, then a timestamped, clearable message log), **Terminal** and **Problems** (empty for now). |
| Terminal | Ctrl+` or the icon at the bottom of the activity bar: Terminal tab of the dock. It is the terminal of the Terminal app, embedded through the `hackeros.terminal` contract (ADR 0063): one `hsh` per tab ("+"), started in the open folder, title = running command or folder, clear, kill. "Open in Integrated Terminal" does a visible `cd` in the active terminal if it is idle, otherwise opens a new terminal. `exit` closes the tab; the last one hides the dock. Without the Terminal app installed, the tab says why. |
| Layout | Sidebar (Ctrl+B) and dock resizable by dragging; the Monaco area always takes all the remaining space; status bar with the last message (a click opens Status), Ln/Col, selection, encoding, line ending and language (changeable). Errors also stay above the editor until dismissed. |

Out of scope (ADR 0062): compilation, execution, "go to definition",
cross-file IntelliSense, source control, debugger, extensions.

## Architecture

| Element | Role |
| --- | --- |
| `CodeEditorWindow.razor` / `.razor.cs` / `.razor.css` | Workbench: menus, activity bar, sidebar, tabs, breadcrumb, Monaco area, watermark, bottom dock (Status, Terminal through `HackEmbeddedComponent<IHackTerminal>`, Problems), status bar. Owns the session, the workspace, recovery and the close guard. |
| `CodeEditorWindow.razor.js` | Collocated module: `createEditor` (Monaco + accessible name), `attachWorkbench` (shortcuts captured before the browser and Monaco, then `OnShortcut(name)`; resize handles that set `--sidebar-width` and `--panel-height` on the root). |
| `CodeEditor.monaco.entry.js` → `wwwroot/monaco/monaco.bundle.js` | `hackeros-dark` theme and `createCodeEditor`: one model per tab (`showDocument`, `closeDocument`), `setLanguage`, `reveal`, `runAction`, `getSelectedText`, `setOptions`, `flush`, `markSaved`, `layout`, `focus`, `destroy`; calls back `OnDirtyChanged(id, modified)` at each toggle, `OnEditorChanged(id, content)` after 400 ms without typing, and `OnCursorChanged(line, column, selection)` after 60 ms. |
| `CodeEditorSession`, `CodeEditorDocument` | Tabs and content in C#; `RelocatePaths` follows a file or folder rename. |
| `CodeEditorWorkspace` | Open folder: file list (exclusions `.git`, `.svn`, `.hg`, `node_modules`, `bin`, `obj`; at most 5,000), creation of files and folders, relative paths. |
| `CodeEditorSearch.cs` | `CodeEditorTextSearch` (line by line, 1,000 occurrences per file, preview cut around the occurrence), `CodeEditorGlob`, `CodeEditorWorkspaceSearch` (5,000 occurrences in total, files over 1 MiB ignored), `CodeEditorQuickOpen` (fuzzy ranking). |
| `CodeEditorExplorer`, `CodeEditorSearchView`, `CodeEditorQuickOpenPalette`, `CodeEditorCursorStatus` | Sidebar views, Ctrl+P box and cursor position, each with its collocated CSS. Since `desk/3`, `CodeEditorExplorer` displays `HackFileView` (no more reference to `HackerOs.AppSdk.FileView`): `Opening` opens a file in a tab (`Handled`), `SelectionChanged` shows it, `Renamed` makes the tabs follow. |

The content of each file belongs to `CodeEditorDocument` (C#). Monaco keeps a
model per tab for the history and position; when a tab becomes active,
`showDocument` replaces the model's text only if it differs from the
document's (reload, recovery).

A keystroke costs nothing on the .NET side. Monaco keeps the text and compares
its model's version to that of the last save (`getAlternativeVersionId`): the
"modified" state is only sent when it toggles (`OnDirtyChanged`), and the
whole text is only sent after 400 ms without typing (`OnEditorChanged`).
Before saving, closing a tab or searching the folder, the window calls
`flush` to receive the pending text; after a save, `markSaved` moves Monaco's
reference. The recovery copy is written 1.5 s after the last modification.
Before, each keystroke sent the whole file to .NET and then restarted the
recovery write: pasting 1,000 lines froze the page for 133 s in headless
Chromium, versus 40 s now (the rest is Monaco itself), and typing in that
file costs about 23% less script. The cursor position is displayed by
`CodeEditorCursorStatus`, which redraws on its own.

Each modification cancels the pending recovery write. The filesystem then
answers `Cancelled`, which `CodeEditorRecoveryStore` and
`CodeEditorFileService` translate to a `Cancelled` status without error text:
the window never displays it. Before, this raw code reached the error banner
("Cancelled") during typing.

Stop requested by the OS (ADR 0069: logout, `kill`, "End task"):
`OnAppStopRequestedAsync` cancels the 1.5 s wait and immediately writes the
recovery copy if a tab is modified; the OS waits for this write before closing
the window. The folder search and the folder walk call
`HackProcess.CheckpointAsync` between two files or folders: the page stays
fluid and the search stops on a stop request.

Since `exec/5` (ADR 0140), the Search view launches the search as a
**background task** (`CodeEditorWorkspaceSearch.SearchInBackgroundAsync`,
[`background-jobs.md`](../framework/background-jobs.md)): the window lists the
folder, then the child process `code-editor:search` reads each file and applies
the pattern, in a Web Worker when the host has one. Unsaved tabs go into the
task input; an invalid pattern is reported immediately, without a process. A
new keystroke cancels the previous task (code 130). `SearchAsync` remains the
inline search, shared with the task (`SearchFilesAsync`) and used by the tests.

Enter in the Ctrl+P box and in the explorer's creation field acts on key
release: the editor takes the focus back when the box closes, and handling on
key down would let the end of the keystroke insert a line break in the file.

## Shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+N / Ctrl+O | New text file / open a file |
| Ctrl+S / Ctrl+Shift+S | Save / save as |
| Ctrl+W | Close the tab |
| Ctrl+P | Go to file (quick open) |
| Ctrl+Shift+F / Ctrl+Shift+E | Search view / Explorer view |
| Ctrl+B | Show or hide the sidebar |
| Ctrl+` | Show or hide the terminal |
| Ctrl+J | Show or hide the bottom dock |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Alt+Z | Automatic word wrap |
| F1, Ctrl+F, Ctrl+H, Ctrl+G, Ctrl+/ | Monaco (palette, find, replace, go to line, comment) |

In the terminal, only Ctrl+` and Ctrl+J are intercepted; the other keys go to
the shell. Chrome keeps Ctrl+N, Ctrl+W and Ctrl+T for itself as long as
HackerOS is not installed as a PWA; each command also has its menu entry.

## Updating Monaco

As before: modify `package.json`, then in
`Apps/System/HackerOs.Apps.CodeEditor` run `npm ci && npm run build`, and
version `wwwroot/monaco/`. The .NET build does not run npm.

## Tasks

- [x] Reproduce the defects (editing area reduced to one line, no creation in
  the tree, tabs that lose their state).
- [x] Monaco: one model per tab, cursor, `reveal`, actions, options,
  `hackeros-dark` theme.
- [x] `CodeEditorWorkspace`: file list, creation, relative paths.
- [x] Folder search (case, whole word, regex, globs, unsaved content) and
  fuzzy quick open.
- [x] Folder search as a `HackBackground` task (`exec/5`, ADR 0140).
- [x] Explorer views (in-place creation, context menu, trash) and Search;
  Ctrl+P box.
- [x] Workbench: menus, activity bar, resizable sidebar and terminal,
  breadcrumb, watermark, Ln/Col status bar.
- [x] VS Code shortcuts captured by the collocated module.
- [x] Tabs that follow a rename; Save All; Close All.
- [x] `FileView`: right-clicking an item no longer opens the background menu.
- [x] Tests: workspace, search, globs, quick open, tab relocation.
- [x] Trial in Chromium: folder, creation, search, Ctrl+P, Ctrl+S, deletion,
  context menu, resizing.

Slowness and dock (ADR 0063):

- [x] Measure typing and pasting in a 1,000-line file, before and after.
- [x] Stop sending the text to .NET on every keystroke (`OnDirtyChanged`,
  send after a pause, `flush`, `markSaved`).
- [x] Bottom dock: Status (facts and log), Terminal, Problems; Ctrl+J.
- [x] Terminal of the Terminal app embedded by contract, "Open in Integrated
  Terminal".
- [x] Trial in Chromium: embedded terminal, `cd`, title, tabs, `exit`,
  Ctrl+J, Status, Problems.
- [x] A cancelled write (recovery replaced by a more recent keystroke, window
  closed) is no longer displayed as an error.

## Re-verified points (v3/11, ADR 0103 §3)

The two points still open in the old tracking (P4-W3-002, P4-W3-007) were
reviewed in the code on 2026-10-17:

- **Offline resources.** Monaco, its workers (`editor`, `json`, `css`,
  `html`, `ts`) and its `codicon.ttf` font are shipped in `wwwroot/monaco/`;
  no CDN. The workers are resolved by `new URL(…, import.meta.url)`, hence
  under `_content/…` like the module. **Fixed:** the published service worker
  did not cache fonts (`.ttf` missing from `offlineAssetsInclude`), which made
  Monaco's icons disappear offline. `.ttf`, `.woff2`, `.svg` and `.webp` are
  added there.
- **Large files.** A document is limited to 1 MiB
  (`CodeEditorDocument.MaxDocumentBytes`) at opening, at writing and in the
  search (larger files are ignored); recovery is limited to 8 MiB and the
  workspace to 5,000 files. The refusal gives a clear message instead of
  freezing the tab.
- **Worker cleanup.** `DisposeAsync` calls `destroy` (subscriptions, editor,
  all models, ARIA observer) then `dispose` of the workbench (keyboard
  listeners, separators) and releases the module. With no open model, Monaco
  itself stops its language workers after its idle period; they are shared
  between windows, so they are not forcibly terminated.
- Still true: the end-to-end proof in a published, offline and lazily loaded
  output is not automated (the E2E tests run on the development host).

OS file control (`desk/3-file-control`, ADR 0155):

- [x] Side explorer tree on `HackFileView` (tree mode, single selection,
  handle of the chosen folder through `SelectedResource`).
- [x] Editor entries added to the OS menu through `ContextMenuOpening`
  (background: New File…/New Folder… in place of "New folder"; folder: the
  same, Find in Folder…, Open in Integrated Terminal).
- [x] Editor-specific deletion removed (the control's), `Delete.*` and
  `Dialog.Delete.*` texts removed, control errors to the status bar.
