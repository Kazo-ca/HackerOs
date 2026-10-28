> [Version française](../../../fr/wasm/framework/context-menu.md)

# OS context menu (`hackeros.context-menu`)

## Purpose

A single context menu for the whole OS, multi-level, drawn by the
platform in the OS theme, which an app attaches to any element of its
window without writing JavaScript. Right click, long press with a finger, the Menu key
and Shift+F10 open it; the keyboard and screen readers move around it as in
the Windows or GNOME menu. It is the basis of the file control menus
(work item 3) and of the entries registered by apps (work item 4) of the plan
[`desktop-and-context-menu-plan.md`](../dev/plans/desktop-and-context-menu-plan.md).

Decisions: [ADR 0150](../dev/adr/0150-os-context-menu.md); implementation
adjustments: [ADR 0154](../dev/adr/0154-context-menu-implementation-adjustments.md).

Since work item 3 (ADR 0155), the file control (`HackFileView`) menus
are OS menus: the explorer provider
defines one menu per control, fills it on each opening (plan §7) and
passes it to the host's `ContextMenuOpening`; the `MudMenu` of `FileView` is no longer
drawn when the view is bound to the OS menu. See
[`embedded-components.md`](embedded-components.md#hackfileview-and-ihackfileview).

Since work item 4 (ADR 0159), the OS adds to these menus the entries that
apps register: see [App entries](#app-entries-in-the-file-menus-work-item-4).

## Using a menu in an app

### In the markup

```razor
@using HackerOs.AppSdk.Blazor.ContextMenus
@using HackerOs.AppSdk.Icons

<HackContextMenu Name="rows" Opening="OnRowsMenuOpening">
    <HackMenuItem Id="open" Title="@L["Menu.Open"]" Icon="OpenIcon" OnClick="@(item => OpenAsync((Row)item!))" />
    <HackMenuGroup>
        <HackMenuItem Id="export" Title="@L["Menu.Export"]">
            <HackMenuItem Id="csv" Title="CSV" OnClick="@(item => ExportAsync((Row)item!, "csv"))" />
            <HackMenuItem Id="json" Title="JSON" OnClick="@(item => ExportAsync((Row)item!, "json"))" />
        </HackMenuItem>
    </HackMenuGroup>
    <HackMenuGroup>
        <HackMenuItem Id="delete" Title="@L["Menu.Delete"]" Shortcut="Del" OnClick="@(item => DeleteAsync((Row)item!))" />
    </HackMenuGroup>
</HackContextMenu>

@foreach (Row row in _rows)
{
    <tr tabindex="0" @attributes="@(Menus.Attach("rows", row))">…</tr>
}

@code {
    private static readonly HackIcon OpenIcon = HackIcons.Vector(IconLibrary.Lucide, "folder-open");

    // Avant chaque ouverture, pour l'objet attaché : masquer, désactiver, ajouter, annuler.
    private void OnRowsMenuOpening(HackMenuOpeningEventArgs args)
    {
        if (args.Item is Row { IsReadOnly: true })
        {
            args.Menu.Disable("delete");
        }
    }
}
```

- `HackContextMenu` draws **nothing** where it is written: it describes the menu
  to the OS. The name (`Name`) is unique within the window.
- `Menus` exists on `WindowAppBase` and on `HackComponentBase`.
  `Menus.Attach(name, object)` returns two attributes (`data-hack-menu`,
  `data-hack-menu-token`); the object stays in C#, under an opaque token. The same
  object attached again keeps its token (by reference for an object, by
  value for a string or a value type).
- The nearest element that carries a token wins: a row in a list
  that is itself attached opens the row's menu, the empty space of the list opens that of the
  list.
- An attribute whose value contains quotation marks is written `@(…)` or between
  apostrophes: `@attributes="@(Menus.Attach("rows", row))"`.
- The action (`OnClick`) receives the attached object and runs in the app's
  context, like an `@onclick` of the component that declared it (the component is
  redrawn afterwards).
- For a row to open from the keyboard (Menu key, Shift+F10), it must
  be able to take focus (`tabindex="0"`).
- Prefer `IsVisible="false"` to an `@if` around an entry: an entry that
  appears after the first render goes to the end of its level.

### In C#

```csharp
protected override void OnAppInitialized()
{
    Menus.Define("rows-area", menu => menu
        .Item("new", L["Menu.New"], _ => CreateAsync(), icon: HackIcons.Vector(IconLibrary.Lucide, "file-plus"))
        .Group(group => group
            .Submenu("sort", L["Menu.SortBy"], sort => sort
                .Item("sort-name", L["Menu.Name"], _ => SortAsync("name"))
                .Item("sort-date", L["Menu.Date"], _ => SortAsync("date"), isDisabled: true))));
}
```

- Same model as the markup; the builder is called again on each opening,
  so titles follow the current language and state.
- `Define` returns an `IDisposable`; closing the window also removes the
  menu. A C# menu and a markup menu with the same name in a window are an
  error (`InvalidOperationException`).

### From a button

```razor
<button @onclick="@(args => Menus.OpenAsync("rows-area", null, args.ClientX, args.ClientY))">⋯</button>
```

`HackContextMenu.Controller` (`IHackContextMenu`) also gives `OpenAsync(object, x, y)`
and `Close()`.

The full example is in the sample app `HackerOs.Samples.WindowApp`
(section "Context Menu").

## Model

| Type | Role |
|---|---|
| `HackMenuDefinition` | A menu: `Name`, `Label` (accessible name), `Groups`; `Find`, `Hide`, `Disable`, `AddGroup`, `Clone`, `Validate`; `MaxDepth = 3` |
| `HackMenuGroupDefinition` | A group of entries: a separator is only drawn between two visible groups |
| `HackMenuItemDefinition` | An entry: `Id`, `Title` (already translated), `Icon`, `IsDisabled`, `IsVisible`, `Shortcut` (text only), `OnClick`, `Children` (submenu, groups) |
| `HackMenuBuilder` | Construction in C#: `Item`, `Submenu`, `Group` |
| `HackMenuOpeningEventArgs` | `Menu` (copy for this opening), `Item`, `Cancel` |
| `HackIcons.Vector(library, name)`, `HackIcons.FromImage(path)` | Described icon (`HackIcon`), drawn by the OS (`HackerOs.AppSdk.Icons`) |
| `HackMenus` | A component's `Menus`: `Attach`, `Define`, `OpenAsync`, `Close`, `IsAvailable` |
| `IHackMenuHost` | OS side of a window's menus (implemented by the platform, passed as a cascading value) |
| `IHackContextMenu` | Contract controller: `Name`, `OpenAsync`, `Close` |

Rules checked on each opening (`Validate`): identifiers present and
unique across the whole menu, three levels at most, no action on an entry
that opens a submenu. The C# builder and `HackMenuItem` refuse a
fourth level from construction.

### Separators

After the filters (`IsVisible`, `Opening` adjustments):

1. a group with no visible entry disappears;
2. a submenu with no visible entry hides its parent entry;
3. a separator is only drawn between two visible groups: never at the start,
   at the end, nor two in a row. The rule applies at each level.

## Architecture

```text
App (SDK)                                         Plateforme (Platform.Blazor)
<HackContextMenu Name="rows">  ── contrat ──►     PlatformContextMenu (HackPlatformComponent)
   HackMenuItem / HackMenuGroup                       │ inscrit le menu dans la fenêtre
<tr @attributes="Menus.Attach("rows", row)">          ▼
        │ data-hack-menu-token              ContextMenuScope (une par fenêtre, IHackMenuHost)
        ▼                                             │
ContextMenuOverlay.razor.js ── jeton, x, y ──► ContextMenuService (un menu ouvert dans tout l'OS)
  (clic droit, appui long, Touche Menu,               │ Build → copie → Opening → Validate → Arrange
   Maj+F10, fermeture, jetons libérés)                ▼
                                             ContextMenuOverlay (bureau : panneaux ; mobile : feuille)
```

- **Contract.** `HackContextMenu` is a typed component
  (`HackContractComponent<IHackContextMenu>`, ADR 0153); the platform provides
  `hackeros.context-menu` through `HackPlatformComponent` (`PlatformContextMenu`),
  with no manifest or app loading. `PlatformContextMenu` draws nothing: it
  registers the menu in the window and renders the controller.
- **Window.** `WindowAppRenderer` creates a `ContextMenuScope` per window
  (`ContextMenuService.CreateScope(AppContext)`) and passes it as a cascading value
  (`IHackMenuHost`); it is destroyed with the window (menus, attached objects,
  open menu). An OS area (desktop, taskbar) creates its own with
  `CreateScope(null)`.
- **Service.** `ContextMenuService` (scoped, like `WindowRuntime`) keeps the
  single open menu (`Current`, `Changed`). On opening: fresh copy of the menu,
  `Opening` in the app's context, validation, layout
  (`ContextMenuLayout.Arrange`). A menu that fails opens nothing and the error goes
  to `/var/log/syslog` (`ILogger`); the OS carries on. An action that throws
  is written to the log in the same way.
- **Rendering.** `ContextMenuOverlay` is in `DesktopShell` (floating panels)
  and `MobileShell` (bottom sheet, `Presentation="Sheet"`). Native component with
  isolated CSS, not `MudMenu`. The keyboard goes through `ContextMenuNavigator`, testable
  without a browser.
- **Script.** `ContextMenuOverlay.razor.js`, the component's module imported by
  the C# (no host references it): listens to `contextmenu`, the long press
  (500 ms, 10 px), the Menu key and Shift+F10 on the focused element;
  cancels the browser menu on an attached element; places the panels
  inside the browser window; closes the menu on a press outside, a
  scroll, a resize or loss of focus; gives focus back to
  the original element; reports the tokens of elements removed from the page.

### What the mouse, finger and keyboard do

| Gesture | Effect |
|---|---|
| Right click on an attached element | The menu opens at the pointer; nothing is highlighted |
| Long press with a finger or stylus (500 ms without moving more than 10 px) | The menu opens (bottom sheet on mobile); the click that follows the finger lift is absorbed |
| Menu key, Shift+F10 | The menu opens at the corner of the focused element, first entry highlighted |
| Up, Down | Previous or next entry, wrapping around, skipping separators and disabled entries |
| Home, End | First or last entry |
| Right, Enter on a parent entry | Opens the submenu on its first entry |
| Left | Closes the submenu |
| Enter, Space | Chooses the entry |
| Esc | Closes one level; at the first level, closes the menu |
| Tab | Closes the menu, like Windows |
| A letter | Goes to the next entry that starts with it |
| Hover over a parent entry | Opens its submenu (on desktop) |
| Click or press outside the menu, scroll, resize, loss of focus | Closes the menu; the click continues to its target |

A text field inside an attached element keeps the browser menu
(copy, paste), unless it is itself attached. An element without a token in a
`data-hack-menu-suppress` area (an OS area that has its own menu) does not
open the browser menu either. A `contextmenu` already canceled by the app
(`@oncontextmenu:preventDefault`) is left to the app.

### Accessibility

`role="menu"` on each panel (accessible name: `Label`, otherwise "Context
menu"; the title of the parent entry for a submenu),
`role="menuitem"`, `role="separator"`, `aria-haspopup="menu"`,
`aria-expanded` and `aria-disabled` written as `"true"`/`"false"`
([`old/common-pitfalls.md`](../dev/old/common-pitfalls.md) §8), highlighting announced
through `aria-activedescendant`. Focus returns to the original element on
closing, before the chosen action (a dialog or an in-place rename that it
opens therefore keeps its focus). Reduced motion: no animation.

### Known limits

- A window detached into another browser window (ADR 0078) keeps the
  browser menu: the OS menu is only drawn in the desktop document and in the
  secondary screens (ADR 0179 §2, `dev/secondary-screen-desktop.md`).
- The order of markup entries is that of their first render (see
  `IsVisible` above).

### Desktop and Start menu menus (work item 5)

The desktop background has the `Desktop` scope; the Desktop app adds to it
"View ▸", "Sort by ▸", "Background..." and "Desktop
settings...". The Start menu carries a shell menu,
`hackeros.start-menu.app`, on each app: Open, Pin / Unpin,
"Send to ▸ Desktop (create shortcut)". Details:
[`desktop.md`](../dev/desktop.md#menus).

## App entries in the file menus (work item 4)

Decisions: [ADR 0151](../dev/adr/0151-app-registered-file-menu-entries.md);
adjustments: [ADR 0159](../dev/adr/0159-app-menu-entries-implementation-adjustments.md).

### Purpose

An app adds its own entries to the file menus ("Extract here",
"Publish as website...") without the app that displays the files knowing
about it. The entries appear everywhere the file control
(`HackFileView`) is displayed: explorer, Open / Save dialogs,
any host app and, in work item 5, the desktop, whatever the provider of the
control chosen by the administrator (ADR 0156).

### Declaring entries

The app's `[AppManifest]` class requests the `shell.context-menu` capability and
declares **one** static method marked `[RegisterFileContextMenu]`:

```csharp
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.Icons;

[AppManifest]
public sealed class ZipperApp : AppDefinition
{
    public const string ExtractIntent = "com.example.zipper.extract.v1";

    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ShellContextMenu];
    public override IReadOnlyList<AppIntentDeclarationManifest> Intents => [new(ExtractIntent)];

    [RegisterFileContextMenu]
    public static void RegisterFileContextMenu(RegisterFileContextMenuContext context)
    {
        context.Register("Menu.Extract", "extract", intent: ExtractIntent,
            icon: HackIcons.Vector(IconLibrary.Lucide, "package-open"),
            types: [".zip", "application/zip"], after: HackFileMenuItems.Open);

        // Un sous-menu : le parent n'a pas d'intent, les enfants prennent sa portée.
        context.Register("Menu.Convert", "convert", scope: FileMenuScope.File | FileMenuScope.Directory);
        context.Register("Menu.ToPng", "to-png", intent: "com.example.zipper.to-png.v1",
            parent: "convert", selection: MenuSelection.Each);
    }
}
```

| Parameter | Role | Default |
|---|---|---|
| `title` | Resource key: English in `Resources/AppManifest.resx`, translations in `Resources/AppManifest.<culture>.resx` | required |
| `name` | Short identifier, unique within the app (`extract-here`) | required |
| `target` | `"self"` or the id of another app that declares the intent | `"self"` |
| `intent` | Intent handed to the target; absent on a submenu parent | none |
| `icon` | `HackIcons.Vector(…)` or `HackIcons.FromImage("icons/x.svg")` (app asset) | none |
| `scope` | `File`, `Directory`, `Background`, `Desktop`, combinable | `File` (the parent's for a child) |
| `types` | `.zip`, `application/zip`, `image/*`, `*`, `inode/directory` | all |
| `selection` | `All` (one launch, all paths), `Each` (one per path), `Single` (hidden if several) | `All` |
| `first` / `after` / `afterGroup` | Common group at the top; right after X in its group; new group after X's | common group before "Properties" |
| `parent` | Submenu of an entry of the same app (3 levels at most) | none |

`X` is a constant of `HackFileMenuItems` (`open`, `copy`, `properties`...)
or the `name` of an entry of the app. The method **only records the calls**:
it runs at build time, twice, and must give the same result. The
generator writes the entries to `manifest.json` (`contextMenus`) and reports
an error through `HOSAPP0026` (rule, exception, missing English title),
`HOSAPP0027` (missing capability) or `HOSAPP0028` (target absent from the host or
that does not declare the intent).

### Receiving the click

The target is launched with its own rights. If it declares the intent, it
receives it in `AppContext.LaunchIntent` (or `OnIntentReceivedAsync` for a
single instance already open), with the payload
`{"paths":["/home/alice/a.zip"],"scope":"File"}`, which `HackFileMenuIntent` reads:

```csharp
if (LaunchIntent is { IntentId: ZipperApp.ExtractIntent } intent)
{
    foreach (string path in HackFileMenuIntent.ReadPaths(intent.PayloadJson))
    {
        await ExtractAsync(path);
    }
}
```

Otherwise, it receives the arguments `["<intent>", "<path>", …]`. In
empty space (`Background` and `Desktop` scopes), the path is the folder
displayed.

### How the OS applies them

1. At startup and on each change of installed apps,
   `FileContextMenuRegistry` (Platform Core) copies the manifest entries
   into `/etc/hackeros/context-menus.d/<appId>.json` (install rank,
   fingerprint, app version), removes the files of apps uninstalled
   for the machine, then rereads the folder. An invalid file is ignored and
   reported in `/var/log/syslog`.
2. The file control provider sets `HackMenuDefinition.FileMenu`
   (`HackFileMenuContext`: scope, items, folder) on the menu it opens.
3. After `Opening`, `ContextMenuService` calls `IFileMenuEntrySource`
   (`FileMenuEntries`, Platform Blazor): filters (`FileContextMenuMatcher`:
   scope, types, selection), apps installed for the user and valid targets
   (`FileContextMenuLauncher.IsAvailable`), entries hidden by the
   user, then placement (`FileMenuEntryPlacement`). Entries have
   the id `app:<appId>/<name>`.
4. On click, `FileContextMenuLauncher` rechecks the target and launches the app through
   `AppLifecycleOrchestrator`, with the OS (`org.hackeros.shell`) as sender.

### Hiding an entry

Settings ▸ **File menus** lists the entries of all apps
(submenus under their parent) with a checkbox per entry. The choice is written
to `~/.config/hackeros/context-menus.config`:

```ini
[hidden]
org.hackeros.file-explorer.extract-here=true
```

Hiding a parent hides its submenu; the file is reread on each
opening, so an edit with `nano` applies to the next menu. A system
app does this through the public API `HackFileMenuEntries`
(`GetEntriesAsync`, `SetHiddenAsync`, capability `apps.manage`).

### Migrated OS entries

| App | Entry | Scope and types | Intent |
|---|---|---|---|
| Explorer | "Extract here" (`extract-here`), after "Open" | `.zip` files, `application/zip` | `org.hackeros.file-explorer.extract-here.v1` |
| Web Server Manager | "Publish as website..." (`publish-site`) | folders, only one | `org.hackeros.web-server-manager.publish-site.v1` |

### Tests

- `Tests/HackerOs.App.Abstractions.Tests/AppManifestContextMenusTests.cs`:
  validator rules, canonical form, JSON round trip.
- `Tests/HackerOs.AppSdk.Blazor.Tests/RegisterFileContextMenuTests.cs`:
  `Register`, payload, default identifiers identical in the SDK and the
  validator.
- `Tests/HackerOs.Tools.ManifestGenerator.Tests/ContextMenuRegistrationTests.cs`:
  titles per culture, `HOSAPP0026` to `HOSAPP0028`, determinism.
- `Tests/HackerOs.Platform.Core.Tests/ContextMenus/FileContextMenuTests.cs`:
  `context-menus.d` folder (rank, rewrite, removal, invalid files),
  filters, preferences, launches, `HackFileMenuEntries`.
- `Tests/HackerOs.Platform.Blazor.Tests/ContextMenus/FileMenuEntriesTests.cs`:
  placement, emptied submenus, click, empty space, service.
- `Tests/HackerOs.Apps.Settings.Tests/FileMenuEntriesPanelTests.cs`: list
  order.
- `Tests/HackerOs.UI.E2E.Tests/FileMenuEntriesTests.cs`: "Extract here" after
  "Open" on an uploaded archive, extraction, "Publish as website..."
  on the extracted folder.

### Tasks (work item 4)

- [x] `contextMenus` manifest contract, schema, serializer, validator, `shell.context-menu` capability.
- [x] SDK: `[RegisterFileContextMenu]`, `RegisterFileContextMenuContext`, `MenuSelection`, `HackFileMenuIntent`, `HackMenuDefinition.FileMenu`.
- [x] Generator: method execution, EN/FR titles, `HOSAPP0026` to `HOSAPP0028`.
- [x] OS: `context-menus.d`, filters, placement, launch, hidden entries.
- [x] Migration of "Extract here" and "Publish as website...".
- [x] Settings ▸ File menus and `HackFileMenuEntries`.
- [x] Tests, ADR 0159, documents and index.

## Tests

- `Tests/HackerOs.AppSdk.Blazor.Tests/HackContextMenuTests.cs`: implicit
  groups, fourth level refused, validation, copy, `Menus` outside a window,
  `Define`, description of the markup menu to the provider, action in the
  app's context, nothing drawn without a provider.
- `Tests/HackerOs.Platform.Blazor.Tests/ContextMenus/`: separator collapsing
  (the plan's "Open with" example), empty submenus, keyboard and hover,
  tokens (same object, values, release), duplicate menus, `Opening` on a
  copy, canceled or broken menus, a single open menu in the OS, action in the
  app's context after closing, platform provider end to
  end.
- `Tests/HackerOs.Ecosystem.Tests`: the service container resolves the contract
  to `PlatformContextMenu` without loading an app.

## Tasks (work item 2)

- [x] SDK model (`HackMenuDefinition`, groups, entries, three levels, validation, copy).
- [x] Construction in C# (`HackMenuBuilder`, `Menus.Define`).
- [x] Markup (`HackContextMenu`, `HackMenuItem`, `HackMenuGroup`) on the `hackeros.context-menu` contract.
- [x] `Menus.Attach` and tokens; `Menus` on `WindowAppBase` and `HackComponentBase`.
- [x] `HackIcons` (vector or image).
- [x] Platform provider (`PlatformContextMenu`), one scope per window (`WindowAppRenderer`).
- [x] Service: one open menu in the OS, `Opening`, separator collapsing, errors to the log.
- [x] Native multi-level rendering (desktop) and bottom sheet (mobile), OS theme, ARIA.
- [x] Script: right click, long press, Menu key, Shift+F10, on-screen placement, closing, focus restored, tokens released.
- [x] Keyboard (`ContextMenuNavigator`).
- [x] Example in `HackerOs.Samples.WindowApp`.
- [x] Tests, ADR 0154, document index.
