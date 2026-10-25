# Plan for extracting the window system and the taskbar

**Status:** architecture proposal and resumption plan  
**Immediate target:** reuse by several hosts of `HackerOs.sln`  
**Future target:** publication as versioned NuGet packages  
**Code concerned:** `wasm2/HackerOs/Platform/HackerOs.Platform.Blazor/`

## 1. Goal

Extract the window engine, its Blazor components and the taskbar from
their current project so that they can be consumed by another application
of the solution without depending on the `HackerOs.Ecosystem` host project.

The extraction must produce projects usable immediately through project
references. Their public APIs, dependencies and static resources must
nevertheless be designed now to allow a later NuGet publication
without a major rewrite.

The result must not export the whole of HackerOS. A consuming application
must be able to use the window system and the taskbar
by providing its own application sources, lifecycle commands,
icons, clock, notifications and theme strategy.

## 2. Current state and couplings to address

The current code is mainly grouped in
`Platform/HackerOs.Platform.Blazor`:

- `Windows/WindowRuntime.cs` owns the state and transitions of windows;
- `Windows/WindowRuntimeState.cs` contains the identity, geometry, focus,
  Z order, modality and constraints;
- `Windows/DesktopArea.razor`, `WindowHost.razor` and `WindowChrome.razor`
  perform the Blazor rendering;
- `Shell/Taskbar.razor` directly injects `WindowRuntime` and other HackerOS
  services;
- `Shell/DesktopShell.razor` composes windows, taskbar, launcher, notifications and
  session;
- `WindowCloseCoordinator` depends on the HackerOS application lifecycle;
- `WindowAppRenderer` depends on `WindowAppBase`, `AppDescriptor` and the HackerOS
  application context;
- window identifiers currently reference the process
  and instance identities of the simulation.

The headless engine is already well separated from the DOM, but its identity model and
the shell components remain tied to the HackerOS domain. The taskbar is more
strongly coupled than the window engine and must go through adaptation
contracts.

### 2.1 Recent change: generic content and dialogs (2026-08)

An additional coupling, absent from the initial writing of this document, exists
in `Platform/HackerOs.Platform.Blazor/Dialogs/`: `FileDialogCoordinator`,
`DialogCoordinator` and `FileDialogWindowAdapter` create owner-modal windows via
`WindowRuntime.Apply(new CreateWindowCommand(...))`, exactly as
`WindowLaunchCoordinator` does for applications. These dialogs (file
selection, message boxes, text input) remain specific to HackerOS
(virtual file system, capabilities) and are not part of the exported scope.

This coupling nevertheless motivated a generic extension already in place:
`WindowRuntimeState` now carries `Content` (`RenderFragment?`) and
`OnRequestClose` (`Func<Task>?`). `DesktopShell.razor` no longer knows the concrete dialog
types; it renders `window.Content` when provided, and routes the
close requested by the user to `window.OnRequestClose` when it exists. This
generic mechanism (not the dialogs themselves) is part of the exported engine (see
3.3): any host can now create a window whose content and
closing logic are provided by the caller, without the engine needing a
special case per window type.

## 3. Target architecture

### 3.1 Proposed projects

Create the following projects under `wasm2/HackerOs/`:

```text
Shared/
  HackerOs.Windowing.Abstractions/
Platform/
  HackerOs.Windowing.Core/
  HackerOs.Windowing.Blazor/
  HackerOs.Taskbar.Blazor/
  HackerOs.Platform.Blazor/          # composition/adaptateurs HackerOS existants
Tests/
  HackerOs.Windowing.Core.Tests/
  HackerOs.Windowing.Blazor.Tests/
  HackerOs.Taskbar.Blazor.Tests/
Samples/
  HackerOs.Windowing.SampleHost/     # consommateur sans HackerOs.Ecosystem
```

The names may be adjusted before creation, but the dependency boundaries
must be kept.

