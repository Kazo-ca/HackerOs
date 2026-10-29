> [Version française](../../../fr/wasm/dev/file-explorer.md)

# File explorer: cut, copy, paste and drag and drop

The APIs intended for apps are in [`file-operations-api.md`](../framework/file-operations-api.md) (`HackFileOperations`), [`file-control-api.md`](../framework/file-control-api.md) (`HackFileView` and dialogs) and [`clipboard-api.md`](../framework/clipboard-api.md) (clipboard).

## Purpose

Work item `rebuild/4-file-explorer` of phase 4 (ADR 0056 §12, ADR 0058 §1).
The explorer (`Apps/System/HackerOs.Apps.FileExplorer`) reaches parity with
the legacy (`src/apps/file-explorer.ts`) and goes beyond it:

- **cut, copy, paste** through Ctrl+X, Ctrl+C, Ctrl+V, the toolbar and the
  context menu ("Cut", "Copy", "Paste", "Paste Into Folder"), through the
  **session clipboard**: another explorer window, or another app, pastes what
  a window has cut;
- **Nautilus-style name conflicts**: a copy whose name is taken becomes
  `rapport (copy).txt`, then `rapport (copy 2).txt`…; a move asks
  "Replace", "Keep both" or "Skip", with "Apply to all";
- **drag and drop** onto a folder, onto the view background, onto a tree
  node, onto a breadcrumb segment, onto the "Home" and "Trash" entries of the
  Places panel, onto the Trash view, between two windows, and from the user's
  real desktop (host files);
- a **failure summary** at the end of each operation (banner and
  notification), with the reason per item; one error does not stop the
  others.
