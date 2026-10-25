# Mobile platform and interface plan

> Update of 2026-10-23: paginated home screen, dock, drawer and mouse aids are
> described in [mobile-home-screen.md](../mobile-home-screen.md) and
> [ADR 0165](../adr/0165-mobile-home-drawer-and-mouse-controls.md), which
> complement the navigation primitives of this archived plan.

**Status:** Phase 0 (foundation) done on 2026-08-18, Phase 1 (manifest and
entry point resolution) done on 2026-08-18, Phase 2a (Mobile shell
primitives), Phase 2b (`MOB-008`, controlled shell switch), Phase 2c
(`MOB-012`/`MOB-013`, application Back contract) and `MOB-011` (navigation
stack and Recent surface, completing steps 1/3/4/5 of §7.3, so Phase 2
is entirely closed) done between 2026-08-18 and 2026-08-23, then
`MOB-015` (reusable virtual keyboard, generalized beyond the Terminal alone,
see §16.11) done on 2026-08-23; see §16; the rest (`MOB-016` to `MOB-018`)
remains at the proposal stage  
**Initial modes:** `desktop`, `mobile`  
**Required extensibility:** later addition of other platforms without reworking the
manifest or the catalog

## 0. Update note (2026-08-18)

This document was written on 2026-08-03, **before** the extraction of the
windows/taskbar engine into standalone packages (`HackerOs.Windowing.Core`,
`HackerOs.Windowing.Abstractions`, `HackerOs.Windowing.Blazor`,
`HackerOs.Taskbar.Blazor`, finalized on 2026-08-17, see
[`window-taskbar-export-plan.md`](window-taskbar-export-plan.md) and
[`progress-and-plan-2026-08-17.md`](progress-and-plan-2026-08-17.md)). The original
text below never names these packages; §16 links the concepts it describes
to their actual location in the code, and resequences
`MOB-002` to `MOB-018` into phases in light of what exists now.

A first slice ("Phase 0") was implemented on 2026-08-18: it lays down
`AppPlatformId`, the `UiPlatformPreference` preference (persistence only, no
shell switch yet), and, anticipating the need for a UI entry point
for this toggle, a `RenderFragment` extensibility on the taskbar
(`ITaskbarClockPanelSource`) allowing a notifications/calendar/toggle
panel to be opened by clicking the clock, with the panel content designed entirely outside the taskbar package. Full detail in §16.

## 1. Goal

Add a complete Mobile mode to HackerOS while keeping the Desktop mode.
An application will be able to support one or more platforms and use either a
shared entry point or a distinct entry point per platform, in a single
manifest and with a single `AppId`.

The Mobile mode must:

- display a single full-screen application at a time;
- replace the Desktop taskbar with an Android-inspired navigation bar
  with Triangle/Back, Circle/Home and Square/Recent;
- offer a coherent navigation stack;
- provide the Terminal with a HackerOS virtual keyboard without opening the phone's
  native keyboard;
- filter the applications incompatible with the active platform;
- allow explicit switching between Desktop and Mobile;
- remember the choice per device;
- use browser detection as long as no explicit choice has been
  recorded.

The Back button in the application's own menu bar is a Desktop-only
feature. On Mobile, Back navigation is provided by the triangle of
the system bar.

## 2. Vocabulary

- **Platform:** logical presentation environment such as `desktop` or
  `mobile`. It is not the real operating system of the phone.
- **Active mode:** platform currently selected by the HackerOS shell.
- **Automatic detection:** platform suggested by the capabilities reported by
  the browser when no explicit preference exists.
- **Platform entry point:** assembly and type used to launch an
  application on one or more platforms.
- **Application Back:** typed request allowing the active application to return
  to its previous internal state.
- **Home:** return to the Mobile home without automatically terminating the application.
- **Recent:** surface displaying the recent mobile applications/windows.

## 3. Extensible platform model

### 3.1 Do not use a closed enum

Introduce a validated identifier, for example `AppPlatformId`, containing a normalized
string:

- ASCII lowercase;
- segments separated by `-` if necessary;
- bounded length;
- known initial values: `desktop` and `mobile`.

The catalog of known platforms belongs to the policy/build system, not to
a `switch` compiled into each application. Adding a third platform
should mainly require:

1. registration of its identifier and its shell capabilities;
2. a shell implementation;
3. compatible application entry points;
4. tests and a build profile.

Existing manifests must not become invalid simply because a
new platform is added.

### 3.2 Capabilities described by the platform

A registered platform exposes a descriptor such as:

- identifier;
- shell family;
- support for floating windows;
- number of visible main surfaces;
- move/resize/minimize/maximize support;
- type of system navigation;
- presence of an application bar;
- keyboard strategy;
- viewport constraints and safe areas.

Application code queries these capabilities through a contract when it needs to adapt a
shared component. It must not multiply direct
`if (platform == mobile)` tests when a capability expresses the need better.

## 4. Single manifest and multiple entry points

### 4.1 Proposed form

Evolve the manifest schema towards a collection of entry points. An
entry can cover one or more platforms:

```json
{
  "id": "org.hackeros.example",
  "kind": "window",
  "platform": {
    "supported": ["desktop", "mobile"],
    "entryPoints": [
      {
        "platforms": ["desktop", "mobile"],
        "assembly": "HackerOs.Apps.Example.dll",
        "type": "HackerOs.Apps.Example.SharedExampleApp"
      }
    ]
  }
}
```

Application using two distinct components with the same `AppId`:

```json
{
  "id": "org.hackeros.example",
  "kind": "window",
  "platform": {
    "supported": ["desktop", "mobile"],
    "entryPoints": [
      {
        "platforms": ["desktop"],
        "assembly": "HackerOs.Apps.Example.dll",
        "type": "HackerOs.Apps.Example.DesktopExampleApp"
      },
      {
        "platforms": ["mobile"],
        "assembly": "HackerOs.Apps.Example.dll",
        "type": "HackerOs.Apps.Example.MobileExampleApp"
      }
    ]
  }
}
```

A Desktop-only application declares only `desktop`. It does not appear
when `mobile` is active. The rule is symmetrical for a Mobile-only
application.

### 4.2 Validation rules

- `supported` contains at least one platform known to the build profile.
- Each supported platform is covered by exactly one entry point.
- An entry point can cover several platforms.
- Two entry points cannot cover the same platform for the same
  manifest, to avoid an ambiguous resolution.
- An entry point cannot reference a platform absent from `supported`.
- All entry points must match the declared `kind` and respect the
  same base/visibility/trimming rules.
- Identity, version, permissions, settings, associations and
  data belong to the common manifest/AppId, not to each entry point.
- Resources can be common or targeted per platform with an
  explicit declaration.
- Dependencies must be resolved for the active platform; an unavailable mandatory
  dependency makes the application unavailable on that platform.

### 4.3 Compatibility and migration

- Introduce a new version of the manifest schema.
- Migrate the old `entryPoint` field to an entry covering `desktop` by default.
- Provide a clear error if an old manifest is launched in Mobile without a
  compatible declaration.
- Update the JSON Schema, the `System.Text.Json` context, the fixtures, the
  CLI validator, the templates and the SDK guides.
- Do not durably maintain two independent sources of truth
  (`entryPoint` and `entryPoints`). Read compatibility may be
  temporary, but canonical serialization uses the new model.

## 5. Entry point resolution

Add a shell-independent service, for example
`IAppPlatformEntryPointResolver`:

```text
Manifest + active platform
  -> compatibility validation
  -> single entry point
  -> type discovery
  -> effective descriptor
  -> launch
```

The catalog keeps a single application identity. It must not create two
cataloged applications for the Desktop and Mobile variants.

Resolution by platform occurs before:

- display in the launcher;
- selection of a file handler;
- resolution of an intent;
- lazy loading of an assembly;
- creation of a process and a surface;
- singleton control.

Expected consequences:

- an incompatible application does not appear in the active launcher;
- it is not offered in Open With;
- an explicit intent towards it returns a typed result
  `PlatformUnsupported`;
- its services can be handled separately if their entry point is not a
  UI surface and if the manifest declares them compatible;
- the same `AppId` keeps settings, grants, associations and data during a
  platform change.

## 6. OS mode selection and persistence