`HackerOs.sln` is a classic `.sln` file (not the `slnx` XML format): each
new project must be added explicitly via `dotnet sln add`, with a
`GlobalSection(ProjectConfigurationPlatforms)` entry and, if placed in an existing
solution folder (`Shared`, `Platform`, `Tests`, `Samples`), a
`GlobalSection(NestedProjects)` entry. No automatic discovery takes place.

### 3.2 `HackerOs.Windowing.Abstractions`

.NET library without Blazor, MudBlazor, browser or HackerOS host dependency.

It must contain:

- `WindowId` and the generic owner identifiers;
- `WindowBounds`, `WindowConstraints` and `WindowVisualState`;
- the immutable descriptions of title, icon and logical content (the logical content
  is a `RenderFragment` provided by the caller, see 2.1 and 3.3), not an opaque
  contract: the exported library is always consumed from Blazor anyway,
  so this type adds no real coupling to a non-Blazor host;
- the window commands and events;
- the closing, confirmation and activation contracts;
- the item source contracts for the taskbar;
- the taskbar command contracts: activate, minimize, restore, close,
  show home or open a surface provided by the host;
- no host theme model. Since the update of 2026-08-19, the
  framework-neutral catalog lives in `HackerOs.Theming.Abstractions` and the
  visual/static asset vocabulary in `HackerOs.Theming.Blazor`; the chrome packages
  consume only the `--hos-*` tokens, never arbitrary CSS.

The `Shared/HackerOs.AppSdk.Icons` project already exists and provides a render-independent
icon identity model; the window and taskbar icon contracts
must reuse it rather than recreate one.

The HackerOS-specific identities (`ProcessId`, `AppInstanceId`, `AppId`) must
not be mandatory in the exportable engine. Use a validated opaque owner
identifier, then provide a HackerOS adapter that keeps the existing
process/application relationships.

### 3.3 `HackerOs.Windowing.Core`

Headless library containing:

- `WindowRuntime`;
- atomic application of commands;
- focus and Z order;
- move, resize and constraints;
- minimize, maximize and restore;
- modality and owner/child relationship;
- adaptation to work area changes;
- optional serializable geometry state;
- deterministic events observable by the host.

It depends on `HackerOs.Windowing.Abstractions` and may reference
`Microsoft.AspNetCore.Components` (only for the `RenderFragment` delegate type
carried by `WindowRuntimeState.Content`, cf. 2.1 and 3.2). It must contain no
`.razor` component, no `RenderFragment` produced by the engine itself, no
MudBlazor and no JavaScript interop: the engine stays headless, it only consumes
the content that the caller provides to it without ever building it.

### 3.4 `HackerOs.Windowing.Blazor`

Razor Class Library containing:

- `DesktopArea.razor`;
- `WindowHost.razor`;
- `WindowChrome.razor`;
- the associated `.razor.css` files;
- the `.razor.js` modules strictly necessary for Pointer Events, geometry
  projection and focus management;
- a configurable root component, for example `WindowSurface`, which receives a
  runtime and a content render fragment; **implemented**, see the note
  under `EXT-WIN-005` in section 10;
- icon and chrome abstractions allowing the consumer to choose its
  rendering without imposing MudBlazor.

The package must embed its resources through the standard Razor
Class Library mechanisms (`_content/{PackageId}/...`). No path may depend on the
name of the host project.

### 3.5 `HackerOs.Taskbar.Blazor`

Separate Razor Class Library so that a consumer can use the window engine
without adopting the HackerOS taskbar.

The taskbar must receive interfaces rather than directly injecting:

- the concrete `WindowRuntime`;
- `AppCatalog`;
- `ISimulationClock`;
- `INotificationQueue`;
- `ISessionService`;
- `AppIntentDispatcher`.

Proposed contracts:

- `ITaskbarWindowSource`: ordered snapshot of displayable windows;
- `ITaskbarCommandDispatcher`: activation, minimization, restoration and closing;
- `ITaskbarLauncher`: opening of the launcher provided by the host;
- `ITaskbarStatusSource`: time, network, simulated battery or custom states;
- `ITaskbarNotificationSource`: counter and opening of the notification center;
- `ITaskbarSessionCommands`: optional lock, sign-out or shutdown;
- `TaskbarOptions`: zone visibility, order, density and labels.

