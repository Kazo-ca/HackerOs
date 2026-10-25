# HackerOs.MobileShell.Blazor

Mobile shell chrome Razor components, driven by host-supplied contracts the
same way `HackerOs.Taskbar.Blazor` drives the Desktop taskbar. Mobile has no
taskbar ([archived mobile plan](../../../../doc/wasm/dev/old/mobile-interface-platform-plan.md) §7.2), so this is a sibling
package rather than a modification of `HackerOs.Taskbar.Blazor`.

## `MobileSystemNavigationBar`

An Android-inspired bottom bar with three buttons — Triangle/Back,
Circle/Home (a pill with the iOS theme), Square/Recent — each at least 44×44 CSS pixels and respecting
`env(safe-area-inset-*)`. The bar owns only the trigger buttons; what
Back/Home/Recent actually do is entirely up to the host's
`IMobileNavigationCommands` implementation.

```razor
<MobileSystemNavigationBar Commands="_navigationCommands" Options="_options" />
```

Unlike `HackerOs.Taskbar.Blazor`'s optional contract parameters, `Commands` is
required — the Mobile shell has no floating-chrome equivalent for these three
actions, so a bar with nothing to dispatch to is a host configuration error,
not a legitimately reduced state.

`MobileSystemNavigationBarOptions` carries only the three buttons' accessible
labels, mirroring `TaskbarOptions`.

This package supplies the navigation controls and Recent surface. The host owns
Back's ordered semantics (overlay → app handler → navigation history → Home).
HackerOS also recognizes an upward gesture starting on the Home control to open
its application drawer. Home pages, the pinned app dock and mouse assistance
belong to Platform Blazor; see [mobile Home](../../../../doc/wasm/dev/mobile-home-screen.md)
and [ADR 0165](../../../../doc/wasm/dev/adr/0165-mobile-home-drawer-and-mouse-controls.md).

## Localization

The package's texts (accessible labels, Back/Home/Recent, the Recent surface) live in `Resources/Strings.resx`
(English) and `Resources/Strings.fr.resx` (French), read through
`HackerOs.Theming.Blazor.Localization.PackageStringLocalizer` (ADR 0104): a host that
registers an `IStringLocalizer<T>` (HackerOS registers its session-language
`SessionStringLocalizer<T>`) gets its language; a host without localization gets English.

Every label of `MobileSystemNavigationBarOptions` and `MobileRecentSurfaceOptions` is now
`string?`: a host value wins, `null` (the default) uses the localized package text.
