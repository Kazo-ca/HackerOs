> [Version française](../../../../fr/wasm/dev/adr/0153-contract-host-implementation-adjustments.md)

# ADR 0153: adjustments to the generic contract host

## Status

Accepted on 2026-10-19.

- **Work item:** `desk/1-contract-host` (work item 1 of the plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §11).
- **Adjusts:** ADR 0148 §1, §3 and §5 on implementation points; its
  decisions remain true.

## Context

ADR 0148 decides that the SDK carries only contracts and that the OS provides at
run time the file control, its dialogs and the context menu. Work item 1
delivers the common mechanics; a few details were not fixed
by the ADR or could not be as stated.

## Decision

### 1. The typed host component: `HackContractComponent<TController>`

The Blazor SDK gains an abstract base class
`HackContractComponent<TController>` (`HackerOs.AppSdk.Blazor.Components`).
A typed component (`HackFileView`, `HackContextMenu`) derives from it, declares its
ordinary `[Parameter]`s and copies them by name in
`AddProviderParameters`. It is rendered through `HackEmbeddedComponent<T>`, which gains
the `Parameters` parameter (dictionary passed by name to the provider, next
to `Options` and `ControllerReady`, which keep priority). The base
exposes `ControllerChanged`, `Unavailable`, `Controller` and the
`OnControllerChangedAsync` hook. A new dictionary is passed only if a
value has changed (shallow comparison), so as not to redraw the
provider on each render of the host.

`HackEmbeddedComponent<T>` keeps its 1.x API: the base is a composition,
not a parent, so that nothing leaves `PublicAPI.Shipped.txt`.

### 2. Parameter version tolerance

`HackEmbeddableComponent` captures the parameters it does not declare
(`UnmatchedParameters`, `CaptureUnmatchedValues`). An app compiled with a
more recent SDK can therefore pass a parameter that the OS provider does not
know yet: it is ignored instead of making the render fail, like
an older library ignoring an optional argument. A provider
does not declare its own `CaptureUnmatchedValues` (Blazor allows only one).

### 3. Registry read at startup in the catalog

The provider registry is built with the catalog
(`AppCatalog.ComponentProviders`, `GetComponentProviders(contract)`), hence from
the manifests read at startup, like the `PATH` commands
(ADR 0098 §4). The order is that of ADR 0063 §3. The installed state is
applied at resolution by `HackComponentHost`.

### 4. Reserved contracts: where the refusal happens

The names live in `HackerOs.App.Abstractions`
(`AppComponentContracts`, `AppComponentProviderScope`) so that the
validator knows them; the SDK repeats them in `HackComponentContracts`.

- **`hackeros.context-menu` (platform)**: `AppManifestValidator.Validate`
  refuses any app that declares it (`manifest.component.contract.platform`);
  the generator therefore stops it at the app's build (`HOSAPP0009`).
- **`hackeros.file-view`, `hackeros.file-dialogs` (system apps)**: the
  manifest alone does not say whether an app is a system app (only the host's
  `ProjectReference` says it, ADR 0048). The refusal therefore happens at the host's
  **aggregation**, through
  `AppManifestValidator.ValidateComponentProviders`, with the new code
  **`HOSAPP0025`** (like `HOSAPP0024` for `oomScoreAdj`). At run time, the
  catalog discards the registration and writes `catalog.component.reserved` in
  `/var/log/syslog`; the app remains installable.

### 5. Platform providers

The platform registers its providers in the service container
(`HackPlatformComponent(contract, type)`), which `HackComponentHost` receives at
startup. They are tried before any app and resolved without loading an
assembly; their `ProviderAppId` is
`HackComponentResolution.PlatformProviderId` (`"platform"`, never a valid app
identifier).

### 6. Error in the system log

When a reserved contract has no provider that loads,
`HackComponentHost` writes `component.unavailable` as an error in
`/var/log/syslog` (source `components`). An open contract without a provider
(no terminal installed) remains a normal state, only shown to
the user.

### 7. Host rights

Nothing changes in the mechanism: the provider is rendered in the host's
window, receives the host's `AppContext` in cascade, and the callbacks it
invokes run in the host's ambient context (`HackComponentBase`).
A test verifies this for the typed component.

## Consequences

- Work items 2 and 3 only have to write their controller, their typed component
  and their provider.
- New unshipped APIs (`PublicAPI.Unshipped.txt`) in
  `App.Abstractions`, `AppSdk`, `AppSdk.Blazor`, `Platform.Core` and
  `Platform.Blazor`; they will be published with SDK 2.0 (work item 6).

## References

- ADR 0048, 0063, 0098, 0126, 0148.
- [`embedded-components.md`](../../framework/embedded-components.md),
  [`app-model.md`](../../framework/app-model.md).
