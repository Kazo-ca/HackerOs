# ADR 0148: the SDK carries only contracts; the OS provides the shared controls and dialogs

## Status

Accepted on 2026-10-19.

- **Benoit's decision:** plan [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)
  approved on 2026-10-19, question Q3 ("the SDK provides the interface, not
  the implementation; the explorer registers at startup as the provider of the
  control and its dialog boxes; the OS acts as a proxy") and Q6 (the generic menu
  is provided by the OS).
- **Completes:** ADR 0063 (embeddable components): its mechanism becomes the
  basis of all shared controls.
- **Replaces:** ADR 0037 §1 (library `HackerOs.AppSdk.FileView`
  compiled into the host app) and §8 (the explorer keeps its own
  "Open With" and "Properties" dialog boxes). The other points of ADR 0037 (single
  model for the three views, in-place renaming, drag and drop, delegated icons,
  cancelable events) remain true for the explorer's implementation.
- **Allowed providers:** system apps only (default adopted, question
  put to Benoit on a card on 2026-10-19 in the "Work item 0" thread; if the
  answer differs, a later ADR replaces §3.3).

## Context

`FileView` is today an SDK library compiled into each app
that uses it (explorer, code editor). A fix in the OS reaches an
app only after its recompilation, and 200 apps would embed 200 copies of the
same code. The terminal (ADR 0063) shows the other way: the SDK provides a
contract, an app registers as a provider in its manifest, the OS loads
the provider on first use and renders it in the host's window.

## Decision

### 1. The SDK contains only contracts

For a shared control or dialog, the SDK ships only:

- the **contract name** (`HackComponentContracts.FileView`…);
- the **controller** (interface, for example `IHackFileView`);
- the **options** record and the event types;
- a **typed host component** (`HackFileView`, `HackContextMenu`) with
  ordinary parameters and `EventCallback`s, built on
  `HackEmbeddedComponent<T>` (ADR 0063 §3).

No visible implementation (view, menu, dialog) is compiled into an
app. The host component receives at run time the `RenderFragment` of the
registered provider and its controller.

### 2. The contracts of this series

| Contract | SDK controller and component | Provider |
|---|---|---|
| `hackeros.file-view` | `IHackFileView`, `HackFileView` | File Explorer |
| `hackeros.file-dialogs` | "Open With", "Properties", name conflict, and the file view of the Open / Save / Choose a folder dialogs (`IFileDialogService` remains the entry point) | File Explorer |
| `hackeros.context-menu` | `IHackContextMenu`, `HackContextMenu`, `Menus.Attach` (ADR 0150) | Platform (`Platform.Blazor`) |

### 3. Registration and resolution

1. **An app** registers as today, in `AppDefinition.Components`
   (ADR 0063 §1). The OS reads the manifests at startup and therefore knows all
   the providers; the app's code is loaded only on first use of the
   contract (`AppLifecycleOrchestrator.EnsureDescriptorAsync`).
2. **The platform** registers its own providers in the registry at
   startup, without a manifest (`hackeros.context-menu`).
3. **Reserved contracts.** `hackeros.file-view` and `hackeros.file-dialogs`
   accept only system apps (ADR 0048); `hackeros.context-menu`
   accepts only the platform. A non-system app that declares a reserved contract
   is refused by `AppManifestValidator` (generator diagnostic) and
   ignored by the OS. `hackeros.terminal` remains open to any app, as
   ADR 0063 provides.
4. The registry keeps the order of ADR 0063 §3 (system apps first, then by
   identifier); a provider that fails to load leaves room for the
   next one.

### 4. The rights are those of the host

The provided rendering runs in the **host app's** process, with its
gateways (`HostContext`) and its capabilities, never those of the provider
(principle of ADR 0037 §1, kept). An app does not see one more file by
displaying the explorer's control. A provided dialog also receives
the gateways of the app that opens it (or the chosen handle, for Open /
Save).

### 5. Provider unavailable

Without a provider (app disabled, load failure, test host), the
component displays its `Unavailable` fragment or the reason. Since the
providers of reserved contracts are system apps, this case is an **error**
written to the system log (`/var/log/syslog`), never a normal state.

### 6. Dialogs

`IDialogService` and `IFileDialogService` keep their shape in the SDK.
The OS acts as a proxy: it opens the `hackeros.file-dialogs` dialog in a
modal window of the calling app. Thus "Open With" and "Properties"
are the same everywhere (explorer, desktop, any app).

## Consequences

- A fix to the file control, the dialogs or the menu reaches
  all apps without recompilation.
- The explorer becomes the single source of truth for the file control:
  it provides it and displays it itself through `HackFileView`.
- The `HackerOs.AppSdk.FileView` library leaves the SDK (ADR 0149).
- Work item 1 of the plan delivers the typed host component, the registry read at
  startup, the reserved contracts and the passing of the host's rights.

## Alternatives rejected

- **Keep `FileView` in the SDK**: each fix forces a recompilation of
  all apps.
- **A global DI service per control**: the explorer would have to be loaded at
  startup; the manifest keeps lazy loading (ADR 0063).

## References

- ADR 0037, 0048, 0063, 0149, 0150.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §4,
  [`embedded-components.md`](../../framework/embedded-components.md),
  [`file-explorer.md`](../file-explorer.md).
