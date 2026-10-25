# ADR 0037: `FileView` as the Canonical File-Listing Control

## Status

Accepted on 2026-08-17.

## Context

`Apps/System/HackerOs.Apps.FileExplorer/FileExplorerWindow.razor` is the only place in HackerOS that
renders virtual-filesystem contents today, and it does so entirely inline: Details/Grid rendering,
multi-selection, sort ordering, an inline `MudMenu`-based context menu, and every file operation
(create/rename/delete/move/copy) are implemented directly in one ~1000-line component, coupled to
`FileExplorerState`. There is no Tree view. Renaming goes through a boxed dialog flow rather than
in-place editing. Icons are drawn from a hardcoded two-branch `if` (`folder` vs. `file-text`) against
`HackerOs.AppSdk.Icons`, with no per-extension table and no seam for a host to override an icon.
Drag & drop is entirely unimplemented — only the unused payload records
`Shared/HackerOs.AppSdk/DragDrop/VirtualFileDragPayload.cs` exist. There is no reusable `ContextMenu`
component anywhere in the codebase and no extension point for a host or a `.zip`-type-specific feature
to add a menu item without editing `FileExplorerWindow.razor` directly.

This monolithic shape blocks two known future needs: (1) a Desktop background that shows file icons —
explicitly a future phase, but one that should reuse the same listing/selection/rename/drag-drop behavior
FileExplorer already has, not reimplement it a second time; and (2) any future app needing a file
picker/browser surface (upload dialogs, archive-contents viewers), which today would have no choice but
to duplicate `FileExplorerWindow`'s inline logic.

The full functional and API specification this ADR accepts is
[`../Global-FileView-And-MessagingSystem/FileViewControl.md`](../old/Global-FileView-And-MessagingSystem/FileViewControl.md);
this ADR records the binding architectural commitments, not the complete spec.

## Decision

Introduce `Shared/HackerOs.AppSdk.FileView`, a new Razor class library owning one reusable `FileView`
control, as the canonical way every host renders virtual-filesystem directory contents in HackerOS,
under these binding constraints:

1. **Host-agnostic, capability-transparent.** `HackerOs.AppSdk.FileView` references only
   `HackerOs.App.Abstractions`, `HackerOs.AppSdk`, `HackerOs.AppSdk.Icons`, and
   `HackerOs.Simulation.Abstractions` — never `HackerOs.Platform.Core` or any concrete platform
   implementation. `FileView` never constructs or resolves its own `IAppFileSystemGateway`/
   `IAppFileSystemWatchGateway`/`IAppIntentGateway`; the host app passes its own already-scoped gateways
   in. `FileView` therefore always runs under exactly the capabilities/authority already granted to its
   host, with no special-casing for embedding scenarios (e.g. a file-open dialog scoped to a selected
   handle).
2. **One shared item/selection model across three view modes** — Tree, Details (sortable columns), and
   Icons — implemented as thin renderers over one common `FileViewItem`/selection/rename/drag-drop
   backing, so behavior is implemented once regardless of which mode is active.
3. **Inline rename**, never a modal dialog: the item's label is swapped for a bound textbox in place.
4. **Drag & drop** reuses the existing, currently-unused `VirtualFileDragPayload`/`VirtualFolderDragPayload`
   records as its wire payload, both intra-control (move into a subfolder) and inter-control (between two
   `FileView` instances).
5. **Icon resolution is delegated**, never hardcoded: `FileView` asks an injectable `IShellIconProvider`
   (supporting both the existing vector icon system and PNG/raster images behind one
   `ShellIconDescriptor` type) and checks a per-item `FileViewItem.IconOverride` first. Neither `FileView`
   nor any host app may hardcode per-extension icon logic going forward.
6. **Every mutating or navigating action is cancelable**: a synchronous "-ing" event with a settable
   `Cancel` flag precedes the action; an "-ed" event follows only if it was not canceled.
7. **Context-menu customization is provider-based**, not markup-based: `IFileViewContextMenuProvider`
   instances, scoped to Background/Directory/File/FileType, mutate an ordered menu-item collection whose
   default entries carry stable, documented IDs (`"open"`, `"rename"`, `"delete"`, ...) specifically so a
   provider can insert relative to them, replacing the current pattern of conditionally rendered
   `MudMenuItem`s baked into `FileExplorerWindow`.
8. **`FileExplorerWindow`'s inline Details/Grid rendering and inline context menu are deprecated** in
   favor of hosting `FileView`. `FileExplorerWindow` retains only host chrome it is uniquely responsible
   for — toolbar, breadcrumb/address bar, `OpenWithDialog`, `FilePropertiesDialog` — none of which move
   into `FileView`. This migration is tracked as Phase 4 of
   [`../Global-FileView-And-MessagingSystem/integrationPlan.md`](../old/Global-FileView-And-MessagingSystem/integrationPlan.md)
   and is not itself accepted by this ADR beyond committing to it as the intended direction; the inline
   code is deleted once the migration lands, not kept as a parallel fallback path.

## Consequences

- A new shared project and its paired `Tests/HackerOs.AppSdk.FileView.Tests` project join the solution
  and the build/test matrix.
- `FileExplorerWindow.razor` shrinks substantially once Phase 4 lands; its current inline
  listing/selection/sort/context-menu code is removed rather than retained alongside `FileView`.
- Any future file-browsing surface (Desktop icons, an upload picker, an archive-contents viewer) is
  expected to host `FileView` rather than reimplement listing/selection/rename/drag-drop — a reviewer
  should treat a new inline file-listing implementation as a regression against this decision.
- `IShellIconProvider` becomes the one sanctioned seam for a future, separately scoped literal host-OS
  icon integration; this ADR does not build one, only reserves where it would plug in.
- Extends MudBlazor usage (`ADR 0016`) into a new shared SDK project for the Details table and context
  menu — both fall within the already-approved "complex data grids/menus" carve-out, not a new exception.
- `FileView`'s live-update behavior (its `Watch` parameter) depends on the filesystem-watch API accepted
  separately in `ADR 0038`; `FileView` itself must degrade to manual `RefreshAsync` calls when no watch
  gateway is supplied, so it does not hard-depend on that ADR's implementation timeline.

## References

- [`../Global-FileView-And-MessagingSystem.md`](../old/Global-FileView-And-MessagingSystem.md) and
  [`../Global-FileView-And-MessagingSystem/FileViewControl.md`](../old/Global-FileView-And-MessagingSystem/FileViewControl.md) —
  full specification.
- [`../Global-FileView-And-MessagingSystem/integrationPlan.md`](../old/Global-FileView-And-MessagingSystem/integrationPlan.md) —
  phased implementation plan (Phase 3 builds the control, Phase 4 migrates `FileExplorerWindow`).
- ADR 0016: Platform UI Library Boundary (MudBlazor) — the complex-surface carve-out this decision relies on.
- [`../apps/file-explorer.md`](../old/apps/file-explorer.md) — current `FileExplorerWindow` architecture this
  ADR commits to changing.
- ADR 0038: Emitter-Authorized Topic Messaging — source of the filesystem-watch API `FileView`'s `Watch`
  parameter consumes.
