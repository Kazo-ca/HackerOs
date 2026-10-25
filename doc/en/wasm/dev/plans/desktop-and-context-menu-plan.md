# Desktop icons, OS-provided file control and central context menu

**Status:** plan **validated by Benoit on 2026-10-19** (summary accepted,
answers to questions Q1 to Q22 of section 3). No code is written. The
ADRs of section 10 are written before the code of each work item.

## 1. Purpose

- Display **icons on the desktop**: the content of `~/Desktop`, plus
  Home folder and Recycle Bin, on a grid where each icon keeps its place.
- Make the **file explorer the source of truth** of the file
  control: it provides it to the OS, uses it itself, and the desktop as well as any app
  displays it without embedding its code.
- Reduce the SDK to **contracts**: interfaces, options, events, and a
  host component that receives at runtime the rendering provided by the OS. An
  improvement of the OS benefits all apps without recompilation, and 200
  apps do not embed the same code 200 times.
- Add a **central context menu**, multi-level, provided by the OS,
  usable in any app, and **extensible by apps** for
  file menus, like the verbs of the Windows registry.

## 2. Existing state (main, 2026-10-19)

| Element | State | Where |
|---|---|---|
| `FileView` | Complete control (tree, details, icons, in-place rename, drag and drop, clipboard, transfers), **SDK library compiled into each app**, public since the 1.0 freeze | `Shared/HackerOs.AppSdk.FileView`, ADR 0037, [`file-explorer.md`](../file-explorer.md) |
| `FileView` menu | Scopes background / folder / file / type, stable identifiers (`open`, `cut`, `copy`, `paste`, `paste-into`, `rename`, `delete`, `delete-permanently`, `new-folder`, `refresh`), `InsertAfter`/`InsertBefore`, **a single level**, providers **specific to the instance** (`MudMenu`) | `ContextMenu/FileViewContextMenuContracts.cs` |
| Added entries | "UnZip Here" and "Publish as a web site…", coded in the explorer | `ZipFileContextMenuProvider`, `PublishSiteContextMenuProvider` |
| "Open with" | Explorer dialog (toolbar), "Always use" | `OpenWithDialog`, ADR 0121 |
| Embeddable components | `hackeros.terminal` contract provided by the Terminal app, displayed by `HackEmbeddedComponent<T>` | ADR 0063, [`embedded-components.md`](../../framework/embedded-components.md) |
| OS dialogs | `IDialogService`/`IFileDialogService` contracts in the SDK, implemented by the platform (`VirtualFolderBrowser` for Open/Save; since `desk/3b`, the `hackeros.file-view` control, ADR 0157) | `HackerOs.AppSdk.Blazor`, `Platform.Blazor/Dialogs` |
| Desktop | `~/Desktop` created for each user; `DesktopArea.BackgroundContent` slot provided but empty | `FileSystemSeeder`, `DesktopArea.razor` |
| Intents | Intents defined by apps, `IntentReceived` for an open instance | ADR 0096, [`app-intents.md`](../../framework/app-intents.md) |

## 3. Benoit's decisions (2026-10-19)

