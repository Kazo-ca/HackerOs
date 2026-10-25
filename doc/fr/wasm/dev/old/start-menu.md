# Start Menu and Quick Launch

## Purpose

The HackerOS desktop Start menu uses a Windows 7-inspired two-column layout
while retaining HackerOS styling and platform contracts. The left column is an
application launcher with ordered per-user quick-launch pins, search, and
catalog categories. The right column provides stable shortcuts to File Explorer
and Settings. All launches continue through the desktop shell callback; the
menu never creates a process or a window directly.

## Architecture

### UI and launch projection

`Platform/HackerOs.Platform.Blazor/Shell/AppLauncher.razor` owns the interaction
surface and its collocated CSS/code-behind. `LauncherAppProjection` builds the
visible list from trusted runtime state. An item is launchable only when all of
the following are true:

- its manifest kind is `Window`;
- its launch visibility is `Visible`;
- the app is currently enabled;
- its supported platforms include Desktop;
- its name, description, identifier, or presentation category matches the
  active search/category filters.

Categories come from `AppManifest.Presentation.Category`; they are not compared
to `AppKind`. This distinction fixes the former empty System/Utilities/Games
tabs. File Explorer (`org.hackeros.file-explorer`) and Settings
(`org.hackeros.settings`) are resolved through the same projection and invoke
the same `OnAppSelected` callback as every other item.

### Per-user pin persistence

`StartMenuPreferencesService` in `HackerOs.Platform.Core` maintains an ordered,
immutable snapshot for each `LocalUserId`. It persists one OS-owned settings
document because settings definitions are registered before a user exists:

```json
{
  "schemaVersion": 1,
  "profiles": {
    "0123456789abcdef0123456789abcdef": {
      "pinnedAppIds": [
        "org.hackeros.file-explorer",
        "org.hackeros.terminal"
      ]
    }
  }
}
```

The canonical path is `/etc/hackeros/start-menu.json`. The document is
device-local (`SyncEligible: false`), system-authority-only, strictly validated,
and limited to 64 unique reverse-domain app IDs per profile. Empty is a valid
choice and is distinct from an uninitialized profile.

The service deliberately does not depend on `AppCatalog`. It preserves a valid
ID when an app is disabled, absent from a build profile, or temporarily
unavailable; the UI hides that pin until the app becomes launchable again.
Writes are optimistic for responsive UI and reconcile with the canonical
document if a revision conflict or rejection occurs.

## Usage

The Ecosystem host registers `StartMenuSettingsDocuments.CreateDefinition()`
and a scoped `StartMenuPreferencesService`. A shell component uses the active
session's opaque `LocalUserId`:

```csharp
await preferences.InitializeAsync(cancellationToken);
IReadOnlyList<string> pins = preferences.GetPinnedAppIds(userId);

await preferences.PinAsync(userId, "org.hackeros.terminal", cancellationToken);
await preferences.MoveAsync(userId, "org.hackeros.terminal", 0, cancellationToken);
await preferences.UnpinAsync(userId, "org.hackeros.terminal", cancellationToken);
```

`PinAsync`, `UnpinAsync`, `ToggleAsync`, and `MoveAsync` return `true` only when
a mutation was committed. Consumers subscribe to `Changed` and unsubscribe or
dispose through their normal Blazor lifecycle.

## Interaction and accessibility

- The taskbar launcher button exposes `aria-haspopup` and `aria-expanded`.
- Escape closes the menu; arrow keys move through the current launch list;
  Enter launches the active item; `aria-activedescendant` mirrors selection.
- Pin controls are native buttons with an accessible name and `aria-pressed`.
- Ordered pins can move up/down without drag-and-drop, preserving keyboard and
  touch access.
- The menu closes after a successful launch and when its backdrop is selected.
- A narrow viewport collapses the two columns without hiding quick links or
  search.

## Key decisions

- Pins store app IDs only, never display names, icon markup, or executable
  launch data.
- The shell owns pin mediation and user isolation; ordinary apps cannot edit
  the aggregate protected document.
- No default pins are silently reintroduced after a user removes every item.
- Catalog filtering and persistence are separate so unavailable pins survive.
- The existing lifecycle/orchestrator launch path remains the single source of
  truth for single-instance policy, process creation, and window creation.

## Completed task list

- [x] Audit the legacy ordered-pin behavior and the current launcher contract.
- [x] Add a strict, per-user canonical settings document and codec.
- [x] Add optimistic pin, unpin, toggle, and reorder operations.
- [x] Ship the two-column launcher, quick links, categories, and accessible pin controls.
- [x] Add focused projection/persistence tests and validate the complete solution.
