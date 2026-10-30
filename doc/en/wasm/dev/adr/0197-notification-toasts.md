> [Version française](../../../../fr/wasm/dev/adr/0197-notification-toasts.md)

# ADR 0197: notification bubbles and "Update ready" as a notification

## Status

Accepted on 2026-10-30, work item 1 of the taskbar series (issue
[#242](https://github.com/Kazo-ca/HackerOs/issues/242), decisions 1 and 2 taken with
Benoit on 2026-10-29), done in the `todo/` series (branch `todo/11-notification-toasts`).
Builds on the notification queue (`INotificationQueue`), the clock panel (`ClockPanel`),
the per-screen taskbars
([ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md): the notification area
stays on screen 1) and the restart after a new build
([ADR 0162](0162-restart-after-a-host-build-change.md),
[ADR 0174](0174-announce-a-new-version-to-open-sessions.md)). Takes the unread
notifications dot (ADR 0196, [PR #294](https://github.com/Kazo-ca/HackerOs/pull/294))
into account without depending on it. Implementation:
[`notification-toasts.md`](../notification-toasts.md).

## Context

- OS and app notifications (`notifications.post`) only show in the clock panel (the
  shade on a phone). Nothing tells the user that a notification arrived.
- `INotificationQueue` raises no change event: its readers poll it (ADR 0196 samples it
  once per simulated second).
- "Update ready" is a fixed `aside` of the host (`App.razor`), moved above the taskbar
  by PR #258. It is not in the panel, cannot be closed, and covers the desktop corner
  until the restart.
- In the panel, a notification action only does something when it launches an app
  (`NotificationAction.LaunchApp`). The OS has no way to answer an action such as
  "Restart".

## Decision

1. **A bubble for each new notification.** The `NotificationToasts` component
   (Platform.Blazor) shows every new notification of the session user, from the OS and
   from apps:
   - on a computer, bottom right, above the taskbar of **screen 1** (the notification
     area stays there, ADR 0179);
   - on a phone, **at the top of the screen**, like phone notifications (the navigation
     bar and the keyboard take the bottom);
   - **3 bubbles at most**. A new bubble pushes the oldest out.
2. **Duration.** A bubble stays **6 s** (simulated time), then leaves. It stays while
   the pointer or the keyboard focus is on it, then gets a fresh 6 s (WCAG 2.2.1, give
   time to read). Its "×" hides the bubble only: the notification stays in the panel, as
   on Windows. A notification dismissed in the panel takes its bubble with it.
3. **What counts as new.** The queue is sampled once per simulated second, as for ADR
   0196. The `INotificationQueue` contract does not change. A notification is new the
   first time a sample sees it. If it is already older than 6 s then, it makes no
   bubble: a sign-in does not replay the panel as bubbles. **Panel open = no bubble**:
   what arrives goes straight to the open list, and makes no bubble after it closes.
4. **Actions shared by the panel and the bubbles.** `NotificationActionRunner` runs an
   action the same way in both places. An action the host registered in
   `ShellNotificationActions` (public service, session scope) runs its handler. A
   `LaunchApp` action launches its app. Either way the notification is then dismissed.
   Any other action belongs to its app, as before.
5. **"Update ready" becomes a notification.** Once a user is signed in, the host posts,
   through `UpdateReadyNotification`, a notification that never expires: source
   `org.hackeros.update`, title "HackerOS x.y.z is available." (or "A HackerOS update is
   ready."), text "Open apps will close.", action "Restart to update"
   (`hackeros.update.restart`). It shows as a bubble when it arrives and stays in the
   panel until it is dismissed or the OS restarts. It is posted once per user and text.
   Once dismissed, it does not come back. When the version becomes known, it replaces
   the previous one. **Sign-in screen**: there is no session, queue or panel there. The
   host's notice stays there, in the bottom corner, with its button. It no longer shows
   once signed in.
6. **Source name.** A bubble shows the app's name in the session language when the
   catalog knows it, "HackerOS" for the OS's own sources (`org.hackeros.*`, which are no
   apps), otherwise the id.
7. **Look.** Same tokens as the panel (left border in the severity's colour), a 180 ms
   slide and fade in, cut by "Reduce motion" (ADR 0105). The container is an
   `aria-live="polite"` region present before the first bubble, so screen readers
   announce each one.

## Consequences

- Apps have nothing to change: every notification they already post makes a bubble. The
  SDK does not change.
- New public API in Platform.Blazor (`PublicAPI.Unshipped.txt`): `NotificationToasts`,
  `NotificationToastPlacement`, `ShellNotificationActions`, `UpdateReadyNotification`. A
  host that does not register `ShellNotificationActions` offers no "Restart" in the
  notification.
- The `above-taskbar` and `above-mobile-nav` classes of the host notice (PR #258) are
  removed: it only shows on the sign-in screens now.
- "Do not disturb" is not done: to decide later (question asked to Benoit).
- The unread dot (ADR 0196) and the bubbles read the same queue without getting in each
  other's way: a bubble does not mark the notification as read.

## Questions asked to Benoit (2026-10-30)

Q1 bubbles at the top on a phone, Q2 hover stops the countdown, Q3 no bubble while the
panel is open, Q4 notice kept on the sign-in screen, Q5 "Do not disturb" out of this work
item. The recommendation is applied everywhere until he answers.
