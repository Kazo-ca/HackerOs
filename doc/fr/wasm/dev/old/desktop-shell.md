# HackerOS Desktop Shell

## Purpose

`Platform/HackerOs.Platform.Blazor/Shell/` provides the user-facing desktop workspace, taskbar, application launcher, notification toast overlay, and session logout controls for HackerOS v3.

## Architecture

The shell is implemented using collocated Blazor components and scoped CSS files:

- `DesktopShell.razor/.css`: Root shell container hosting the desktop workspace, window outlet (`DesktopArea`), taskbar, popovers, and notification overlays. Exposes an optional `BackgroundContent` parameter (`INT-012`), forwarded unchanged to `DesktopArea.BackgroundContent` — `DesktopShell` doesn't need to know what the content is, only that a future composition root may want to supply some.
- `Platform/HackerOs.Windowing.Blazor/DesktopArea.razor`: renders `BackgroundContent` (`INT-011`) in its own `<section class="background-layer">`, positioned in DOM order after the `desktop-grid` background and before the `window-layer` so window chrome always occludes it. This is infrastructure only — no host currently supplies `BackgroundContent`; per the [confirmed decision](Global-FileView-And-MessagingSystem.md#key-decisions-already-made), this does not host a `FileView` on the desktop or render desktop icons, and wiring an actual desktop-icons feature into this slot is a distinct future phase, not opened here. Omitting the parameter renders exactly the same DOM as before it existed (backward compatible) — see `Tests/HackerOs.Platform.Blazor.Tests/Shell/DesktopAreaBackgroundContentTests.cs`.
- `HackerOs.Taskbar.Blazor.Taskbar`: the taskbar itself is no longer a HackerOS-specific
  component. It moved to the standalone `HackerOs.Taskbar.Blazor` package (see
  [`window-taskbar-export-plan.md`](window-taskbar-export-plan.md), `EXT-WIN-007`/`008`)
  and is driven entirely by contracts (`ITaskbarWindowSource`, `ITaskbarCommandDispatcher`,
  `ITaskbarLauncher`, `ITaskbarStatusSource`, `ITaskbarClockPanelSource`,
  `ITaskbarSessionCommands`). `DesktopShell.razor` renders it with the fully-qualified tag
  and supplies HackerOS-specific implementations of those contracts from
  `Shell/TaskbarAdapters.cs` (binding to `WindowRuntime`, `AppCatalog`, `ISimulationClock`,
  `INotificationQueue`, `ISessionService`, `AppIntentDispatcher`). The old
  `Shell/Taskbar.razor/.css` was deleted once the migration was verified end-to-end in the
  browser with zero observable behavior change.
- `AppLauncher.razor/.css/.cs`: Windows 7-inspired two-column Start menu bound to
  the trusted catalog/enablement/session state. It provides ordered per-user
  quick-launch pins, dynamic presentation categories, search, keyboard
  navigation, and stable File Explorer/Settings shortcuts. Every selection is
  returned through the existing shell launch callback; see
  [`start-menu.md`](start-menu.md).
- `ClockPanel.razor/.css`: Panel rendered inside the taskbar clock's host-owned container
  (`ITaskbarClockPanelSource`/`ClockPanelContent`, see
  [`mobile-interface-platform-plan.md`](mobile-interface-platform-plan.md) Phase 0). Combines
  notifications (bound to `INotificationQueue`, superseding the old always-on `NotificationCenter`
  toast overlay), a minimal calendar, and the Auto/Desktop/Mobile platform-preference toggle
  (`UiPlatformPreferenceService`).
- `LogoutDialog.razor/.css`: Modal confirmation dialog for session logout/shutdown with active process warning list and clean session termination via `ISessionService`.

## Theme & Accessibility

- Semantic design tokens (`--hos-*`) inherited from the root
  `HackerOs.Theming.Blazor.ThemeScope` supply the selected desktop palette,
  typography, shape, taskbar, launcher, and window treatment. See
  [`theming.md`](theming.md); host `app.css` is not a second theme definition.
- All interactive controls provide full keyboard navigation, focus indicators (`:focus-visible`), and ARIA attributes (`role="contentinfo"`, `role="tablist"`, `role="listbox"`, `role="status"`, `aria-live="polite"`).

## Key Decisions

- **Platform Contract Binding**: Shell components bind only to platform abstractions (`WindowRuntime`, `AppCatalog`, `INotificationQueue`, `ISessionService`, `ISimulationClock`), avoiding concrete application references.
- **Scoped Component Styling**: Styles are strictly contained in `.razor.css` files; inline styles and `<style>` blocks in Razor markup are forbidden.

## Task List

- [x] `P2-SHELL-001` Implement `DesktopShell.razor/.css` with work area, window outlet, taskbar, launcher, and notification outlets.
- [x] `P2-SHELL-001A` Define shared shell design tokens; these now live in the
  `HackerOs.Theming.Blazor` RCL and are selected by one root scope.
- [x] `P2-SHELL-002` Implement `Taskbar.razor/.css` from process/window state, simulation clock, and status.
- [x] `P2-SHELL-003` Implement `AppLauncher.razor/.css` from `AppCatalog` with search, categories, keyboard nav, and launch intents.
- [x] `P2-SHELL-004` Implement desktop shortcuts/settings policy.
- [x] `P2-SHELL-005` Implement `NotificationCenter.razor/.css` bound to `INotificationQueue` with toasts, severity, expiry, and ARIA announcements.
- [x] `P2-SHELL-006` Implement `LogoutDialog.razor/.css` for logout/shutdown confirmation, active process list, and clean session exit.
- [x] `P2-SHELL-007` Apply Gothic/Hacker visual design tokens and restrained colors.
- [x] `P2-SHELL-008` Support keyboard-only operation, focus indicators, screen readers, reduced motion, and text containment.
- [x] `P2-SHELL-009` Add unit and component tests in `Tests/HackerOs.Platform.Blazor.Tests/Shell/DesktopShellTests.cs`.
- [x] `INT-011`/`INT-012`/`INT-013`/`INT-014` Add the `BackgroundContent` background-layer slot to
  `DesktopArea`/`DesktopShell` (infrastructure only, no desktop-icons feature) — see
  [`Global-FileView-And-MessagingSystem/integrationPlan.md` Phase 6](Global-FileView-And-MessagingSystem/integrationPlan.md#phase-6--desktopareadesktopshell-background-slot-infrastructure-only).
- [x] Replace the launcher category bug with presentation-category filtering,
  add the Windows 7 layout and quick links, and persist ordered per-user pins.
- [x] Apply the shared desktop theme contract to the desktop, exported window
  chrome, taskbar, and launcher without duplicating theme CSS in a host.