Each optional feature must disappear cleanly when its contract
is not provided. The taskbar must not create a fake global service to
replace a missing integration.

**Implemented** with only `RequestLogout()` on `ITaskbarSessionCommands`
(lock/shutdown not exposed for lack of a real consumer; to add when a
host needs them, not before) and `TaskbarOptions` limited to the launcher labels
(`LauncherMark`/`LauncherLabel`, the only HackerOS text currently hard-coded);
zone visibility is already covered by the absence/presence of each contract,
so not duplicated in the options.

**Razor compilation pitfall to remember** for any future exported Razor Class Library
(Phase D included): a `Microsoft.NET.Sdk.Razor` project that references
the `Microsoft.AspNetCore.Components.Web` package but whose `_Imports.razor`
does **not** have `@using Microsoft.AspNetCore.Components.Web` compiles with no
error or warning, but Razor then recognizes no `@onclick` (nor the
other DOM events) as an event binding: it renders them as a literal
HTML attribute (`@onclick="..."` visible as is in the DOM), and clicks
therefore silently do nothing. `HackerOs.Windowing.Blazor` (3.4) had this
`@using`; `HackerOs.Taskbar.Blazor` did not have it at creation and stayed
broken throughout the development of the component until it was
diagnosed in the browser. Systematically check this `@using` in the
`_Imports.razor` of any new RCL, and confirm with a browser test
that a click produces an observable effect, not only that the build succeeds.

### 3.6 `HackerOs.Platform.Blazor`

The existing project becomes the HackerOS composition layer:

- implementation of the adapters between the exportable contracts and
  `AppCatalog`/processes/sessions/notifications;
- `WindowAppRenderer` and validation of `WindowAppBase`;
- `WindowCloseCoordinator` linked to `AppLifecycleOrchestrator`;
- HackerOS-specific Desktop and Mobile shells;
- application launcher, dialogs, notification center and session UX.

Thus, the exportable packages do not know the full model of the OS.

Verified in the current code: `WindowCloseCoordinator` depends on
`AppLifecycleOrchestrator` (`HackerOs.Platform.Core.Lifecycle`) and
`WindowAppRenderer` depends on `AppDescriptor`
(`HackerOs.Platform.Core.Discovery`) as well as on `WindowAppBase`/
`IWindowCloseGuard` (`HackerOs.AppSdk.Blazor`). Both therefore confirm the
boundary above and remain in `HackerOs.Platform.Blazor` without any change
of public signature during the extraction. The dialog system (2.1) follows the
same rule: `FileDialogWindowAdapter` stays in `HackerOs.Platform.Blazor` and
uses only the generic `Content`/`OnRequestClose` mechanism of the exported engine.

## 4. Expected consumption API

The internal consumer must be able to register the services without knowing
the implementation:

```csharp
builder.Services.AddHackerOsWindowing(options =>
{
    options.InitialWorkArea = new WindowBounds(0, 0, 1280, 720);
});

builder.Services.AddHackerOsTaskbar();
```

The Razor composition must stay simple:

```razor
<WindowSurface Runtime="WindowRuntime"
               WindowContent="RenderWindowContent" />

<HackerTaskbar WindowSource="TaskbarWindowSource"
               Commands="TaskbarCommands"
               Options="TaskbarOptions" />
```

These examples represent the ergonomics sought. The final names must
be chosen after the public API review.

## 5. Migration strategy without breaking changes

1. First copy the generic contracts into the new projects without deleting
   the existing types.
2. Write the contract tests of the extracted engine.
3. Have the new engine used through adapters keeping the current
   HackerOS signatures.
4. Move the Razor components and their collocated assets.
5. Replace the JS import paths with the `_content` paths of the new project.
6. Extract the taskbar after the engine has stabilized and introduce its abstract
   data sources.
7. Migrate `DesktopShell` to the new components.
8. Add a sample host that references neither `HackerOs.Ecosystem` nor the HackerOS
   application projects.
