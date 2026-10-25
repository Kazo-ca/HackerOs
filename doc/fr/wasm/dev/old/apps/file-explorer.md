# HackerOS File Explorer Application (`org.hackeros.file-explorer`)

## Purpose

`Apps/System/HackerOs.Apps.FileExplorer/` provides the graphical virtual file manager and directory browser for HackerOS v3, implemented as a first-party `AppKind.Window` application.

## Architecture

As of Phase 4 (`INT-001`–`INT-005`, see `docs/Global-FileView-And-MessagingSystem/integrationPlan.md`), this app hosts the shared `Shared/HackerOs.AppSdk.FileView` control for listing/selection/rename/drag-drop/context-menu instead of implementing those inline (`ADR 0037`). This window keeps only the chrome `FileView` deliberately doesn't own:

- **`FileExplorerWindow.razor/.css`**: Primary window UI inheriting `WindowAppBase`. Renders the toolbar (Back, Forward, Up, Refresh, Home, Address Bar, Search box, view-mode toggle — Details/Icons/Tree), the actions toolbar (New Folder, New File, Rename, Delete, Properties, Open With, Upload, Download, Compress, Extract), and the status bar, around a hosted `<FileView>`. Toolbar actions that `FileView` doesn't expose a public method for (create/delete) call `AppContext.FileSystem` directly — the same raw gateway calls `FileView`'s own internal context-menu handlers make, just from this project instead of that one — then call `FileView.RefreshAsync()`. Rename/Properties/Open With instead go through `FileView`'s public `SelectedItem`/`FileViewItem.Rename()` surface with zero duplicated logic. Subscribes to `FileView.OnPathChange` (keeps the address bar and back/forward history in sync when the user navigates *inside* `FileView`, e.g. double-clicking a folder) and `FileView.Opened` (dispatches non-directory activations through the existing app-intent flow, since `FileView` itself only handles folder navigation, not file launching).
- **`FileExplorerState.cs`**: Navigation history only now (`CurrentPath`, `_backStack`/`_forwardStack`, `NavigateTo`/`Back`/`Forward`/`Up`) plus `ViewMode` (now `HackerOs.AppSdk.FileView.FileViewMode` directly, not a redundant local enum) and `SearchQuery`. Sorting and selection moved to `FileView` entirely and were deleted here rather than kept as an unused parallel path.
- **`FileExplorerZipService.cs`**: The one implementation of zip compress/extract against the virtual filesystem, shared between the toolbar's Compress/Extract buttons and `ZipFileContextMenuProvider`.
- **`ZipFileContextMenuProvider.cs`**: The first real `IFileViewContextMenuProvider` in the codebase (`INT-003`, the integration plan's worked example) — a `FileType`-scoped provider matching `.zip` files that inserts `"UnZip Here…"` immediately after `FileView`'s default `"open"` context-menu item, calling into `FileExplorerZipService`.
- **`OpenWithDialog.razor/.css`**: Modal dialog presenting catalog apps (`AppCatalog`) and allowing the user to select an app to open a file with via `AppIntentDispatcher`. Unchanged — explicitly excluded from the `FileView` migration.
- **`FilePropertiesDialog.razor/.css`**: Modal dialog displaying entry metadata (Path, Kind, Size, Owner, Permissions, Modified Date), now fed directly from a `FileViewItem`'s already-fetched `Metadata` rather than a separate `StatAsync` call. Unchanged — explicitly excluded from the `FileView` migration.
- **`app.manifest.json`**: Manifest declaring reverse-domain ID `org.hackeros.file-explorer`, multi-instance window policy, and capabilities (`apps.launch`, `filesystem.user-home.read`, `filesystem.user-home.write`, `dialogs.file-open`, `dialogs.file-save`, `dialogs.folder-select`).

`FileView` itself gained one small addition during this migration that isn't in its original accepted spec: a `Filter` parameter plus `RefreshFilter()` (see `FileViewControl.md`), added because the control had no client-side search hook and `INT-001` explicitly called for routing the search box through `FileView`'s parameters instead of duplicating its rendering.

## Key Features & User Experience

- **Navigation**: Full directory navigation, address-bar editing, keyboard shortcuts (`Enter` on address bar), and history stack traversal — all driven through `FileView.NavigateAsync`/`CurrentDirectory`/`OnPathChange` rather than a separate directory-listing implementation.
- **View Modes**: Details (sortable table), Icons (tile grid, marquee multi-select), and Tree (lazy-expand) — all three of `FileView`'s modes, not just the original two.
- **File Operations**: Create folder/file, rename (in-place, no dialog — `FileView`'s own inline rename), delete, and inspect entry properties.
- **Intent Dispatch & Open With**: Double-clicking a file raises `FileView.Opened`, which this window dispatches through `LaunchAppIntent` or the "Open With" app-selection dialog.
- **Drag & drop**: Inherited from `FileView` (`FV-006`) — intra- and inter-window drag/copy now work, previously unimplemented.
- **Event Auto-Refresh**: Subscribes to `IAppEventGateway`/`FileView`'s own live-watch wiring for real-time filesystem updates without polling.

## Known gaps carried from this migration

- `FileView.NavigateAsync` throws `InvalidOperationException` on a failed enumerate (e.g. permission denied). This window catches that for its own explicit navigation calls (toolbar/address bar), but a double-click *inside* `FileView` into a forbidden folder calls `NavigateAsync` from `FileView`'s own internal handler, outside this window's try/catch — a pre-existing `FileView` robustness gap surfaced by this migration, not fixed here.
- `FileView` exposes no "is loading" indicator, so the old "Loading directory items…" message could not be reproduced; only the empty-directory and error messages remain.

## Task Checklist

- [x] `P2-FILE-001` Create manifest for `org.hackeros.file-explorer` with capabilities and window entry point.
- [x] `P2-FILE-002` Implement toolbar, breadcrumbs, address bar, navigation history, and CWD state.
- [x] `P2-FILE-003` Implement details/grid view, multi-selection, and sort ordering.
- [x] `P2-FILE-004` Implement folder/file creation, rename, delete, and properties dialog.
- [x] `P2-FILE-005` & `P2-FILE-006` Implement intent dispatch for opening files and "Open With" dialog.
- [x] `P2-FILE-007` Implement real-time event subscription auto-refresh.
- [x] `P2-FILE-008` Add unit tests in `Tests/HackerOs.Apps.FileExplorer.Tests/`.
- [x] `INT-001`–`INT-005` Migrate onto `FileView` (Phase 4) — see
  `docs/Global-FileView-And-MessagingSystem/integrationPlan.md`.
