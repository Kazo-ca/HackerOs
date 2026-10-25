# ADR 0154: adjustments to the OS context menu

## Status

Accepted on 2026-10-20.

- **Work item:** `desk/2-context-menu` (work item 2 of the plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §11).
- **Adjusts:** ADR 0150 §1, §4, §5 and §6 on implementation points;
  its decisions remain true.

## Context

ADR 0150 decides a single multi-level menu provided by the platform, which
apps attach to their elements through `Menus.Attach`. A few details
were not fixed or could not be as stated once the code was
written on the contract host of work item 1 (ADR 0153).

## Decision

### 1. The provider registers, the shell draws

`HackContextMenu` is a `HackContractComponent<IHackContextMenu>`; the
platform provides the contract through `HackPlatformComponent`
(`PlatformContextMenu`). The provider rendered in place of the menu in the window
**draws nothing**: it registers the menu in the window and returns the controller.
The open menu is drawn by `ContextMenuOverlay`, a single one per shell
(`DesktopShell`, `MobileShell`), above all windows: this is what
guarantees "a single menu open in the whole OS" and positioning in the
browser window, not in the app's.

The SDK itself collects the entries from the markup (`HackMenuItem`,
`HackMenuGroup` in cascade) and passes the provider only a builder
(`Build`), the name, the accessible name and `Opening`. A missing menu shows
nothing in its place (`HackContractComponent.DefaultUnavailable`, new hook);
the OS already writes the error to the log (ADR 0153 §6).

### 2. One scope per window

`IHackMenuHost`, an SDK interface implemented by the platform
(`ContextMenuScope`), is given in cascade by `WindowAppRenderer`, **one per
window**, destroyed with it (menus, attached objects, open menu). The uniqueness
of a menu's name therefore holds per window. `Menus` (type `HackMenus`) exists on
`WindowAppBase` **and** on `HackComponentBase`: `WindowAppBase` does not derive from
`HackComponentBase`. Outside a window (unit test), `Menus.Attach` returns no
attribute and nothing opens.

### 3. Tokens and release

The same object attached again receives the same token (by reference for an
object, by value for a string or a value type, recreated at each render):
redrawing a list does not make the table grow. The tokens of elements
leaving the page are reported by the script (observation of removals, checked
one second later so as not to forget an element redrawn or moved in
a detached window); closing the window empties everything.

### 4. The script is the component's own

The script of ADR 0150 §5 is `ContextMenuOverlay.razor.js`, a module specific to the
component (AGENTS.md, "Component-scoped"), imported by the C#, rather than a
`wwwroot` file. It respects a `contextmenu` already canceled by an app
(the current `MudMenu` of `FileView` thus stays intact until work item 3),
leaves the browser menu in an unattached text field, and handles
only the desktop document: a detached window (ADR 0078) keeps the
browser menu. The `data-hack-menu-suppress` attribute marks an OS area that
has its own menu (`HackMenuAttributes.Suppress`).

### 5. Order and focus

- An action runs **after** the menu closes and focus returns to the
  original element, so that a dialog or in-place rename it opens
  keeps the focus. A press outside the menu does not give focus back (the user
  put it elsewhere) and continues to its target, as in Windows.
- The highlight is announced through `aria-activedescendant` on the panel that has
  the focus, rather than by focus moved from entry to entry.
- Opened with the keyboard, the menu highlights its first entry; opened with the
  mouse or a finger, none.

### 6. Errors

A menu whose builder, `Opening` or check fails opens nothing;
an action that throws is caught. Both are written to `/var/log/syslog`
by `ILogger<ContextMenuService>`; the shell continues.

### 7. Razor syntax

An attribute value that contains quotes is written with `@(…)`:
`@attributes="@(Menus.Attach("rows", row))"`, `OnClick="@(item => Export(item, "csv"))"`.
The examples of ADR 0150 and of the plan without `@(…)` do not compile as is.

### 8. Icons

`HackIcon` and `HackIcons` live in `HackerOs.AppSdk.Icons` (next to
`IconLibrary`); `HackerOs.AppSdk.Blazor` now references this package.

## Consequences

- Work item 3 builds the file control's menus on this model and
  removes the `MudMenu` from `FileView`; work item 5 creates the desktop scope
  (`ContextMenuService.CreateScope(null)`) and marks its areas.
- New unshipped APIs (`PublicAPI.Unshipped.txt`) in `AppSdk.Icons`,
  `AppSdk.Blazor` and `Platform.Blazor`, published with SDK 2.0 (work item 6).

## References

- ADR 0063, 0078, 0148, 0150, 0153.
- [`context-menu.md`](../../framework/context-menu.md),
  [`embedded-components.md`](../../framework/embedded-components.md).
