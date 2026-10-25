# Blazor App SDK

## Purpose

Provide the Blazor-specific application boundary without forcing Terminal or
Service apps to reference Blazor. This project defines `WindowAppBase`, standard
virtual file dialogs, and mandatory scoped-asset validation.

## Architecture

`HackerOs.AppSdk.Blazor` references the headless App SDK and simulation contracts.
The dependency never points in the opposite direction.

`WindowAppBase` derives from Blazor `ComponentBase` and receives an
`IAppExecutionContext` from the future window host. It validates that the manifest
is valid, has kind `Window`, and remains bound to the same app instance.

No window manager, chrome, drag/resize interop, or dialog renderer is implemented
in this slice. Those belong to the platform Blazor project. The SDK provides the
stable component and service contract they will consume.

## Lifecycle safety

Framework lifecycle overrides are sealed, including parameter assignment,
initialization, parameter updates, render decisions, and post-render callbacks.
Window apps customize behavior through these hooks:

- `OnAppInitialized` / `OnAppInitializedAsync`;
- `OnAppParametersSet` / `OnAppParametersSetAsync`;
- `ShouldRenderApp`; and
- `OnAppAfterRender` / `OnAppAfterRenderAsync`.

The sealed asynchronous post-render method always performs framework work before
calling the app hook. Future window JS module initialization will be added inside
that framework-owned path, preventing the previous failure where a component
forgot `base.OnAfterRenderAsync` and silently disabled drag/resize.

## File dialogs

Window apps call protected helpers:

- `OpenFileAsync(OpenFileDialogRequest, CancellationToken)`;
- `SaveFileAsync(SaveFileDialogRequest, CancellationToken)`; and
- `SelectFolderAsync(SelectFolderDialogRequest, CancellationToken)`.

Requests support virtual initial directories, extension/media filters,
multi-select, requested read/write access, suggested save names, default
extensions, overwrite confirmation, and folder creation policy.

Results use canonical `VirtualPath` values and explicit Selected/Cancelled status.
They never expose browser DOM elements, IndexedDB keys, or native device paths.
The future platform service must enforce dialog capabilities, filesystem
permissions, owner-window modality, and cancellation.

## Scoped assets

`Directory.Build.targets` runs for every Razor SDK project and fails the build if
a `.razor` file contains:

- `<style>`;
- `<script>`;
- a `style=` attribute; or
- a raw JavaScript event attribute such as `onclick=`.

Blazor event bindings such as `@onclick` remain valid. Component assets must use
collocated `Component.razor.css` and `Component.razor.js` files. Shared assets may
live in dedicated static files but are never embedded in Razor markup.

The enforcement was validated with a temporary invalid component: its build
failed, the probe was removed, and the clean SDK build passed.

## Key decisions

- Window UI contracts live in a separate Razor SDK project.
- App components cannot override framework lifecycle methods.
- File dialogs select HackerOS virtual paths only.
- Dialog authorization belongs to the platform implementation, not app code.
- Inline CSS and JavaScript are build errors.
- No template JavaScript, CSS, or image assets remain in the SDK.

## Task list

- [x] Create the component-only Razor SDK project.
- [x] Define `WindowAppBase` and validate window manifests.
- [x] Seal lifecycle methods and expose safe app hooks.
- [x] Define typed file-open, file-save, and folder-selection contracts.
- [x] Delegate helpers with the bound app execution context.
- [x] Add build-time inline asset rejection.
- [x] Add focused lifecycle and dialog delegation tests.
- [ ] Implement the platform window runtime and framework post-render setup.
- [ ] Implement modal dialog UI with scoped assets.
- [ ] Enforce dialog capabilities and virtual filesystem permissions.
- [ ] Add rendered component tests once the platform renderer exists.