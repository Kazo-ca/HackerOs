> [Version française](../../../../fr/wasm/dev/adr/0155-file-control-implementation-adjustments.md)

# ADR 0155: adjustments to the file control provided by the explorer

## Status

Accepted on 2026-10-20.

- **Work item:** `desk/3-file-control` (work item 3 of the plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §11).
- **Adjusts:** ADR 0148 §1, §2 and §6, ADR 0149 §3 and plan §7 on
  implementation points; their decisions remain true.

## Context

ADR 0148 decides that the SDK carries only the `hackeros.file-view` contract and
that the file explorer provides it at run time, with its dialogs
(`hackeros.file-dialogs`) and the menus of plan §7 on the OS menu
(ADR 0150). Work item 1 wrote the contract host (ADR 0153), work item 2
the OS menu (ADR 0154). This work item makes the explorer the provider of the
control and its dialogs, and has it use them for itself and for
the code editor. Several details were not fixed.

## Decision

### 1. `FileView` becomes the explorer's implementation, without changing place

The code of `HackerOs.AppSdk.FileView` stays in its library until
SDK 2.0 (work item 6, ADR 0149), which removes it from the SDK. Until then:

- the explorer uses it as an internal implementation
  (`InternalsVisibleTo("HackerOs.Apps.FileExplorer")`);
- the `ExplorerFileView` provider gives the view an internal binding,
  `FileViewMenuBinding`, in cascade. With it, the view no longer draws its
  `MudMenu`: each row, tile, tree node and the background carry the
  attributes of `Menus.Attach` (ADR 0154 §3), and the OS menu opens on them;
- without a binding (a 1.x app that still displays `FileView` directly), the view
  keeps its old menu. The public API of `FileView` does not change.

The view's `MudMenu` therefore disappears from the explorer and from every host of the
contract, as ADR 0154 announced.

### 2. The contract API

In `HackerOs.AppSdk.Blazor.Files`:

- **`HackFileView`** (`HackContractComponent<IHackFileView>`) takes
  **ordinary parameters** rather than an options record
  (`HackFileViewOptions` of ADR 0149 §3 does not exist): `InitialDirectory`,
  `Mode`/`ModeChanged`, `Features`, `AllowMultiSelect`, `Filter`,
  `SelectedResource`, and the callbacks `Opening`, `Navigated`,
  `SelectionChanged`, `Renamed`, `Deleted`, `ContextMenuOpening`,
  `ErrorOccurred`. All are passed by name to the provider; a test
  verifies that none is forgotten.
- **`IHackFileView`**: current folder, items, selection, `CanPaste`,
  `IsBusy`, `StateChanged`, `NavigateAsync`, `RefreshAsync`, `RevealAsync`,
  `ClearSelection`, `RefreshFilter`, `WhenIdleAsync` (a window that closes
  waits for ongoing copies, ADR 0069), and **`CanExecute`/`ExecuteAsync`**
  whose commands are the identifiers of the menu entries. A toolbar
  and the menu thus do exactly the same thing.
- **`HackFileViewItem`**: snapshot of an item (path, name, folder,
  size, MIME type, date). The same view item always gives the same
  snapshot, so that the `Menus.Attach` token stays stable.
- **`HackFileMenuOpeningEventArgs`**: scope, targets, folder, menu to
  modify, `Cancel`.
- In `HackerOs.AppSdk.Files` (without Blazor, for work item 4):
  **`FileMenuScope`** (`File`, `Directory`, `Background`, `Desktop`) and
  **`HackFileMenuItems`**, with two more identifiers than the plan:
  `open-with-other` ("Choose another application...") and the prefix
  `open-with:<appId>` for the apps in the submenu.

`SelectedResource` is an addition: the code editor opens a folder chosen
in the OS dialog, with the handle of that choice
(`WithSelectedHandle`). The control lists and renames in that folder with the
same handle, as ADR 0148 §4 provides for dialogs.

### 3. The control itself does what the explorer used to do around it

`ExplorerFileView` does, **with the host's rights** (ADR 0148 §4: its
gateways are those of `HostContext`, its callbacks run in the host's
ambient context): the trash and permanent deletion after
confirmation, the session clipboard, copies and moves with the
name conflict, import of files from the host, opening with the default
app and "Open With", the "Always use" choice (ADR 0121 §4).

"Appears only if the host handles them" (plan §7) becomes the
**`Features`** parameter (`Clipboard`, `Delete`, `Rename`, `CreateFolder`,
`DragDrop`, `OpenWith`, `Properties`, all by default): a host removes what it does not want,
the entry disappears from the menu and the key does nothing.

`Opening` lets the host open an item itself (`Handled = true`): the code
editor opens a file in a tab rather than in its default app; a
folder not handled is opened in the view. Errors go to
`ErrorOccurred`, or to a notification if the host does not want them.

### 4. The menus

- One empty menu per control (`Menus.Define`), **filled at each opening**
  by `ExplorerFileMenu` (pure function, tested) then passed to the host's
  `ContextMenuOpening`.
