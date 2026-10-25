# ADR 0149: SDK 2.0, removal of `HackerOs.AppSdk.FileView` and file contracts

## Status

Accepted on 2026-10-19.

- **Benoit's decision:** plan [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)
  approved on 2026-10-19, question Q5 ("SDK 2.0 now: `FileView` is
  removed; 1.x apps are refused and must be recompiled").
- **Amends:** ADR 0114 (SDK 1.0 freeze): the list of covered packages loses
  `.FileView`; the 1.0 compatibility fixture becomes a proof of refusal.
- **Takes over:** the additions pending for 1.3, `PublishAsync` (ADR 0145)
  and `HackCrossOriginIsolation` (ADR 0146), which ship in 2.0.

## Context

SDK 1.x promises that an app compiled against `1.x` runs on any OS `1.y`
(ADR 0114 §2). `HackerOs.AppSdk.FileView` has been part of it since the freeze. Removing
it in favor of the `hackeros.file-view` contract (ADR 0148) is a break,
therefore a major version. The OS already refuses an app of another major version (ADR 0098 §3).

## Decision

### 1. Version

`build/Versions.props`: `HackerOsVersion` = `2.0.0`, published by
work item 6 of the plan, once work items 1 to 5 are merged. Until then, the
additions stay in `PublicAPI.Unshipped.txt` and the removals are written there
`*REMOVED*` (ADR 0114 §1).

### 2. Removed from the SDK

- The entire `HackerOs.AppSdk.FileView` package: `FileView`, `FileViewTree`,
  `FileViewDetails`, `FileViewIcons`, `FileViewBreadcrumb`,
  `FileViewDropZone`, `FileViewItem`, the menus
  (`IFileViewContextMenuProvider` and its types), `IShellIconProvider`,
  `ShellIcon`, `ShellIconDescriptor`, and `FileTransfer` in its class
  form.
- Its public types are written `*REMOVED*`. The code becomes the
  **internal implementation** of the file explorer
  (`Apps/System/HackerOs.Apps.FileExplorer`), which is not covered by the
  promise (ADR 0114 §2).
- 2.0 keeps no `[Obsolete]` type for the transition: as with
  1.0, an app recompiles against the new surface.

### 3. Added to the SDK

| Type | Role | ADR |
|---|---|---|
| `IHackFileView`, `HackFileView`, `HackFileViewOptions`, events | `hackeros.file-view` contract | 0148 |
| `IHackFileOperations` | Copy, move, move to trash, import, Nautilus-style name conflicts; implemented by the explorer so that these rules live in only one place | 0148 |
| `IHackContextMenu`, `HackContextMenu`, `HackMenuItem`, `HackMenuGroup`, `Menus.Attach`, `Menus.Define` | OS context menu | 0150 |
| `[RegisterFileContextMenu]`, `RegisterFileContextMenuContext`, `FileMenuScope`, `MenuSelection`, `HackFileMenuItems` | Menu entries registered by apps | 0151 |
| `HackIcons` (`FromImage`, `Vector`) | Description of an icon, without rendering | 0150, 0151 |

The `IHackFileOperations` and `HackIcons` types replace `FileTransfer` and
`ShellIcon` for apps. `IHackFileOperations` goes through the
`hackeros.file-dialogs` contract (ADR 0148 §2), which also carries the name
conflict dialog.

### 4. Covered packages

The table of ADR 0114 §2 becomes, for 2.0: `HackerOs.App.Abstractions`,
`HackerOs.Simulation.Abstractions`, `HackerOs.Theming.Abstractions`,
`HackerOs.AppSdk`, `.Blazor`, `.Terminal`, `.Web`, `.Web.Blazor`, `.Icons`,
`.VirtualKeyboard`, `HackerOs.Theming.Blazor`, the build contract and the
manifest format. The rest of ADR 0114 does not change.

### 5. Fixtures and proof

- `Tests/fixtures/sdk-compat` (app 1.0.0, committed `.nupkg`) stays
  as is and becomes the test of the **refusal** of a 1.x app by the catalog
  (different major version).
- A new 2.0.0 fixture is produced by the same `produce-fixture.sh`
  after publication, committed and never rebuilt; it uses
  `HackFileView` and registers a menu entry, so that the 2.x promise covers
  the new contracts.

### 6. Migration

- `CodeEditor` (side explorer) moves to `HackFileView`.
- A 1.x app from another repository replaces `FileView` with `HackFileView`,
  `IFileViewContextMenuProvider` with the controller's `ContextMenuOpening` or with
  a registered entry, `FileTransfer` with `IHackFileOperations`, then
  recompiles.
- [`sdk-packages.md`](../../framework/sdk-packages.md) gains a "SDK 2.0" section with
  the list of breaking changes and this migration table;
  [`sdk-reference.md`](../../framework/sdk-reference.md) and
  [`building-on-hackeros.md`](../../framework/building-on-hackeros.md) follow.

## Consequences

- Every installed 1.x app is refused at the next startup of OS 2.0 and
  shown as incompatible; its files are not touched.
- The base apps are recompiled with the OS, nothing changes for them.
- `PublishAsync` and `HackCrossOriginIsolation` are published in 2.0.0 and not
  in 1.3.0; no 1.3 is published.

## References

- ADR 0037, 0098, 0114, 0132, 0145, 0146, 0148, 0150, 0151.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §9,
  [`sdk-packages.md`](../../framework/sdk-packages.md).
