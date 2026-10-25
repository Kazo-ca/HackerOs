> [Version française](../../../../fr/wasm/dev/adr/0171-mobile-file-explorer-and-full-screen-dialogs.md)

# ADR 0171: mobile file explorer and full-screen dialogs

## Status

Accepted on 2026-10-25 at Benoit's request ("I would like a different version
of this application for mobile; the FileOpen, FolderOpen, etc. dialogs must
too. They must open full screen"). Complements
[ADR 0165](0165-mobile-home-drawer-and-mouse-controls.md) (mobile entry point
of system apps) and [ADR 0157](0157-file-dialogs-show-the-file-control.md)
(file dialogs). Also fixes the alignment of "File Explorer" in the Start menu.

## Context

- `AppDefinition.MobileEntryPoint` exists and the generated manifest declares
  one entry point per platform (`platform.entryPoints`), but the OS did not use
  it: the lazy loader always resolved the Desktop entry point. An app therefore
  could not have a window of its own on the phone.
- On mobile, the platform dialogs (Open, Save, Choose folder, text input,
  message, Open with, Properties, conflict) did not show at all:
  `FileDialogWindowAdapter`, which turns them into windows, was only created by
  the Desktop shell. And even when created, a dialog is born without the focus:
  the single surface of mobile showed the app and minimized the dialog.
- In the Start menu, a `<button>` centres its content. Rows whose description is
  long enough fill the row; "File Explorer"'s is the shortest, so its icon and
  text were shifted to the right.

## Decision

### 1. Entry point per platform

- Discovery keeps the main (Desktop) entry point and also resolves, in the
  host's assemblies, those the manifest declares for the other platforms:
  `AppDescriptor.PlatformEntryPointTypes` and
  `AppDescriptor.EntryPointTypeFor(platform)`. A type that cannot be found, is
  abstract or has the wrong base is ignored: the app falls back to its main
  entry point, so a variant can never keep it from starting.
- `WindowAppRenderer` creates the active platform's component on the window's
  first render and keeps it for the window's lifetime. A platform switch already
  restarts the windows whose entry point differs
  (`PlatformShellSwitchCoordinator`); nothing is swapped live.

### 2. Mobile file explorer

- `FileExplorerApp.MobileEntryPoint` is `FileExplorerMobileWindow`, which
  inherits `FileExplorerWindow`: same state, same commands, same file control
  (`HackFileView`); only the layout changes. The shared members of the Desktop
  window go from `private` to `private protected` (no public API added to the
  Desktop window).
- Layout of a phone file manager: top bar (back, folder name, search, "⋮"
  menu), Home and Trash chips, breadcrumb, list or grid. At the bottom: the
  actions on the selection (Copy, Cut, Rename, Delete, More), otherwise "Paste
  here" when the clipboard holds files, otherwise the floating "+" button (New
  folder, New file, Upload). The other commands are in bottom sheets.
- One tap opens the item (`HackFileView.OpenOnTap`, see §3). Selection mode
  ("⋮" menu ▸ Select) turns a tap into an add or a remove
  (`FileView.TapTogglesSelection`) and ends with the last item deselected. The
  double click does not open a second time. A long press still opens the OS file
  menu as a sheet (ADR 0150 §6).
- The window implements `IAppBackHandler`: the system Back button closes the
  sheet, then the search, then the selection, leaves the Trash, then goes back
  to the previous folder; after that the OS takes over. The manifest does not
  declare `supportsBack` (no Back button in the Desktop chrome).
- The Type column is hidden in the mobile list; the icon is enough.

### 3. Full-screen dialogs on mobile

- `MobileShell` also creates `FileDialogWindowAdapter`: the session's dialogs
  become windows on mobile as on Desktop.
- `SingleSurfacePresentationPolicy.SelectSurfaceStack` gives the stack shown:
  the app's window, then its modal dialogs (a dialog opened from a dialog last).
  `SelectPrimary` returns the top of the stack, even before the dialog has the
  focus. The system Back button therefore closes the dialog (step 1 of
  `MobileNavigationCommandsAdapter`).
- `SingleSurfaceArea` draws the whole stack, maximized to the screen
  (`WindowsToMaximize`); only the last window takes input, the others are
  `inert`. The owner is no longer minimized (`WindowsToMinimize`) and stays
  mounted under its dialog, so the app waiting for the answer keeps its state.
  Each window host is its own stacking context: nothing of an app (a floating
  button) draws over its dialog.
- One tap enters a folder or takes a file, with no double click (Benoit's
  choice, "for mobile only"): the `HackFileView` contract gains the `OpenOnTap`
  parameter (off by default), which `FileDialogBrowser` turns on for the mobile
  platform (`FileDialogBrowser.OpenOnTap`, `null` follows the active platform).
  The explorer passes it to `FileView.TapOpens`.
- Mobile dialogs have no title bar (Benoit's choice): they have their Cancel
  buttons and the system Back.
- Desktop does not change: dialogs keep their size and the double click there.

### 4. Start menu

- `.app-launch-button` aligns its content to the left
  (`justify-content: flex-start`) and the text takes the remaining space. Every
  row, pinned or not, has its icon at the same position.

## Consequences

- A third-party app can declare a phone-specific window with
  `MobileEntryPoint`; that is now the one the user sees.
- Public API added (unshipped, next minor): `AppDescriptor.PlatformEntryPointTypes`,
  `AppDescriptor.EntryPointTypeFor`, `SingleSurfacePresentationPolicy.SelectSurfaceStack`
  and `WindowsToMaximize`, `FileDialogBrowser.OpenOnTap`, the
  `FileExplorerMobileWindow` class and `ExplorerFileView.OpenOnTap`. In the SDK
  (compatible addition, next minor 2.1): `HackFileView.OpenOnTap`. Another
  provider of the control (ADR 0156) that ignores this parameter keeps the
  double click.
- In a dialog that accepts several files, a tap on a file takes it alone;
  Ctrl+click still selects.

## Verification

- Tests: `SingleSurfacePresentationPolicyTests` (stack, dialog without focus,
  minimized dialog, owner kept, nested dialog), `AppEntryPointDiscoveryTests`
  (mobile type kept, fallback, shared entry point), `FileViewIconsTests` (tap
  that opens, double click ignored, selection mode),
  `FileExplorerMobileWindowTests`, `FileDialogBrowserTests` (`OpenOnTap` passed
  to the control).
- Browser at 390 × 844 in mobile mode: mobile explorer, folder opened with one
  tap, "+" ▸ New folder full screen, the Text Editor's Open dialog full screen,
  one tap on "Documents" enters it, Back closes the dialog. Desktop Start menu:
  "File Explorer"'s icon at the same position as the others.
- Docs: [file-explorer.md](../file-explorer.md#mobile-explorer-adr-0171),
  [mobile-home-screen.md](../mobile-home-screen.md#full-screen-dialogs-adr-0171).