- **Targets**: an item outside the selection becomes the selection; an
  item in the selection acts on the whole selection. The entries act on the
  targets taken at opening, not on the selection at the time of the click.
- **Scope**: `Directory` if all targets are folders, `File`
  otherwise, `Background` for the empty space.
- **Several items**: "Rename" and "Open With" absent,
  "Properties" disabled; "Open" disabled for several folders.
- **"Open With ▸"** on a file: the apps from
  `IAppCatalogGateway.GetHandlersAsync` (user's default first), then
  a group "Choose another application...". No "Open With" on a folder,
  as in the plan.
- An empty group is kept between editing and "Properties": the entries
  registered by apps will come there (work item 4); the OS collapses the
  separators around an empty group.
- Displayed shortcuts: Ctrl+X, Ctrl+C, Ctrl+V, F2, Delete, Shift+Delete, F5.

### 5. The provided dialogs go through `IDialogService`

`IDialogService` gains three methods with a default implementation (a host
written earlier answers `Unavailable`): `OpenWithAsync`,
`ShowFilePropertiesAsync`, `ResolveFileConflictAsync`, with their
request and result records (`HackFileDialogs`). The platform
(`DialogCoordinator`) is only the proxy: it queues the request with the
context of the calling app, shows it in a modal window of that app
(`ProvidedFileDialog`, which gives this context in cascade then renders
`hackeros.file-dialogs`), and returns the result that the provider gives through
`Completed`. A closing or a result of another type means "nothing"
(`Cancelled`, `Closed`, `Skip`).

The `ExplorerFileDialogs` provider is in native markup (in the style of the
OS app picker), no longer MudBlazor: `OpenWithDialog`,
`FilePropertiesDialog`, `FileConflictDialog`, `OpenWithChoice` and
`FileExplorerDialogBase` are removed from the explorer.

### 6. The view of the Open / Save dialogs

ADR 0148 §2 also places in `hackeros.file-dialogs` the file view of the
Open / Save / Choose a folder dialogs, today
`VirtualFolderBrowser` in the platform. This replacement touches the platform's
dialog pipeline and the UI tests that drive it; it is
**not** in this work item: Benoit chose (2026-10-20) a separate
`desk/3b` work item, after this one.

### 7. What remains for the following work items

- `IHackFileOperations` (ADR 0149 §3): work item 6. Until then, an app that
  copies outside the control still uses `FileTransfer`.
- "UnZip Here..." and "Publish as a web site..." become registered
  entries in work item 4; until then, the explorer window adds them
  through its own `ContextMenuOpening` (`FileExplorerMenuEntries`), so
  only in its window.
- The `Desktop` scope and its menu: work item 5.
- The view of the Open / Save dialogs: work item `desk/3b` (Benoit's choice,
  2026-10-20).

### 8. Nothing assumes the explorer is the only provider

ADR 0156, accepted during this work item, lets an administrator choose a
third-party app as provider of the two contracts; its code comes in a
later work item. This work item prepares for it:

- the hosts (code editor, explorer window) only talk to the
  contract: parameters of `HackFileView`, `IHackFileView`, identifiers of
  `HackFileMenuItems`, requests of `IDialogService`;
- the explorer window touches the internal implementation (`FileView`)
  only for the breadcrumb and the drops on Home and Trash; with another
  provider, only these two extras disappear;
- `ExplorerFileMenu` and `FileViewMenuBinding` are internal to the explorer:
  another provider builds its menus itself on the OS menu, with
  the same identifiers (ADR 0156 §5);
- the dialog proxy (`DialogCoordinator`, `ProvidedFileDialog`) renders the
  contract through the registry, not the explorer by name.

## Consequences

- A fix to the control, its menus or its dialogs reaches
  the explorer, the code editor and any app that displays `HackFileView`
  without recompiling them.
- The code editor no longer references `HackerOs.AppSdk.FileView`; its
  side explorer displays `HackFileView` as a tree and adds its entries
  ("New File", "New Folder", "Search in Folder",
  "Open in Terminal") through `ContextMenuOpening`. Its own deletion
  to the trash is removed: it is the control's.
- The additions are in `PublicAPI.Unshipped.txt` (SDK, platform, apps) and
  published with 2.0 (ADR 0149 §1).

## References

- [ADR 0148](0148-sdk-contracts-and-os-provided-controls.md),
  [ADR 0149](0149-sdk-2-0-file-view-removal.md),
  [ADR 0150](0150-os-context-menu.md),
  [ADR 0153](0153-contract-host-implementation-adjustments.md),
  [ADR 0154](0154-context-menu-implementation-adjustments.md),
  [ADR 0156](0156-replaceable-file-control-provider.md),
  [ADR 0121](0121-user-file-associations.md).
- [`file-explorer.md`](../file-explorer.md),
  [`embedded-components.md`](../../framework/embedded-components.md),
  [`context-menu.md`](../../framework/context-menu.md),
  [`code-editor.md`](../code-editor.md).