### 6.1 Preference model

Persist a per-device preference:

```text
UiPlatformPreference
  SelectionSource: Auto | Explicit
  ExplicitPlatformId: string?     # required only for Explicit
  LastResolvedPlatformId: string  # diagnostic, not a source of authority
  Revision
```

This preference must be stored in a device-scoped settings document of the
system shell, for example under the protected AppId `org.hackeros.shell`. It must not
follow the user in roaming, because the choice is specific to the device/browser.

### 6.2 Automatic detection

Create `IPlatformEnvironmentProbe` behind a Browser implementation. The automatic
decision combines signals rather than the width alone:

- reported viewport and screen;
- coarse/fine pointer;
- presence of hover;
- touch points;
- PWA/standalone mode;
- user-agent client hints when available, used as a hint and not as the
  sole authority.

The policy returns a reasoned and loggable result. Example:

```text
mobile if touch/coarse environment without hover and logical width below the threshold
desktop otherwise
```

The thresholds are configurable and tested. A one-off resize of a Desktop
window must not be confused with a device change.

### 6.3 Explicit change

The mode can be changed in Settings and, possibly, in a quick action
of the shell. During an explicit change:

1. validate that the target platform is installed and enabled;
2. warn the UI applications and ask for confirmation for dirty states;
3. cleanly cancel the surface operations in progress;
4. stop the UI instances with a typed reason `PlatformChanged`;
5. keep the compatible services that do not depend on the shell, or
   restart them if their contract requires it;
6. persist the device-scoped preference atomically;
7. rebuild the target shell;
8. resolve the launcher, associations and entry points again;
9. display Home on Mobile or the desktop on Desktop.

Do not hot-swap the component type of an existing instance. A
Desktop instance and a Mobile instance can share the same `AppId`, but the
entry point change goes through a controlled stop/relaunch in order to
respect the lifecycle and the resources.

Provide a "Use automatic detection" action that removes the
explicit override and returns to the browser result.

## 7. Mobile shell

### 7.1 Full-screen windows

In Mobile:

- a single main surface is visible;
- its bounds correspond to the work area between safe areas, system bar and
  possible virtual keyboard;
- move, resize, chrome minimization, maximization and
  restoration are unavailable;
- dialogs are full screen or presented as adapted modal sheets;
- secondary windows belonging to the application are represented in the
  navigation stack rather than as floating windows when possible;
- the process and lifecycle state stays managed by the same kernel.

The exportable engine must not simulate a reduced 1280x720 window. The presentation
mode must apply a real `SingleFullScreenSurface` policy.

### 7.2 Android-inspired system bar

Create `MobileSystemNavigationBar.razor` and its collocated CSS file. The bar is
fixed at the bottom, respects `env(safe-area-inset-bottom)` and exposes three buttons with
accessible labels:

- **Triangle: Back**;
- **Circle: Home**;
- **Square: Recent**.

The visual inspiration corresponds to classic Android navigation, without copying
proprietary assets. Use CSS/SVG shapes owned by the project,
sized for a touch target of at least 44×44 CSS pixels.

### 7.3 Back semantics

Processing order of the Mobile triangle:

1. close or go back in the topmost system dialog/modal if it is cancelable;
2. call the Back handler of the active application if it explicitly exposes this
   support and can go back;
3. return to the previous entry of the application or shell navigation
   stack;
4. if no previous state exists, close/remove the active surface according to its
   closing policy and return to Home;
5. at Home without history, do nothing and produce a discreet accessible feedback.

Back must not bypass a confirmation of unsaved data.

### 7.4 Home semantics

- Hide the active surface and display the Mobile home.
- Do not automatically terminate the process.
- Publish a blur/background event in the OS session.
- Respect the simulated suspension policy and the resource limits.
- A new press on the application icon restores its singleton instance or
  creates one according to its manifest.

### 7.5 Recent semantics

- Display the recent UI instances compatible with the active platform.
- Present a safe preview, the title and the icon without exposing sensitive content
  when an application requests preview protection.
- Allow activation and closing with possible confirmation.
- Do not confuse Recent with the list of all processes/services.
- Exclude applications that became incompatible or disabled after a
  policy update.

## 8. Application Back contract

Add an SDK contract without dependency on the shell component, for example:

```csharp
public interface IAppBackHandler
{
    bool CanNavigateBack { get; }
    ValueTask<AppBackResult> NavigateBackAsync(
        AppBackRequest request,
        CancellationToken cancellationToken);
}
```

The result is typed: `Handled`, `NotHandled`, `Blocked`, `ConfirmationRequired`
or `Faulted`. Changes of `CanNavigateBack` are observable so that the
shell updates its controls without polling.

The manifest explicitly declares Back support for each entry point or
for the shared application. Discovery validates that the type implements the
announced contract.

### Desktop

- If the application declares Back, display a Back button in its menu bar or
  Desktop application bar.
- The button is disabled when `CanNavigateBack` is false.
- It is not added to the system chrome of applications that do not declare it.
- The documented keyboard shortcut uses the same contract.

### Mobile

- Do not duplicate the Back button in the application bar.
- The system triangle calls the same contract.
- An application can render its own internal navigation, but must not
  hide or silently intercept the system bar.

## 9. Terminal and Mobile virtual keyboard

### 9.1 Principle

On Mobile, the Terminal uses a HackerOS virtual keyboard integrated into its surface.
The phone's native keyboard must not open for the terminal area.

Create a common terminal input abstraction so that physical keyboard,
virtual keyboard and tests produce the same typed events towards the session.

### 9.2 Minimum features

The virtual keyboard must provide:

- letters/digits layout;
- symbols layout useful to the shell (`/`, `-`, `_`, `.`, `|`, `>`, `<`, `~`,
  `*`, quotes and backslash);
- Shift and caps lock;
- Ctrl, Alt/Meta according to the terminal policy;
- Escape, Tab, Enter and Backspace;
- arrow keys, Home/End and Page Up/Page Down;
- useful shortcuts such as Ctrl+C, Ctrl+D, Ctrl+L and Ctrl+R;
- visual state of the modifiers;
- accessible labels and configurable tactile/visual feedback;
- bounded repeat for Backspace and arrows;
- internationalization of labels without changing the shell characters.

### 9.3 Browser integration

- The terminal surface must not depend on a text `<input>` that triggers the
  native keyboard.
- Use `inputmode="none"`, programmatic focus and minimal interop only
  if the mobile engines require it; actually test Chrome Android and the
  chosen Safari/iOS policy.
- Provide an explicit fallback if a browser forces its keyboard anyway,
  without creating two competing input sources.
- When the HackerOS keyboard appears, recompute the terminal work area and its
  number of rows/columns without covering the prompt.
- Orientation and safe-area changes trigger a bounded and cancelable
  terminal resize event.
- The keyboard must never receive or keep sensitive content outside the
  active session.

### 9.4 Desktop

The Desktop Terminal keeps the physical keyboard and current behavior. An optional virtual
keyboard can be exposed as an accessibility tool, but it is not
enabled automatically.

## 10. Lifecycle and data during the platform change

- A platform change never changes the `AppId`.
- Grants, settings, private data and associations stay associated with the
  common application.
- Presentation settings specific to a platform use distinct keys or
  documents under the same AppId.
- Desktop geometry is not applied in Mobile.
- The Mobile Recent history is not interpreted as a Desktop window
  placement.
- Pending intents and files are kept only if their contract is
  serializable and if the target entry point accepts them.
- Volatile component states are not implicitly transferred between two
  entry types. An application can provide an explicit migration/reconstruction
  through its persistent model.
- If an active application does not support the target platform, ask for
  confirmation if it contains a dirty state, then stop it and remove it from the visible
  surfaces.

## 11. Accessibility, orientation and responsive design

- Respect WCAG 2.2 AA, zoom and user text sizes.
- Touch targets of at least 44×44 CSS pixels.
- Navigation usable with a screen reader and external keyboard.
- Announce Home/Recent/Back changes and the platform change.
- Support portrait and landscape without covering content, dialogs or terminal.
- Use safe-area insets for devices with a notch or system bar.
- Do not assume a fixed width nor identify Mobile through user-agent alone.
- Test long texts, translations, RTL according to the product decision, contrast,
  reduced motion and 200/400% magnification.

