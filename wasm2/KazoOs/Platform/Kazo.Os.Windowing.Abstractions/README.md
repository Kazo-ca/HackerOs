# Kazo.Os.Windowing.Abstractions

Renderer- and host-independent window contracts: identity (`WindowId`,
`WindowOwnerId`), geometry (`WindowBounds`, `WindowConstraints`), visual
state and modality (`WindowVisualState`, `WindowModality`), the immutable
`WindowRuntimeState` snapshot, and the commands/events
`Kazo.Os.Windowing.Core`'s `WindowRuntime` engine consumes and produces.

This package has no dependency on Blazor rendering, a browser, or any
particular host application model — only the `RenderFragment` delegate type
(carried by `WindowRuntimeState.Content`, for window content) is referenced,
via `Microsoft.AspNetCore.Components`.

## Usage

```csharp
WindowRuntimeState state = new(
    WindowId.FromGuid(Guid.NewGuid()),
    appId: "my-app",
    ownerInstanceId: WindowOwnerId.FromGuid(Guid.NewGuid()),
    title: "My Window",
    iconAssetPath: null,
    new WindowBounds(60, 50, 420, 280),
    restoreBounds: null,
    zOrder: 0,
    WindowVisualState.Normal,
    new WindowConstraints(isResizable: true, minWidth: 260, minHeight: 180));
```

Pair this package with `Kazo.Os.Windowing.Core` for the headless engine
that applies commands built from these types and emits these events, with
`Kazo.Os.Windowing.Blazor` for the Blazor components that render a
`WindowRuntime`'s state, and `Kazo.Os.Taskbar.Blazor` for a taskbar over the
same runtime.

See `Kazo.Os.Windowing.SampleHost` in the source repository for a complete,
minimal host that uses only these packages.
