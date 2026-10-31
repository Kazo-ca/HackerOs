> [Version française](../../../../fr/wasm/dev/adr/0216-do-not-disturb.md)

# ADR 0216: "Do not disturb" mode

## Status

Accepted on 2026-10-31, issue [#296](https://github.com/Kazo-ca/HackerOs/issues/296), done
in the `todo/` series (branch `todo/22-do-not-disturb`). Completes the notification bubbles
([ADR 0197](0197-notification-toasts.md)), the unread notifications dot
([ADR 0196](0196-unread-notifications-dot.md)) and the OS sounds
([ADR 0204](0204-os-event-sounds.md)). The five questions of the issue were asked to Benoit on
2026-10-31; the choices below are the recommendations, taken while waiting for his answer.
Implementation: [`notification-toasts.md`](../notification-toasts.md).

## Context

- Every new notification shows a bubble for 6 s (ADR 0197) and plays the sound of its
  severity (ADR 0204). During a presentation or focused work, one wants to cut both without
  losing the notifications.
- OS settings are real layered `.config` files: `/etc/hackeros/` for the machine,
  `~/.config/hackeros/` for the user (ADR 0044).
- The clock panel is also the phone's shade (`ClockPanel`).

## Decision

1. **What the mode does.** While "Do not disturb" is on, a new notification shows **no
   bubble and plays no sound**. It goes to the clock panel as usual, and the unread dot
   (ADR 0196) still lights up: nothing is lost. **Everything is cut, errors included.** The
   other OS sounds (sign-in and sign-out, message boxes) do not change. Turning the mode on
   hides the bubbles already shown; turning it off does not replay what arrived meanwhile.
2. **Where to turn it on.** In both places:
   - a "Do not disturb" switch at the top of the Notifications section of the clock panel,
     so also in the phone's shade;
   - a new **Settings ▸ Notifications** page (`--page=notifications`), with the same switch
     and the origin of the setting ("System" / "User" badge).
3. **Where it is saved.** Per user, in the layered `notifications` document: key
   `doNotDisturb` (boolean, `false` by default) of `~/.config/hackeros/notifications.config`.
   An administrator can set the machine's default in `/etc/hackeros/notifications.config`.
   The file is watched: an edit with `nano` applies at once. The setting follows the user
   and survives a reload.
4. **Service.** `DoNotDisturbService` (Platform.Core, session scope) reads the document,
   exposes `IsEnabled` and `Changed`, and writes the user layer (`SetAsync`). The bubbles
   (`NotificationToasts`) treat it like an open panel (`NotificationToastFeed.IsSuppressed`).
   The sound server (`OsSoundService`) does not play the sound of a notification posted
   while the mode is on.
5. **Indicator.** A moon (Lucide `moon` icon) in the taskbar's notification area, next to
   the clock, with the tooltip and accessible name "Do not disturb is on". A click opens the
   clock panel, which holds the switch. On a phone, the same moon shows on the shade handle,
   whose name announces it too.

## Consequences

- Apps have nothing to change; the SDK does not change.
- New document listed in `OsSettingsDocumentRegistry` (`notifications`), new public
  service `DoNotDisturbService` and new component `DoNotDisturbIndicator`
  (`PublicAPI.Unshipped.txt`).
- No automatic time range and no per-app exceptions: to add later if needed (new `TODO`
  issue).
