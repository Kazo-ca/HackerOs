# HackerOS App Framework

A modular Blazor WebAssembly framework that turns the HackerSimulator project into
a **developer ecosystem**. A developer writes a single Blazor component that
inherits from a window or terminal base class, drops it into the project, and the
component **self-registers**: it appears in the START menu, launches into a
draggable window, and shows up on the taskbar &mdash; with **no manual wiring**.

## Purpose

- Provide reusable base classes (`WindowAppBase`, `TerminalAppBase`) so new
  applications are created by inheritance, not boilerplate.
- Discover and register applications automatically via reflection, using a single
  `[App]` attribute as the only extension point.
- Compose a desktop shell (desktop surface + taskbar + START menu) that hosts any
  number of self-registered applications.
- Enforce a strict **module-oriented, scoped-asset** convention: every component's
  markup, styles and script live in collocated files
  (`Component.razor` + `Component.razor.css` + `Component.razor.js`) with **no
  inline JS or CSS in markup**.

## Architecture

The framework is split into two projects, both located under
`wasm2/HackerOs/Ecosystem/` per the project directory rules.

### `HackerOs.AppFramework` (Razor Class Library)

| Area | Type | Responsibility |
| --- | --- | --- |
| Abstractions | `AppAttribute` | Marks a component as a self-registering app (name, id, icon, category, description, sort order, visibility). |
| Abstractions | `AppKind` | Enumeration: `Window` or `Terminal`. |
| Registry | `AppDescriptor` | Immutable metadata for one application. `TryCreate(Type)` validates the base class and reads the `[App]` attribute. |
| Registry | `AppRegistry` | `DiscoverFrom(assemblies)` scans for decorated components; `Register`, `Find`, `Launch(...)` open a window via the window manager. |
| Components | `WindowAppBase` | Inherits `WindowBase`; copies `Title`/`Icon` from `[App]`. Base class for windowed apps. |
| Components | `TerminalAppBase` | Inherits `WindowBase`; renders a `TerminalHost` inside window chrome. Base class for console apps. |
| Components | `TerminalHost` | Reusable interactive console (echo, line editing, backspace, command submit). |
| Components | `AppLauncher` | The START menu; lists registered apps grouped by category. |
| Components | `Desktop` | The shell: composes `DesktopArea` + `TaskBarComponent` + `AppLauncher`. |
| Extensions | `ServiceCollectionExtensions` | `AddHackerOsAppFramework(params Assembly[])` registers the window manager, terminal services and the populated `AppRegistry`. |

The framework builds on two existing libraries: **BlazorWindowManager**
(window base class, window manager service, desktop area, taskbar) and
**BlazorTerminal** (the terminal rendering component).

### `HackerOs.Ecosystem` (Blazor WebAssembly host)

The runnable application. `Program.cs` calls
`AddHackerOsAppFramework(typeof(App).Assembly)` and `App.razor` renders a single
`<Desktop>`. Sample modules live under `Modules/`.

### Launch flow

```
AppLauncher / shell command
        -> AppRegistry.Launch("APP_ID")
        -> WindowManagerService.CreateWindow<TComponent>()
        -> WindowRenderComponent renders the component in a window
        -> TaskBarComponent adds a taskbar button automatically
```

## Usage

### 1. Create a windowed application

Add a `.razor` component that inherits `WindowAppBase` and is decorated with
`[App]`. Put its styles in `MyApp.razor.css` and any script in `MyApp.razor.js`.

```razor
@inherits WindowAppBase
@attribute [App("My App", Id = "vendor.myapp", Icon = "\U0001F680", Category = "Development",
    Description = "Does something useful")]

<WindowContent Window="this">
    <div class="my-app">Hello from a self-registered window!</div>
</WindowContent>
```

That is all: rebuild and the app appears in the START menu and taskbar.

### 2. Create a terminal application

Derive from `TerminalAppBase` and implement `OnCommandAsync`. This can be a pure
C# class &mdash; no markup needed.

```csharp
[App("My Shell", Id = "vendor.myshell", Kind = AppKind.Terminal, Category = "Development")]
public sealed class MyShellApp : TerminalAppBase
{
    protected override string Prompt => "you@host:~$ ";
    protected override string? Banner => "My Shell 1.0";

    protected override Task OnCommandAsync(string command)
    {
        WriteLine($"you typed: {command}");
        return Task.CompletedTask;
    }
}
```

### `[App]` attribute reference

| Member | Meaning |
| --- | --- |
| `Name` (ctor) | Display name in launcher, title bar and taskbar. |
| `Id` | Stable identifier; defaults to the component's full type name. |
| `Description` | Tooltip text in the launcher. |
| `Icon` | Short glyph (emoji recommended, keeps the framework dependency free). |
| `Category` | Grouping used to organise the launcher. |
| `Version` | Optional version string. |
| `HiddenFromLauncher` | Hide from the START menu (still launchable by id). |
| `SortOrder` | Ordering within a category. |

### `TerminalAppBase` API

- `OnCommandAsync(string command)` &mdash; handle a submitted command (required).
- `OnStartedAsync()` &mdash; optional hook after the terminal is ready.
- `Prompt`, `Banner`, `Columns`, `Rows` &mdash; overridable presentation.
- `Write`, `WriteLine`, `ClearScreen` &mdash; output helpers.



The suite (13 tests) covers: desktop boot & self-registration, the START menu
listing, each sample app rendering and behaving, terminal echo/command execution,
launching apps from the console, and taskbar minimize/restore/multi-window
tracking.

## Task checklist

- [ ] Framework Razor Class Library scaffolded and building.
- [ ] `[App]` attribute + `AppRegistry` reflection self-registration.
- [ ] `WindowAppBase` and `TerminalAppBase` base components.
- [ ] `TerminalHost` interactive console (echo, line editing, commands).
- [ ] Desktop shell + taskbar + START/app launcher.
- [ ] Host WebAssembly app with three sample modules (scoped css/js each).
- [ ] Fix `WindowRenderComponent` to resolve external window component types.
- [ ] Fix taskbar minimize/restore to update the window component state.
- [ ] Playwright suite (13 tests) written and passing.
- [ ] Documentation.
