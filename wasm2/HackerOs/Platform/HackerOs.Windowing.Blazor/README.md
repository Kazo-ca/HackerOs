# HackerOs.Windowing.Blazor

Razor components that render a `HackerOs.Windowing.Core` `WindowRuntime`:
`DesktopArea` (the workspace surface), `WindowHost` (one window's chrome
frame), and `WindowChrome` (title bar, minimize/maximize/close, and the
pointer-driven move/resize gesture). No MudBlazor dependency.

## Usage

```razor
<DesktopArea Windows="Runtime.Windows"
             OnFocus="HandleFocus"
             OnMinimize="HandleMinimize"
             OnToggleMaximize="HandleToggleMaximize"
             OnClose="HandleClose"
             OnGesture="HandleGesture">
    <WindowContent Context="window">
        @window.Content
    </WindowContent>
</DesktopArea>
```

`window.Content` is the `RenderFragment` the host supplied when it created
that window's `WindowRuntimeState` — this package never needs to know what
a window's content actually is.

Pair this package with `HackerOs.Taskbar.Blazor` for a taskbar over the same
runtime. See `HackerOs.Windowing.SampleHost` in the source repository for a
complete, minimal host that uses only these packages.

## Several screens

Give every `DesktopArea` the same `WindowLayers` and its own `DisplayId`. The area of
screen 1 renders each window once; a window on another screen has its element moved into
that screen's window layer (`WindowPortal`), so changing screen never recreates it. Add the
screen to the runtime (`AddDisplayCommand`) before rendering its area.

```razor
<DesktopArea Windows="Runtime.Windows" Layers="Layers" ... />
<DesktopArea Windows="Runtime.Windows" Layers="Layers" DisplayId="DisplayId.FromNumber(2)" ... />
```

See `doc/en/wasm/dev/window-portal.md` in the source repository (ADR 0178 §3).

## Localization

The package's texts (accessible labels, window buttons such as Minimize, Maximize, Restore, Close, Back) live in `Resources/Strings.resx`
(English) and `Resources/Strings.fr.resx` (French), read through
`HackerOs.Theming.Blazor.Localization.PackageStringLocalizer` (ADR 0104): a host that
registers an `IStringLocalizer<T>` (HackerOS registers its session-language
`SessionStringLocalizer<T>`) gets its language; a host without localization gets English.

## Single surface (phones)

`SingleSurfaceArea` shows one window at a time, full screen and without chrome,
as `MobileShell` does. It renders the stack given by
`SingleSurfacePresentationPolicy.SelectSurfaceStack`: the app's window, then
its owner-modal dialogs. Every window of the stack is maximized; only the top
one takes input, the windows under it stay mounted and `inert`. Each window host
is its own stacking context, so nothing in an app draws over its dialog
(ADR 0171).
