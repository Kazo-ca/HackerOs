# Embeddable components

An app can display in its window a component that another app has
registered with the OS, without referencing its assembly, the way Kate displays
Konsole's KPart. Decision: [ADR 0063](../dev/adr/0063-embeddable-app-components.md).
The same mechanism carries the controls and dialogs that the OS provides to all
apps (the SDK only contains their contracts):
[ADR 0148](../dev/adr/0148-sdk-contracts-and-os-provided-controls.md), adjusted by
[ADR 0153](../dev/adr/0153-contract-host-implementation-adjustments.md).

## Registering a component (provider app)

```csharp
[AppManifest]
public sealed class TerminalApp : AppDefinition
{
    // ...
    public override IReadOnlyList<AppComponent> Components =>
        [new(HackComponentContracts.Terminal, typeof(TerminalEmbed))];
}
```

```razor
@inherits HackEmbeddableComponent
<TerminalPanel @ref="_panel" AppContext="HostContext" ... />

@code {
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) await ControllerReady.InvokeAsync(_panel);
    }
}
```

Rules (checked by `AppManifestValidator`): `Window` app, contract in lowercase
words separated by dots, one contract per app, type in the entry point's
assembly. The component derives from `HackEmbeddableComponent` and receives
`Options` and `ControllerReady`; `HostContext` is the context of the host
window.

## Embedding a component (host app)

```razor
<HackEmbeddedComponent TController="IHackTerminal"
                       Contract="@HackComponentContracts.Terminal"
                       Options="_options"
                       ControllerChanged="terminal => _terminal = terminal">
    <Unavailable Context="reason"><p>@reason</p></Unavailable>
</HackEmbeddedComponent>
```

Keep the same `Options` instance from one render to the next: a new
instance is passed on to the component. The controller remains valid as long as
`HackEmbeddedComponent` is rendered.

## Typed component (contract provided by the OS)

For an OS contract, the SDK gives a typed component that the app uses
like any Blazor component. It derives from
`HackContractComponent<TController>` and copies its parameters by name
(reduced form; the real `HackFileView` is described further down):

```csharp
public sealed class HackFileView : HackContractComponent<IHackFileView>
{
    [Parameter] public string? Path { get; set; }
    [Parameter] public EventCallback<string> Opened { get; set; }

    protected override string Contract => HackComponentContracts.FileView;

    protected override void AddProviderParameters(IDictionary<string, object?> parameters)
    {
        parameters[nameof(Path)] = Path;
        parameters[nameof(Opened)] = Opened;
    }
}
```

The provider (`HackEmbeddableComponent`) declares the same names as
`[Parameter]`. A parameter it does not know yet (app compiled with a
newer SDK) is put in `UnmatchedParameters` instead of making the render
fail; it must therefore not declare its own
`CaptureUnmatchedValues`. The base exposes `ControllerChanged`, `Unavailable`,
`Controller` and the `OnControllerChangedAsync` hook, and only passes a
new set of parameters if a value has changed. Underneath,
`HackEmbeddedComponent<T>` receives these values through its `Parameters` parameter
(`Options` and `ControllerReady` keep priority).

**Rights.** The provider is rendered in the host's window, with the host's
`AppContext`; the callbacks it invokes (`Opened`...)
run in the host's context. An app does not see one more file
by displaying another app's control.

## Registry and providers

- **Apps.** The catalog builds the registry at startup from the
  manifests (`AppCatalog.ComponentProviders`,
  `GetComponentProviders(contract)`): system apps first, then by
  identifier. `HackComponentHost` skips uninstalled apps and loads
  the provider's assembly on first use.
- **Administrator's choice** (ADR 0156, ADR 0158). For
  `hackeros.file-view` and `hackeros.file-dialogs`, `HackComponentHost` reads
  `/etc/hackeros/components.config` (`IComponentProviderSelection`, reread on
  each resolution) and tries the chosen app first; the others remain
  fallbacks, the explorer first. If the chosen app is disabled for the
  user or fails to load, the next one takes over and
  `component.fallback` is written as a warning in `/var/log/syslog`
  (source `components`), once per session. With no line, the explorer
  applies.

  ```ini
  [providers]
  hackeros.file-view=com.example.files
  hackeros.file-dialogs=com.example.files
  ```

  The choice is made in Settings ▸ OS components
  (`HackComponentProviders`, capability `apps.manage`): a non-system app
  goes through the dangerous capability approval window, with the risk sentence
  "This app's code will run in every application
  that displays files, with their rights."; a system app or the
  return to the explorer only require an administrator. Each
  decision goes to `/var/log/auth.log` (`component.provider`). An app
  uninstalled for the machine loses its line.
- **Platform.** The platform registers its own providers in the
  service container (`services.AddSingleton(new HackPlatformComponent(contrat, typeof(Composant)))`);
  they come before any app, without loading, and their `ProviderAppId` is
  `HackComponentResolution.PlatformProviderId` (`"platform"`).
- **Who can provide** (`AppComponentContracts.GetProviderScope`):

