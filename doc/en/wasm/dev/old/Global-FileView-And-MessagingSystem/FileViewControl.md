# `FileView` Control Specification

Parent document: [`../Global-FileView-And-MessagingSystem.md`](../Global-FileView-And-MessagingSystem.md)

## Purpose

`FileView` is a single reusable Razor component that renders the contents of one virtual-filesystem
directory and lets the user browse, select, rename, and rearrange its entries. It is the one control
every file-listing surface in HackerOS is meant to build on:

- `HackerOs.Apps.FileExplorer` hosts it today's Details/Grid/(new) Tree area, wrapped in its own
  toolbar, breadcrumb bar, address bar, and dialogs (properties, open-with) — none of which move into
  `FileView` itself; those stay host chrome.
- The Desktop background is a **future** host (explicitly out of scope for the current integration
  phase — see [`../Global-FileView-And-MessagingSystem.md`](../Global-FileView-And-MessagingSystem.md#key-decisions-already-made)):
  it would host a chromeless `FileView` in Icons mode over `/home/{user}/Desktop`.
- Any future app that needs to show or pick files (an upload dialog, an archive-contents viewer, a
  package manager's file browser) hosts it instead of reimplementing listing/selection/rename/drag-drop.

## Project & dependencies

New Razor class library `Shared/HackerOs.AppSdk.FileView/HackerOs.AppSdk.FileView.csproj`, following the
existing `HackerOs.AppSdk.Blazor` pattern (see [`../blazor-app-sdk.md`](../blazor-app-sdk.md)):

- References `HackerOs.App.Abstractions` (for `VirtualPath`, `AppCapabilities`), `HackerOs.AppSdk`
  (for `IAppExecutionContext`/gateway types), `HackerOs.AppSdk.Icons` (for `IIconCatalog`/`HackerIcon`,
  the vector half of icon rendering), and `HackerOs.Simulation.Abstractions` (for
  `IAppFileSystemGateway`, `FileSystemEntrySnapshot`, `FileSystemDirectorySnapshot`, and the topic/watch
  contracts from [`MessagingSystem.md`](MessagingSystem.md)).
- Does **not** reference `HackerOs.Platform.Core` or any concrete platform implementation —
  `FileView` only ever talks to gateway interfaces handed to it by its host, exactly like every other app
  boundary in this codebase (see `app-execution-context.md`).
- May reference `MudBlazor` for the Details table (sortable columns) and context-menu rendering, per
  [`../platform-ui-library.md`](../platform-ui-library.md) — complex grid/menu surfaces are the
  approved use case for it.

`FileView` never constructs or resolves an `IAppFileSystemGateway`/`IAppEventGateway` itself; the host
app passes its own already-scoped gateways in as parameters. This means `FileView` runs under **exactly**
the capabilities/authority already granted to its host app — it cannot see or do more than the host that
embeds it could already do directly. A file-open dialog embedding a chromeless `FileView` over a
selected-handle-scoped gateway is therefore automatically constrained to that handle, with no special
casing inside `FileView`.

## Public parameters (host-facing, declarative)

| Parameter | Type | Purpose |
| --- | --- | --- |
| `FileSystem` | `IAppFileSystemGateway` (required) | Scoped filesystem access supplied by the host. |
| `Watch` | `IFileSystemWatchGateway?` | Optional — when supplied, `FileView` subscribes to live directory-change notifications (see [`MessagingSystem.md`](MessagingSystem.md#filesystem-watch-api)) instead of relying solely on `Refresh()`. |
| `InitialDirectory` | `VirtualPath` | Directory shown on first render. |
| `Mode` | `FileViewMode` | `Tree`, `Details`, or `Icons`. Two-way bindable (`@bind-Mode`). |
| `FolderActivation` | `FileViewFolderActivationMode` | `Navigate` (default), `NewWindow`, or `Custom` — see [Folder double-click behavior](#folder-double-click-behavior). |
| `OnCustomFolderActivate` | `Func<FileViewItem, Task>?` | Required when `FolderActivation == Custom`; invoked instead of navigating/launching. |
| `IconProvider` | `IShellIconProvider?` | Overrides the default DI-registered icon provider for this instance only. |
| `ContextMenuProviders` | `IReadOnlyList<IFileViewContextMenuProvider>` | Ordered customizers applied to every generated context menu; see [Context menu customization](#context-menu-customization). |
| `AllowMultiSelect` | `bool` | Default `true`. |
| `AllowDragDrop` | `bool` | Default `true`; see [Drag & drop](#drag--drop). |
| `Columns` | `IReadOnlyList<FileViewColumn>?` | Overrides the default Details-mode column set. |
| `Filter` | `Func<FileViewItem, bool>?` | Optional predicate limiting what appears in `Items`, across all three modes (added `Phase 4`/`INT-001` — `FileExplorerWindow`'s search box needed a hook this control didn't originally have; see [External scripting surface](#external-scripting-surface)'s `RefreshFilter()`). A filtered-out item is invisible everywhere: not rendered, not selectable, not counted. Evaluated fresh on every `Items` rebuild; changing what the delegate itself would return does **not** re-render until `RefreshFilter()` is called. |

`FileView` intentionally exposes no `Toolbar`/`Breadcrumb`/`AddressBar` — those remain host
responsibility (`FileExplorerWindow` keeps its own, driving `FileView` via `CurrentDirectory`/`Navigate`).

## External scripting surface

Beyond the declarative parameters above, `FileView` exposes a plain C# imperative surface so host
code-behind can drive and observe it exactly like the [motivating
example](../Global-FileView-And-MessagingSystem.md#motivating-example) — this is why the events below are
ordinary `event EventHandler<T>` members, not only Blazor `EventCallback` parameters: a toolbar button's
click handler living in a *different* component needs to reach into a `FileView` instance by reference
(`this.fileBrowser`), the way the example does, which `[Parameter] EventCallback` alone cannot support.

```csharp
public sealed partial class FileView
{
    public VirtualPath CurrentDirectory { get; private set; }
    public IReadOnlyList<FileViewItem> Items { get; }
    public FileViewItem? SelectedItem { get; }
    public IReadOnlyList<FileViewItem> SelectedItems { get; }

    public Task NavigateAsync(VirtualPath path, CancellationToken cancellationToken = default);
    public Task RefreshAsync(CancellationToken cancellationToken = default);
    public void RefreshFilter(); // re-applies Filter to the last fetched listing, no filesystem round-trip
    public void ClearSelect();
    public void SelectByName(string fileName);
}
```

`FileViewItem` (the per-row/per-tile/per-node model, shared across all three view modes):

```csharp
public sealed class FileViewItem
{
    public VirtualPath FullPath { get; }
    public string FileName { get; }
    public bool IsDirectory { get; }
    public FileSystemEntryMetadata Metadata { get; }

    /// <summary>Per-item icon override; set to bypass <see cref="IShellIconProvider"/> for this item.</summary>
    public ShellIconDescriptor? IconOverride { get; set; }

    public bool IsSelected { get; }
    public bool IsRenaming { get; }

    public void Select(bool additive = false);
    public void Deselect();

    /// <summary>Puts this item into inline rename mode (label becomes a textbox). No dialog is shown.</summary>
    public void Rename();
}
```

## View modes

`FileViewMode` is `Tree`, `Details`, or `Icons`. All three modes render from the same `Items`/selection
state — switching `Mode` does not lose selection or scroll position further than necessary.

- **Details**: a sortable table (`FileViewColumn` per column: `Key`, `Header`, `SortAccessor`,
  optional fixed `Width`). Default columns: Name, Type, Size, Modified — matching
  `FileExplorerState`'s existing sort keys today, so migrating `FileExplorerWindow` (see
  `integrationPlan.md`) is a like-for-like swap. The `Type` column reads `"Folder"` for directories and
  `".ext File"` (or plain `"File"` with no extension) otherwise — renamed and reformatted from the
  originally planned `Kind` column on 2026-08-19 (`8cd6f4c`) to read better once real data was on
  screen. Clicking a header toggles ascending/descending sort; the active sort column/direction is
  exposed as `SortColumn`/`SortDescending` so a host can persist it. Independent of the selected sort,
  folders always group before files — the sort only orders within each group, matching common
  file-explorer UX (also added 2026-08-19).
- **Icons**: a wrapping grid of tiles, icon-above-label, matching the existing "Grid" mode visually but
  renamed to `Icons` to match the request's terminology. High-density and multi-select via marquee
  drag are in scope; label wrapping/truncation follows `../design-system.md` tokens.
- **Tree**: new. Each directory node is lazily expandable (children fetched via `FileSystem.EnumerateAsync`
  only on first expand, then cached and kept live by the watch subscription like every other mode).
  Multi-select within Tree selects entries, not just the expansion state. Reuses the same `FileViewItem`
  and the same rename/drag-drop/context-menu machinery as the other two modes — a `FileViewItem`
  does not know or care which mode is currently rendering it.

Internally, `FileView.razor` is a thin mode switch over three collocated sub-components
(`FileViewDetails.razor`, `FileViewIcons.razor`, `FileViewTree.razor`), each with its own
`.razor.css`, sharing a common `FileViewItem`/selection/rename/drag-drop backing implemented once in
`FileView.razor.cs` so the three renderers never duplicate behavior — only markup/layout differs.

## Inline rename

Calling `item.Rename()` (imperatively, or via the default context-menu "Rename" item, or a keyboard
shortcut such as `F2`) sets `FileViewItem.IsRenaming = true`. Every renderer swaps that item's label
`<span>` for a bound `<input>` in place — **no modal dialog**. Enter/blur commits (fires
`Renaming` → `MoveAsync` with the same directory as source/destination and the new name → `Renamed`);
Escape cancels without any filesystem call. The textbox pre-selects the name without its extension for
files (matching common OS behavior), and the full name for directories.

Because rename reuses `MoveAsync` (source path → sibling path with new name, same parent), it goes
through the identical cancelable event path as a drag-drop move (see below) — a host that wants to block
renaming entirely only needs to handle one event, `Moving`, not a separate rename-specific one. A
dedicated `Renaming`/`Renamed` pair still exists (see [Cancelable events](#cancelable-eventing-model))
purely so a subscriber can distinguish "the user is editing a label in place" from "a drag-drop just
happened," which matters for UI feedback even though the underlying filesystem operation is identical.

## Drag & drop

`AllowDragDrop="true"` (default) enables:

- **Intra-control drag**: dragging a selection onto a directory row/tile/node in the *same* `FileView`
  moves the selection into that directory (Ctrl held = copy).
- **Inter-control drag**: dragging out of one `FileView` and dropping onto another (e.g. two
  `FileExplorer` windows) — the drop target only needs to be a `FileView`; it does not need to know the
  source. The drag payload reuses the existing, currently-unused
  `Shared/HackerOs.AppSdk/DragDrop/VirtualFileDragPayload.cs` records
  (`VirtualFileDragPayload`/`VirtualFolderDragPayload`), serialized into the HTML5 `DataTransfer` as
  `application/json` under a stable custom MIME type (`application/vnd.hackeros.file-drag+json`) so a
  drop handler can distinguish a HackerOS-internal drag from an OS-level file drag before attempting to
  parse it.
- **External OS file drop** (dragging a real file from the host operating system into a `FileView`) is
  explicitly **out of scope** for this control version; it is a natural, separately-scoped follow-up once
  the filesystem gateway has an upload path that accepts a browser `File`/`Blob` (FileExplorer's existing
  upload button is the current, separate mechanism for that).

Interop is component-scoped JavaScript (`FileView.razor.js`, per this repo's collocated-asset rule) doing
only what C# cannot: reading/writing `DataTransfer`. All decision logic (is this a valid drop target? copy
or move? which entries?) lives in C#.

Every drop first raises the cancelable `Moving`/`Copying` event (see below) before touching the
filesystem, so a host can veto (e.g. "don't allow drops into a read-only mounted directory") without
`FileView` needing to know why.

## Icon resolution (`IShellIconProvider`)

Per the [confirmed decision](../Global-FileView-And-MessagingSystem.md#key-decisions-already-made),
`FileView` never decides icons itself — it asks an injected `IShellIconProvider`:

```csharp
public interface IShellIconProvider
{
    ShellIconDescriptor Resolve(FileViewIconRequest request);
}

public readonly record struct FileViewIconRequest(
    VirtualPath Path,
    bool IsDirectory,
    string? Extension,
    string? MediaType);

public enum ShellIconKind { Vector, Png, Custom }

public sealed record ShellIconDescriptor(ShellIconKind Kind, string? LibraryOrPath, string? Name)
{
    public static ShellIconDescriptor Vector(IconLibrary library, string name) => new(ShellIconKind.Vector, library.ToString(), name);
    public static ShellIconDescriptor Png(string assetPathOrUri) => new(ShellIconKind.Png, assetPathOrUri, null);
}
```

- The default `IShellIconProvider` (`Platform`-registered, DI singleton, same registration pattern as
  `IIconCatalog` today) maps well-known extensions to `HackerOs.AppSdk.Icons` Lucide icons (folder, generic
  file, and a curated per-extension table: `.txt`→`file-text`, `.zip`→`file-archive`, `.exe`→`app-window`,
  etc.), and falls back to a generic file/folder icon for anything unrecognized. This mirrors — and
  replaces — `FileExplorerWindow`'s current hardcoded two-icon `if`.
- A **PNG/raster** icon is just as valid a return: `ShellIconDescriptor.Png(...)` points at an asset path
  or data URI; a dedicated small renderer component (`ShellIcon.razor`, alongside `FileView` in the same
  package so any host can use it standalone) switches on `ShellIconDescriptor.Kind` and renders either
  `<HackerIcon Library="..." Name="..." />` (vector) or `<img>` (PNG/raster), so callers never need to
  branch on icon kind themselves.
- **Per-item override**: `FileViewItem.IconOverride` is checked before calling `IShellIconProvider` at
  all — this is exactly what the motivating example's `item.IconOverride = ShellIconDescriptor.Png(...)`
  inside an `OnPathChange` handler relies on: external code can repaint icons for its own custom file
  types (`.hack` in the example) without `FileView`, or the shell provider, ever needing to know that
  extension exists.
- **Per-instance override**: the `IconProvider` parameter lets one `FileView` (e.g. an Icon Viewer-style
  preview surface) use a completely different provider without touching global DI.
- This design leaves room for a later, genuinely OS-shell-backed provider (extracting real Windows/macOS
  icons via JS interop) to be swapped in as an alternate `IShellIconProvider` implementation without any
  `FileView` change — the interface boundary is exactly where that future work plugs in, even though
  building it is explicitly not part of this task.

## Folder double-click behavior

`FolderActivation` (default `Navigate`) controls what happens when a directory item is activated
(double-click, Enter key, or the context menu's default action):

1. **`Navigate`** (default): `FileView` calls `NavigateAsync` on itself — no new window, no intent
   dispatch. This is what `FileExplorer` uses today and keeps using.
2. **`NewWindow`**: `FileView` does **not** open a window itself. It calls
   `IAppIntentGateway.OpenFileAsync(path, mediaType: "inode/directory")` on the host-supplied intent
   gateway and lets file-association resolution (`FileAssociationResolver`, per
   `../app-intents-and-associations.md`) decide which app opens the folder — "let the Shell manage it,"
   per the original request. See `integrationPlan.md` for the manifest/media-type work this requires;
   `FileView` itself only needs an `IAppIntentGateway` parameter and this one call. Today that resolves to
   `HackerOs.Apps.FileExplorer` once it declares itself as the directory handler; nothing about `FileView`
   hardcodes that app ID.
3. **`Custom`**: `FileView` calls `OnCustomFolderActivate(item)` instead of doing anything itself. The
   host is fully responsible for what "opening" a folder means (e.g. a custom explorer step in a wizard).

Every case still raises the cancelable `Opening` event first (see below) regardless of which activation
mode is configured, so a host can veto activation universally without caring which mode is active.

## Cancelable eventing model

Every action that mutates state or navigates raises a cancelable "-ing" event synchronously before doing
anything observable, followed — only if not canceled — by an informational "-ed" event after the action
completes. This is the literal `e.Cancel = true` pattern requested. Every "-ing" args type derives from:

```csharp
public abstract class FileViewCancelEventArgs : EventArgs
{
    public bool Cancel { get; set; }
}
```

| Before (cancelable) | After (informational) | Raised when |
| --- | --- | --- |
| `Navigating` (`FileViewNavigatingEventArgs { VirtualPath From, VirtualPath To }`) | `OnPathChange` (`FileViewPathChangedEventArgs { VirtualPath Path }`) | `NavigateAsync` is called, including via folder activation in `Navigate` mode. `OnPathChange` is named to match the motivating example exactly. |
| `Opening` (`FileViewOpeningEventArgs { FileViewItem Item, FileViewFolderActivationMode Mode }`) | `Opened` | Any item activation (double-click/Enter/context-menu default), before mode-specific handling (navigate/launch/custom) runs. |
| `Renaming` (`FileViewRenamingEventArgs { FileViewItem Item, string ProposedName }`) | `Renamed` | Inline rename commit, before the underlying `MoveAsync`. |
| `Creating` (`FileViewCreatingEventArgs { VirtualPath ParentDirectory, string Name, FileSystemEntryKind Kind }`) | `Created` | A create action initiated through `FileView`'s own UI (e.g. context-menu "New Folder"). Programmatic creation via the host's own `IAppFileSystemGateway` call (as in the motivating example's toolbar button) does **not** go through this event — it's outside `FileView` entirely, which is why that example calls `fileSystem.CreateFileAsync` directly and only asks `FileView` to `ClearSelect()`/select/`Rename()` afterward. |
| `Deleting` (`FileViewDeletingEventArgs { IReadOnlyList<FileViewItem> Items }`) | `Deleted` | Delete action from `FileView`'s own context menu or keyboard shortcut. |
| `Moving` (`FileViewMovingEventArgs { IReadOnlyList<FileViewItem> Items, VirtualPath Destination }`) | `Moved` | Drag-drop move and inline rename (see above). |
| `Copying` (`FileViewCopyingEventArgs { IReadOnlyList<FileViewItem> Items, VirtualPath Destination }`) | `Copied` | Drag-drop copy (Ctrl-modified drop). |
| `SelectionChanging` (`FileViewSelectionChangingEventArgs { IReadOnlyList<FileViewItem> ProposedSelection }`) | `SelectionChanged` | Any selection change, including programmatic `Select()`/`ClearSelect()`. |
| `ContextMenuOpening` (`FileViewContextMenuOpeningEventArgs { FileViewContextMenuScope Scope, FileViewItem? Item, FileViewMenuItemCollection Items }`) | *(none — the menu simply opens or doesn't)* | Right-click/context-menu key, after provider customization has already run (see below), so a host-level handler sees and can still adjust the final item list. |

All events are plain `public event EventHandler<TArgs>?` members, dispatched synchronously in
subscription order (matching `IEventBus`'s existing exception-isolation convention — one throwing
handler must not prevent the rest from running or corrupt `FileView`'s own state).

## Context menu customization

There is no reusable `ContextMenu` component in the codebase today; this control introduces the first
one, `Shared/HackerOs.AppSdk.FileView`'s `FileViewContextMenu.razor` (MudBlazor `MudMenu`-backed, per the
existing `FileExplorerWindow` pattern, just extracted and made generic).

Customization is provider-based, not markup-based, so a host app can compose menu changes without owning
`FileView`'s markup:

```csharp
public enum FileViewContextMenuScope { Background, Directory, File, FileType }

public interface IFileViewContextMenuProvider
{
    /// <summary>Which scope this provider applies to; for <see cref="FileViewContextMenuScope.FileType"/>,
    /// <see cref="Matches"/> is also consulted per extension/media type.</summary>
    FileViewContextMenuScope Scope { get; }

    bool Matches(FileViewItem item); // ignored for Background/Directory/File scopes

    /// <summary>Mutates the in-progress item collection: insert, reorder, remove, or fully replace.</summary>
    void Customize(FileViewContextMenuContext context, FileViewMenuItemCollection items);
}
```

`FileViewMenuItemCollection` is an ordered list of `FileViewMenuItem { string Id, string Label,
ShellIconDescriptor? Icon, Func<Task> OnActivate, bool IsSeparatorBefore }`, with every **default** item
(Open, Rename, Delete, Cut/Copy/Paste, Properties, New ▸, ...) given a stable, documented `Id`
(`"open"`, `"rename"`, `"delete"`, ...) specifically so a provider can locate and insert relative to it —
`items.InsertAfter("open", new FileViewMenuItem("unzip-here", "UnZip Here…", ..., OnUnzip))` — or call
`items.Clear()` first to replace the menu wholesale.

Providers are supplied via `FileView.ContextMenuProviders` and are evaluated in list order for the
`Background` (right-click on empty space), `Directory`, generic `File`, then every matching `FileType`
provider, before `ContextMenuOpening` fires as one final, host-level veto/adjustment point. This exact
sequence realizes the request's own example: a host registers one `FileType` provider matching `.zip`
that inserts a `"UnZip Here…"` item immediately after the default `"open"` item.

## Accessibility & keyboard

Follows the existing repo-wide bar (`../accessibility.md`, WCAG 2.2 AA): full keyboard navigation
(arrow keys move focus/selection in all three modes, `Enter` activates, `F2` renames, `Delete` deletes,
`Ctrl+A` selects all), visible focus indicators, and ARIA roles appropriate to each mode (`role="tree"`
for Tree, `role="grid"` for Details, `role="listbox"` for Icons).

## Explicit non-goals (this control version)

- External OS file drag-in (see [Drag & drop](#drag--drop)).
- Literal host-OS shell icon extraction (see [Icon resolution](#icon-resolution-ishelliconprovider)).
- Any toolbar/breadcrumb/address-bar chrome — host responsibility.
- Multi-pane/split view, thumbnails/previews, and column customization persistence — not requested; can
  layer on top of `Columns`/`FileViewColumn` later without a breaking change.