| # | Question | Answer |
|---|---|---|
| Q1 | Desktop content | `~/Desktop` **plus** Home folder and Recycle Bin, which the user can choose to display |
| Q2 | Arrangement | **Grid + free position**: snapped and remembered positions, "Auto-arrange", "Align to grid" |
| Q3 | Delivery of the control | The SDK provides **the interface, not the implementation**; an SDK component receives a `RenderFragment` from the OS at runtime. The explorer **registers at startup** as provider of the control **and of its dialog boxes**; other apps never reference it; the OS acts as proxy, the SDK as contract |
| Q4, Q4 bis | Reading the added entries | Like Windows: the registration method runs **at installation** (at build time for built-in apps); the result is a file read at startup |
| Q5 | Public `FileView` | **SDK 2.0 now**: `FileView` is removed; 1.x apps are refused and must be recompiled |
| Q6 | Provider of the generic menu | **The OS** (the platform) |
| Q7 | Filter by type | **Declared filter** (`types`) and number of selected items |
| Q8 | Groups and separators | `after: "X"` = in the group of X; `afterGroup: "X"` = new group after the one of X |
| Q9 | Order between apps | **Installation order** |
| Q10 | Submenus of apps | **Yes**, by `parent` (entry of the same app) |
| Q11 | Multiple selection | **Declared per entry**: `All` (default), `Each`, `Single` |
| Q12 | Entries without position | **A common group** at the end of the menu |
| Q13 | `target` | `"self"` **or the identifier of another app** that declares the intent |
| Q14 | Menu in an app's markup | **Attributes + helper**: `@attributes="Menus.Attach("files", item)"` on any element |
| Q15 | Control of added entries | **Capability + setting**: declared capability, and Settings lets the user hide an app's entries |
| Q16 | Identifiers of default entries | **Short** (`open`, `copy`…) with C# constants in the SDK |
| Q17 | "Open with" | Always a **submenu**, ending with "Choose another application…" |
| Q18 | Desktop background menu | **4th scope `Desktop`** |
| Q19 | Translated title | **Resource key**, EN and FR texts copied at installation |
| Q20 | Mobile | **Menu yes** (long press), **icons no** |
| Q21 | Apps on the desktop | **`.desktop`** files |
| Q22 | This document | In `docs/`, through a PR to `main` |
| Q23 | File control provider (2026-10-20) | **Replaceable** by a third-party app; chosen by an administrator in Settings, the default explorer ([ADR 0156](../adr/0156-replaceable-file-control-provider.md)) |

Details retained without a question ("defaults" of the validated summary):
automatic collapsing of separators (§6.2), content of the default menus (§7),
file locations (§5, §8), Menu key and Shift+F10 (§6.3).

## 4. Architecture: the SDK is the contract, the OS is the proxy

```text
App (SDK 2.0: contracts only)
  <HackFileView Options=… OnOpened=… />   <button @attributes="Menus.Attach("row", item)">
        │ hackeros.file-view contract               │ hackeros.context-menu contract
        ▼                                           ▼
OS (Platform.Blazor) ── provider registry ── HackComponentHost
        │ read at startup from the manifests         │
        ▼                                           ▼
File explorer (system app)                     Platform (shell)
  FileViewProvider, file dialogs                 ContextMenuProvider, ContextMenuRegistry
  (loaded on first use)                          (/etc/hackeros/context-menus.d/*.json)
```

- **Generic host component.** `HackEmbeddedComponent<T>` (ADR 0063)
  becomes the base: the SDK exposes for each contract a typed component
  (`HackFileView`, `HackContextMenu`) with ordinary parameters and
  `EventCallback`s; at runtime, the OS gives it the
  `RenderFragment` of the registered provider and the controller (`IHackFileView`).
- **Registration.** A provider declares itself in its `[AppManifest]`
  (`Components`, like the terminal). The OS reads the manifests at startup and
  thus knows all providers; an app's code is loaded only on
  first use of the contract (`AppLifecycleOrchestrator.EnsureDescriptorAsync`).
- **Providers.**

| Contract | SDK controller and component | Provider |
|---|---|---|
| `hackeros.file-view` | `IHackFileView`, `HackFileView` | Explorer |
| `hackeros.file-dialogs` | Open with, Properties, name conflict dialogs, and the file view of the Open / Save / Choose a folder dialogs (finally the `hackeros.file-view` control in the platform dialog, [ADR 0157](../adr/0157-file-dialogs-show-the-file-control.md)) | Explorer |
| `hackeros.context-menu` | `IHackContextMenu`, `HackContextMenu`, `Menus.Attach` | Platform |

- **Replaceable provider.** A third-party app can provide `hackeros.file-view`
  and `hackeros.file-dialogs` in place of the explorer, if an administrator
  chooses it in Settings ([ADR 0156](../adr/0156-replaceable-file-control-provider.md)).
