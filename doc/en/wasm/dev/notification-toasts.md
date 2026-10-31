> [Version française](../../../fr/wasm/dev/notification-toasts.md)

# Notification bubbles

Work item 1 of the taskbar series (issue [#242](https://github.com/Kazo-ca/HackerOs/issues/242)),
implementation of [ADR 0197](adr/0197-notification-toasts.md), done in the `todo/` series.

## 1. What the user sees

- Each new notification from the OS or an app shows as a **bubble**: bottom right, above
  the taskbar of screen 1 on a computer, at the top of the screen on a phone. It carries
  the app's name (or "HackerOS"), the title, the text, the action buttons and a "×".
- A bubble leaves after **6 s**. The pointer or the keyboard focus on it holds it; it gets
  a fresh 6 s once they leave. 3 bubbles at most: the oldest makes room.
- The "×" hides the bubble. The notification stays in the clock panel (the shade on a
  phone). Dismissing it in the panel also removes its bubble.
- No bubble while the panel is open: the notification arrives in the list.
- **Update ready**: once signed in, "HackerOS x.y.z is available. Open apps will close."
  arrives as a bubble and stays in the panel with the "Restart to update" button. On the
  sign-in screen, the notice in the bottom corner stays, with the same button.

## 2. Architecture

| Part | Role |
| --- | --- |
| `NotificationToastFeed` (internal, `Platform.Blazor/Shell`) | Samples `INotificationQueue` once per simulated second, keeps the ids already seen, decides the bubbles (`Toasts`), when they end (`HideAtUtc`), holding (`Hold`/`Release`), `Close` and `IsSuppressed` (panel open). Does not show a notification already older than 6 s when it first sees it. |
| `NotificationToasts.razor` (+ `.razor.cs`, `.razor.css`) | Draws the stack in an `aria-live="polite"` region. Parameters: `Placement` (`AboveTaskbar` or `TopOfScreen`) and `IsPanelOpen`. |
| `NotificationActionRunner` (internal) | Runs an action for the panel and for the bubbles: first `ShellNotificationActions`, then `NotificationAction.LaunchApp`. Dismisses the notification once the action ran. |
| `ShellNotificationActions` (public, session scope) | Registry of the actions the OS runs itself: `Register(actionId, handler)` returns a handle that removes the registration. Registered in `EcosystemServiceCollectionExtensions`. |
| `UpdateReadyNotification` (public) | Posts "Update ready" (source `org.hackeros.update`, no expiry, action `hackeros.update.restart`) once per user and text, and registers "Restart". |
| `App.razor` (Ecosystem.Host) | Creates `UpdateReadyNotification` with `RestartForUpdateAsync`. After each render of the desktop, calls `Show` for the signed-in user, in their language. Shows the `update-notice` only outside a session. |
| `DesktopShell.razor` / `MobileShell.razor` | Place `<NotificationToasts>`: above the taskbar of screen 1 with `IsPanelOpen = _clockPanel.IsOpen`, or at the top with `IsPanelOpen = _isShadeOpen`. |

The `INotificationQueue` contract does not change. The app SDK does not change: every
notification posted through `notifications.post` makes a bubble.

## 3. Styles

`NotificationToasts.razor.css` reuses the panel's tokens (`--hos-bg-surface`,
`--hos-border`, `--hos-accent`, `--hos-warning`, `--hos-error`, `--hos-success`,
`--hos-font-mono`). The stack sits at `z-index: 5000`, above the windows and the taskbar,
below the window menus and screen veils (9000). It lets clicks through; only the bubbles
take them. The entrance (slide + fade, 180 ms) only plays under `[data-motion="enabled"]`
and `prefers-reduced-motion: no-preference`.

## 4. Tests

- `NotificationToastFeedTests`: bubble at the next second, leaves after 6 s, stack of 3,
  hold and release, "×", dismissal in the panel, panel open, old notifications at start,
  session user and sign-out.
- `UpdateReadyNotificationTests`: once per user, no expiry, "Restart" runs then the
  notification is dismissed, a new version replaces it, an app's action is left to the
  app, unregistration.

## 5. Task list

- [x] `NotificationToastFeed` and its tests.
- [x] `NotificationToasts` on the desktop (screen 1) and on a phone.
- [x] Actions shared by the panel and the bubbles (`NotificationActionRunner`, `ShellNotificationActions`).
- [x] "Update ready" as a persistent notification with "Restart"; notice kept on the sign-in screen.
- [x] ADR 0197, docs, `features.md`, memory, change entry.
- [ ] "Do not disturb": to decide (question Q5 asked to Benoit).