| Contract | Provider allowed | Refusal |
| --- | --- | --- |
| `hackeros.terminal` | any windowed app | none |
| `hackeros.file-view`, `hackeros.file-dialogs` | any windowed app (`AdministratorSelected`); the administrator chooses the one in service, the explorer by default (ADR 0156) | none (`HOSAPP0025` is removed, ADR 0158 §1) |
| `hackeros.context-menu` | platform | `manifest.component.contract.platform` (`HOSAPP0009` at the app's build) |

- **Unavailable.** An OS contract (files or menu) with no
  provider that loads is an OS error: `component.unavailable` is written in `/var/log/syslog`
  (source `components`). An open contract without a provider is only
  shown to the user (`Unavailable`).

## Contracts

| Contract | Controller | Options | Provider |
| --- | --- | --- | --- |
| `hackeros.terminal` (`HackComponentContracts.Terminal`) | `IHackTerminal` | `HackTerminalOptions` (`WorkingDirectory`, `ShowBanner`, `FocusOnStart`) | Terminal (`TerminalEmbed`) |
| `hackeros.file-view` (`HackComponentContracts.FileView`) | `IHackFileView`, component `HackFileView` (ADR 0155) | `HackFileView` parameters | File Explorer (`ExplorerFileView`) |
| `hackeros.file-dialogs` (`HackComponentContracts.FileDialogs`) | none: opened through `IDialogService.OpenWithAsync`, `ShowFilePropertiesAsync`, `ResolveFileConflictAsync` (`HackFileDialogs`, ADR 0155 §5) | `Dialog`, `Request`, `Completed` parameters | File Explorer (`ExplorerFileDialogs`) |
| `hackeros.context-menu` (`HackComponentContracts.ContextMenu`) | `IHackContextMenu`, component `HackContextMenu` ([`context-menu.md`](context-menu.md)) | none | Platform (`PlatformContextMenu`, which registers the menu; `ContextMenuOverlay` draws it, ADR 0154) |

### `HackFileView` and `IHackFileView`

```razor
<HackFileView InitialDirectory="@home"
              Mode="HackFileViewMode.Icons"
              Features="HackFileViewFeatures.All & ~HackFileViewFeatures.Delete"
              Opening="OnOpening"
              ContextMenuOpening="OnMenu"
              ControllerChanged="files => _files = files" />
```

| Parameter | Role |
| --- | --- |
| `InitialDirectory`, `Mode`/`ModeChanged`, `AllowMultiSelect`, `Filter` | Folder, layout (`Details`, `Icons`, `Tree`, `Desktop`), selection, filter (call `RefreshFilter` again). |
| `Features` | What the control does on its own (`Clipboard`, `Delete`, `Rename`, `CreateFolder`, `DragDrop`, `OpenWith`, `Properties`); a removed function disappears from the menu and its key does nothing. |
| `SelectedResource` | Handle of a folder chosen in the OS dialog: the control lists with it. |
| `Opening` | Before an item is opened; `Handled = true` lets the host open it itself. |
| `Navigated`, `SelectionChanged`, `Renamed`, `Deleted`, `ErrorOccurred` | What happened; without `ErrorOccurred`, errors become notifications. |
| `DesktopOptions`, `DesktopPositions`, `DesktopPositionsChanged` | `Desktop` mode only (ADR 0160 §2): `[view]` options, cell of each item (`::home`, `::trash` for system icons), positions to save after a move or a new item. The host keeps `desktop.config`, the provider places with `HackDesktopLayout`. See [`desktop.md`](../dev/desktop.md). |
| `ContextMenuOpening` | After the plan §7 menu is built: `Scope`, `Items`, `Directory`, `Menu` (find, hide, disable, add a group), `Cancel`. Entries registered by apps are added **afterwards** by the OS (ADR 0159 §4). |

`IHackFileView`: `CurrentDirectory`, `Items`, `SelectedItems`, `CanPaste`,
`IsBusy`, `StateChanged`, `NavigateAsync`, `RefreshAsync`, `RevealAsync`,
`ClearSelection`, `RefreshFilter`, `WhenIdleAsync`, and
`CanExecute`/`ExecuteAsync(command)` whose commands are the
identifiers of `HackFileMenuItems` (`open`, `cut`, `paste`, `rename`,
`delete`, `properties`...). Everything runs with the host's rights.

Since ADR 0165, the explorer window combines its search with a
**Show hidden files and folders** command, off by default:
names starting with a dot are hidden. This filter also applies to the
already loaded descendants of the tree and to their keyboard navigation. The mobile
home configuration is described in [mobile-home-screen.md](../dev/mobile-home-screen.md).

A control provider (the explorer, or the third-party app chosen by the
administrator) **sets `HackMenuDefinition.FileMenu`** (`HackFileMenuContext`:
scope, items, displayed folder) on each file menu it opens:
this is what allows the OS to add app entries to it (ADR 0159 §4, see
[`context-menu.md`](context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4)).

### `IHackTerminal`

| Member | Role |
| --- | --- |
| `RunCommandAsync(line, token)` | Displays the line at the prompt, runs it after the keys already typed, returns `HackTerminalCommandResult(CommandLine, ExitStatus, Output)`. The token interrupts the command (status 130). |
| `SetWorkingDirectoryAsync(path)` | Visible `cd` in the terminal. |
| `SendInputAsync(text)` | Keyboard bytes (`\r` = Enter, `\u0003` = Ctrl+C). |
| `ReadOutput(max)` | Recently displayed text, without control sequences (256 KiB kept). |
| `ClearAsync()`, `FocusAsync()` | Clear the screen, take focus. |
| `WorkingDirectory`, `Title`, `IsRunning`, `IsBusy` | State. |
| `OutputReceived`, `CommandCompleted`, `StateChanged`, `Exited` | Events on the render thread. |

The shell is a child of the host's process: the host must request
`shell.host`.

## Architecture

| Item | Project | Role |
| --- | --- | --- |
| `AppComponent`, `AppComponentManifest`, `AppManifest.Components` | `HackerOs.App.Abstractions` | Declaration and manifest (`components`, omitted if empty). |
| `HackComponentContracts`, `IHackTerminal`, `HackTerminalOptions`, `HackTerminalCommandResult` | `HackerOs.AppSdk` (`.Components`) | Contracts. |
| `AppComponentContracts`, `AppComponentProviderScope` | `HackerOs.App.Abstractions` | Contracts known to the OS and who can provide them (ADR 0148 §3.3, ADR 0156). |
| `IHackComponentHost`, `HackComponentResolution`, `HackEmbeddableComponent`, `HackEmbeddedComponent<T>`, `HackContractComponent<T>` | `HackerOs.AppSdk.Blazor` (`.Components`) | Blazor side of the SDK. |
| `AppCatalog.ComponentProviders`, `AppComponentProvider` | `HackerOs.Platform.Core` | Provider registry read at startup. |
| `HackComponentHost`, `HackPlatformComponent` | `HackerOs.Platform.Blazor` (`.Components`) | Platform providers first, then the administrator's choice (`Selection`), then the first installed provider that loads (`AppLifecycleOrchestrator.EnsureDescriptorAsync`); checks the type, writes `component.fallback` (`ReportWarning`) and `component.unavailable`. |
| `ComponentProviderService`, `ComponentProvidersDocument`, `IComponentProviderSelection` | `HackerOs.Platform.Core` (`.ComponentProviders`) | `/etc/hackeros/components.config`: reading, approval, writing, audit, removal on uninstall (ADR 0156, ADR 0158). |
| `HackComponentProviders`, `IAppComponentProviderAdministrationGateway`, `HackComponentContractInfo`, `HackComponentProviderInfo` | `HackerOs.AppSdk` (`.Administration`) | Provider choice for Settings (`apps.manage`). |
| `OsComponentsPanel` | `HackerOs.Apps.Settings` | "OS components" tab. |
| `TerminalEmbed` | `HackerOs.Apps.Terminal` | Provider of `hackeros.terminal`. |

## Tasks

- [x] `Components` in `AppDefinition`, the mapper, `AppManifest`, the schema,
  the serializer, the test canonical form and the validator.
- [x] `IHackTerminal` and `HackTerminalOptions` contracts.
- [x] `HackEmbeddableComponent`, `HackEmbeddedComponent<T>`,
  `IHackComponentHost`.
- [x] `HackComponentHost` and `EnsureDescriptorAsync` (lazy loading).
- [x] `TerminalPanel` implements `IHackTerminal` (input queue, transcript,
  command result); `TerminalEmbed` registered by the Terminal app.
- [x] The editor embeds the terminal through the contract in its dock.
- [x] Tests: manifest, validator, mapper, registry, sequence
  cleanup.
- [x] Work item 1 of the desktop plan (ADR 0148, ADR 0153): typed component
  `HackContractComponent<T>` and `Parameters`, `UnmatchedParameters`, registry
  in `AppCatalog`, reserved contracts (`HOSAPP0025`,
  `catalog.component.reserved`, `manifest.component.contract.platform`),
  `HackPlatformComponent`, `component.unavailable` in the log; tests of the
  validator, the catalog, aggregation, the registry and the typed component.
- [x] Work item 3c of the desktop plan (ADR 0156, ADR 0158): file
  contracts open to any windowed app (`HOSAPP0025` removed),
  `/etc/hackeros/components.config`, administrator's choice tried
  first, `component.fallback` fallback, `HackComponentProviders` and the
  "OS components" tab with the approval window, `component.provider`
  in `auth.log`, line removed on uninstall for the machine; tests
  of the service, the gateway, the registry, the catalog, the validator and
  aggregation.
- [x] Work item 3 of the desktop plan (ADR 0155): `HackFileView`, `IHackFileView`
  and the provided dialogs, `ExplorerFileView` and `ExplorerFileDialogs`
  registered by the explorer; test of the passing of all parameters.