## 12. Security and authority

- Changing the presentation mode is a device-scoped setting allowed to
  the user of the device; modifying the installed/enabled platforms remains an
  Administrator/System operation.
- An application cannot force the global mode without a dedicated intent/capability and
  user confirmation.
- The platform identifier provided by the browser grants no capability.
- A Mobile variant does not obtain more permissions than the Desktop variant of the
  same AppId.
- Recent previews respect sensitive content.
- The terminal keyboard must not inject commands other than through the typed
  input stream of the active session.
- Entry point routing always validates assembly, type, AppId, kind,
  platform, build profile and SDK compatibility before instantiation.

## 13. Required tests

### Manifests and catalog

- single entry point covering Desktop and Mobile;
- two distinct entry points under the same AppId;
- Desktop-only application hidden in Mobile and vice versa;
- missing coverage, duplicate or unknown platform rejected;
- fictitious third platform registered without changing the parser;
- resolution of intents, associations and dependencies according to the platform;
- compatibility/migration of historical manifests.

### OS mode

- first start Auto Desktop and Auto Mobile;
- explicit choice persisted per device;
- return to Auto;
- platform change with clean or dirty applications;
- compatible and incompatible services;
- reconstruction after reload;
- no state leak between two devices/users.

### Mobile shell

- surface always full screen in portrait/landscape;
- no Desktop drag/resize/chrome active;
- Back: dialog, app, history, closing and Home;
- Home keeps the process according to its policy;
- Recent activates and closes the right instances;
- safe areas, screen reader, external keyboard and focus;
- immediate disappearance of incompatible/disabled applications.

### Mobile Terminal

- no native keyboard on opening and on focus;
- input of letters, symbols and modifiers;
- Ctrl+C, Ctrl+D, Tab, Escape, arrows and repeat;
- viewport resize when the keyboard appears;
- orientation, reconnection, cancellation and closing;
- ordinary commands and full-screen terminal applications;
- no double event between physical and virtual keyboard.

### Browser/PWA

- real Chrome Android or documented emulation;
- Safari/iOS strategy and evidence according to the supported matrix;
- PWA installation, offline reload and update;
- persistence of the mode per device;
- Desktop/Mobile switch without mixing assets or entry points;
- console and network without errors.

## 14. Implementation plan

- [x] `MOB-001` Create `AppPlatformId` and the extensible platform registry.
  **Phase 0, 2026-08-18**: `Shared/HackerOs.App.Abstractions/AppPlatformId.cs`
  (validated type + `WellKnownAppPlatforms.Desktop`/`.Mobile`). No capability
  descriptors yet (`MOB-002`, still open).
- [x] `MOB-002` Define the platform capability descriptors and the shell
  contracts. **Phase 1, 2026-08-18**:
  `Shared/HackerOs.App.Abstractions/AppPlatformCapabilities.cs`
  (`AppPlatformCapabilities`, `IAppPlatformCapabilityRegistry`/
  `AppPlatformCapabilityRegistry`, descriptors `WellKnownAppPlatformCapabilities.Desktop`/
  `.Mobile`). Not yet consumed by a shell UI (that remains Phase 2, `MOB-009`/`010`).
- [x] `MOB-003` Evolve the manifest towards several entry points covering
  one or more platforms. **Phase 1, 2026-08-18**:
  `Shared/HackerOs.App.Abstractions/AppManifestPlatform.cs`
  (`AppManifestPlatform`, `AppPlatformEntryPointManifest`,
  `AppManifestPlatformSupport.Resolve` normalizer). `AppManifest.EntryPoint` became
  optional; `AppManifest.Platform` is the new field, mutually
  exclusive with `EntryPoint` (validated by `AppManifestValidator`). A
  historical manifest using only `entryPoint` is treated as covering `desktop`
  alone, without any of the 40+ existing `app.manifest.json` needing to
  be modified; see §16.5.
- [x] `MOB-004` Update JSON Schema, source-generated serialization,
  validation, fixtures, CLI and templates. **Phase 1, 2026-08-18**: schema:
  `Shared/HackerOs.App.Abstractions/Schema/manifest.schema.v1.json` (optional
  `platform` + `oneOf` rule entryPoint/platform); serialization:
  `AppManifestJsonSerializerContext` + canonical sort in
  `AppManifestJsonSerializer.CreateCanonicalManifest`; validation: see
  `MOB-003`; fixtures: `window-multi-platform.valid.json` and
  `invalid-platform-ambiguous.json` under `Schema/Fixtures/`; CLI:
  `Tools/HackerOs.Tools.ManifestValidator` works without modification (the
  source generator includes `AppManifestPlatform` automatically, type reached
  from `AppManifest`), verified by validating a real `platform` manifest.
  No `dotnet new` template project found in the repository for this batch,
  none to update.
- [x] `MOB-005` Implement `IAppPlatformEntryPointResolver` and integrate it into
  discovery, catalog, intents and associations. **Phase 1,
  2026-08-18, discovery slice only**:
  `Platform/HackerOs.Platform.Core/Discovery/AppPlatformEntryPointResolver.cs`
  + `AppEntryPointDiscovery.Discover` now takes an optional `activePlatform`
  (default `desktop`, behavior unchanged for any existing caller).
  A manifest not supporting the requested platform is excluded from the
  result without error, per §5. Integration with intents/associations
  (`FileAssociationResolver`, `AppIntentDispatcher`) is deliberately
  **deferred to Phase 2** (`MOB-008`): filtering these surfaces on the user's
  *current* platform preference before the shell knows how to rebuild cleanly
  (stopping dirty instances, etc.) would hide all existing
  apps as soon as a user flips the Phase 0 Mobile toggle, with no
  Mobile shell yet to take them over; see §16.5.
- [x] `MOB-006` Add the device-scoped `Auto | Explicit` preference and its
  migration. **Phase 0, 2026-08-18, persistence slice only**:
  `Platform/HackerOs.Platform.Core/Shell/UiPlatformPreferenceSettingsDocuments.cs`
  + `UiPlatformPreferenceService.cs` (scope `OsAdmin`/`SyncEligible:false` pending a
  durable `InstallationId` for a real `AppDevice` scope, see
  §16.3), exposed in the clock panel (§16.4). The controlled 9-step sequence
  of §6.3 (warn dirty apps, stop UI instances with the reason `PlatformChanged`,
  rebuild the shell...) remains to be done; this batch deliberately does
  nothing more than persist the choice and notify a future
  subscriber, without ever changing the rendering of `DesktopShell`.
- [x] `MOB-007` Implement the Browser `IPlatformEnvironmentProbe` with testable
  decision reasons. **Phase 0, 2026-08-18**:
  `Platform/HackerOs.Platform.Core/Shell/IPlatformEnvironmentProbe.cs`
  (`PlatformEnvironmentPolicy.Decide`, pure and testable without a browser) +
  `Platform/HackerOs.Platform.Blazor/Shell/BrowserPlatformEnvironmentProbe.cs`
  (minimal JS interop, `wwwroot/platformEnvironmentProbe.js`).
- [x] `MOB-008` Implement the controlled shell switch and the `PlatformChanged`
  stop reason. **Phase 2b, 2026-08-18, pragmatic slice, not the literal 9
  steps of §6.3**:
  `Platform/HackerOs.Platform.Blazor/Shell/PlatformShellSwitchCoordinator.cs`
  confirms each open window via `WindowCloseGuardRegistry`, stops its
  instance with `ProcessExitReason.PlatformChanged` (new, in
  `Shared/HackerOs.Simulation.Abstractions`) then closes its window, before
  persisting via `UiPlatformPreferenceService`. `ClockPanel.razor` now
  routes Auto/Desktop/Mobile through this coordinator rather than calling
  `UiPlatformPreferenceService` directly, in order to confirm *before*
  persisting (exact order of §6.3). `App.razor` subscribes to
  `UiPlatformPreferenceService.Changed` and renders `MobileShell` or `DesktopShell`
  according to `Current.ActivePlatform`; the shell switch is therefore truly
  live, without reload, proven by a real browser E2E test
  (`PlatformShellSwitchTests.cs`). Detail of the accepted simplifications in §16.7.
