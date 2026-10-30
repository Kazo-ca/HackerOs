> [Version française](../../../../fr/wasm/dev/adr/0196-unread-notifications-dot.md)

# ADR 0196: unread notifications dot next to the clock

## Status

Accepted on 2026-10-30. Request from Benoit on 2026-10-30 (thread "Explain the
yellow dot on Applications"). Questions Q1 to Q6 below were asked to him in the
thread; the recommendation applies until he picks something else.

- **Replaces:** the Start menu and Home button dot of
  [ADR 0186](0186-whats-new-change-list.md) §4.5. The rest of ADR 0186 does not
  change (notification after sign-in, Settings ▸ What's new page).
- **Complements:** [ADR 0134](0134-automatic-server-connection-and-status-icon.md)
  (taskbar notification area).

## Context

- Since ADR 0186, a yellow dot showed at the top right of the "Applications"
  button (and of the Home button on a phone) while unseen changes remained.
  Nothing explained it: no tooltip, and no visible link with the "3 new changes
  since your last visit" notification kept in the clock panel.
- Benoit prefers the notification area to say it: a dot next to the time, and a
  click on the notification that opens the right Settings page (the **View**
  button already did).
- The notification queue (`INotificationQueue`) raises no change event and does
  not know what was read.

## Questions asked to Benoit

| # | Question | Choices (⭐ recommended) |
|---|----------|--------------------------|
| Q1 | What lights the clock dot? | **A ⭐ any unread notification** \| B only what's new |
| Q2 | When does it go out? | **A ⭐ when the panel opens** (notifications stay in the list) \| B when the list is empty |
| Q3 | The Applications button dot? | **A ⭐ removed** (on a phone, it moves to the shade handle) \| B kept as well |
| Q4 | A dot on the Settings app in the Start menu? | **A ⭐ no** \| B yes |
| Q5 | The tooltip? | **A ⭐ on the clock**, with the count, also the accessible name \| B on the dot only |
| Q6 | Changes seen from Settings without the notification? | **A ⭐ the notification closes by itself** \| B it stays |

## Decision

### 1. The clock dot

- The taskbar clock button carries a dot (color `--hos-taskbar-attention`, the
  same as the old one) while at least one active notification of the user is
  unread. Every source counts: what's new, low storage, OOM, apps.
- Its accessible name and tooltip (`title`) give the count: "System clock,
  notifications, and calendar: 3 unread notifications". With nothing unread, the
  tooltip stays "System clock, notifications, and calendar".
- Contract of the `HackerOs.Taskbar.Blazor` package:
  `ITaskbarClockPanelSource.UnreadCount` (0 by default, other hosts do not
  change). `TaskbarClockPanelSourceAdapter.SetUnreadCount` sets it.

### 2. Read and unread

- Opening the clock panel marks every notification it shows as read, including
  those that arrive while it is open. They stay in the list until the user
  dismisses them (×), like the Windows notification center.
- `NotificationAttention` (Platform.Blazor, `Shell/`) keeps the read state in
  memory for the page: a notification only lives as long as the page anyway. As
  the queue raises no event, it samples it once per simulated second, at the
  clock's pace, and only notifies when the count changes.
- The `INotificationQueue` contract (Simulation.Abstractions) does not change.

### 3. Phone

- The dot moves from the Home button to the notification shade handle, at the
  top of the screen, right of the grabber. Same rule: opening the shade reads the
  notifications. The handle's accessible name and tooltip give the count.

### 4. What's new

- The Applications and Home buttons no longer carry a dot.
  `ITaskbarLauncher.HasAttention` and `MobileSystemNavigationBar.HomeHasAttention`
  stay in their packages for other hosts; HackerOS no longer uses them.
- The notification after sign-in does not change: its **View** button opens
  Settings ▸ What's new and dismisses it.
- When every change has been seen, wherever (page opened from Settings or from
  the notification), `WhatsNewService` dismisses its notification: it has nothing
  left to say.
- No dot on the Settings app.

## Consequences

- One place shows what waits for the user, and it explains itself on hover.
- Any notification now lights the dot, not only what's new.
- The read state is lost when the page reloads, like the notifications
  themselves.
- Tests: `NotificationAttentionTests`, `TaskbarLocalizationTests` (clock dot and
  name), `TaskbarClockPanelSourceAdapterTests`, `WhatsNewServiceTests`
  (notification dismissed when everything is seen).
