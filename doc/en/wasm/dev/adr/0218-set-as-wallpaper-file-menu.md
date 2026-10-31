> [Version française](../../../../fr/wasm/dev/adr/0218-set-as-wallpaper-file-menu.md)

# ADR 0218: "Set as wallpaper" in the menu of images

## Status

Accepted on 2026-10-31, with the recommendations applied while waiting for
Benoit's answers (see "Questions asked to Benoit"). Issue
[#298](https://github.com/Kazo-ca/HackerOs/issues/298), `todo/base` series
(branch `todo/24-set-as-wallpaper`).

- **Completes:** [ADR 0202](0202-desktop-wallpaper-images.md) §6 (entry left
  out of the wallpaper work item),
  [ADR 0151](0151-app-registered-file-menu-entries.md) and
  [ADR 0159](0159-app-menu-entries-implementation-adjustments.md) (apps' menu
  entries).
- **Does not change:** the `appearance.config` keys of ADR 0202 §1, nor the
  Settings ▸ Appearance ▸ Wallpaper panel.

## Context

- Wallpaper images (ADR 0202) are only chosen in Settings. On Windows, macOS
  or GNOME, the menu of an image offers "Set as wallpaper".
- Apps' menu entries are declared **at build time** (ADR 0151 §2, ADR 0159
  §1): the menu does not know how many screens are open, while the issue asks
  for one submenu entry per screen on the extended desktop (ADR 0178).
- An entry's payload is `{"paths":[…],"scope":"File"}`: an app with several
  entries on the same intent does not know which one was chosen.

## Decision

### 1. A small service app declared by Settings

The `HackerOs.Apps.Settings` project declares a second app,
`org.hackeros.settings.wallpaper` ("Wallpaper"), of kind `Service`, hidden
from the launcher (`LaunchVisibility.Hidden`), run on the main thread (a
task of an instant per click: a worker would start a .NET runtime each time).
It declares the intent `org.hackeros.settings.set-wallpaper.v1` and the menu
entries. On each click, the menu launches it; it:

1. reads the path of the payload and the name of the chosen entry (§3);
2. reads the file **as the session user** and checks, like the shell
   (ADR 0202 §3), that it is 16 MB at most and that its first bytes are those
   of a PNG, a JPEG or a WebP;
3. writes the key in the **user layer** of `appearance.config` (`wallpaper`
   for "screen 1 and the others", `wallpaperScreen<n>` for screen n); the
   shell follows the file and changes the background at once;
4. posts a "Wallpaper changed" notification (or the error: key locked by the
   administrator, unreadable file, not an image), then stops.

No window opens (Q1). The path is kept as it is, like "Browse…" (ADR 0202 Q2).

### 2. Screen filter of menu entries

Two optional fields are added to the `contextMenus` entries:

| Field | Values | Meaning |
| --- | --- | --- |
| `screens` | `single`, `multiple` | The entry only appears with a single open screen, or with two or more. Absent: always. |
| `screen` | 2 to 9 | The entry only appears when screen n is open. |

The SDK sets them after `Register`:
`RegisterFileContextMenuContext.RestrictToScreens(name, MenuScreens.Single | Multiple)`
and `RestrictToScreen(name, n)`. The manifest validator refuses another
value, a number outside 2 to 9 and an entry that has both fields. The OS
counts screen 1 and the open secondary screens (`ScreenService`). A parent
whose children are all filtered out disappears (ADR 0150 §3), as for the
other filters. The Settings ▸ File Menus setting lists these entries like the
others.

### 3. The entry's name in the payload

The payload becomes `{"paths":[…],"scope":"File","entry":"<name>"}`.
`HackFileMenuIntent.ReadEntry(payload)` reads it (`null` when missing).
Existing apps ignore the field; an app can thus bind several entries to a
single intent.

### 4. Settings' entries

For `.png`, `.jpg`, `.jpeg`, `.webp`, `image/png`, `image/jpeg`,
`image/webp`, `File` scope, a single image (`selection: single`), placed
right after "Open with":

- `set-wallpaper` "Set as wallpaper" (`screens: single`);
- `set-wallpaper-on` "Set as wallpaper" (`screens: multiple`), submenu:
  - `set-wallpaper-all` "Screen 1 and the others" (key `wallpaper`);
  - `set-wallpaper-screen-2` to `set-wallpaper-screen-9` "Screen n"
    (`screen: n`, key `wallpaperScreen<n>`).

They appear wherever the OS draws the file menu: File Explorer, desktop,
file dialogs (ADR 0159 §4).

## Consequences

- The SDK gains `MenuScreens`, `RestrictToScreens`, `RestrictToScreen` and
  `HackFileMenuIntent.ReadEntry` (in `PublicAPI.Unshipped.txt`, next minor);
  `AppContextMenuEntryManifest` gains `Screens` and `Screen`.
- The Settings project declares two apps: its
  `Resources/AppManifest*.resx` keys are prefixed with the app's id
  (ADR 0159 §3).
- The mobile keys (`mobileWallpaper`, [ADR 0217](0217-mobile-home-wallpaper.md)) are not written: the SDK does not tell an app which interface is active. The mobile image is chosen in Settings.
- An installed app that sets `screens` or `screen` on an older OS shows its
  entries everywhere (field ignored): harmless, the entry still works.

## Alternatives rejected

- **Open Settings** on every click (Q1 B): interrupts the user for a
  one-click action.
- **Nine intents**, one per screen: clutters the manifest; the entry's name in
  the payload serves every app.
- **A fixed submenu of nine screens** (Q2 C): cluttered menu with a single
  screen.

## Questions asked to Benoit

Asked on 2026-10-31 in the "Définir une image comme fond d'écran" thread, with
the recommendation applied while waiting for his answer:

- **Q1** What the click does: A (recommended) applies without a window and
  confirms with a notification; B opens Settings ▸ Appearance ▸ Wallpaper with
  the image applied.
- **Q2** Choosing the screen: A (recommended) a screen-count filter in the
  entry model, submenu only with several screens; B a single entry only
  (screen 1 and the others); C a fixed submenu of nine screens.

## Tasks

- [x] `screens` and `screen` fields, validator, generator (`RestrictToScreens`, `RestrictToScreen`).
- [x] Screen filter in `FileMenuEntries` (count and numbers of the open screens).
- [x] `entry` in the payload, `HackFileMenuIntent.ReadEntry`.
- [x] `org.hackeros.settings.wallpaper` service app and its entries.
- [x] Tests: validator, registration context, filter, payload, service.
- [x] Docs fr/en (`wallpapers.md`, `context-menu.md`), `features.md`, change entry.
