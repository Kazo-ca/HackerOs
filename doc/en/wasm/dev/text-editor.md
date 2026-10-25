> [Version française](../../../fr/wasm/dev/text-editor.md)

# Text editor

System app `HackerOs.Apps.TextEditor` (`org.hackeros.text-editor`). This document
describes how it currently works and the search with replacement added by the
`rebuild/4-text-editor` work item (phase 4, [ADR 0058](adr/0058-base-app-features.md) §3).

## Role

The editor opens, edits and saves text files of the virtual file system, with
optimistic version control on write. It goes only through the public SDK
([`app-sdk.md`](../framework/app-sdk.md)) and no longer has an exemption at the
SDK boundary since `rebuild/4-sdk-user-and-apps`.

## Architecture

| Element | Role |
| --- | --- |
| `TextEditorApp` | `[AppManifest]` manifest: capabilities `apps.launch`, home folder read and write, Open/Save dialog boxes, notifications; `text/plain` file handler. |
| `TextEditorDocument` | State of a document: path, content, MIME type, loading state, modified or not, revision read. |
| `TextEditorFindEngine` | Search and replace logic, without Blazor or JavaScript, tested on its own (`Tests/HackerOs.Apps.TextEditor.Tests`). |
| `TextEditorWindow.razor` | Window: menus, search and replace bar, text area, status bar, keyboard shortcuts. |
| `TextEditorWindow.razor.js` | What C# cannot reach: selection in the text area (`selectRange`, `getSelectionRange`) and blocking of browser shortcuts (`guardEditorShortcuts`). |

### Opening and saving

- Extensions opened: `.txt`, `.log`, `.conf`, `.json`, `.md`; binary files and
  files larger than 512 KiB are rejected.
- Outside the home folder, access comes from the token handed over by the dialog
  box (`WithSelectedHandle`).
- Saving sends the revision read. If the file changed in the meantime
  (`RevisionConflict`), the editor reports the conflict and offers "Save As".

## Find and replace (ADR 0058 §3)

| Shortcut | Action |
| --- | --- |
| Ctrl+F | Opens the search bar alone; closes it if it is already in that mode. |
| Ctrl+H | Opens the bar with the "Replace with" row; closes it if it is already in that mode. |
| Enter / Shift+Enter (search) | Next / previous occurrence, wrapping around to the start. |
| Enter (replace) | "Replace". |
| Ctrl+Alt+Enter (replace) | "Replace All". |
| Esc | Closes the bar. |

The ▸/▾ chevron on the left shows or hides the replace row without closing the
bar. The "Edit" menu also offers "Find" and "Replace".

- **Options shared** by search and replace: "Aa" (match case) and "ab|" (whole
  word: the occurrence must not be attached to a letter, a digit or `_`). No
  regular expressions in this phase.
- **Replace** replaces the occurrence selected in the text area if it matches
  the search, then selects the next one (after the inserted text, so that a
  replacement that contains the search is not picked up again). Otherwise it
  only selects the next occurrence: a second click replaces it.
- **Replace All** replaces all the occurrences in a single modification of the
  document and displays their number ("12 replacements").
- A replacement is an ordinary modification: the document becomes modified and
  saving keeps conflict detection.