- since `web/5-static-sites`, **"Publish as a web site…"** in a folder's menu:
  opens Web Server Manager with this folder as the root of a new site
  ([`static-sites.md`](static-sites.md)). Since `desk/4` (ADR 0159), it is an
  entry **registered by Web Server Manager**, and "Extract here" an entry
  registered by the explorer (`[RegisterFileContextMenu]` of
  `FileExplorerApp`, intent `org.hackeros.file-explorer.extract-here.v1`:
  the explorer opens on the archive's folder and extracts it); the window no
  longer adds anything through `ContextMenuOpening`. See
  [`context-menu.md`](../framework/context-menu.md#app-entries-in-the-file-menus-work-item-4).
- since `desk/3-file-control` (ADR 0155), **the explorer provides the OS file
  control** (`hackeros.file-view`) and its dialogs (`hackeros.file-dialogs`),
  and uses it itself: see the next section.

## File control provider (ADR 0148, ADR 0155)

The window hides names starting with a dot by default, notably the mobile
home configuration `~/Desktop/.mobile/layout.conf` (ADR 0166). The eye button
**Show hidden files and folders** in the view bar toggles their display for
this window. The predicate is combined with the search through `Filter`,
without modifying files or permissions; the tree's descendants and its
keyboard navigation follow this filter.

The manifest (`FileExplorerApp.Components`) registers two components:

| Contract | Component | Role |
|---|---|---|
| `hackeros.file-view` | `ExplorerFileView` | `FileView` (internal implementation, ADR 0155 §1) bound to the OS menu, with the trash, the clipboard, transfers and their conflicts, opening and "Open with". Controller `IHackFileView`. |
| `hackeros.file-dialogs` | `ExplorerFileDialogs` | "Open with" ("Always use"), "Properties", name conflict, in native markup. |

```text
Host app ──► HackFileView (SDK) ──► registry ──► ExplorerFileView (explorer)
                                                   ├─ FileView + FileViewMenuBinding ──► Menus.Attach (OS menu)
                                                   ├─ ExplorerFileMenu (menus of plan §7) ──► host's ContextMenuOpening
                                                   └─ IDialogService ──► DialogCoordinator ──► ProvidedFileDialog ──► ExplorerFileDialogs
```

### Desktop mode and shortcuts (`desk/5`, ADR 0160)

`ExplorerFileView` also provides the control's `Desktop` mode, which the
Desktop app displays on `~/Desktop` (see [`desktop.md`](desktop.md)): the
`FileViewDesktop` grid, the Home and Trash icons with their menus (Open, Empty
trash, Properties) and their drops, and the Trash full or empty depending on
`~/.local/share/Trash/files`. Opening a desktop folder opens an explorer
window; the `trash:///` argument opens the window on the Trash view.

In all modes, a valid `.desktop` file is displayed with its app's name and
icon (grayed out if the app is missing) and opening it launches the app;
dropping an app dragged from the Start menu creates a shortcut in the target
folder.

## Mobile explorer (ADR 0171)

On the mobile platform, the OS opens `FileExplorerMobileWindow`, the app's
`MobileEntryPoint` ([ADR 0171](adr/0171-mobile-file-explorer-and-full-screen-dialogs.md)).
The window inherits `FileExplorerWindow`: same state, same commands, same
`HackFileView`. Only the layout changes:

- at the top: Back, the folder name, Search and the "⋮" menu (Select, List or
  Grid, hidden files, Refresh); then the Home and Trash chips and the
  breadcrumb;
- at the bottom: the actions on the selection (Copy, Cut, Rename, Delete,
  More), otherwise "Paste here" when the clipboard holds files, otherwise the
  "+" button (New folder, New file, Upload);
- one tap opens the item (`HackFileView.OpenOnTap`); in selection mode, it adds
  or removes it (`FileView.TapTogglesSelection`); a long press opens the OS file
  menu;
- the system Back button (`IAppBackHandler`) closes the sheet, the search, the
  selection, leaves the Trash, then goes back through the history.

The dialogs the explorer opens (folder name, confirmation, properties) show
full screen (see
[`mobile-home-screen.md`](mobile-home-screen.md#full-screen-dialogs-adr-0171)).

## Architecture

Since `desk/3`, this diagram is that of the `ExplorerFileView` provider, which
the window displays through `HackFileView` (previous section).

```text
ExplorerFileView ──► FileView (internal)    ── Ctrl+X/C/V, menus ──► ClipboardRequested
        │                 │  FileView.razor.js  ── drops ──────────► DropRequested
        │                 ├─ FileViewBreadcrumb (breadcrumb, drop targets)
        │                 └─ FileViewDropZone   (Home, Trash, Trash view)
        ├──► HackClipboard ──► IAppClipboardGateway ──► AppClipboardGateway ──► SystemClipboard (one selection per session)
        └──► HackFileOperations (SDK) ──► HackFile / HackDirectory / HackTrash (app's ambient context)
```

| Piece | Where | Role |
|---|---|---|
| `HackClipboard`, `HackClipboardFileList`, `HackClipboardFileOperation` | `Shared/HackerOs.AppSdk/Clipboard/` | Public facade: text and file list (GNOME format `x-special/gnome-copied-files`). |
| `IAppClipboardGateway` | same | `IAppExecutionContext.Clipboard` gateway: `clipboard.read` / `clipboard.write`, `Changed` event. |
| `SystemClipboard` | `Platform/HackerOs.Platform.Core/Clipboard/` | OS clipboard: one selection per session, MIME targets (`text/plain`, `x-special/gnome-copied-files`), cleared at logout. In memory, like a real clipboard. Sequence number, origin and text sharing with the real machine: [`clipboard.md`](clipboard.md) (ADR 0065). |
| `AppClipboardGateway` | `Platform/HackerOs.Platform.Core/Execution/` | Trusted per-instance gateway: checks the capability on each call, sees only its session, replays `Changed` in the ambient context of the subscribed app. |
| `HackFileOperations` and contracts (formerly `FileTransfer`) | `Shared/HackerOs.AppSdk/IO/HackFileOperations*.cs` | Copy, move, import of host files, sending to trash; Nautilus names, conflicts, refusal of a folder into itself, failure summary. Runs on the `Hack*` API, hence with the app's rights. In the SDK since 2.0 (ADR 0161 §2); `IDialogService.AsFileConflictResolver` (`HackerOs.AppSdk.Blazor.Files`) asks the conflict question through `hackeros.file-dialogs`. |
| `FileView` (additions) | `Apps/System/HackerOs.Apps.FileExplorer/FileControl/` (in `Shared/HackerOs.AppSdk.FileView` before SDK 2.0, ADR 0161 §1) | Drop on the background, `DropRequested`, `ClipboardRequested`, `CanPaste`, `CutPaths` (dimmed items), Ctrl+A. |
| `FileViewBreadcrumb`, `FileViewDropZone` | same | Clickable and droppable breadcrumb; generic drop zone for the host's chrome. |
| `FileView.razor.js` | same | Native `dragover` / `drop` listeners: move/copy cursor, target highlight, capture of the content and the host's `File` objects. Since ADR 0065, Ctrl+V follows the `paste` event (`attachPasteTarget`): files copied on the real machine and pasted into the view are imported like a drop. |
| `ExplorerFileDialogs` | `Apps/System/HackerOs.Apps.FileExplorer/` | "Replace / Keep both / Skip" window (and "Open with", "Properties") of the `hackeros.file-dialogs` contract, in native markup since `desk/3`; the old MudBlazor `FileConflictDialog` is removed. |

## Usage and API

### Background archives (ADR 0140)

Compress, Extract and "Extract here" go through `HackBackground` tasks
([`background-jobs.md`](../framework/background-jobs.md)):
`FileExplorerZipService.CompressInBackgroundAsync` and `ExtractInBackgroundAsync`
launch the child processes `file-explorer:compress` and
`file-explorer:extract`, which read, compress or decompress and write through
their own gateways (same rights as the explorer), in a Web Worker when the
host has one. An error comes back as a text key (`ZipFailure`) and the window
translates it. These tasks are tracked like transfers: a stop request waits
for them to finish.

Staying in the window, with their reason written in the code: copies and
moves (`HackFileOperations`, OS calls awaited with a checkpoint between two
items, and the conflict question in the window) and the download of a
selection as `.zip` (the bytes go to the browser from the window and would
quickly exceed the 16 MiB of a result).

### `FileView`

| Parameter | Role |
|---|---|
| `DropRequested` | Receives each drop (`FileViewDropRequestedEventArgs`: `Destination`, `Entries`, `HostFiles`, `Copy`). Without it, the view moves (Ctrl: copies) itself and ignores host files. |
| `ClipboardRequested` | Receives Ctrl+X/C/V and the menu entries (`FileViewClipboardRequestedEventArgs`: `Action`, `Items`, `Destination`). Without it, these entries do not exist. |
| `CanPaste` | Grays out "Paste" and "Paste Into Folder". |
| `CutPaths` | Cut paths: their items are dimmed (`FileViewItem.IsCut`). |

`FileViewMenuItem` gains `IsDisabled`. Identifiers of the new entries:
`cut`, `copy`, `paste-into` (folder), `paste` (background).

```razor
<FileViewBreadcrumb Owner="_fileView" RootLabel="Computer" />
<FileViewDropZone Owner="_fileView" Destination="_home">…</FileViewDropZone>
<FileViewDropZone Owner="_fileView" Dropped="OnTrashDropAsync">…</FileViewDropZone>
```

The cursor indicates the operation during hover: move, copy with Ctrl or for
host files. The hovered target is framed with dotted lines.

### Command line and "Show in folder" (ADR 0039, ADR 0096 §1)

| Arguments | Effect |
|---|---|
| none | Opens the home folder. |
| `<folder>` | Opens this folder (opening an `inode/directory`, `xdg-open ~/Documents`). |
| `<parent> --select=<path>` | Opens `<parent>`, selects the item and scrolls to it: this is what `RevealFileIntent` (`HackApps.RevealFileAsync`) sends, the equivalent of `org.freedesktop.FileManager1.ShowItems`. |
| `--select=<path>` alone | Opens the parent folder of `<path>` and selects it. |

A relative or malformed argument is ignored. The explorer is not
single-instance: each request opens a window, like Nautilus launched with a
path. `FileView.RevealAsync(path)` does the work: navigation to the parent,
selection, then `revealItem` of `FileView.razor.js` (`scrollIntoView` then
focus).

## Languages (ADR 0104)

All explorer texts (toolbars, Places, Trash, Open with / Properties / name
conflict dialogs, context menus, error messages, transfer summaries) come from
`Resources/Strings.resx` (English) and `Resources/Strings.fr.resx` (French),
read by `AppContext.GetLocalizer<Strings>()` in the process language. The app
name is translated by `Resources/AppManifest.fr.resx`.

- The failure summary of a transfer is built by the window
  (`Transfer.<Copy|Move|Trash|Upload>.One/Many`) from
  `FileTransferResult.Failures`; the reason for each failure comes from the
  `FileView` SDK.
- Dates ("Deleted on" column of the Trash, "Modified" of Properties) and sizes
  follow `AppContext.FormatCulture` (`LC_TIME`, `LANG`): short date and time
  of the locale, units `o/Ko/Mo/Go` in French.
- File and folder names are never translated (`NewFile.txt`, `Archive.zip`,
  `~/.local/share/Trash`); only the name proposed for a new folder is, like
  "Untitled Folder" under GNOME.

## Key decisions

- **One clipboard per session, in Platform Core.** Like X11's `CLIPBOARD`
  selection held by the desktop's clipboard manager: all apps in the session
  share it, another session does not see it, it is cleared at logout and does
  not survive a restart. The old in-memory `AppClipboardGateway` class of the
  SDK, not wired in, is removed.
- **GNOME format.** The list is stored as `x-special/gnome-copied-files` and
  as `text/plain`: an app that reads text sees the paths (ADR 0056 §12).
- **`HackFileOperations` in the SDK** (ADR 0161 §2, formerly `FileTransfer` of
  `HackerOs.AppSdk.FileView`). Every app, every file control provider and
  every command take up the same rules; the code only uses the `Hack*` API,
  hence the app's capabilities and rights.
- **`HackDirectory.CopyAsync`.** .NET has no `Directory.Copy`; ADR 0058 asks
  for a recursive copy. The engine copies the tree in one transaction;
  between two mounts, the facade recreates it entry by entry (same fallback
  as `MoveAsync`).
- **Drops read by a native listener.** The C# handler of a `drop` cannot rely
  on `window.event`; the capture-phase `drop` listener of `FileView.razor.js`
  keeps the content and the host's `File` objects, which the C# then reads.
  The content of a host file is only read at import time
  (`IJSStreamReference`). The archived document
  `old/Global-FileView-And-MessagingSystem/FileViewControl.md` said dropping
  host files was out of reach: this is no longer the case.
- **Replacing a folder deletes it permanently**, like Nautilus in "Replace"
  mode; the window says so.
- **Sending from the toolbar**: goes through `HackFileOperations.ImportAsync`
  and no longer replaces an existing file (copy name), like a drop.
- **Explorer capabilities**: `clipboard.read`, `clipboard.write` and
  `notifications.post` (failure summary), all normal.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Execution/AppClipboardGatewayTests.cs`:
  sharing between apps of a session, session isolation, capabilities,
  `Changed`, clearing at logout, `HackClipboard` facade, GNOME format.
- `Tests/HackerOs.AppSdk.Tests/IO/HackFileOperationsTests.cs`: copy names,
  recursive copy, folder into itself, in-place move, conflict choice, "Apply
  to all", isolated failure, import, trash.
- `Tests/HackerOs.AppSdk.Blazor.Tests/HackFileOperationDialogsTests.cs`:
  conflict question asked by the provider's dialog.
- `Tests/HackerOs.Apps.FileExplorer.FileControl.Tests/FileViewDropAndClipboardTests.cs`:
  drop on the background, handover to `DropRequested`, host files, shortcuts,
  cut items, breadcrumb.
- `Tests/HackerOs.AppSdk.Tests/IO/HackDirectoryTests.cs`: `HackDirectory.CopyAsync`.
- `Tests/HackerOs.Apps.FileExplorer.Tests/FileExplorerLaunchArgumentsTests.cs`: folder, `--select`, deduced parent, ignored arguments (`v3/4`).

## Task list of work item `rebuild/4-file-explorer`

- [x] Complete `IAppClipboardGateway`, `HackClipboard`, `HackClipboardFileList` (GNOME format), old members `[Obsolete]`.
- [x] `SystemClipboard` and `AppClipboardGateway` in Platform Core, wired by the factory and the host.
- [x] `HackDirectory.CopyAsync` (recursive copy).
- [x] `FileTransfer`: copy, move with conflicts, import of host files, trash, failure summary.
- [x] `FileView`: drop on the background and the tree nodes, `DropRequested`, `ClipboardRequested`, `CanPaste`, `CutPaths`, Ctrl+A, menu entries.
- [x] `FileViewBreadcrumb` and `FileViewDropZone`; native listeners and host files in `FileView.razor.js`.
- [x] Explorer: Cut/Copy/Paste toolbar, breadcrumb, drops on Home, Trash and the Trash view, conflict window, failure notification, sending through `FileTransfer`.
- [x] Tests, this document, `app-sdk.md`, `hack-file-api.md`, `trash.md`, plan and index.

## Tasks of work item `v3/4-intents`

- [x] The explorer opens the folder passed as an argument (it always opened the home folder).
- [x] `--select=<path>`: selection and scrolling (`FileView.RevealAsync`, `revealItem`).
- [x] Tests and this document.

## Tasks of work item `v3/4b` (ADR 0121)

- [x] "Open with" offers "Always use this app for .ext files" when the chosen
  app declares the extension; the choice is written to
  `~/.config/hackeros/file-associations.json` by
  `AppContext.Catalog.SetUserDefaultHandlerAsync`
  ([`user-file-associations.md`](user-file-associations.md)).
- [x] The explorer declares `file-associations.read`.

## Tasks of work item `exec/5-background-jobs` (ADR 0140)

- [x] Compress, Extract and "UnZip Here…" as `file-explorer:compress` and `file-explorer:extract` tasks.
- [x] Copies and download kept in the window, reason written.
- [x] Round-trip test of compression then extraction (`FileExplorerZipJobsTests`).

## Tasks of work item `desk/3-file-control` (ADR 0155)

- [x] SDK contracts: `HackFileView`, `IHackFileView`, `HackFileViewItem`, `HackFileViewFeatures`, `HackFileMenuOpeningEventArgs`, `FileMenuScope`, `HackFileMenuItems`.
- [x] `ExplorerFileView`: `FileView` bound to the OS menu (`FileViewMenuBinding`), commands, trash, clipboard, transfers, opening.
- [x] Menus of plan §7 (`ExplorerFileMenu`), "Open with" as a submenu.
- [x] Dialogs provided (`ExplorerFileDialogs`) through `IDialogService` and the platform proxy; MudBlazor dialogs removed.
- [x] Explorer window on `HackFileView`; "UnZip Here…" and "Publish as a web site…" through `ContextMenuOpening` (replaced in work item 4 by registered entries, ADR 0159).
- [x] Code editor on `HackFileView` ([`code-editor.md`](code-editor.md)).
- [x] View of the Open / Save dialogs: work item `desk/3b` ([ADR 0157](adr/0157-file-dialogs-show-the-file-control.md)).

## Series `explorer/base` (ADR 0185)

Improvements decided in [ADR 0185](adr/0185-file-explorer-improvements.md) (issue #241). The "archive opened
as a folder" points (§6 to §9) are not part of this series.

- [x] `explorer/1-empty-state` (§2): the empty-folder message is inside the shared control's list
  (`FileView.EmptyText`): a row under the column headers in Details mode, centred in Icons mode. A search
  that hides everything shows `No results for "x".` (`FileView.NoMatchText`, set by the explorer window);
  another filter shows "Nothing to show here.". Nothing on the desktop or in the tree. The Open and Save
  dialogs get it too. Tests: `FileViewEmptyStateTests`.
- [x] `explorer/2-stray-drop` (§3): a drop outside every drop zone does nothing, in every window
  (detached windows included): the browser no longer opens the file. The guard is installed by the JS
  initializer of `HackerOs.Platform.Blazor` (`installStrayDropGuard`): `dragover` and `drop` listeners on
  `window`, bubble phase, that act only when no zone cancelled the event ("not allowed" cursor, default
  action cancelled). Text fields keep the browser's own text drop.
- [x] `explorer/3-resizable-sidebar` (§1): a handle on the right edge of the Places panel
  (`ExplorerSidebarResizer`, role `separator`): dragging resizes it between 140 and 400 px, a double
  click restores the default width (150 px), the left and right arrows change it by 16 px, Home and End
  go to the bounds. The width is kept per user in `~/.config/hackeros/file-explorer.json`
  (`sidebarWidth`; `FileExplorerSettings` keeps the other keys). Desktop only: the mobile window has no
  panel. Tests: `FileExplorerSettingsTests`.
- [x] `explorer/4-hover-tooltips` (§16): an OS-styled bubble appears after the mouse rests 600 ms on
  an item: full name, type, size (file), modification date and item count (folder). It lives in the
  shared control (`attachTooltips` in `FileView.razor.js`, lines from `FileView.GetItemTooltipAsync` and
  `FileViewTooltip`), so the desktop and the dialogs get it too. Mouse only: never on touch (on a phone
  the long press opens the menu). A click, a key, the wheel, a drag or leaving the item hides it.
  Tests: `FileViewTooltipTests`.
- [x] `explorer/5-recursive-size` (§5): recursive total size in the file service
  (`IAppFileSystemGateway.MeasureAsync`, `FileSystemTreeSize`, `FileSystemTreeMeasure`) and the SDK
  (`HackDirectory.GetTotalSizeAsync`, `HackDirectoryInfo.GetTotalSizeAsync`, `HackDirectorySize`, see
  [`hack-file-api.md`](../framework/hack-file-api.md)), added to the `PublicAPI.Unshipped.txt` files. The
  folder Properties dialog shows "1,234 bytes (5 files, 2 folders)". Tests: `HackDirectorySizeTests`.
- [x] `explorer/6-file-menu-actions` (§10 to §13): the explorer window (desktop and mobile) adds to the
  menu of one or more items, just before Properties: "Compress to .zip…" (name asked, prefilled),
  "Download" (one file as it is, otherwise a `.zip`), "Copy path" (absolute paths, one per line, on the
  text clipboard) and "Send to ▸": Desktop (a shortcut to each item, not a copy), Compressed folder (a
  `.zip` next to them, default name numbered when taken), My computer (download) and each network drive
  that is ready (a copy at its root). Code in `FileExplorerWindow.Menu.cs`, through the control's
  `ContextMenuOpening` event: the desktop and the dialogs keep the control's menu. The shortcut to a
  file or a folder is the SDK's new `HackDesktopLink` (`Type=Link`, see
  [`desktop-api.md`](../framework/desktop-api.md)), which the control shows and opens. Tests:
  `HackDesktopLinkTests`, `FileExplorerMenuActionsTests`.
- [x] `explorer/7-folder-previews` (§14): in Icons mode, a folder's icon becomes an open folder with
  the small icons of its first 4 entries (folders first, then by name, hidden entries left out); an
  empty folder keeps the closed icon. The content is read only for tiles on screen
  (`attachFolderPreviews` in `FileView.razor.js`, an `IntersectionObserver` that calls
  `FileView.LoadFolderPreviewsAsync`), then cached per folder revision (`FileView.Previews.cs`). Each
  previewed folder is watched (its immediate children, 64 folders at most): a change inside it reads
  the preview again while the old one stays drawn; these watches end when the view leaves the folder. Details, tree and desktop modes do not change;
  a replaced icon (shortcut) gets no preview. Tests: `FileViewFolderPreviewTests`.
- [x] `explorer/8-network-drive-refresh` (§15): a network drive folder that is shown (or watched by
  any app) follows the changes made on the host computer. When an app watches a network drive folder
  (`AppFileSystemWatchGateway`), the file service (`IExternalFileSystemChanges.TrackExternalChanges`)
  lists that folder again every 5 seconds while the page is visible, at once when the page comes back
  (tab shown again or focus, `IHostActivitySignal` / `BrowserHostActivitySignal`), and at once when
  the browser's `FileSystemObserver` reports a change (`IObservableHostFolder`, recent Chrome). Each
  listing is compared with the previous one (`HostFolderChangeTracker`): created, deleted or modified
  entries are published through the same path as the OS's own writes, and the folder's revision is
  raised. One loop per folder, stopped when the last watch ends. Tests:
  `HostFolderChangeTrackerTests`. Not checked in a real browser: it needs a real host folder picked
  by the user.
- [x] `explorer/9-drag-out-download` (§4, §5): dragging items out of the browser downloads them
  (Chrome and Edge, `DownloadURL` entry): one file as it is, a folder or several items as a `.zip`
  (`Folder.zip`, or `Archive.zip` for several). The browser takes the content only during `dragstart`,
  so it is prepared as soon as the left mouse button goes down on an item (`attachExternalDrag` in
  `FileView.razor.js` calls `FileView.PrepareExternalDragAsync` with the selected items): the size the
  OS gives decides first (`FileMetadata.Length`, or `MeasureAsync` for a folder), then the content is
  read or compressed into a blob of the page (`prepareDownload`, one at a time). Beyond about 50 MB
  nothing is prepared and the drag shows a bubble "Too large to drag out of the browser (size): use
  Download"; dragging into a HackerOS folder still works. Applies to every view of the shared control,
  the desktop included; not on touch. If the drag starts before the preparation ends (large folder), it
  stays internal. Code in `FileView.ExternalDrag.cs`. Tests: `FileViewExternalDragTests`.
- [x] `explorer/network-drives-sidebar-alignment` (after the series, Benoit's request of 2026-10-29):
  the "Network drives" section of the left panel is left-aligned like "Places" (the global button style
  centred their content; `.sidebar-entry` now sets `justify-content: flex-start`). A name too long no
  longer wraps nor gets cut: it stays on one line and the panel shows a horizontal scrollbar
  (`overflow-x: auto` on `.explorer-sidebar`).
- [x] Errors in a message box (Benoit's request of 2026-10-29): the explorer's errors no longer show
  as red text above the list (nor in the mobile banner). They open the SDK message box of type Error
  (`MessageBoxIcon.Error`, OK button, title "File Explorer"); the same error reported twice opens one
  box. Everything goes through `ReportError` in `FileExplorerWindow.razor`.
