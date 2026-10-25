# ADR 0165: mobile home, drawer and mouse aids

## Status

Accepted on 2026-10-23 at Benoit's request. Completes the archived mobile plan
for the launcher and the home screen, while keeping the Back/Home/Recents contracts.
Replaces the exclusion of mobile icons from [ADR 0152 §7](0152-desktop-icons.md#7-mobile):
the home screen now has app shortcuts. The explorer's Desktop mode on
`~/Desktop` remains reserved to the desktop.

## Decision

- `MobileShell` displays a home screen when no window is in the foreground.
  A dock precedes the system navigation. The Android Home circle, the iOS
  central handle or the dock open the drawer by swiping up; a
  dock button remains an explicit alternative.
- The drawer displays the windowed apps that are visible, enabled and mobile-compatible.
  The fourteen windowed system apps declare the same component as the
  desktop and mobile entry point through `MobileEntryPoint`; a desktop-only
  third-party app is not launched by bypassing its manifest.
  It allows search, launch, adding to a page and pinning to the dock. Touching
  an open app restores its instance rather than creating another window.
- Pages scroll by touch, stylus or mouse. Editing opens through a
  button, long press or right click. It allows reordering, transfer between
  pages, adding/removing pages and removing shortcuts without uninstalling.
- The VFS file `~/.config/hackeros/.mobile/layout.json` is specific to the user.
  It contains `schemaVersion: 1`, `pages` (ordered arrays of IDs) and `dock`.
  It is a standard hidden folder: the explorer hides names starting
  with a dot, with an explicit command to show hidden files
  per window. This command reuses the provider's filtering contract.
  The mobile home screen presents only apps.
- Limits: 24 pages, 20 apps per page, 5 in the dock. No duplicates between pages;
  an app can be both in the dock and on a page. Unavailable IDs
  are kept so they can find their place again after reactivation.
- An editing action writes an atomic replacement of the file. Writes
  are serialized, never triggered on every movement. Invalid data
  stays intact on read; a save failure keeps the last valid projection.
- `mobile.config` is a layered OS document registered in the SDK registry.
  `mouseHelpers=true` by default shows arrows and tips. Hiding them does not
  disable the mouse gestures or the drawer's accessible commands.
- The logic stays in C#. The RCL module `wwwroot/mobileGestures.js` shares
  Pointer Events capture, long press and size observation. It transmits
  a delta on release and suppresses the stray click of a gesture, letting
  through a simple click on the top handle and the panel's clicks.
- The shells share `WindowProcessObserver`: launches through intent or
  file association are also presented on mobile, and the windows of
  stopped processes are removed. No server dependency or new SDK contract.

## Verification

See [mobile-home-screen.md](../mobile-home-screen.md): format, architecture,
usage, persistence/navigation tests and Android/mouse, iOS/touch scenarios.