9. Delete the old types only after all tests and
   consumers succeed.
10. Keep, if necessary, an obsolete facade for one version in order to
    ease internal migrations.

## 6. NuGet preparation

Each project intended for publication must define:

- `PackageId`, `Version`, `Authors`, `Description`, `PackageTags` and license;
- generation of the package and the symbols;
- public XML documentation without warnings;
- `README` included in the package;
- minimal dependencies with explicit compatible versions;
- build determinism and Source Link;
- Razor static resources correctly packaged;
- documented and tested trimming compatibility;
- SemVer policy and API compatibility baseline.

Do not publish immediately. First produce the `.nupkg` files locally and
consume them from a temporary NuGet source in the sample host. This will detect
the transitive dependencies or assets that work through project reference
but are missing in a package.

## 7. Theme and customization

- The exportable components provide accessible fallback values.
- Colors, spacing, sizes, radii and animations use a documented
  set of prefixed CSS variables.
- The consumer overrides the tokens in its global stylesheet; it does not provide
  executable CSS text to the component.
- The chrome and the taskbar expose bounded icon fragments or contracts.
- The base features remain usable without MudBlazor.
- If a MudBlazor variant is kept, it belongs to an optional adapter
  package, not to the engine or the abstractions.

## 8. Required tests

### Headless tests

- creation and deletion;
- single focus and Z order;
- move and eight resize directions;
- min/max constraints and viewport change;
- minimize, maximize and restore;
- owner/modal, closing and focus restoration;
- deterministic order of events;
- separation between generic identity and HackerOS metadata.

### Component tests

- rendering of the content provided by a consumer;
- complete keyboard navigation;
- accessible labels and reduced animations;
- taskbar with all optional sources present or absent;
- theme adaptation through tokens;
- absence of inline CSS/JS in Razor.

### Browser and packaging tests

- mouse, touch and pen drag/resize;
- focus and Z order without rendering race;
- import of modules from `_content`;
- console and network without errors;
- sample application consuming the projects;
- sample application consuming the local `.nupkg` files;
- Release publication and trimming analysis.

## 9. Risks to control

- **Leak of the HackerOS domain:** prevent the process, catalog or
  session types from becoming mandatory dependencies of the package.
- **API too generic:** keep contracts oriented to windows and taskbar,
  without building a universal UI framework.
- **Assets missing in NuGet:** test the produced packages, not only the
  project references.
- **Fragile interop:** make framework initialization unavoidable and
  destruction deterministic.
- **Double state:** `WindowRuntime` remains the single source of truth.
- **Transitive MudBlazor:** isolate or remove this dependency in the generic
  packages.
- **Mobile break:** do not freeze in the engine assumptions specific to
  Desktop floating windows. The mobile plan is defined in
  `docs/mobile-interface-platform-plan.md`.

## 10. Implementation plan

