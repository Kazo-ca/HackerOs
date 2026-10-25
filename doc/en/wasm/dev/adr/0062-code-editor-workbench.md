# ADR 0062: the code editor as a VS Code-style workbench

## Status

Accepted on 2026-10-11.

- **Plan context:** after phase 4 (rebuild finished), request from
  Benoit on 2026-10-11: the editor does not take up all the space, one can neither
  open a folder properly, nor have several useful tabs, nor create
  files and folders in the list; he wants the features of VS Code,
  without compilation or "go to definition", but with search in
  the whole open folder.
- **Supersedes** the last bullet of section 4 of ADR 0058 ("The CodeEditor
  terminal panel stays at a single shell"): the editor's terminal panel has
  several tabs, one `hsh` per tab, like the console.
- **Completes** ADR 0020 (Monaco, collocated JavaScript boundary, everything
  else in C#) without changing its decisions.

## Context

The editor displayed a toolbar, a tab strip, the `FileView` tree of a folder
and a single Monaco whose content was replaced on
each tab change (undo history and scroll position lost).
Monaco's height depended on a `flex` without absolute positioning: with the
terminal panel open in the default window (800 × 560), only
one or two lines remained visible. Nothing made it possible to create a file in
the folder, to search in its files or to open one by name.

## Decision

### 1. VS Code layout

The window follows the VS Code workbench: menu bar (File, Edit, View, Go
as `MudMenu`, ADR 0016), activity bar (Explorer, Search, terminal), resizable
sidebar, editor group (tabs, breadcrumb, Monaco),
resizable terminal panel, status bar (folder, unsaved
files, Ln/Col, encoding, line ending, language). Monaco occupies an area
in `position: absolute; inset: 0`: it always takes all the remaining space.
With no tab open, a watermark recalls the shortcuts.

### 2. One Monaco model per tab

Each tab has its own Monaco model (`showDocument`, `closeDocument`), created on
first opening: the undo history, selection and scroll position
of each file are kept. The content remains owned by
`CodeEditorDocument` in C#; Monaco sends back `(id, content)` on each
modification, and the window is redrawn only if the "modified" state
changes. The cursor is reported separately (`OnCursorChanged`) to a small
status bar component.

### 3. The open folder is a C# workspace

`CodeEditorWorkspace` represents the folder chosen in the "Open
Folder" dialog and goes through the file gateway tied to that choice
(`WithSelectedHandle`). It lists the files (skipping `.git`,
`node_modules`, `bin`, `obj`..., at most 5,000), creates files and folders, and
serves the explorer, the search and quick open. The files of the
folder are read and saved through that same gateway.

### 4. Search and quick open in C#

Search in the folder (Ctrl+Shift+F) and quick open (Ctrl+P) are
written in C# (`CodeEditorWorkspaceSearch`, `CodeEditorQuickOpen`), not
in Monaco: they read files that Monaco has never opened, and the
rule of ADR 0020 keeps the logic in C#. The search handles case, whole
word, regular expressions (.NET, 250 ms timeout per line), the
"files to include/exclude" globs, and reads the unsaved content of the open
tabs, like VS Code. Find and replace within a file stays
Monaco's (Ctrl+F, Ctrl+H).

### 5. Deleting goes through the trash

The Delete key and the explorer menu send items to the
trash (`HackTrash`, ADR 0052); "Delete permanently" asks for
confirmation, as in the file explorer. The tabs of deleted
files stay open with their content: saving recreates the file.
Renaming a file or folder in the tree moves the tabs concerned
(`CodeEditorSession.RelocatePaths`).

### 6. Context menu of `FileView` items

Right-clicking an item of `FileView` (tree, details, icons) stops the
propagation of the event: until now, the background handler also received the
click and replaced the item's menu with the background's. All the apps
that display `FileView` benefit.

## Out of scope

Compilation and execution, "go to definition", cross-file IntelliSense, source
control, debugger, extensions, multiple folders:
Benoit excluded them. The command palette is Monaco's (F1).

## Consequences

- The editor remains an app on the public SDK (ADR 0055): `IJSRuntime` is used
  only to import its own module.
- The Monaco bundle exposes a slightly wider API (`showDocument`,
  `closeDocument`, `reveal`, `runAction`, `getSelectedText`, `setOptions`);
  it is rebuilt by `npm run build` as before.
- The default window size (800 × 560, `WindowLaunchCoordinator`)
  is not changed: the editor grows like the other apps.

## Rejected alternatives

- **Search through Monaco models** (`model.findMatches`): every file of the
  folder would have to be loaded into Monaco, and the logic would move to
  JavaScript.
- **A single reloaded Monaco model** (what exists): undo and
  position are lost on each tab change.