- **Rights.** The provided rendering runs with **the rights of the host app**:
  the provider receives the host's gateways (`HostContext`), never its
  own, like `FileView` today (ADR 0037 §1). An app therefore does not see
  more files by displaying the explorer's control.
- **Unavailable.** If the provider is missing (app disabled, loading
  failed), the component displays its `Unavailable` content; the explorer and
  the platform being system components, this case remains an error.

## 5. The desktop

> Implemented by work item 5: see [`desktop.md`](../desktop.md) and
> [ADR 0160](../adr/0160-desktop-implementation-adjustments.md) (the desktop is
> the system app `org.hackeros.desktop`, `desktop.config` is a user-only
> file).

- `DesktopShell` fills `DesktopArea.BackgroundContent` with `HackFileView`
  in **Desktop mode** on `~/Desktop`, with the rights of the session.
- **System icons**: Home folder and Recycle Bin, virtual entries
  added by the desktop (not files), checked in Settings ▸
  Desktop. Recycle Bin menu: Open, Empty the recycle bin; drop = move to
  the recycle bin.
- **Desktop mode** of the control: grid of cells, icon in the chosen cell,
  drag to move (snapped), lasso selection, in-place rename,
  drag and drop with the explorer and between windows (ADR 0037 §4).
- **Positions** in `~/.config/hackeros/desktop.config`
  (`[positions]` `nom=colonne,ligne`, `[view]` `auto-arrange`,
  `icon-size`, `show-home`, `show-trash`); a new file takes the
  first free cell; an absent name is forgotten at the next
  save. Auto-arrange sorts by name, then keeps
  the order.
- **App shortcuts**: `~/Desktop/<nom>.desktop` in the freedesktop format
  (`[Desktop Entry]`, `Type=Application`, `Name`, `Exec=<appId> [args]`,
  `Icon`). Created by dragging an app from the Start menu onto the desktop or by
  "Send to ▸ Desktop". Everywhere the control shows them: icon and name
  of the app, double-click = launch (`LaunchApp`). A `.desktop` of an
  uninstalled app is displayed grayed out.
- **Mobile**: no icons; the mobile launcher stays as is.

## 6. Generic context menu (`hackeros.context-menu`)

### 6.1 Model

An entry: `Id`, `Title`, `Icon` (vector library or asset image),
`IsDisabled`, `IsVisible`, `Children` (submenu, depth 3 at
most), action (C# callback in the app, or intent for a registered entry).
Entries are arranged in **groups**; a separator separates two groups.

### 6.2 Separators

The menu never contains two consecutive separators, nor one at the start, nor at the
end, nor around an empty or entirely hidden group. Example: "Open
with" is alone in its group; if it is hidden, its separator disappears.

### 6.3 In an app

```razor
<HackContextMenu Name="files">
    <HackMenuItem Id="preview" Title="@L["Menu.Preview"]" OnClick="item => PreviewAsync((Row)item)" />
    <HackMenuGroup>
        <HackMenuItem Id="export" Title="@L["Menu.Export"]">
            <HackMenuItem Id="csv" Title="CSV" OnClick="item => ExportAsync((Row)item, "csv")" />
        </HackMenuItem>
    </HackMenuGroup>
</HackContextMenu>

@foreach (Row row in _rows)
{
    <tr @attributes="Menus.Attach("files", row)">…</tr>
}
```

- The same menu can also be built in C# (`Menus.Define("files", builder =>
  …)`).
- `Menus.Attach(name, objet)` returns the attributes to set
  (`data-hack-menu`, token); the object stays on the C# side in a table of
  the instance, emptied when the element leaves the rendering. A small platform script
  captures `contextmenu`, the finger long press, the Menu key and
  Shift+F10, and hands control back to C# with the token.
- Callbacks only serve the menu of the instance that declares them.
- Rendering: native platform component (not `MudMenu`), OS theme,
  keyboard (arrows, Enter, Esc, right arrow to open a submenu),
  ARIA `menu`/`menuitem`, repositioned to stay on screen.

## 7. File control menus

Four scopes: `File`, `Directory`, `Background` (empty space) and `Desktop`
(= `Background` + desktop entries). Identifiers as constants
(`HackFileMenuItems.Open`…).

```text
File                                     Folder
Open                        open         Open                        open
-                                        -
Open with ▸                 open-with    Open with ▸                 open-with
   <apps for the type, default first>    -
   -                                     Cut / Copy                  cut, copy
   Choose another application…           Paste into folder           paste-into
