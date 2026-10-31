# HackerOs.Taskbar.Blazor

A taskbar Razor component (`Taskbar`) driven entirely by host-supplied
contracts instead of a fixed set of injected services: `ITaskbarWindowSource`,
`ITaskbarCommandDispatcher`, `ITaskbarLauncher`, `ITaskbarStatusSource`,
`ITaskbarClockPanelSource`, `ITaskbarSessionCommands`. Every contract
parameter is optional — the corresponding taskbar region (launcher trigger,
clock, session control) simply does not render when its contract isn't
supplied.

The clock is the taskbar's one extensible surface: `ITaskbarClockPanelSource`
controls whether its panel is open, and the `ClockPanelContent` `RenderFragment`
parameter supplies what's inside it. This package owns only the trigger button
and the panel's anchoring/positioning — it never inspects `ClockPanelContent`,
so the panel's actual design (notifications, calendar, a platform-mode toggle,
anything else) lives entirely in the host application, outside this package.
`ITaskbarClockPanelSource.UnreadCount` (0 by default) puts a dot on the clock
and gives the count in its accessible name and tooltip (ADR 0196).

## Usage

```razor
<Taskbar WindowSource="_windowSource"
         Commands="_commands"
         Launcher="_launcher"
         StatusSource="_status"
         ClockPanelSource="_clockPanel"
         ClockPanelContent="@ClockPanelFragment"
         Options="_options" />
```

This example omits `SessionCommands` — a host with no session concept just
doesn't provide it, and the taskbar renders without that region. HackerOS itself
no longer supplies it: its Power button (sign out, switch user, restart) lives in
the Start menu (ADR 0195). `TaskbarOptions`
carries only the labels a host would otherwise be forced to see hardcoded (the
launcher trigger's mark and text); which taskbar zones appear is controlled
entirely by which contracts you supply.

Pair this package with `HackerOs.Windowing.Blazor` for the window chrome that
these contracts typically sit alongside. See `HackerOs.Windowing.SampleHost`
in the source repository for a complete, minimal host that uses only these
packages, including sample `ITaskbarWindowSource`/`ITaskbarCommandDispatcher`/
`ITaskbarLauncher`/`ITaskbarStatusSource` implementations.

## Context menus

`ITaskbarContextMenus` (the `ContextMenus` parameter) lets the host give the taskbar its own
context menus (ADR 0194): `TaskbarAttributes` are set on the taskbar itself and
`WindowItemAttributes(id)` on the button of each window, with `@attributes`. The taskbar never
reads them; the host's menu system does (HackerOS attaches its OS context menu there). Without the
parameter the browser's menu stays.

## Localization

The package's texts (accessible labels, the launcher label and the window states) live in `Resources/Strings.resx`
(English) and `Resources/Strings.fr.resx` (French), read through
`HackerOs.Theming.Blazor.Localization.PackageStringLocalizer` (ADR 0104): a host that
registers an `IStringLocalizer<T>` (HackerOS registers its session-language
`SessionStringLocalizer<T>`) gets its language; a host without localization gets English.

`TaskbarOptions.LauncherLabel` overrides the package's "Applications" when set; left `null` it
is localized. The clock is formatted with the long time pattern (`"T"`) of
`ITaskbarStatusSource.FormatCulture`: a default interface member returning the invariant culture
(`HH:mm:ss`), which HackerOS implements with the session's `LC_TIME` culture (`9:00:00 AM` in
`en-US`, `09:00:00` in `fr-FR`).