- [x] `MOB-009` Create the single full-screen surface Mobile shell. **Phase 2a,
  2026-08-18**: engine: `Platform/HackerOs.Windowing.Core/SingleSurfacePresentationPolicy.cs`
  (pure logic: selects the main window, minimizes the others,
  reuses Maximize to fill the work area) +
  `WindowConstraints.IsMovable` (new, applied by `WindowRuntime.Move`,
  defense in depth). Blazor: `Platform/HackerOs.Windowing.Blazor/SingleSurfaceArea.razor`
  (reuses `WindowHost` with its new parameter `ShowChrome=false`) +
  `Platform/HackerOs.Platform.Blazor/Shell/MobileShell.razor` (composes the
  single surface and the system bar). Fixing a latent bug discovered
  during the feasibility spike (`WindowChrome` already ignored `IsResizable`
  for non-resizable file dialogs), see §16.6.
- [x] `MOB-010` Create `MobileSystemNavigationBar.razor/.css` with Back/Home/Recent.
  **Phase 2a, 2026-08-18**: new package `Platform/HackerOs.MobileShell.Blazor/`
  (sibling of `HackerOs.Taskbar.Blazor`, as planned in §16.2), contract
  `IMobileNavigationCommands`. `Back`/`Recent` are documented no-ops as long as
  `MOB-011`/`MOB-012` do not exist; `Home` hides the active surface via
  Minimize without terminating the process (§7.4), implemented in
  `MobileNavigationCommandsAdapter`.
- [x] `MOB-011` Implement the navigation stack and the Recent surface.
  **2026-08-23**: see §16.10 for the file-by-file detail.
- [x] `MOB-012` Add `IAppBackHandler`, its events and the manifest
  validation. **Phase 2c, 2026-08-19**: see §16.9 for the file-by-file
  detail (`AppBackContracts.cs`, `AppManifest.SupportsBack`,
  `AppBackHandlerRegistry`). `CanNavigateBackChanged` is not yet wired to the
  chrome (see the gap documented at the end of §16.9).
- [x] `MOB-013` Add the Back button on Desktop only for applications that
  declare it. **Phase 2c, 2026-08-19**: see §16.9 (`WindowChrome`, Desktop
  only; Mobile does not duplicate it by construction, `ShowChrome=false`).
- [x] `MOB-014` Add the reference Desktop/Mobile variants under a common AppId
  and an application with a shared entry point. **Phase 1, 2026-08-18,
  partial slice: a single shared entry point** (the first example of
  §4.1, not yet the two distinct variants of the second example):
  `Apps/Samples/HackerOs.Samples.PlatformApp/` (`PlatformDemoWindow.razor` +
  `app.manifest.json` declaring `platform.entryPoints` for `desktop`+`mobile`
  through the same type) and `Tests/HackerOs.Samples.PlatformApp.Tests/` (validation,
  resolution by platform, and consistency of the archived JSON file; everything
  goes through the real `AppManifestValidator`/`AppCatalog`/`AppEntryPointDiscovery`).
  Like the existing samples, this project is not wired into the build catalog of
  `HackerOs.Ecosystem`; it is an isolated demonstration/test project,
  not an app really launchable in the shell.
- [x] `MOB-015` Implement the HackerOS virtual keyboard of the Mobile Terminal.
  **2026-08-23**: generalized into a reusable control independent of the
  Terminal, see §16.11.
- [ ] `MOB-016` Adapt the terminal viewport, safe areas and orientation.
- [ ] `MOB-017` Add unit, component, Playwright, accessibility and
  PWA tests for the complete matrix.
- [ ] `MOB-018` Update SDK, developer guide, existing manifests, ADRs,
  integration list and user documentation.

## 15. Definition of completion

Mobile support is complete when:

- a common application works with a single entry point on Desktop and
  Mobile;
- a second application uses two entry points under the same AppId;
- incompatible applications are absent from all discovery
  surfaces and return a typed error on explicit launch;
- a dummy third platform can be registered and resolved without changing
  the manifest format;
- the explicit choice is persisted per device and Auto follows browser detection;
- the Desktop/Mobile switch respects lifecycle, dirty states and data;
- Mobile applications are always full screen;
- Triangle, Circle and Square implement exactly Back, Home and Recent;
- the application Back is explicit, testable and displayed in the application bar
  on Desktop only;
- the Mobile Terminal works with the HackerOS keyboard without a native keyboard;
- the unit/component/browser/PWA/accessibility matrix passes in Release;
- all manifests, SDK guides and architecture documents are synchronized.

## 16. Phased roadmap (added 2026-08-18)

### 16.1 What "Phase 0" delivered

See `MOB-001`/`006`/`007` above for the file-by-file detail. In addition to
these three tasks, Phase 0 added a building block that was not named in this
document originally: a real UI entry point for the platform choice.
`HackerOs.Taskbar.Blazor.Taskbar` now exposes `ITaskbarClockPanelSource` +
a parameter `RenderFragment? ClockPanelContent`; the taskbar owns
only the trigger (clock button) and the panel anchor, never its
content. `Platform/HackerOs.Platform.Blazor/Shell/ClockPanel.razor` (provided by the
host application, outside the taskbar package) combines notifications, a
minimal calendar, and the Auto/Desktop/Mobile selector wired to
`UiPlatformPreferenceService`. This same change merged the former notification bell
(`ITaskbarNotificationSource`, whose panel was never displayed, a
pre-existing bug) into this new single entry point.

Flipping the toggle does **not** yet change the shell rendering: it is a
preference persistence only, with a `Changed` event ready to be consumed by a
future phase (`MOB-008`/`009`).

### 16.2 Link to the real packages

This document, written before the windows/taskbar extraction, never names the
packages that now carry the concepts it describes:

- **§3 (capability descriptors, `MOB-002`)**: contract to define alongside
  `HackerOs.Windowing.Abstractions` (same conventions as `WindowId`/
  `WindowBounds`/`WindowVisualState`).
- **§7.1 (`SingleFullScreenSurface`, `MOB-009`)**: presentation policy to
  add to `HackerOs.Windowing.Core`/`WindowRuntime`; **not verified** that the
  current engine (designed for floating windows) supports a single
  non-movable/non-resizable surface; a spike is needed before
  estimating `MOB-009` in detail.
- **§7.2 (`MobileSystemNavigationBar.razor`, `MOB-010`)**: new package
  sibling of `HackerOs.Taskbar.Blazor` (Mobile has no taskbar), not a
  modification of it.
- **§6 (preference and detection, `MOB-006`/`007`)**: done in Phase 0, see
  §16.1.

### 16.3 Open risk: non-durable `InstallationId`

`UiPlatformPreference` (§6.1) must stay local to the device and never
synchronize between the devices of a same user. The scope
`SettingsScope.AppDevice` already exists for that, but
`EcosystemServiceCollectionExtensions.cs` regenerates `InstallationId` at each
startup (`Guid.NewGuid()`, never persisted); using it would break
persistence at each page reload. Phase 0 therefore uses the scope
`OsAdmin` + `SyncEligible:false` (same practical result: no
synchronization), noting the migration to a real `AppDevice` scope as a
mechanical follow-up once `InstallationId` is made durable, not a blocker for
the rest of this plan.

### 16.4 Proposed sequencing for `MOB-002` to `MOB-018`

```text
Phase 1: Manifest and entry point resolution [DONE on 2026-08-18]
  MOB-002, MOB-003, MOB-004, MOB-005, MOB-014 (partial: one reference app)
  Depends on: AppPlatformId (Phase 0, done)

Phase 2: Mobile shell and controlled switch [DONE on 2026-08-18]
  Phase 2a (sub-slice): MOB-009, MOB-010, see §16.6. Feasibility spike
  (§16.2) confirmed additive, no restructuring of the engine.
  Phase 2b (sub-slice): MOB-008, in a pragmatic version, see §16.7.
  Phase 2c (sub-slice): MOB-012, MOB-013 (IAppBackHandler contract, Desktop
  Back button, Mobile triangle calls step 2 of §7.3), see §16.9.
  Remaining outside Phase 2: MOB-011 (navigation stack, Recent; the Recent of
  MOB-010 and steps 1/3/4 of the Back sequence of §7.3 remain no-ops
  in the meantime)
  Depends on: Phase 1 (apps must declare a Mobile entry point)

Phase 3: Mobile Terminal virtual keyboard
  MOB-015, MOB-016
  Depends on: Phase 2 (needs a real Mobile surface to attach to)

Phase 4: Hardening
  MOB-017 (complete test matrix), MOB-018 (SDK/docs/ADR/manifests)
  Depends on: Phases 1-3
```

