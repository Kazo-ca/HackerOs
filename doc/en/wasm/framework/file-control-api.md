> [Version française](../../../fr/wasm/framework/file-control-api.md)

# OS file control: usage from an app

This document describes what an app gets when it displays the file control (`HackFileView`) or opens an OS file dialog, and what changes for it depending on the host. The explorer provider (`ExplorerFileView`, contracts and composition) is in [`dev/file-explorer.md`](../dev/file-explorer.md).

## Display through `HackFileView`

Every app displays it through the SDK's `HackFileView` (`HackerOs.AppSdk.Blazor.Files`)
and so does the explorer: its window renders `<HackFileView>` and keeps the chrome
(bars, Locations, Trash, status bar). The Rename,
Delete, Cut, Copy, Paste, Properties and Open with buttons call
`IHackFileView.ExecuteAsync` with the identifier of the corresponding menu
entry, so they do the same thing as the menu. New folder/file
by name, upload, download, compression, extraction and dropping onto the
Trash stay in the window; they also go through `IHackFileView`
(current folder, selection, `RefreshAsync`). Only the breadcrumb and
drops on Home and Trash use the internal implementation (`FileView`): if
an administrator chooses another provider (ADR 0156, code in a later work
item), the window displays that provider and only these two extras
disappear.

Everything runs **with the host's rights** (`HostContext`): the code
editor that displays the control only sees, copies and deletes what it can.

## Menus

Menus (plan §7): `ExplorerFileMenu` fills the menu on each opening; an
item outside the selection becomes the selection; "Open with ▸" lists the
apps for the type (default first, `open-with:<appId>`) then "Choose another
application..."; several items: neither Rename nor Open with, Properties
disabled. A host removes functions through `Features` and adjusts the menu
through `ContextMenuOpening`. Details: [ADR 0155](../dev/adr/0155-file-control-implementation-adjustments.md).

## File dialogs

Since `desk/3b` ([ADR 0157](../dev/adr/0157-file-dialogs-show-the-file-control.md)),
the OS Open, Save and Choose a folder dialogs also show
this control (the platform's `FileDialogBrowser`, with the rights of the app
that makes the request): Open without `Features`, Save and Choose a folder with
New folder and Rename. A file activated there is taken by the dialog
(`Opening` set to `Handled`), not opened in its app. Without a provider, the
platform falls back to its `VirtualFolderBrowser` list.

On the mobile platform ([ADR 0171](../dev/adr/0171-mobile-file-explorer-and-full-screen-dialogs.md)),
the dialogs open full screen and pass `OpenOnTap` to the control: one tap
enters a folder or takes a file, with no double click. An app can ask for the
same behavior with `<HackFileView OpenOnTap="true">`.

## Language

- The menus and dialogs of the provided control (`ExplorerFileView`,
  `ExplorerFileDialogs`) speak the language of the **host app**
  (`HostContext.GetLocalizer<Strings>()`): in the code editor in French,
  the tree menu is in French even if the explorer runs in English.
  Keys `Menu.*`, `Dialog.Loading`, `Error.DialogsUnavailable` (desk/3).
