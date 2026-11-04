> [Version française](../../../fr/wasm/framework/embedded-components.md)

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
        [new(KazoComponentContracts.Terminal, typeof(TerminalEmbed))];
}
```

```razor
@inherits KazoEmbeddableComponent
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
assembly. The component derives from `KazoEmbeddableComponent` and receives
`Options` and `ControllerReady`; `HostContext` is the context of the host
window.

## Embedding a component (host app)

```razor
<KazoEmbeddedComponent TController="IKazoTerminal"
                       Contract="@KazoComponentContracts.Terminal"
                       Options="_options"
                       ControllerChanged="terminal => _terminal = terminal">
    <Unavailable Context="reason"><p>@reason</p></Unavailable>
</KazoEmbeddedComponent>
```

Keep the same `Options` instance from one render to the next: a new
instance is passed on to the component. The controller remains valid as long as
`KazoEmbeddedComponent` is rendered.

## Typed component (contract provided by the OS)

For an OS contract, the SDK gives a typed component that the app uses
like any Blazor component. It derives from
`KazoContractComponent<TController>` and copies its parameters by name
(reduced form; the real `KazoFileView` is described further down):

```csharp
public sealed class KazoFileView : KazoContractComponent<IKazoFileView>
{
    [Parameter] public string? Path { get; set; }
    [Parameter] public EventCallback<string> Opened { get; set; }

    protected override string Contract => KazoComponentContracts.FileView;

    protected override void AddProviderParameters(IDictionary<string, object?> parameters)
    {
        parameters[nameof(Path)] = Path;
        parameters[nameof(Opened)] = Opened;
    }
}
```

The provider (`KazoEmbeddableComponent`) declares the same names as
`[Parameter]`. A parameter it does not know yet (app compiled with a
newer SDK) is put in `UnmatchedParameters` instead of making the render
fail; it must therefore not declare its own
`CaptureUnmatchedValues`. The base exposes `ControllerChanged`, `Unavailable`,
`Controller` and the `OnControllerChangedAsync` hook, and only passes a
new set of parameters if a value has changed. Underneath,
`KazoEmbeddedComponent<T>` receives these values through its `Parameters` parameter
(`Options` and `ControllerReady` keep priority).

**Rights.** The provider is rendered in the host's window, with the host's
`AppContext`; the callbacks it invokes (`Opened`...)
run in the host's context. An app does not see one more file
by displaying another app's control.

## Registry and providers

- **Apps.** The catalog builds the registry at startup from the
  manifests (`AppCatalog.ComponentProviders`,
  `GetComponentProviders(contract)`): system apps first, then by
  identifier. `KazoComponentHost` skips uninstalled apps and loads
  the provider's assembly on first use.
- **Administrator's choice** (ADR 0156, ADR 0158). For
  `kazoos.file-view` and `kazoos.file-dialogs`, `KazoComponentHost` reads
  `/etc/kazoos/components.config` (`IComponentProviderSelection`, reread on
  each resolution) and tries the chosen app first; the others remain
  fallbacks, the explorer first. If the chosen app is disabled for the
  user or fails to load, the next one takes over and
  `component.fallback` is written as a warning in `/var/log/syslog`
  (source `components`), once per session. With no line, the explorer
  applies.

  ```ini
  [providers]
  kazoos.file-view=com.example.files
  kazoos.file-dialogs=com.example.files
  ```

  The choice is made in Settings ▸ OS components
  (`KazoComponentProviders`, capability `apps.manage`): a non-system app
  goes through the dangerous capability approval window, with the risk sentence
  "This app's code will run in every application
  that displays files, with their rights."; a system app or the
  return to the explorer only require an administrator. Each
  decision goes to `/var/log/auth.log` (`component.provider`). An app
  uninstalled for the machine loses its line.
- **Platform.** The platform registers its own providers in the
  service container (`services.AddSingleton(new KazoPlatformComponent(contrat, typeof(Composant)))`);
  they come before any app, without loading, and their `ProviderAppId` is
  `KazoComponentResolution.PlatformProviderId` (`"platform"`).
- **Who can provide** (`AppComponentContracts.GetProviderScope`):