- [x] `EXT-WIN-001` Create `HackerOs.Windowing.Abstractions` and document its public
  API. **Completed 2026-08-17** (late follow-up, after Phase D): the
  initial deferral ("the generic contracts live in `HackerOs.Windowing.Core`
  ... the actual split remains to be done before Phase B") had never been
  closed although Phases B to D had already finished without it.
  `WindowId`, `WindowOwnerId`, `WindowBounds`, `WindowConstraints`,
  `WindowVisualState`, `WindowModality`, `WindowRuntimeState`, and all the
  commands/events (`WindowCommand`/`WindowEvent` and their derived types)
  are moved into the new project `Platform/HackerOs.Windowing.Abstractions/`
  (same type names, namespace `HackerOs.Windowing.Abstractions` instead of
  `HackerOs.Windowing.Core`; a non-backward-compatible change but without real
  risk since `PublicAPI.Shipped.txt` was empty for both projects, so
  nothing had yet been published under SemVer). `HackerOs.Windowing.Core` now
  contains only `WindowRuntime` (the engine) and references Abstractions by
  `ProjectReference`. The 30 consumer files (`HackerOs.Windowing.Blazor`,
  `HackerOs.Taskbar.Blazor`, `HackerOs.Platform.Blazor`, the sample host, and
  all the test projects concerned) received the additional `using`/`@using`
  needed. `dotnet build HackerOs.sln` in Debug and in Release passes with 0
  warnings/0 errors (including the `PublicApiAnalyzers` baselines
  regenerated via `dotnet format analyzers --diagnostics RS0016`), and
  `dotnet test` on `HackerOs.Windowing.Core.Tests`, `HackerOs.Taskbar.Blazor.Tests`,
  `HackerOs.Platform.Blazor.Tests` and `HackerOs.Ecosystem.Tests` passes
  entirely. Verified in the browser: the sample host and the
  `window`/`taskbar` scenarios of the harness work identically, without console
  error.
- [x] `EXT-WIN-002` Decouple the generic identities from the HackerOS process
  identities. `WindowRuntimeState` now carries `WindowOwnerId` (opaque,
  derived from the app instance) instead of `ProcessId`/`AppInstanceId`.
- [x] `EXT-WIN-003` Move the headless engine into `HackerOs.Windowing.Core`.
  The project now references only `Microsoft.AspNetCore.Components`, no
  HackerOS dependency.
- [x] `EXT-WIN-004` Port the runtime tests and add a HackerOS-independent
  contract suite. `HackerOs.Windowing.Core.Tests` (11 tests) references
  only `HackerOs.Windowing.Core`.
- [x] `EXT-WIN-005` Create the Razor Class Library `HackerOs.Windowing.Blazor` with
  collocated assets. `DesktopArea.razor`, `WindowHost.razor` and
  `WindowChrome.razor` (+ `.razor.css`/`.razor.js`) moved from
  `Platform.Blazor`, served from `_content/HackerOs.Windowing.Blazor/...`.
  `WindowSurface` (see 3.4) is **implemented 2026-08-17**: configurable
  component that receives `Runtime` (a `WindowRuntime`) and `WindowContent`,
  subscribes itself to `Runtime.StateChanged`, and translates the
  `OnFocus`/`OnMinimize`/`OnToggleMaximize`/`OnClose`/`OnGesture` callbacks of
  `DesktopArea` into `Runtime.Apply(...)` calls, exactly the boilerplate that
  `HackerOs.Windowing.SampleHost/App.razor` had reimplemented by hand until then.
  The sample host is migrated to `<WindowSurface Runtime="WindowRuntime">` to
  prove real consumption, not only compilation; verified in the
  browser: opening through the launcher, focus, interaction (increment),
  maximize/restore, minimize/restore from the taskbar, and close all work
  without console error. `DesktopShell` (the full HackerOS host) is
  deliberately not migrated to `WindowSurface`: it also handles dialogs
  (`Content`/`OnRequestClose`) and closing with guard/confirmation through
  `WindowCloseCoordinator`, two concerns outside the minimal scope that
  `WindowSurface` deliberately covers; forcing it would introduce a break
  without real benefit, contrary to the "migration without breaking changes" spirit of
  section 5.
- [x] `EXT-WIN-006` Eliminate the mandatory MudBlazor dependencies of the exportable
  chrome. `WindowChrome.razor` no longer uses `MudIconButton`; native
  buttons + scoped-CSS SVG icons. Verified in the browser: minimize,
  maximize/restore, close and the pointer move gesture
  work identically.
- [x] `EXT-WIN-007` Define the taskbar source and command contracts.
  `ITaskbarWindowSource`, `ITaskbarCommandDispatcher`, `ITaskbarLauncher`,
  `ITaskbarStatusSource`, `ITaskbarNotificationSource`,
  `ITaskbarSessionCommands`, `TaskbarOptions` in `HackerOs.Taskbar.Blazor`.
- [x] `EXT-WIN-008` Create `HackerOs.Taskbar.Blazor` and its component tests.
  `Taskbar.razor` now receives only these contracts (all nullable) instead of
  injecting `WindowRuntime`/`ISimulationClock`; each optional zone
  disappears cleanly when its contract is not provided (verified in the
  browser for all five). `HackerOs.Taskbar.Blazor.Tests` covers the pure
  interaction logic (`TaskbarWindowInteraction`, 4 tests). Pitfall
  encountered, see the note in section 3.5 below.
- [x] `EXT-WIN-009` Implement the HackerOS adapters in
  `HackerOs.Platform.Blazor`. Six classes in `Shell/TaskbarAdapters.cs`:
  `TaskbarWindowSourceAdapter`, `TaskbarCommandDispatcherAdapter` (routes
  `Close` through the same close-guard mechanism as `DesktopShell`),
  `TaskbarLauncherAdapter`/`TaskbarNotificationSourceAdapter` (themselves own
  their open/closed state so that the shell panel and the taskbar
  trigger stay synchronized whichever of the two changed the state),
  `TaskbarStatusSourceAdapter` (tick every simulated second),
  `TaskbarSessionCommandsAdapter`.
- [x] `EXT-WIN-010` Migrate `DesktopShell` without changing its public behavior.
  The former `Platform.Blazor/Shell/Taskbar.razor` is deleted (no more
  references). Fully verified in the browser against the complete HackerOS
  host: launcher, app selection, minimize/restore through the taskbar,
  notifications, Esc key, window closing, sign-out, pointer move
  gesture. A real bug found by this verification (not by
  compilation): `OpenLogout()` invoked through a raw method call
  on the adapter instead of a Blazor `EventCallback` no longer triggered the
  automatic re-render of the parent; fixed by an explicit call.
- [x] `EXT-WIN-011` Create an internal sample host without dependency on
  `HackerOs.Ecosystem`. `Samples/HackerOs.Windowing.SampleHost` references
  only the exportable desktop closure: `HackerOs.Theming.Abstractions`,
  `HackerOs.Theming.Blazor`, `HackerOs.Windowing.Abstractions`,
  `HackerOs.Windowing.Core`, `HackerOs.Windowing.Blazor`,
  `HackerOs.AppSdk.Icons` and `HackerOs.Taskbar.Blazor`; its own application
  source (two
  trivial Blazor components), its own taskbar adapters, and
  enables neither notifications nor session commands (zones cleanly absent,
  verified in the browser). Confirms that the exported engine/chrome/taskbar
  are usable without any HackerOS type.
- [x] `EXT-WIN-012` Add the packaging metadata and produce local
  NuGet packages. `Platform/Packaging.props` is imported by the seven projects of
  the desktop closure: the four historical Windowing/Taskbar packages,
  `HackerOs.Theming.Abstractions`, `HackerOs.Theming.Blazor`, as well as
  `HackerOs.AppSdk.Icons` (public dependency of Taskbar). It sets
  `Version=0.1.0-local`, `Authors`, `RepositoryUrl`/`RepositoryType`,
  `PackageTags`, `PackageLicenseFile`/`PackageReadmeFile`,
  `GenerateDocumentationFile=true`, `.snupkg` symbols, and GitHub SourceLink
  (`EmbedUntrackedSources`). Each project adds its `PackageId` and its
  `Description`. A `README.md` exists in each of the seven project
  folders. `dotnet pack -c Release -o artifacts/local-nupkg` produces
  the seven packages in topological order Theme contracts, Theme Blazor,
  Windowing abstractions, Windowing core, Windowing Blazor, Icons, Taskbar
  (`.nupkg` + `.snupkg`) without warnings, including
  under `TreatWarningsAsErrors=true` with generated XML doc. The addition of
  Icons to the closure also completed the six missing public XML comments of
  `IconCatalog`, without behavioral change.
  `artifacts/` and `*.nupkg` are already covered by the repository `.gitignore`.
- [x] `EXT-WIN-013` Test a sample consuming only the local packages.
  `Samples/HackerOs.Windowing.SampleHost/nuget.config` declares a local
  source (`<clear/>` + `nuget.org` + `../../artifacts/local-nupkg`). The csproj
  keeps the `ProjectReference` by default for development, but
  `-p:UseLocalHackerOsPackages=true` replaces them with seven
  `PackageReference Version="0.1.0-local"`. The dedicated workflow
  `.github/workflows/window-taskbar-packages.yml` packs the closure in
  topological order, restores the sample in an isolated NuGet cache, checks
  that `project.assets.json` contains no dependency of type `project`,
  then builds and publishes the sample in Release.
  Verified in the browser (harness `hackeros-windowing-sample-host`, port 5254):
  no console error, identical rendering (launcher + clock, without
  notifications/session as expected), and above all the packaged static assets
  of the RCL (`DesktopArea.razor.js`, `WindowHost.razor.js`,
  `WindowChrome.razor.js`) load with `200 OK` from
  `_content/HackerOs.Windowing.Blazor/...`: historical proof that the
  `staticwebassets/` content of the `.nupkg` works at runtime, not only by
  coincidence of project reference. The workflow also checks that
  `_content/HackerOs.Theming.Blazor/themes.css` is physically present in
  the package-only publication. The window pointer move gesture
  (synthetic `pointerdown`/`pointermove`/`pointerup`) was also
  replayed successfully against the packaged assets, with an exact delta
  (`+60,+30`). The default mode remains `ProjectReference` (normal development
  ergonomics in the repository); `nuget.config` and the conditional mode
  allow replaying the verification without editing the csproj.
  `HackerOs.MobileShell.Blazor` is package-ready and shares the theme, but
  deliberately remains outside this **desktop** closure/test EXT-WIN-013.
- [x] `EXT-WIN-014` Add API baseline, Release/trimming tests and versioning
  documentation. `Microsoft.CodeAnalysis.PublicApiAnalyzers` (3.3.4) is
  referenced by the seven projects of the closure through `Packaging.props`. Each project has
  its `PublicAPI.Shipped.txt` (empty, nothing has been published yet) and
  `PublicAPI.Unshipped.txt` (current public surface, generated with
  `dotnet format analyzers --diagnostics RS0016 --include-generated`, the only
  way to also surface the members generated by the Razor compiler
  such as `OnAfterRenderAsync`/`DisposeAsync`/the `[Parameter]` accessors;
  without this flag the tool silently ignores generated code). RS0041
  ("oblivious reference type") is disabled via `NoWarn` in
  `Packaging.props` only for the `BuildRenderTree` overrides
  generated by Razor, which have no `#nullable enable` context and cannot
  be fixed by hand without breaking at every upgrade of
  the Razor SDK. Any future change to the public surface of these
  seven projects will fail the build (RS0016/RS0017) until
  `PublicAPI.Unshipped.txt` is updated: intended behavior.
  Release/trimming proof: `dotnet publish
  Samples/HackerOs.Windowing.SampleHost -c Release` finishes without any
  warning (no `warn`/`IL####` in the full output), the IL Linker
  runs ("Optimizing assemblies for size") and the seven
  assemblies of the exported closure appear
  properly trimmed in `wwwroot/_framework/` next to the sample itself.
  **Rechecked 2026-08-17** after the addition of `HackerOs.Windowing.Abstractions`,
  then extended on 2026-08-19: the workflow now produces the seven
  `.nupkg`/`.snupkg` of the desktop closure without warnings.
- [x] `EXT-WIN-015` Update the solution, the architecture documents and the
  integration list. `HackerOs.sln` already contains the 6 exported projects/tests/
  sample from the previous phases (no addition required here). Updated:
  `docs/window-runtime.md` (removes the obsolete claim "no RenderFragment"
  and "coupled to ProcessId/AppInstanceId", documents `HackerOs.Windowing.Core`,
  `WindowOwnerId`, and the approved `RenderFragment? Content`); `docs/desktop-shell.md`
  (replaces the description of the former `Shell/Taskbar.razor` with
  `HackerOs.Taskbar.Blazor.Taskbar` + `Shell/TaskbarAdapters.cs`);
  `docs/integration-task-list.md` sections 11 and 14 (relocation note
  pointing to this document, `P2-WIN-*`/`P2-SHELL-002` history kept as
  is in accordance with the "never delete a task" rule).
  `dotnet build HackerOs.sln -c Debug` finishes with 0 warnings/0 errors after
  each of the sections `EXT-WIN-012` to `014` (packaging metadata,
  `Microsoft.CodeAnalysis.PublicApiAnalyzers`, RS0041 in `NoWarn`).
  **Updated 2026-08-17** (`EXT-WIN-001`): `HackerOs.sln` now contains
  a 7th exported project, `HackerOs.Windowing.Abstractions`, added
  via `dotnet sln add ... -s Platform` (so with the
  `ProjectConfigurationPlatforms` and `NestedProjects` entries correctly generated,
  not edited by hand); `dotnet build HackerOs.sln -c Debug` and
  `-c Release` both finish with 0 warnings/0 errors.

## 11. Definition of completion

The extraction is complete when:

- [x] an application of the solution uses windows and taskbar without referencing
  `HackerOs.Ecosystem` (`Samples/HackerOs.Windowing.SampleHost`, `EXT-WIN-011`);
- [x] the headless engine depends on neither Blazor nor the HackerOS services
  (`HackerOs.Windowing.Core`: zero project references, only
  `Microsoft.AspNetCore.Components` for the `RenderFragment` delegate type);
- [x] the taskbar consumes only its public contracts
  (`HackerOs.Taskbar.Blazor`, `EXT-WIN-007`/`008`);
- [x] HackerOS keeps its behavior through explicit adapters
  (`Shell/TaskbarAdapters.cs`, verified in the browser without regression, `EXT-WIN-009`/`010`);
- [x] the headless, component and browser tests pass (`dotnet test HackerOs.sln`
  and `HackerOs.BrowserHarness.Tests` checks at each phase). A permanent Playwright
  E2E test was missing for the `taskbar` scenario (verified only by
  hand through the browser during Phase C): added
  `Taskbar_reacts_to_contracts_and_hides_optional_zones_cleanly` in
  `Tests/HackerOs.E2E.Tests/IndexedDbBrowserContractTests.cs`, which exercises the
  background→focused→minimized→focused cycle by click, the dynamic addition of a
  window, notification and clock pushed without a taskbar command, the
  launcher, sign-out, and the clean disappearance/reappearance of each
  of the 5 optional zones when its contract is removed; `taskbar` was also
  added to the scenario list of the axe-core accessibility test. Both
  suites `HackerOs.E2E.Tests` (15/15) and `HackerOs.UI.E2E.Tests` (4/4) pass
  entirely (requires a prior Release build of
  `Tests/HackerOs.BrowserHarness.Tests` and `test/test`, which these suites launch
  themselves via `dotnet run --no-build -c Release`);
- [x] the static resources work from a project reference and from
  a local NuGet package (`EXT-WIN-011` then `EXT-WIN-013`);
- [x] the Release publication produces no unexplained diagnostic
  (`EXT-WIN-014`: `dotnet publish -c Release` of the sample, 0 warnings);
- [x] the public documentation allows another developer to integrate the packages
  without reading their source code (`README.md` in each of the seven projects of the closure,
  complete public XML doc under `GenerateDocumentationFile=true`).
- [x] the same project reference can apply the nine themes to the chrome and to
  the taskbar through a single `ThemeScope`, without copying assets into the host; see
  [`theming.md`](theming.md) and ADR 0041. This improvement of 2026-08-19 does
  not change the headless engine contracts.

**State: extraction completed on 2026-08-13, closed for good on 2026-08-17.**
`EXT-WIN-001` to `015` are all complete; see section 10 for the phase-by-phase
detail. On 2026-08-13, `EXT-WIN-001` remained explicitly deferred
(`[~]`) and the `WindowSurface` component of section 3.4 did not exist;
the status line of that date wrongly claimed that everything was complete. These
two points are closed on 2026-08-17: see `EXT-WIN-001` (section 10) and
the `WindowSurface` note under `EXT-WIN-005` for the evidence.