-                                        Rename                      rename
Cut / Copy                  cut, copy    Delete                      delete
Rename                      rename       Delete permanently          delete-permanently
Delete                      delete       -
Delete permanently          delete-perm… <entries of apps without position>
-                                        -
<entries of apps without position>       Properties                  properties
-
Properties                  properties

Empty space                              Desktop (in addition to empty space)
New folder                  new-folder   View ▸                      view
Paste                       paste           Icon size, Auto-arrange,
Refresh                     refresh         Align to grid,
-                                           Show Home folder, Show Recycle Bin
<entries of apps without position>       Sort by ▸                   sort
-                                        -
Properties                  properties   Background…                 background (opens Settings)
```

- "Open with" lists the apps that declare the type (associations of
  ADR 0121, user's default first), then "Choose another
  application…", which opens the dialog with "Always use this app".
- "Delete permanently", "Cut / Copy / Paste" appear
  only if the host handles them (as today).
- The entries "UnZip Here" and "Publish as a web site…" become
  **registered entries**: the explorer registers "Extract here" (`types:
  [".zip"]`, `target: "self"`), Web Server Manager registers "Publish as a web
  site" (scope `Directory`, `target: "self"`). They therefore also
  appear on the desktop.
- A host can still adjust the menu of its instance (`ContextMenuOpening`
  event of the controller) with callbacks, as in §6.3.

## 8. Extension by apps

### 8.1 Declaration

```csharp
[AppManifest]
public sealed class SuperApp : AppDefinition
{
    // …
    [RegisterFileContextMenu]
    public static void RegisterFileContextMenu(RegisterFileContextMenuContext context)
    {
        context.Register(title: "Menu.Super", name: "super", target: "self",
            icon: HackIcons.FromImage("/assets/super-menu-icon.png"),
            intent: "com.example.super.v1", first: true);
        context.Register(title: "Menu.Archive", name: "archive", target: "self",
            icon: HackIcons.Vector(IconLibrary.Lucide, "archive"),
            scope: FileMenuScope.File | FileMenuScope.Directory);
        context.Register(title: "Menu.Compress", name: "compress", parent: "archive",
            intent: "com.example.compress.v1", selection: MenuSelection.All);
        context.Register(title: "Menu.Extract", name: "extract", parent: "archive",
            intent: "com.example.extract.v1", types: [".zip", "application/zip"],
            selection: MenuSelection.Each, afterGroup: "copy");
    }
}
```

| Parameter | Role |
|---|---|
| `title` | Resource key of the app; EN and FR texts copied at registration |
| `name` | Unique identifier within the app (referenced by `parent`) |
| `target` | `"self"` or the identifier of another app that declares the intent; checked at registration and at runtime |
| `intent` | Intent defined by the app (ADR 0096); absent on a parent entry |
| `icon` | `HackIcons.FromImage(asset)` or `HackIcons.Vector(bibliothèque, nom)` |
| `scope` | `File`, `Directory`, `Background`, `Desktop` (combinable); `File` by default |
| `types` | Extensions, media types, `image/*`, `*`; absent = all |
| `selection` | `All` (default), `Each`, `Single` |
| `first` / `after` / `afterGroup` / `parent` | Position (§8.3) |

### 8.2 Registration at installation

- The manifest generator (`HackerOs.Tools.ManifestGenerator`) calls the
  `[RegisterFileContextMenu]` method **at build time** for built-in apps and
  writes the result into `manifest.json` (`contextMenus`) and
  `BuiltInApplications.json`. For an app installed later, the OS runs
  the method **once at installation**, in a context that only
  records the calls.
- The OS writes `/etc/hackeros/context-menus.d/<appId>.json` (`root 0644`),
  with the installation rank; built-in apps are written there at first
  startup and at each OS update. Startup reads this folder:
  no app code runs to build a menu.
- Uninstalling the app removes its file; a disabled app (ADR 0048) has
  its entries hidden.
- **Capability** `shell.context-menu` (normal) required to register
  entries; without it, the generator produces a diagnostic and the OS ignores the
  entries. Settings ▸ Applications lists the entries of each app and
  lets the user hide them, in `~/.config/hackeros/context-menus.config`
  (`[hidden]` `<appId>.<name>=true`).
  *Implemented (ADR 0159): the method runs at build time for all apps
  (there is no package installation at runtime), the titles come
  from `Resources/AppManifest.resx`, and the setting is the Settings ▸
  File menus tab.*
- The generator checks: unique `name`, `parent` existing in the same app
  and without intent, depth ≤ 3, `after`/`afterGroup` targeting a known
  identifier of the OS or of the same app, valid `target`.

### 8.3 Placement

1. Default entries form groups (§7).
2. `first: true`: group at the very top of the menu, followed by a separator;
   several apps share this group there.
3. `after: "X"`: right after X, **in the group of X**, without separator.
4. `afterGroup: "X"`: **new group** after the group of X.
5. Without position: the apps' **common group**, before "Properties".
6. Several apps in the same place: **installation order**, then declaration
   order in the app.
7. An anchor absent from the current menu (for example `paste-into` on a
   file): the entry goes into the common group.
8. Filters (`scope`, `types`, `selection`) and hidden entries
   apply before placement; separators then collapse
   (§6.2).

### 8.4 Execution

- `selection: All`: one launch with all paths; `Each`: one
  launch per item; `Single`: entry hidden if there are several items.
- The target app receives the intent (`AppContext.Intent`, `IntentId` = the declared
  intent, payload `{"paths":[…],"scope":"File"}`); without a declared
  intent, the launch arguments `["{intent}", "{chemin}", …]`. An already open
  single instance receives `IntentReceived`.
- The app is launched **with its own rights**: the menu entry gives it
  no access to files that it would not otherwise have.

## 9. SDK 2.0

- `HackerOsVersion` goes to `2.0.0`. `AppCatalog` already refuses an app
  of another major version: any 1.x app must be recompiled.
- **Removed**: the `HackerOs.AppSdk.FileView` library (`FileView`,
  `FileViewTree`, `FileViewDetails`, `FileViewIcons`, menus
  `IFileViewContextMenuProvider`, `FileViewBreadcrumb`, `FileViewDropZone`,
  `ShellIcon`…); its public types go to `*REMOVED*`. Its code
  becomes the internal implementation of the explorer.
- `FileTransfer` (copy, move, Nautilus conflicts) remains useful to
  apps: it goes into the SDK as a contract (`IHackFileOperations`) implemented
  by the explorer, so that its rules live in only one place.
- **Added**: contracts `IHackFileView`, `HackFileView`, `HackContextMenu`,
  `Menus.Attach`, `[RegisterFileContextMenu]`,
  `RegisterFileContextMenuContext`, `HackFileMenuItems`, `HackIcons`.
- The additions pending for 1.3 (`PublishAsync`, ADR 0145;
  `HackCrossOriginIsolation`, ADR 0146) go into 2.0.
- The `Tests/fixtures/sdk-compat` fixture (1.0.0 app) becomes the proof of the
  **refusal** of a 1.x app; a new 2.0.0 fixture is added.
- `CodeEditor` (side explorer) moves to `HackFileView`.
- [`sdk-packages.md`](../../framework/sdk-packages.md) gains an "SDK 2.0" section with the
  list of breaking changes and the migration.

## 10. ADRs to write

Written by work item 0 (branch `desk/0-adrs`, towards `desk/base`).

| ADR | Subject |
|---|---|
| 0148 | The SDK carries only contracts; shared controls and dialogs are provided by the OS at runtime (completes ADR 0063, replaces ADR 0037 §1 and §8) |
| 0149 | SDK 2.0: removal of `HackerOs.AppSdk.FileView`, file contracts, migration (amends ADR 0114) |
| 0150 | Central OS context menu (model, groups, separators, `Menus.Attach`, keyboard and long press) |
| 0151 | Menu entries registered by apps (declaration, registration at installation, placement, execution, `shell.context-menu` capability) |
| 0152 | Desktop icons (Desktop mode, positions, system icons, `.desktop` files) |

## 11. Work items

The work items follow one another in this order; each updates the
documentation and the [`README.md`](../../README.md) index.

| # | Work item | Content | Depends on |
|---|---|---|---|
| 0 | ADR | ADR 0148 to 0152 | n/a |
| 1 | Generic contract host | Typed host component, provider registry read at startup, passing the host's rights | 0 |
| 2 | OS context menu | `hackeros.context-menu`, native multi-level rendering, `Menus.Attach`, platform script, keyboard, long press | 1 |
| 3 | File control provided by the explorer | `hackeros.file-view`, `IHackFileView`, default menus (§7) on the OS menu, "Open with" as a submenu, provided dialogs, explorer and code editor migrated | 1, 2 |
| 4 | Entries registered by apps | Generator, `context-menus.d`, placement, capability, Settings, "Extract here" and "Publish as a web site" registered | 2, 3 |
| 5 | Desktop | Desktop mode, positions, system icons, `.desktop`, `Desktop` menu, Settings ▸ Desktop | 3, 4 |
| 6 | SDK 2.0 | Removal of `FileView`, `PublicAPI`, fixtures, `sdk-packages.md`, 2.0.0 publication, final verification | 3, 4, 5 |

## 12. Tasks

- [x] Discuss the idea with Benoit (Q1 to Q22).
- [x] Validate the summary.
- [x] Write this plan.
- [x] Work item 0: ADR 0148 to 0152 ([0148](../adr/0148-sdk-contracts-and-os-provided-controls.md), [0149](../adr/0149-sdk-2-0-file-view-removal.md), [0150](../adr/0150-os-context-menu.md), [0151](../adr/0151-app-registered-file-menu-entries.md), [0152](../adr/0152-desktop-icons.md)).
- [x] Work item 1: generic contract host (`HackContractComponent<T>`, registry in `AppCatalog`, reserved contracts and `HOSAPP0025`, platform providers, error in the log; [ADR 0153](../adr/0153-contract-host-implementation-adjustments.md), [`embedded-components.md`](../../framework/embedded-components.md)).
- [x] Work item 2: OS context menu (`HackContextMenu`, `Menus.Attach`/`Define`/`OpenAsync`, platform provider, native multi-level rendering and mobile sheet, script for right click, long press, Menu key and Shift+F10, keyboard and ARIA; [ADR 0154](../adr/0154-context-menu-implementation-adjustments.md), [`context-menu.md`](../../framework/context-menu.md)).
- [x] Work item 3: file control provided by the explorer (`HackFileView`, `IHackFileView`, `ExplorerFileView` bound to the OS menu, menus of §7 with "Open with" as a submenu, dialogs provided by `IDialogService`, explorer and code editor migrated; [ADR 0155](../adr/0155-file-control-implementation-adjustments.md), [`file-explorer.md`](../file-explorer.md)). The view of the Open / Save dialogs is the separate work item `desk/3b` (Benoit's choice, ADR 0155 §6).
- [x] Work item 3b: the Open / Save / Choose a folder dialogs show the `hackeros.file-view` control with the rights of the requesting app (`FileDialogBrowser`, fallback to `VirtualFolderBrowser` without a provider; [ADR 0157](../adr/0157-file-dialogs-show-the-file-control.md)).
- [x] Work item 3c: replaceable file control provider (contracts open to any windowed app, `HOSAPP0025` removed, `/etc/hackeros/components.config`, administrator's choice after approval, `component.fallback` fallback, "OS components" tab; [ADR 0156](../adr/0156-replaceable-file-control-provider.md), [ADR 0158](../adr/0158-replaceable-file-provider-implementation-adjustments.md)).
- [x] Work item 4: entries registered by apps (`[RegisterFileContextMenu]` run at build time for all apps, manifest `contextMenus`, `/etc/hackeros/context-menus.d` aligned at startup, filters, placement, launch by intent or arguments, `shell.context-menu` capability, `HOSAPP0026` to `HOSAPP0028`, Settings ▸ File menus and `HackFileMenuEntries`, "Extract here" and "Publish as a web site…" registered; [ADR 0159](../adr/0159-app-menu-entries-implementation-adjustments.md), [`context-menu.md`](../../framework/context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4)).
- [x] Work item 5: desktop (system app `org.hackeros.desktop` rendered by the shell under the windows, Desktop mode of the file control, `HackDesktopLayout` grid, `~/.config/hackeros/desktop.config` through `HackDesktopSettings`, Home folder and Recycle Bin, `.desktop` shortcuts by dragging from the Start menu and "Send to ▸ Desktop", View and Sort by menus, Settings ▸ Desktop; [ADR 0160](../adr/0160-desktop-implementation-adjustments.md), [`desktop.md`](../desktop.md)).
- [x] Work item 6: SDK 2.0 (`HackerOs.AppSdk.FileView` removed, its code in the explorer under `FileControl/`, `HackFileOperations` copy rules in the SDK, `PublicAPI` published, version `2.0.0`, 1.x apps refused at the catalog and at the host build, fixture `Contoso.Sdk2Compat.2.0.0`, migration in [`sdk-packages.md`](../../framework/sdk-packages.md#sdk-20); [ADR 0161](../adr/0161-sdk-2-0-implementation-adjustments.md)) and final verification (§13).

## 13. Final verification

Done on 2026-10-20 on the `desk/6-sdk-2` branch (complete series: `desk/base`
with work items 0 to 5 and 6), in a Linux container, .NET 10.

| Verification | Result |
|---|---|
| `dotnet build HackerOs.sln` (Debug) | succeeded, 0 warnings, 0 errors |
| Unit and integration tests (41 `Tests/*.Tests` projects excluding E2E) | 3,968 passed, 0 failed |
| `build/publish-trimmed.sh` and `HackerOs.TrimmedPublish.E2E.Tests` | `RESULT: PASS`; 136 `.wasm` files, 27.7 MiB, 8.3 MiB in Brotli |
| `build/verify-hosts.sh` (Ecosystem and its variants, test host, server) | `RESULT: PASS`, all 13 lines |
| `HackerOs.E2E.Tests` | 18 passed |
| `HackerOs.UI.E2E.Tests` | 28 passed |
| `HackerOs.Templates.E2E.Tests` (including a host that refuses 1.x apps with `HOSAPP0021` and launches the 2.0 app) | 6 passed |
| `HackerOs.Pwa.E2E.Tests` | 6 passed |

No test was disabled or ignored. The failures known from before the series
(PWA without IPv6, unstable tests listed in
[`building-and-testing.md`](../building-and-testing.md)) did not show up
during this pass.

What the series delivers:

- an SDK made of contracts: `HackFileView`, the file dialogs and the context
  menu are rendered by the OS at runtime (ADR 0148, 0153);
- the OS context menu (`Menus.Attach`, multi-level, keyboard, long
  press; ADR 0150, 0154);
- the file control provided by the explorer, also shown in the
  Open / Save dialogs, and replaceable by the administrator
  (ADR 0155, 0156, 0157, 0158);
- the menu entries registered by apps (`[RegisterFileContextMenu]`,
  ADR 0151, 0159);
- the desktop on `~/Desktop` (ADR 0152, 0160);
- SDK 2.0.0, which removes the public `FileView` and refuses 1.x apps
  (ADR 0149, 0161).
