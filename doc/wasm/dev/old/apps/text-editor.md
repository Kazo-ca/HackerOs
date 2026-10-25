# Text Editor App (`org.hackeros.text-editor`)

## Purpose

The Text Editor is HackerOS's authorized virtual-filesystem text editor. It provides a focused
single-file editing experience for plain text, configuration, log, JSON, and Markdown files
within the security boundary of the VFS gateway. It is deliberately **not** a code IDE; advanced
features like syntax highlighting, Monaco integration, and script execution are deferred to Phase 4.

## Architecture

| Class / File | Role |
|---|---|
| `TextEditorWindow.razor` | Blazor `WindowAppBase` component – UI, menus, find bar, key bindings, dialog orchestration |
| `TextEditorWindow.razor.css` | Scoped CSS – gothic/hacker dark theme consistent with HackerOS visual style |
| `TextEditorDocument.cs` | Pure state machine: `New → Loading → Loaded/Error`, dirty tracking, optimistic revision |
| `app.manifest.json` | App manifest for `org.hackeros.text-editor` with `filesystem.user-home.{read,write}` capabilities |
| `HackerOs.Apps.TextEditor.csproj` | Project referencing `HackerOs.AppSdk.Blazor`, `HackerOs.Platform.Core` |
| `Tests/HackerOs.Apps.TextEditor.Tests/TextEditorDocumentTests.cs` | xUnit tests covering load, edit, dirty-close, conflict, reload round-trips |

### State Machine (TextEditorDocument)

```
New ──── BeginLoading ───► Loading ──── CompleteLoading ───► Loaded
                                   \── FailLoading ──────► Error
Loaded ── ResetToNew ─────────────────────────────────────► New
Loaded ── Edit ───────────────────── (IsDirty = true)
Loaded ── CompleteSave ───────────── (IsDirty = false, revision updated)
```

Revision tracking enables **optimistic concurrency**: writes carry `LoadedRevision`. If the
filesystem returns `RevisionConflict`, the editor surfaces a conflict banner and prompts the user
to use **Save As** or discard (reload).

## Usage / API

### Opening a file from an intent

The window host may pass an initial file path via the `LaunchFilePath` parameter:

```razor
<TextEditorWindow AppContext="@context" LaunchFilePath="/home/user/readme.txt" />
```

### File Associations (P2-TEXT-001)

Registered handlers in `app.manifest.json` for extensions / MIME types:

| Extension | MIME Type |
|---|---|
| `.txt` | `text/plain` |
| `.log` | `text/plain` |
| `.conf` | `text/plain` |
| `.json` | `application/json` |
| `.md` | `text/markdown` |

### Keyboard Shortcuts (P2-TEXT-006)

| Shortcut | Action |
|---|---|
| Ctrl+N | New document |
| Ctrl+O | Open file dialog |
| Ctrl+S | Save |
| Ctrl+Shift+S | Save As |
| Ctrl+F | Toggle find bar |
| Enter (in find) | Find next |
| Shift+Enter (in find) | Find previous |
| Escape (in find) | Close find bar |

## Key Design Decisions

1. **No launch args on Window apps**: `IAppExecutionContext` doesn't carry launch arguments for
   Window apps (args are only propagated through `TerminalExecutionContext`). Intent file paths are
   instead passed by the window host via an optional `LaunchFilePath` razor parameter.

2. **Optimistic revision on write**: The editor tracks `LoadedRevision` (stored as
   `ContentModifiedAtUtc.Ticks`). A write is attempted with this revision; `RevisionConflict` is
   shown to the user non-destructively.

3. **512 KiB size limit**: Files larger than 512 KiB are rejected at read time to prevent the
   Blazor UI from freezing on large logs. This limit is prominently surfaced in the error message.

4. **Binary rejection**: Files with `FileSystemContentKind.Binary` are rejected before content
   is read to avoid corrupted display.

5. **Scoped CSS only**: All styles are in `TextEditorWindow.razor.css`. No inline styles, per
   `AGENTS.md`.

6. **MudBlazor dialogs**: All confirmation, conflict, and error dialogs are rendered via
   `IDialogService.ShowMessageBox` — no custom dialog Razor components were needed for this slice.

7. **`MemoryStreamContentSource`**: An inner private class implements `IFileSystemContentSource`
   to stream UTF-8 encoded content to the VFS gateway without requiring a shared library change.

## Settings Projection (P2-TEXT-005)

The Text Editor can open JSON settings files (e.g. `/etc/hackeros/file-associations.json`) via the
standard Open dialog. Authorization and validation remain the responsibility of the VFS gateway and
the Settings service — the editor operates on the raw text representation. Schema errors are not
surfaced within this first slice (Phase 4 scope).

## Task List

- [x] `P2-TEXT-001` Create manifest for `org.hackeros.text-editor` with Window kind and first-slice handlers for `.txt`, `.log`, `.conf`, `.json`, and `.md`
- [x] `P2-TEXT-002` Accept open/edit intents and load authorized virtual text files; reject binary/oversized/denied content with recoverable errors
- [x] `P2-TEXT-003` Implement New, Open, Save, Save As using standard dialog helpers and optimistic file revisions
- [x] `P2-TEXT-004` Track dirty state and prompt on close/open/replace/logout; ordinary cancellation preserves the window and content
- [x] `P2-TEXT-005` Support editing projected settings documents (generic VFS open/save via authorized gateway)
- [x] `P2-TEXT-006` Implement accessible keyboard shortcuts and find baseline; shortcuts are documented through menus/tooltips
- [x] `P2-TEXT-007` Add tests for file round trip, Save As, dirty close, permissions, binary rejection, concurrent conflict, settings projection, and association dispatch