This sequencing is a proposal of dependencies, not a dated commitment;
to be revised/prioritized at the start of each phase according to the available bandwidth,
as [`progress-and-plan-2026-08-17.md`](progress-and-plan-2026-08-17.md) §8 already does for
the other work items.

### 16.5 What "Phase 1" delivered

See `MOB-002`/`003`/`004`/`005`/`014` above for the file-by-file detail.
Summary of the design decisions:

- **Backward compatibility without file migration**: rather than migrating the
  40+ existing `app.manifest.json` to the new format (§4.3 envisages it
  as a physical migration), `AppManifest.EntryPoint` stayed a field
optional field in its own right and `AppManifestPlatformSupport.Resolve` virtually normalizes
any `entryPoint`-only manifest as covering `desktop`
alone. A single normalized entry point (`AppManifestPlatformResolution`) then serves
both validation and resolution: no double source of
truth in the sense of §4.3, just two input JSON syntaxes for a single internal
model.
- **`platform` and `entryPoint` are mutually exclusive**, enforced both
  by the JSON Schema (`oneOf` rule) and by `AppManifestValidator`
  (`manifest.platform.required`/`manifest.platform.ambiguous`).
- **Discovery stays stable by default**: `AppEntryPointDiscovery.Discover`
  now resolves each manifest via `IAppPlatformEntryPointResolver` for
  an active platform (new optional parameter, default `desktop`), so
  no existing caller (WebAssembly lazy loading included, see
  `BuildKnownLazyAppDescriptorRegistry`/`WebAssemblyLazyAssemblyTransport`) has
  changed observable behavior.
