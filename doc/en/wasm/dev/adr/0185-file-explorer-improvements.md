> [Version française](../../../../fr/wasm/dev/adr/0185-file-explorer-improvements.md)

# ADR 0185: File Explorer improvements

## Status

Accepted on 2026-10-29. Decisions made with Benoit in the "Améliorer l'explorateur de fichiers"
thread (questions Q1 to Q18 asked on 2026-10-28, answered on 2026-10-29). Partly implemented
on 2026-10-29 in the `explorer/base` series (chantiers 1 to 9): every point except the archive
opened as a folder (points 6 to 9), done on 2026-10-30 in the `todo/base` series (work item
`todo/10-zip-as-folder`, issue #241). See the implementation note at the
end.

## Context

Benoit asked for nine File Explorer improvements:

1. resize the left panel (Places and Network Drives);
2. show "This directory is empty" inside the list, not above it;
3. download a file by dragging it out of the browser;
4. a text file dropped on the explorer is imported, but the browser also opens it;
5. open an archive (zip) as a read-only folder;
6. more context menu options (Compress, Send to…);
7. a folder icon that previews its content in Icon view;
8. a network drive that refreshes on its own when a file is added to it;
9. an information tooltip when hovering a file or folder.

State of the code when this was decided (main `fd52528c`):

- The window (`FileExplorerWindow.razor`) shows the empty-folder message in a `div` above the
  shared file control (`HackFileView`, contract `hackeros.file-view`, ADR 0148 and 0155). The
  Open and Save dialogs (ADR 0157) don't have it.
- The `explorer-sidebar` panel width is fixed in CSS.
- `FileView.razor.js` reads drops in its drop zones, but a drop that lands outside a zone is not
  cancelled, so the browser opens the file.
- Compress, Extract and Download exist only in the toolbar. The manifest adds "Extract here" to
  the menu of a `.zip` (ADR 0159). No app declares that it opens `.zip` files.
- The control watches the displayed folder (`IAppFileSystemWatchGateway`): a change made inside
  HackerOS updates the list. The network drives' `hostfs` provider (ADR 0181 §2) does not see
  changes made outside HackerOS.
- The SDK has a text input dialog (`IDialogService.TextInputAsync`, `TextInputDialogRequest`:
  title, text, default value, placeholder).
- A file reports its size (`FileMetadata.Length`). A folder does not report the total size of its
  content; the `du` command computes it on its own.

## Decision

1. **Resizable left panel.** A handle on the panel's right edge, to drag, with a minimum and
   maximum width (about 140 to 400 px). The width is remembered per user in the explorer state.
   Double-clicking the handle restores the default width. The panel does not collapse.

2. **Empty folder inside the list.** The message moves into the shared file control: in Details
   view, a row under the column headers; in Icon view, centred in the area. The area stays a
   drop target and keeps its context menu. A search with no match shows "No results for "x"".
   The Open and Save dialogs get it too.

3. **Drop opened by the browser (bug).** Fixed for the whole OS: a drop that lands anywhere other
   than an intended drop zone does nothing, in any window (the page cancels the default
   behaviour of `dragover` and `drop`).

4. **Drag out of the browser to download.** Chrome and Edge only (`DownloadURL`, one file per
   drag). A single file is downloaded as is. Several items or a folder are downloaded as one
   `.zip`. On Firefox and Safari, the Download button stays the only way.

5. **Size limit of the external drag.** The content must be ready when the drag starts, so it is
   prepared as soon as the item is pressed. The limit is about 50 MB and is decided from the
   **size reported by the OS**, without reading the content: `FileMetadata.Length` for a file.
   For a folder or a multiple selection, the OS does not report the total size yet: it must be
   added (a recursive total size in the file service and the SDK, which the `du` command and the
   Properties dialog can also use). Above the limit, dragging out of the browser is disabled and
   a tooltip suggests "Use Download". Dragging to a HackerOS folder still works.

6. **Open an archive.** File Explorer declares itself as the app for `.zip` files (manifest
   `fileHandlers`). Double-click opens the archive as a read-only folder, with a breadcrumb such
   as Documents › photos.zip › subfolder. "Extract here" stays in the menu.

7. **Only the explorer browses the archive.** No file system mount: the explorer reads the
   `.zip` and presents its entries itself. Other apps and the terminal don't see inside the
   archive. Opening a file of the archive first extracts it to a temporary folder (`/tmp`), then
   opens it with its app. A change to that copy does not go back into the archive.

8. **Formats.** `.zip` only to start with (`System.IO.Compression`). `.tar`, `.tar.gz`, `.7z`
   and `.rar` are out of scope.

9. **Strictly read-only archive.** Dragging an item out of the archive extracts it to the
   destination. Dragging an item into it, renaming or deleting is refused with a clear message.

10. **New context menu options** for one or more files or folders: Compress to .zip, Download,
    Copy path and a Send to ▸ submenu. Compress opens a **HackerOS text input dialog** to choose
    the archive name, prefilled with the item's name when it is alone, otherwise "Archive.zip".
    The text input dialog already exists (`TextInputAsync`); if it lacks something it needs (for
    example file name validation, or selecting the name without the extension), that is added to
    the OS dialog rather than building one inside the explorer.

11. **"Send to" destinations.** Desktop, Compressed folder (.zip), My computer (download), then
    each mounted network drive (a copy is made there).

12. **"Send to ▸ Desktop" creates a shortcut** to the file or folder, as on Windows. `.desktop`
    files currently point only to apps (ADR 0160): a shortcut to a file must be added, which the
    desktop knows how to open.

13. **No destinations added by other apps for now.** The "Send to" list is fixed by the OS. A
    manifest declaration will come when an app (for example a Hack Store app) needs it.

14. **Content preview on a folder icon.** In Icon view, the icon shows an open folder with 3 or 4
    mini icons of its first items, by type. Content is read only for folders visible on screen,
    then cached, and the preview updates with the folder watch. Details and tree views don't
    change.

15. **Automatic refresh of a network drive.** When the browser has it, the explorer uses the
    folder observation API (`FileSystemObserver`, recent Chrome). Otherwise it re-reads the
    displayed folder every 5 seconds while the window is visible, and also when the window gets
    focus back. Changes found go through the same path as the current watch, so every app that
    watches the folder sees them.

16. **Hover tooltip.** An OS-styled tooltip appears after about 600 ms, with the full name,
    type, size and modification date, plus the item count for a folder. It lives in the shared
    control, so the desktop and dialogs get it too. No tooltip on mobile, where a long press
    opens the menu.

17. **Mobile (ADR 0171).** Also on mobile: the empty-folder message, archives, new menu options,
    folder previews and network drive refresh. Desktop only: the resizable panel, dragging out of
    the browser and tooltips.

18. **Organisation.** An `explorer/base` series, with one chantier (one PR to `explorer/base`)
    per topic. The two bugs (empty folder, file opened by the browser) come first. As for every
    series, the final PR to `main` waits for Benoit's go-ahead.

## Questions asked to Benoit

Answers of 2026-10-29. "Recommendation" means Benoit took the recommended option.

| Q | Topic | Answer |
| --- | --- | --- |
| 1 | Resizable panel | Recommendation: handle, remembered width, double-click = default |
| 2 | Empty folder message | Recommendation: in the shared control, dialogs included |
| 3 | Drop opened by the browser | Recommendation: fixed for the whole OS |
| 4 | Drag out of the browser | Recommendation: single file as is, otherwise one `.zip` |
| 5 | Size limit | Recommendation (50 MB), relying on the size reported by the OS; add it where missing |
| 6 | Double-click on an archive | Recommendation: opened as a read-only folder |
| 7 | Mount or explorer only | **B**: only the explorer browses the archive, an opened file is extracted to `/tmp` |
| 8 | Formats | Recommendation: `.zip` only |
| 9 | Writing into the archive | Recommendation: strictly read-only |
| 10 | Menu options | Recommendation, and Compress asks for the name through a HackerOS text input dialog (create or complete it if something is missing) |
| 11 | Send to destinations | Recommendation: Desktop, Compressed folder, My computer, network drives |
| 12 | Send to Desktop | Recommendation: shortcut |
| 13 | Destinations from other apps | Recommendation: not for now |
| 14 | Folder preview | Recommendation: mini icons in Icon view |
| 15 | Refresh a network drive | Recommendation: `FileSystemObserver`, otherwise re-read every 5 s and on focus |
| 16 | Hover tooltip | Recommendation: OS tooltip in the shared control |
| 17 | Mobile | Recommendation: the list of point 17 |
| 18 | Organisation | Recommendation: `explorer/base` series, bugs first |

## Consequences

- The shared file control changes (empty-folder message, tooltips, folder previews): file
  dialogs and the desktop benefit without work on their side.
- The file service and the SDK gain a recursive total size (point 5), added to the
  `PublicAPI.Unshipped.txt` files for the next SDK version.
- `.desktop` files gain a shortcut to a file (point 12), to be described as a complement to
  ADR 0160.
- The OS text input dialog may gain file name validation (point 10).
- The archive is not visible outside the explorer (point 7): a file opened from the archive is a
  temporary copy.
- Only Chrome and Edge allow dragging out of the browser, and only a recent Chrome has
  `FileSystemObserver`; elsewhere, periodic re-reading costs one folder read every 5 seconds
  while the window is visible.
- Implementation: `explorer/base` series, see the note below.

## Implementation note (2026-10-29)

Each chantier is detailed in [`file-explorer.md`](../file-explorer.md). Points to remember:

- Point 12: the shortcut to a file or a folder is a `.desktop` file of type `Type=Link`, with
  `URL=file:///path` (each segment percent-encoded). It complements ADR 0160, and the SDK's
  `HackDesktopLink` reads and writes it.
- Points 10 to 13: the menu entries are added by the explorer window, through the control's
  `ContextMenuOpening` event; the desktop and the dialogs keep the control's menu.
- Point 15: following the changes made on the host lives in the file service
  (`IExternalFileSystemChanges`), not in the explorer: every app that watches a network drive
  folder gets it.
- Points 4 and 5: dragging out of the browser applies to every view of the shared control, the
  desktop included. If the drag starts before the preparation ends, it stays internal.
- Points 6 to 9 (archive opened as a folder): done on 2026-10-30 (work item
  `todo/10-zip-as-folder`). The explorer reads the `.zip` through its own gateway
  (`ZipArchiveFileSystemGateway`), turned on only in its windows; a path such as
  `/home/u/photos.zip/2024` exists for it alone. Details in [`file-explorer.md`](../file-explorer.md).