- The browser no longer receives Ctrl+S, Ctrl+O, Ctrl+N, Ctrl+F or Ctrl+H when
  the focus is in the editor (Ctrl+H opened Chrome's history).

### `TextEditorFindEngine` API

```csharp
var options = new TextEditorFindOptions(MatchCase: true, WholeWord: false);

List<int> matches = TextEditorFindEngine.FindMatches(content, "cat", options);
int next = TextEditorFindEngine.FindNextFrom(content, "cat", fromOffset: 10, options);

TextEditorReplaceResult step = TextEditorFindEngine.Replace(
    content, "cat", "dog", selectionStart, selectionLength, options);
// step.Content, step.Replaced, step.NextMatchStart (-1 s'il n'en reste plus)

TextEditorReplaceAllResult all = TextEditorFindEngine.ReplaceAll(content, "cat", "dog", options);
string summary = TextEditorFindEngine.DescribeReplacementCount(all.Count, L); // "12 replacements", « 12 remplacements »
```

`FindMatches(content, query)` without options keeps the old behavior:
substring, case-insensitive.

## Opening and saving a file

- **From the explorer** (double click, `OpenFileIntent`): the OS launches the
  editor with `argv = [app id, path]`. `WindowAppBase.LaunchFile` reads this
  path (first absolute argument); the editor loads the file and keeps its path,
  so **Save** (Ctrl+S) writes in place, without a dialog. Before, the path never
  arrived (`LaunchFilePath` was set by no one): the editor opened empty and Save
  behaved like Save as. CodeEditor follows the same rule.
- **Text or binary** is decided on the content (`HackTextContent.TryDecode`,
  SDK): strict UTF-8 with no control byte other than tab, line endings, form
  feed, backspace and escape. An empty file created by "New File" (labeled
  binary by the file system) therefore opens.
- **Save as** to another path no longer requires the revision of the open file:
  the target is created, or replaced after confirmation in the dialog (before:
  "Destination directory not found").
- **Open/Save dialogs** (Platform Blazor): a double click or Enter on a file
  opens it (Open) or takes its name (Save, which then asks before replacing);
  the dialog fills its window and the list scrolls, the bottom buttons are no
  longer cut off.

## Stop requested by the OS (ADR 0069)

When the OS asks the editor to stop (logout, `kill`, "End task") while the
document is modified, `OnAppStopRequestedAsync` writes the text into a
`HackRecoveryDraft` draft
(`~/.local/state/org.hackeros.text-editor/recovery/draft.json`, folder
`0700`) with the path of the original file; the OS waits for this write before
closing the window. At the next launch without a file, the draft is taken back
(`TakeAsync`) as a **new** modified buffer, titled
"name (recovered)", and the status bar says so: it never overwrites the
original file without the user choosing where to save it. Closing the window
oneself always goes through the "Save changes?" question and leaves no draft.

## Languages (ADR 0104)

The menus, the search bar, the status bar, the dialogs and the error messages
come from `Resources/Strings.resx` / `Strings.fr.resx`
(`AppContext.GetLocalizer<Strings>()`). `TextEditorDocument` receives the
window's localizer for its titles ("Untitled", "(recovered)") and
`DescribeReplacementCount` takes it as a parameter; without a localizer, they
read the current process's language (English outside an app, hence in unit
tests). Plurals follow the language's rule (0 and 1 singular in French).

## Decisions

- Replace does not handle regular expressions (default choice of ADR 0058,
  retained for phase 4).
- The actual selection of the text area decides "Replace", as in desktop
  editors. Without the JavaScript module (rendering without a browser), the
  editor falls back to the current occurrence of the search.
- Replacement goes through `TextEditorDocument.Edit`: the text area's native
  undo (Ctrl+Z) does not cover a replacement, like any value written by Blazor.

## Task list (`rebuild/4-text-editor`)

- [x] "Match case" and "Whole word" options in `TextEditorFindEngine.FindMatches`.
- [x] `Replace`, `ReplaceAll`, `FindNextFrom` and `DescribeReplacementCount`, tested without an interface.
- [x] Replace row in the search bar, Ctrl+H, "Edit → Replace" menu.
- [x] Reading the text area's selection and blocking browser shortcuts.
- [x] Documentation, rebuild plan and index updated.

## Task list (feedback after `rebuild/4-text-editor`, delivered with `rebuild/4-file-explorer`)

- [x] Read the launch file from the process arguments (TextEditor, CodeEditor).
- [x] Decide text/binary on the content (`HackTextContent`).
- [x] Save as to a new file.
- [x] Open/Save dialogs: double click, layout within the window.