- **Scope deliberately not covered by this phase**: filtering
  `FileAssociationResolver`/`AppIntentDispatcher`/the launcher on the
  *current* platform preference (§5: "an incompatible application
  does not appear in the active launcher"). Doing it now would hide
  all existing apps (none declares `mobile` yet) as soon as a
  user flips the Mobile toggle laid down in Phase 0, while no Mobile shell
  exists to take them over. This integration comes back with the controlled
  shell switch of `MOB-008` (Phase 2), which knows how to suspend/
  stop incompatible instances cleanly before removing them from the visible
  surfaces.
- **`AppPlatformCapabilities` (`MOB-002`) is not yet consumed** by
  shell code; Phase 1 only registers the Desktop and
  Mobile descriptors; their first real consumer will be the Mobile shell of `MOB-009`/
  `MOB-010` (Phase 2).

### 16.6 What "Phase 2a" delivered

Sub-slice of Phase 2 covering `MOB-009` and `MOB-010`, delivered in isolation
before `MOB-008` (§16.7); at the time of this slice, `MobileShell` was
deliberately reachable from no real route (see last point
below, which became obsolete since Phase 2b). `MOB-011` (navigation
stack/Recent) and `MOB-012` (`IAppBackHandler`) remain undone. See
`MOB-009`/`010` above for the file-by-file detail.

- **Feasibility spike (§16.2) done before any code**: the windows engine
  already supports, without restructuring, the two mechanisms that
  `SingleFullScreenSurface` needs: `Maximized` fills exactly the work area
  tracked by `WindowRuntime`, and `Minimized` already hides a window
  end to end (engine + rendering). `MOB-009` reuses both rather than
  recomputing a separate geometry or visibility mechanism.
- **Latent bug fixed upstream**: the spike discovered that `WindowChrome`
  displayed its resize handles without checking
  `Constraints.IsResizable`, and that `DesktopShell.HandleGesture` had no
  `try/catch` around `WindowRuntime.Apply`: a real bug (not only
  latent: file dialogs already create `isResizable:false`
  windows via `FileDialogWindowAdapter.cs`) that would have thrown an unhandled
  `InvalidOperationException` on the first attempted resize
  of a file dialog. Fixed before the rest of `MOB-009` so as not to
  build on an already broken constraints mechanism.
- **`WindowConstraints.IsMovable`** (new, default `true`) added as defense
  in depth next to `IsResizable`; the engine had no concept
  of "cannot be moved" before Phase 2a; `WindowRuntime.Move` now throws
  the same exception as `Resize` for a pinned window.
- **`SingleSurfaceArea` (Blazor) stays in `HackerOs.Windowing.Core`/`.Blazor`**,
  not in a Mobile-only package, because the "a single
  surface visible at a time" behavior is a generic presentation mode of the windowing
  engine (just like the floating mode), even if Mobile is its
  first consumer; consistent with the §16.2 note that already placed
  `SingleFullScreenSurface` in `HackerOs.Windowing.Core`/`WindowRuntime`.
  `WindowHost` gains a `ShowChrome` parameter (`true` by default, so no
  behavior change for `DesktopArea`) rather than a new
  duplicated component.
- **`HackerOs.MobileShell.Blazor` is a new package sibling of
  `HackerOs.Taskbar.Blazor`**, not a modification of it, exactly
  as anticipated in §16.2: Mobile has no taskbar. Unlike the
  taskbar contracts (all optional), `MobileSystemNavigationBar.Commands`
  is required: the system bar has no Desktop equivalent where it would
  cleanly disappear when absent.
- **`MobileShell.razor` (`Platform/HackerOs.Platform.Blazor/Shell/`) composes
  `SingleSurfaceArea` + `MobileSystemNavigationBar`** exactly as
  `DesktopShell.razor` composes `DesktopArea` + `Taskbar`. At the time
  of the Phase 2a delivery, it was reachable from no real route or startup
  service and was only tested at the unit level (build + direct tests
  on `WindowRuntime`); Phase 2b (§16.7) made it truly reachable
  and adds the browser E2E proof. `MobileNavigationCommandsAdapter`
  really implements only `RequestHome` (hides the active surface via
  Minimize, §7.4); `RequestBack`/`RequestRecent` remain documented no-ops;
  §7.3 explicitly accepts "do nothing" as a valid terminal outcome in
  the absence of a navigation stack/application Back handler (`MOB-011`/`012`).
- **`MOB-008` followed immediately in the same session, in Phase 2b**; see
  §16.7 for the detail: `MobileShell` had to exist before the controlled
  shell switch made sense, so the two slices
  naturally followed one another rather than being separated by a delay.

### 16.7 What "Phase 2b" (`MOB-008`) delivered

Makes `MobileShell` truly reachable: selecting Mobile in the clock
panel now switches the rendered shell **live, without reload**,
and a notification pane reachable by swiping from the top of the
Mobile screen (§16.8) reuses this same panel to return to Desktop.
Proven by real end-to-end browser tests
(`Tests/HackerOs.UI.E2E.Tests/PlatformShellSwitchTests.cs`,
`ClockPanelMobileToggleTests.cs`, `MobileNotificationShadeSwipeTests.cs`), in
addition to the unit tests of the coordinator
(`Tests/HackerOs.Platform.Blazor.Tests/Shell/PlatformShellSwitchCoordinatorTests.cs`,
built against a real `AppLifecycleOrchestrator`/`WindowRuntime`, not doubles).

- **`PlatformShellSwitchCoordinator`** (`Platform/HackerOs.Platform.Blazor/Shell/`)
  implements a pragmatic version of the 9-step sequence of §6.3, not
  literal point by point: for each window that actually needs
  to restart (see next point), confirms via
  `WindowCloseGuardRegistry.ConfirmCloseAsync` (step 2); if all accept,
  stops the owning instance with `ProcessExitReason.PlatformChanged`
  (new, step 4) then forces the closing of the window (covers step 3:
  no surface operation survives the stopping of its instance); finally
  persists the new choice via `UiPlatformPreferenceService` (step 6). The
  actual change of rendering (step 7) and the resulting display (step 9)
  are the responsibility of `App.razor`, not of the coordinator; see below.
- **Only the windows that really need it are restarted**:
  refinement made afterwards (the first draft systematically stopped
  all open windows, more cautious than necessary). §6.3 says it
  explicitly: "do not hot-swap the component type of an
  existing instance... the entry point change goes through a controlled
  stop/relaunch"; it is the *type change* that requires
  the stop, not the platform change itself. `WindowRuntime` is a
  DI singleton shared between `DesktopShell` and `MobileShell`; a window
  whose app declares a shared entry point for both platforms
  (like `HackerOs.Samples.PlatformApp`) therefore survives the
  shell switch as is: the new shell simply re-presents it (with or
  without chrome) without touching the running instance. `SelectWindowsNeedingRestart`
  compares, for each window, the entry point resolved
  (`AppManifestPlatformSupport.Resolve`) on the active platform against that
  of the target platform; only a difference (or an app not supported on
  the target) triggers confirmation + stop. `RequestAutoAsync` stays
  conservative (stops everything) since the platform resulting from Auto is
  not known in advance without rerunning the detection.
- **Confirmation-before-persistence order respected**: Phase 0 had wired
  `ClockPanel.razor` to call `UiPlatformPreferenceService.SetExplicitAsync`/
  `ClearToAutoAsync` directly, which would have persisted the choice before any
  confirmation. `ClockPanel.razor` now calls
  `PlatformShellSwitchCoordinator.RequestExplicitAsync`/`RequestAutoAsync`
  instead; if a window refuses to close, nothing is persisted and nothing
  changes on screen (no error message added; the window's guard
  already had to display its own confirmation, same reasoning as
  `WindowCloseCoordinator.CloseAsync`).
- **`App.razor` becomes the real switching point**: it now subscribes to
  `UiPlatformPreferenceService.Changed` (moved from the lazy
  initialization of `DesktopShell.OnInitialized`, now called at application
  boot) and renders `<MobileShell />` or `<DesktopShell />` in the
  `EcosystemHostView.Desktop` case according to `Current.ActivePlatform`.
- **Step 5 (compatible services) deliberately simplified**: no service
  (`AppKind.Service`) is stopped or restarted by this coordinator; all
  running services stay active as is. No service in the repository
  declares today a restart requirement tied to the platform; adding
  this distinction remains a mechanical exercise for a future session if such
  a service appears.
- **Step 8 (re-resolution of launcher/associations/intents) still deferred**:
  the reason documented in §16.5 remains valid: no embedded manifest
  declares a Mobile-only entry point (only the `HackerOs.Samples.PlatformApp` sample
  declares `platform.entryPoints`, shared desktop+mobile), so any app that
  launches today launches identically whatever the active platform
  is; launching a Desktop app on Mobile simply displays its Desktop UI
  full screen without chrome, which is an honest and expected state as long as
  no app declares a real Mobile variant.

### 16.8 Mobile notification pane by swiping (post-`MOB-008`)

Added afterwards to replace a first draft of `MOB-008` (a placeholder
"Switch to Desktop" button laid in a corner of `MobileShell`) with
something closer to a real Mobile shell: a pane reachable by
swiping from the top of the screen, like an Android notification panel,
which reuses **the same `ClockPanel` component** as the Desktop clock
panel (notifications, calendar, and the Auto/Desktop/Mobile selector,
so the return to Desktop is done from this same pane, without a
separate control).

- **`MobileShell.razor.js`** (new, collocated) exposes
  `attachSwipeDownGesture`/`detachSwipeDownGesture`, on the same pointer
  capture model as `WindowChrome.razor.js`: a handle button at the top of the screen
  (`.mobile-shade-handle`, also clickable/focusable for accessibility, not
  only draggable) triggers `[JSInvokable] OnSwipeDownDetected` after a
  downward swipe of at least 32px.
- **Real bug found while testing, not only latent**: without
  `event.preventDefault()` in the `pointerdown` handler, a truly reliable
  mouse swipe (driven by the system, not a synthetic event)
  can be hijacked by text selection/the browser's native drag and drop before delivering the expected `pointermove`
  sequence; fixed to match exactly the already used and working pattern of
  `WindowChrome.razor.js`.
- **Test limit discovered in practice**: neither `page.Mouse` (system-level
  input simulation) nor Playwright's `Locator.DispatchEventAsync` reliably
  deliver a real `PointerEvent` (with `pointerId`/`clientY`/
  `button` correctly filled in) to a custom handler based on
  `setPointerCapture`, in headless Chromium; confirmed by direct
  experimentation. `MobileNotificationShadeSwipeTests.cs` builds and dispatches
  real `new PointerEvent(...)` through `page.EvaluateAsync` rather than
  relying on either.
- **`MobileShell` no longer injects `PlatformShellSwitchCoordinator` directly**:
  since the pane reuses `ClockPanel` as is, it is this component that already handles
  the whole platform switch flow (Phase 2b above).

### 16.9 Application Back contract (`MOB-012`/`MOB-013`, post-§16.8)

Delivers the SDK contract and step 2 of the ordered Back sequence of §7.3:
"call the Back handler of the active application if it explicitly exposes
this support". `MOB-011` (navigation stack, Recent surface) remains
deliberately out of scope: it is a broader subsystem that deserves its
own dedicated slice, not an incremental addition to this one.

- **`Shared/HackerOs.AppSdk.Blazor/AppBackContracts.cs`** (new): the
  exact contract of §8: `AppBackSource` (`SystemNavigationBar` /
  `DesktopChromeButton` / `KeyboardShortcut`), `AppBackRequest`,
  `AppBackResultStatus` (`Handled`/`NotHandled`/`Blocked`/
  `ConfirmationRequired`/`Faulted`), `AppBackResult`, and `IAppBackHandler`
  (`CanNavigateBack`, `CanNavigateBackChanged`, `NavigateBackAsync`).
- **`AppManifest.SupportsBack`** (new bool, `Window` only, same guard
  as `FileHandlers`) declares Back support for an entry point
  or the shared application, as requested by §8. The documented keyboard shortcut
  in §8 ("The documented keyboard shortcut uses the same
  contract") is not wired in this slice.
- **`AppEntryPointDiscovery` validates conformance by reflection**, without a
  project reference to `HackerOs.AppSdk.Blazor`: exactly the same pattern as
  `DerivesFrom` for base types: a new `Implements` helper
  compares `Type.GetInterfaces()` by full name. `manifest.SupportsBack ==
  true` without a type implementing `IAppBackHandler` fails with
  `discovery.backHandler.missing`.
- **`WindowRuntimeState.SupportsBack`**: declarative field set only once
  at window creation from `AppManifest.SupportsBack`
  (`WindowLaunchCoordinator`), not a live mirror of
  `IAppBackHandler.CanNavigateBack`.
- **Real bug found by the test, not only latent**:
  `WindowRuntime.Copy()` (the private helper that `Create()` uses to set
  `zOrder`/`isFocused` on the initial state) forgot to carry over
  `SupportsBack`; any window created with `supportsBack: true` silently lost
  this fact upon creation. Detected by
  `WindowLaunchCoordinatorTests.Present_projects_the_manifests_supportsBack_declaration_onto_the_window`,
  which tests at the real `WindowRuntime` level (not a double), fixed in one
  line.
- **`AppBackHandlerRegistry` + capture in `WindowAppRenderer`**: exactly the same
  pattern as `WindowCloseGuardRegistry`/`IWindowCloseGuard` (already in place for
  closing): `AddComponentReferenceCapture` retrieves the rendered component,
  and if it implements `IAppBackHandler`, registers it by `WindowId` in a
  singleton DI registry. Reference-safe registrations: the disposal of an old renderer cannot
  remove the handler of a newer component for the same window.
- **Back button in `WindowChrome`, Desktop only**: rendered
  conditionally on `Window.SupportsBack`, in `.window-identity` before the
  icon. Propagated `WindowChrome` → `WindowHost` → `DesktopArea` →
  `DesktopShell.HandleBack`, which calls
  `BackHandlers.NavigateBackAsync(id, new AppBackRequest(DesktopChromeButton))`.
  Mobile does not duplicate this button by construction, not by an explicit
  condition: `SingleSurfaceArea` always renders `WindowHost` with
  `ShowChrome="false"`, so `WindowChrome` (and its Back button) is
  never instantiated there; compliant with §8 ("Do not duplicate the Back button in
  the application bar" on the Mobile side).
- **`MobileNavigationCommandsAdapter.RequestBack`** now calls
  `BackHandlers.NavigateBackAsync` for the primary window (same selection
  as `RequestHome`, via `SingleSurfacePresentationPolicy.SelectPrimary`), with
  `AppBackSource.SystemNavigationBar`. Implemented as `async void`, a choice
  already correct for the existing synchronous contract
  `IMobileNavigationCommands.RequestBack() -> void`, a fire-and-forget
  triggered by the click of the system triangle, not a method whose caller
  waits for a result.
- **Documented gap, deliberately deferred**: §8 says "The button is
  disabled when `CanNavigateBack` is false." This slice does not wire this
  state; the Desktop button is always active as soon as `SupportsBack` is
  true, without listening to `IAppBackHandler.CanNavigateBackChanged`. Wiring the live
  state would require an additional notification channel from the captured
  component to `WindowChrome`; to be done in a later slice if a real
  need arises.
- **Only step 2 of the ordered sequence of §7.3 is delivered.** Steps 1
  (system dialog/modal stack), 3 (application/shell navigation stack)
  and 4 (close-and-return-Home) await `MOB-011`; a `NotHandled` result
  (or the absence of a handler) remains a silent no-op; this is
  the valid terminal outcome of step 5 as long as the previous steps do not exist to prevent it.

### 16.10 What "MOB-011" delivered (2026-08-23)

Completes steps 1/3/4/5 of the Back sequence of §7.3 left open
since §16.9, and delivers the Recent surface of §7.5, without introducing a
heavy fixture or a new engine parallel to `WindowRuntime`.

- **`MobileNavigationHistory`** (new,
  `Platform/HackerOs.Platform.Blazor/Shell/`): a reactive
  `Stack<WindowId>`: it subscribes to `WindowRuntime.StateChanged`,
  recomputes `SingleSurfacePresentationPolicy.SelectPrimary` at each tick, and
  pushes the old primary as soon as it changes as long as the window still exists
  (closed = never pushed). This reactive diff captures *any* change of
  primary (an intent that launches a window, an activation from Recent,
  anything else) without any call site needing to push
  explicitly. `TryPopPrevious` silently ignores stale entries
  (window closed since) and marks the returned entry
  as an expected return (`_pendingReturn`) so that the following
  `WindowFocusedEvent` is not pushed again as a new navigation forward.
- **Step 1 (cancelable dialog/modal)**: no separate dialog stack
  was built; the primary window itself already carries
  `Modality`/`OnRequestClose` (`WindowRuntimeState`), and
  `SingleSurfacePresentationPolicy.SelectPrimary` already surfaces an
  owner-modal dialog box as primary while it is open
  (it has the highest z-order). `RequestBack` therefore treats
  `Modality: OwnerModal` + non-null `OnRequestClose` as the
  "cancelable" signal and routes directly to this delegate; no new
  modal stack concept was needed.
- **Step 3 (return to the previous entry)**: `history.TryPopPrevious`,
  then Restore (if minimized) + Focus, the same form as
  `TaskbarWindowInteraction.HandleClick` for a minimized item.
  `SingleSurfaceArea.OnParametersSetAsync` (pre-existing, unmodified)
  automatically re-minimizes the old primary window at the next render;
  this slice did not need to handle the minimization itself.
- **Step 4 (guarded closing and return Home)**: `RequestBack` takes a
  `Func<WindowId, Task> closeWindow` delegate rather than directly injecting
  `WindowCloseCoordinator`/`WindowCloseGuardRegistry`; **no unit
  test in the repository builds these two real services** (their only
  consumer, `PlatformShellSwitchCoordinatorTests`, uses a private fixture
  of a complete kernel with 12 parameters); building that here would have made
  this slice overflow well beyond "small and self-contained". The real guarded
  path (`CloseCoordinator.CloseAsync(id, ct =>
  CloseGuards.ConfirmCloseAsync(id, ct))`) is injected from
  `MobileShell.razor`, identical to what
  `TaskbarCommandDispatcherAdapter.Close` already does for the taskbar; the
  adapter's unit tests use a simple recording delegate
  instead, the real guarded path remaining exercised by the browser
  E2E test.
- **Step 0, deliberate addition not named by §7.3**: an open notification
  pane or the open Recent surface are closed with absolute priority
  before everything else; it is obviously the real top of the
  UI stack even if the 5 literal steps do not name it separately
  (same spirit as the "pragmatic slice" already accepted for `MOB-008`
  in §16.7).
- **Recent surface**: `MobileRecentProjection` (new, static and pure,
  same style as `SingleSurfacePresentationPolicy`) sorts
  `WindowRuntime.Windows` by descending `ZOrder`; the monotonic focus
  counter that `WindowRuntime` already maintains directly serves as a recency
  signal, with no new tracking. `MobileRecentItem`/`MobileRecentSurface`
  (new, `HackerOs.MobileShell.Blazor`, exact mirror of the generic-contract/HackerOS-projection
  separation already used by
  `HackerOs.Taskbar.Blazor`/`TaskbarWindowSourceAdapter`) display only
  title + icon; **no thumbnail capture mechanism exists
  anywhere in the repository**; the preview protection of §7.5 is therefore satisfied
  by omission (nothing sensitive is ever rendered) rather than by an active
  indicator. Filtering by platform compatibility/live activation
  is not applied either, with exactly the same reasoning as
  §16.5 for the launcher: no app in the current catalog declares a
  Mobile-only entry point, and disabling an app while one of its
  windows remains open is a case not handled elsewhere in the repository either.
  Activating an item closes the Recent surface and reuses
  `TaskbarCommandDispatcherAdapter` (built once in
  `MobileShell.razor.OnInitialized`) for Restore/Activate/Close; zero
  duplicated closing logic compared to the taskbar.
- **`RequestHome` does not clear the history**: going back later to an
  app still lets the user go back up through what it had, as
  Android does.
- **Two real bugs found by the E2E test, not only latent**: neither
  is caused by the code added in this slice; both
  existed since Phases 2a/2b and had never been detected because
  no earlier Mobile test took a screenshot or measured
  real geometry (only the ARIA role/visibility were checked,
  insensitive to box height):
  1. **`.mobile-shell` never filled the screen.** Its `height: 100%`
     goes up through `#ecosystem-host` to `ThemeScope`, which renders as
     `display: contents`, a value that does not reliably propagate a percentage
     height basis to descendants depending on the rendering engines.
     Result measured in E2E: `.mobile-shell` was only 61px high (the
     natural height of the system bar), with `.mobile-shell-workspace`
     (`flex: 1`) squashed to 0; nothing under the system bar was visible or
     reachable, the bar itself ending up stuck at the top of the screen
     for lack of space for the content above. Fixed by anchoring
     `.mobile-shell` directly to the viewport (`position: fixed; inset: 0;`)
     instead of `width/height: 100%`, the same mechanism as the
     shade/Recent backdrops, which entirely bypasses the percentage chain.
  2. **`.mobile-shade-backdrop` (`position: fixed`, `z-index: 1100`,
     introduced in Phase 2b) physically covered `MobileSystemNavigationBar`**,
     which had no `z-index` of its own; once bug 1 was fixed and the
     system bar really at the bottom of the screen, Playwright failed with
     "intercepts pointer events" when trying to click Back while the
     pane was open. Concretely, step 0 of this slice (Back
     closes the overlay with priority) was unreachable: impossible to
     click Back while an overlay covered the bar. Fixed by giving
     `.mobile-nav-bar` `position: relative` and `z-index: 1200` (above the
     shade/Recent backdrops, both at 1100); the system bar now stays
     clickable above any panel, as on real Android.

  The two bugs hid each other: as long as bug 1 was not fixed, the system bar and the pane occupied the same 61px band
  at the top of the screen (bug 2 was already present but manifested
  differently). The three pre-existing Mobile E2E tests
  (`PlatformShellSwitchTests`, `MobileNotificationShadeSwipeTests`,
  `ClockPanelMobileToggleTests`) were replayed after the two fixes and
  still pass without regression.
- Tests: 4 new cases on `MobileNavigationCommandsAdapterTests`
  (dialog cancellation, history return, guarded closing, priority closing
  of the overlay), `MobileNavigationHistoryTests` (5 cases:
  push/pop, stale entries, non-re-push of the return) and
  `MobileRecentProjectionTests` (3 cases: sort, mapping, empty list), 16 tests
  in total in `HackerOs.Platform.Blazor.Tests`, all built against a
  real `WindowRuntime`/`AppBackHandlerRegistry`, no mocking framework.
  `dotnet build HackerOs.sln --configuration Release`: 0 warnings/0
  errors (required regenerating `PublicAPI.Unshipped.txt` of
  `HackerOs.MobileShell.Blazor` via `dotnet format analyzers
  --diagnostics RS0016 --include-generated`, same procedure as
  `EXT-WIN-014`).
- **Finding made while writing the E2E browser test, outside the scope of this
  slice**: no real application of the `HackerOs.Ecosystem` catalog
  declares Mobile support (only `HackerOs.Samples.PlatformApp`, an isolated
  demonstration project not wired into the build catalog, does so; see
  `MOB-014`). The launcher that would allow launching one is a Desktop-only chrome
  (`AppLauncher.razor`, never composed in `MobileShell`), and
  `PlatformShellSwitchCoordinator.SelectWindowsNeedingRestart` already stops
  any window whose app resolves no entry point on the target
  platform; so switching to Mobile with a Desktop app open closes it
  rather than making it appear. Concretely: **today, no real application
  window can exist while Mobile is active**
  through the real product. The multi-window Back/Recent scenario is therefore
  entirely proven at the unit level above against a real
  `WindowRuntime` (which does not go through this platform filtering), and not
  reproducible in E2E with the current catalog. The browser test
  (`MobileRecentAndOverlayBackTests.cs`) proves instead what is
  really observable today: step 0 (priority closing of the notification
  pane then of Recent), the empty state of Recent ("No recent
  apps"), and the absence of error at step 5 (no-op at Home). This is not a
  retreat of the scope of `MOB-011`; the multi-window logic is done and
  tested; it is a pre-existing and documented limit of what the
  app catalog allows observing in real conditions as long as no
  app declares a Mobile variant (`MOB-014` covers only an isolated
  demonstration example).

### 16.11 What "MOB-015" delivered (2026-08-23): reusable virtual keyboard

The virtual keyboard of §9 is delivered, but generalized beyond the Terminal alone,
at explicit request in session: a reusable control and a "SplitContainer-style"
container that shrinks a content region when the keyboard
deploys below it, rather than a Terminal-only component. The Terminal is
the first consumer, not the only possible one.

- **`Shared/HackerOs.AppSdk.VirtualKeyboard/` (new, zero dependency)**:
  RCL package joining the exportable batch (`Packaging.props`, PublicAPI,
  README), without any reference to HackerOS or the Terminal:
  - `VirtualKey`/`VirtualKeyPressedEvent`: renderer-independent key contract,
    modeled on the shape of `TerminalKeyEvent` (`Shared/HackerOs.AppSdk/
    FullScreenTerminalContracts.cs`) for a direct mapping, but owned by
    this new package rather than by the Terminal.
  - `VirtualKeyboardOptions`: per-row toggles (modifiers, symbols,
    arrows), same shape as `MobileSystemNavigationBarOptions`.
  - `VirtualKeyboard.razor`: 100% button rendering; no `<input>`/
    `contenteditable` anywhere, which structurally guarantees that it
    can never trigger a native keyboard. "Sticky" Shift/Ctrl/Alt
    modifiers, consumed by the next key; deliberately simpler
    than a real keyboard (no separate caps lock), since the surface must never
    fill a standard text area. Bounded repeat
    (`PeriodicTimer`, 40 repeats max) for Backspace/arrows; new
    ground, no long-duration repeat pattern existed elsewhere
    in this repository.
  - `VirtualKeyboardHost.razor`: the reusable container. No
    `ResizeObserver`/`visualViewport` detection; the shrinking of the content region
    (`flex:1`) when the keyboard appears is a simple conditional flexbox reflow,
    since this keyboard does not detect a native keyboard: it takes its place.
- **Terminal integration (`Shared/HackerOs.AppSdk.Terminal/TerminalPanel.razor`)**,
  not in the new package: new `UseVirtualKeyboard` (sets
  `inputmode="none"` on the line-mode `<input>`), `OnExitRequested`
  (unbound by default) and `PushVirtualKey(VirtualKeyPressedEvent)`. In
  full-screen mode, routes to `TerminalFullScreenSession.TryPostKey`, the same
  entry point that `HandleFullScreenKeyAsync` already uses, so `nano` and
  any other full-screen command inherit virtual keyboard support with no
  change. In line mode, manipulates `_inputBuffer` directly in C#
  rather than synthesizing a DOM `KeyboardEvent`; browsers
  do not apply the default text editing behavior to a keyboard event
  sent programmatically (not "trusted").
  - **Ctrl+D** (no existing precedent): acts only on an empty line
    (like a real shell, EOF only on an empty line), invokes
    `OnExitRequested` if bound. Non-empty line: no-op.
  - **Escape** (no precedent in line mode): clears only the current line,
    distinct from Ctrl+L (clears the scrollback) and Ctrl+C (cancels the
    active command); takes up the meaning already established elsewhere in this repository
    (`nano`/`PromptAsync` already treats `TerminalKey.Escape` as "cancel
    the current edit").
- **Wiring into a real Mobile shell explicitly out of scope**: no
  app in the catalog (Terminal included) declares Mobile support
  today (finding of `MOB-011`), so there is no real Mobile Terminal
  to wire. This slice delivers the reusable infrastructure and the
  integration point on the Terminal side; actually enabling it in a Mobile
  Terminal remains a follow-up conditional on `MOB-014` covering a real
  Mobile variant.
- **Browser proof, scoped to avoid a heavy fixture**:
  `TerminalPanel` requires a real `IAppExecutionContext`; building a
  fake one in the harness would have required the same kind of complete fixture
  as the one judged disproportionate for `WindowCloseCoordinator` during
  `MOB-011`. The new scenario `Tests/HackerOs.BrowserHarness.Tests/
  VirtualKeyboardTerminalBrowserScenario.razor` instead takes up the already standalone
  pattern of `TerminalFullScreenBrowserScenario.razor` (self-sufficient full-screen
  loop, without `AppContext`), wrapped in `VirtualKeyboardHost`.
  The Playwright test `Virtual_keyboard_drives_the_full_screen_terminal_without_a_native_input`
  (`Tests/HackerOs.E2E.Tests/IndexedDbBrowserContractTests.cs`) types
  letters and Ctrl+X on screen, confirms that the terminal receives them through the existing
  `TerminalKeyEvent` pipeline, and verifies that no `<input>`/
  `[contenteditable]` exists anywhere in the page. **Not covered by an
  automated test this time**: the new line mode behavior of
  `TerminalPanel` (`PushVirtualKey`, Ctrl+D, Escape); consistent with the fact
  that the existing branches of `HandleKeyDownAsync` have themselves
  never had automated coverage in this repository (no bUnit here, UI proof
  only through the browser harness).
- **Not verified in this slice**: the assumption that the existing
  `ResizeObserver` → `UpdateTerminalViewport` → `TryPostKey(Resize)` pipeline
  already reflows a full-screen command correctly when the keyboard appears/
  disappears live; the new scenario displays the keyboard statically
  (fixed `IsKeyboardVisible="true"`) and never tests the
  toggle. To be verified concretely when `MOB-016` is tackled, not
  assumed.
- Tests: 4 new unit cases (`Tests/HackerOs.AppSdk.VirtualKeyboard.Tests`)
  for `VirtualKeyboardOptions`/`VirtualKeyPressedEvent`, plus the new
  E2E test above. `dotnet build HackerOs.sln --configuration Release`: 0
  warnings/0 errors (required regenerating `PublicAPI.Unshipped.txt`
  via `dotnet format analyzers --diagnostics RS0016 --include-generated`,
  same procedure as for `HackerOs.MobileShell.Blazor` during `MOB-011`).
  The suites `HackerOs.AppSdk.Terminal.Tests` (9/9) and the pre-existing full-screen
  E2E test were replayed: no regression.
