> [Version française](../../../../fr/wasm/dev/adr/0157-file-dialogs-show-the-file-control.md)

# ADR 0157: the Open / Save / Choose a folder dialogs show the file control

## Status

Accepted on 2026-10-20.

- **Work item:** `desk/3b-file-dialogs`, work item separate from work item 3 chosen
  by Benoit (option "Separate work item 3b" of the card in the Desk 3 thread,
  2026-10-20, ADR 0155 §6).
- **Adjusts:** ADR 0148 §2 (`hackeros.file-dialogs` row of the table) on
  the contract that carries the file view of these three dialogs; its other
  decisions remain true.

## Context

The Open, Save and Choose a folder dialogs
(`IFileDialogService`) are drawn by the platform: `OpenFileDialog`,
`SaveFileDialog` and `FolderSelectDialog`, with a homemade list,
`VirtualFolderBrowser`. Work item 3 made the explorer's file control
(`hackeros.file-view`) the source of truth for displaying
files (ADR 0155). ADR 0148 §2 places "the file view of the
Open / Save / Choose a folder dialogs" under `hackeros.file-dialogs`, and
leaves `IFileDialogService` as the entry point.

## Decision

### 1. The view of these dialogs is the `hackeros.file-view` contract

The platform keeps the dialog: title, file type, file name,
replacement confirmation, New folder, buttons, and the delivery of
handles (`FileDialogCoordinator`, unchanged). Only the list changes: a
common platform component, `FileDialogBrowser`, displays
**`HackFileView`**, therefore the control of the app that provides
`hackeros.file-view`, rather than `VirtualFolderBrowser`.

We do not add a third dialog to `hackeros.file-dialogs`: the
provider would have to redraw the whole dialog and the platform would lose the
delivery of handles. The control is enough, and the provider chosen by an
administrator (ADR 0156) reaches these dialogs with nothing more.
`hackeros.file-dialogs` keeps "Open With", "Properties" and the name
conflict.

### 2. With the rights of the requesting app

The dialog keeps the calling app's context
(`FileDialogPresentation.Context`, set by the coordinator) and gives it in
cascade to the control, like `ProvidedFileDialog` (ADR 0155 §5): the control
lists, creates and renames with that app's rights (ADR 0148 §4), as
`VirtualFolderBrowser` did with its gateway. Since the dialog window
is not an app window, `FileDialogBrowser` also creates that app's menu
scope (`ContextMenuService.CreateScope`): the file menu
opens in the dialog as in a window.

### 3. What the control does in each dialog

| Dialog | `Features` | Selection kept | Activated file |
|---|---|---|---|
| Open | `None` (browse) | files; several if `AllowMultiple` | opens the dialog with that file |
| Save | `CreateFolder`, `Rename` | none | takes its name |
| Choose a folder | `CreateFolder`, `Rename` if `AllowCreateFolder`, otherwise `None` | folders | n/a |

- An activated file (double-click, Enter, "Open" in the menu) goes through
  `Opening` with `Handled = true`: the dialog takes it, no app
  opens. A folder is not handled: the control enters it.
- The file type filter becomes `Filter` (name and MIME type); folders
  remain visible. Changing the type calls `RefreshFilter`.
- The dialog draws its bar above the control: parent folder, breadcrumb
  (the control has none, ADR 0155 §8), "New folder" when `CreateFolder`
  is allowed (ADR 0175) and the Details / Icons choice.
  The control's errors (`ErrorOccurred`) are displayed in the dialog
  until the next folder.
- Choosing a folder creates its folder without revision of the parent (the contract
  gives none); a taken name still fails (`AlreadyExists`). The created folder
  is selected (`RevealAsync`).

### 4. Fallback to the platform list

Without a `hackeros.file-view` provider (no installed or enabled app that
provides it, a test host), or without context (a presentation built
by hand), `FileDialogBrowser` displays `VirtualFolderBrowser` in the
`Unavailable` of `HackFileView`. The dialogs thus always work, as
they did before; this fallback comes after that of ADR 0156 (return to
the explorer), when even the explorer is missing.

## Consequences

- The dialogs have the same list as the explorer (columns, sorting, icons,
  OS menu) and follow its fixes without recompiling the apps.
- `FileDialogPresentation.Context` and `FileDialogBrowser` are added to
  the platform API (`PublicAPI.Unshipped.txt`). The SDK API does not change.
- Fix found while checking the dialogs: the content of a window
  (`.window-content` of `WindowHost`) is explicitly placed in the second
  row of its grid. Placed automatically, it slipped into a third
  row as soon as the window's layer (`.window-overlay`, ADR 0127 §3)
  was rendered, and left an empty band under the title bar.
- The end-to-end tests that drive the OS dialogs read rows (`role=row`)
  rather than options; those of the test bench without an explorer
  (`HackerOs.E2E.Tests`) cover the fallback.

## References

- [ADR 0148](0148-sdk-contracts-and-os-provided-controls.md),
  [ADR 0155](0155-file-control-implementation-adjustments.md),
  [ADR 0156](0156-replaceable-file-control-provider.md).
- [`file-explorer.md`](../file-explorer.md),
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md),
  [`sdk-reference.md`](../../framework/sdk-reference.md).