| Contract | Provider allowed | Refusal |
| --- | --- | --- |
| `kazoos.terminal` | any windowed app | none |
| `kazoos.file-view`, `kazoos.file-dialogs` | any windowed app (`AdministratorSelected`); the administrator chooses the one in service, the explorer by default (ADR 0156) | none (`HOSAPP0025` is removed, ADR 0158 §1) |
| `kazoos.context-menu` | platform | `manifest.component.contract.platform` (`HOSAPP0009` at the app's build) |

- **Unavailable.** An OS contract (files or menu) with no
  provider that loads is an OS error: `component.unavailable` is written in `/var/log/syslog`
  (source `components`). An open contract without a provider is only
  shown to the user (`Unavailable`).

## Contracts

| Contract | Controller | Options | Provider |
| --- | --- | --- | --- |
| `kazoos.terminal` (`KazoComponentContracts.Terminal`) | `IKazoTerminal` | `KazoTerminalOptions` (`WorkingDirectory`, `ShowBanner`, `FocusOnStart`) | Terminal (`TerminalEmbed`) |
| `kazoos.file-view` (`KazoComponentContracts.FileView`) | `IKazoFileView`, component `KazoFileView` (ADR 0155) | `KazoFileView` parameters | File Explorer (`ExplorerFileView`) |
| `kazoos.file-dialogs` (`KazoComponentContracts.FileDialogs`) | none: opened through `IDialogService.OpenWithAsync`, `ShowFilePropertiesAsync`, `ResolveFileConflictAsync` (`KazoFileDialogs`, ADR 0155 §5) | `Dialog`, `Request`, `Completed` parameters | File Explorer (`ExplorerFileDialogs`) |
| `kazoos.context-menu` (`KazoComponentContracts.ContextMenu`) | `IKazoContextMenu`, component `KazoContextMenu` ([`context-menu.md`](context-menu.md)) | none | Platform (`PlatformContextMenu`, which registers the menu; `ContextMenuOverlay` draws it, ADR 0154) |

### `KazoFileView` and `IKazoFileView`

```razor
<KazoFileView InitialDirectory="@home"
              Mode="KazoFileViewMode.Icons"
              Features="KazoFileViewFeatures.All & ~KazoFileViewFeatures.Delete"
              Opening="OnOpening"
              ContextMenuOpening="OnMenu"
              ControllerChanged="files => _files = files" />
```

| Parameter | Role |
| --- | --- |
| `InitialDirectory`, `Mode`/`ModeChanged`, `AllowMultiSelect`, `Filter` | Folder, layout (`Details`, `Icons`, `Tree`, `Desktop`), selection, filter (call `RefreshFilter` again). |
| `OpenOnTap` | One click or tap opens the item, with no double click, as on a phone; off by default (ADR 0171). |
| `Features` | What the control does on its own (`Clipboard`, `Delete`, `Rename`, `CreateFolder`, `DragDrop`, `OpenWith`, `Properties`); a removed function disappears from the menu and its key does nothing. |
| `SelectedResource` | Handle of a folder chosen in the OS dialog: the control lists with it. |
| `Opening` | Before an item is opened; `Handled = true` lets the host open it itself. |
| `Navigated`, `SelectionChanged`, `Renamed`, `Deleted`, `ErrorOccurred` | What happened; without `ErrorOccurred`, errors become notifications. |
| `DesktopOptions`, `DesktopPositions`, `DesktopPositionsChanged` | `Desktop` mode only (ADR 0160 §2): `[view]` options, cell of each item (`::home`, `::trash` for system icons), positions to save after a move or a new item. The host keeps `desktop.config`, the provider places with `KazoDesktopLayout`. See [`desktop.md`](../dev/desktop.md). |
| `ContextMenuOpening` | After the plan §7 menu is built: `Scope`, `Items`, `Directory`, `Menu` (find, hide, disable, add a group), `Cancel`. Entries registered by apps are added **afterwards** by the OS (ADR 0159 §4). |

`IKazoFileView`: `CurrentDirectory`, `Items`, `SelectedItems`, `CanPaste`,
`IsBusy`, `StateChanged`, `NavigateAsync`, `RefreshAsync`, `RevealAsync`,
`ClearSelection`, `RefreshFilter`, `WhenIdleAsync`, and
`CanExecute`/`ExecuteAsync(command)` whose commands are the
identifiers of `KazoFileMenuItems` (`open`, `cut`, `paste`, `rename`,
`delete`, `properties`...). Everything runs with the host's rights.

Since ADR 0165, the explorer window combines its search with a
**Show hidden files and folders** command, off by default:
names starting with a dot are hidden. This filter also applies to the
already loaded descendants of the tree and to their keyboard navigation. The mobile
home configuration is described in [mobile-home-screen.md](../dev/mobile-home-screen.md).

A control provider (the explorer, or the third-party app chosen by the
administrator) **sets `KazoMenuDefinition.FileMenu`** (`KazoFileMenuContext`:
scope, items, displayed folder) on each file menu it opens:
this is what allows the OS to add app entries to it (ADR 0159 §4, see
[`context-menu.md`](context-menu.md#app-entries-in-the-file-menus-work-item-4)).

### `IKazoTerminal`

| Member | Role |
| --- | --- |
| `RunCommandAsync(line, token)` | Displays the line at the prompt, runs it after the keys already typed, returns `KazoTerminalCommandResult(CommandLine, ExitStatus, Output)`. The token interrupts the command (status 130). |
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
| `AppComponent`, `AppComponentManifest`, `AppManifest.Components` | `Kazo.Os.App.Abstractions` | Declaration and manifest (`components`, omitted if empty). |
| `KazoComponentContracts`, `IKazoTerminal`, `KazoTerminalOptions`, `KazoTerminalCommandResult` | `Kazo.Os.AppSdk` (`.Components`) | Contracts. |
| `AppComponentContracts`, `AppComponentProviderScope` | `Kazo.Os.App.Abstractions` | Contracts known to the OS and who can provide them (ADR 0148 §3.3, ADR 0156). |
| `IKazoComponentHost`, `KazoComponentResolution`, `KazoEmbeddableComponent`, `KazoEmbeddedComponent<T>`, `KazoContractComponent<T>` | `Kazo.Os.AppSdk.Blazor` (`.Components`) | Blazor side of the SDK. |
| `AppCatalog.ComponentProviders`, `AppComponentProvider` | `Kazo.Os.Platform.Core` | Provider registry read at startup. |
| `KazoComponentHost`, `KazoPlatformComponent` | `Kazo.Os.Platform.Blazor` (`.Components`) | Platform providers first, then the administrator's choice (`Selection`), then the first installed provider that loads (`AppLifecycleOrchestrator.EnsureDescriptorAsync`); checks the type, writes `component.fallback` (`ReportWarning`) and `component.unavailable`. |
| `ComponentProviderService`, `ComponentProvidersDocument`, `IComponentProviderSelection` | `Kazo.Os.Platform.Core` (`.ComponentProviders`) | `/etc/kazoos/components.config`: reading, approval, writing, audit, removal on uninstall (ADR 0156, ADR 0158). |
| `KazoComponentProviders`, `IAppComponentProviderAdministrationGateway`, `KazoComponentContractInfo`, `KazoComponentProviderInfo` | `Kazo.Os.AppSdk` (`.Administration`) | Provider choice for Settings (`apps.manage`). |
| `OsComponentsPanel` | `Kazo.Os.Apps.Settings` | "OS components" tab. |
| `TerminalEmbed` | `Kazo.Os.Apps.Terminal` | Provider of `kazoos.terminal`. |

## Tasks

- [x] `Components` in `AppDefinition`, the mapper, `AppManifest`, the schema,
  the serializer, the test canonical form and the validator.
- [x] `IKazoTerminal` and `KazoTerminalOptions` contracts.
- [x] `KazoEmbeddableComponent`, `KazoEmbeddedComponent<T>`,
  `IKazoComponentHost`.
- [x] `KazoComponentHost` and `EnsureDescriptorAsync` (lazy loading).
- [x] `TerminalPanel` implements `IKazoTerminal` (input queue, transcript,
  command result); `TerminalEmbed` registered by the Terminal app.
- [x] The editor embeds the terminal through the contract in its dock.
- [x] Tests: manifest, validator, mapper, registry, sequence
  cleanup.
- [x] Work item 1 of the desktop plan (ADR 0148, ADR 0153): typed component
  `KazoContractComponent<T>` and `Parameters`, `UnmatchedParameters`, registry
  in `AppCatalog`, reserved contracts (`HOSAPP0025`,
  `catalog.component.reserved`, `manifest.component.contract.platform`),
  `KazoPlatformComponent`, `component.unavailable` in the log; tests of the
  validator, the catalog, aggregation, the registry and the typed component.
- [x] Work item 3c of the desktop plan (ADR 0156, ADR 0158): file
  contracts open to any windowed app (`HOSAPP0025` removed),
  `/etc/kazoos/components.config`, administrator's choice tried
  first, `component.fallback` fallback, `KazoComponentProviders` and the
  "OS components" tab with the approval window, `component.provider`
  in `auth.log`, line removed on uninstall for the machine; tests
  of the service, the gateway, the registry, the catalog, the validator and
  aggregation.
- [x] Work item 3 of the desktop plan (ADR 0155): `KazoFileView`, `IKazoFileView`
  and the provided dialogs, `ExplorerFileView` and `ExplorerFileDialogs`
  registered by the explorer; test of the passing of all parameters.
