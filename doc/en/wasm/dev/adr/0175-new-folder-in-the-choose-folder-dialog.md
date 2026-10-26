> [Version française](../../../../fr/wasm/dev/adr/0175-new-folder-in-the-choose-folder-dialog.md)

# ADR 0175: "New folder" in the Choose folder dialog

## Status

Accepted on 2026-10-26 at Benoit's request ("in the `Open Folder` dialog we must
add the option to add a new folder"). Complements
[ADR 0157](0157-file-dialogs-show-the-file-control.md) (the dialogs show the file
control) and [ADR 0171](0171-mobile-file-explorer-and-full-screen-dialogs.md)
(full-screen dialogs on mobile).

## Context

- Code Editor's "Open Folder" dialog is the platform's Choose folder dialog
  (`FolderSelectDialog`). It can create a folder since ADR 0157, but only when the
  app asks for it (`SelectFolderDialogRequest.AllowCreateFolder`). Code Editor did
  not: there was no way to create a folder.
- When the app asked for it, creation used a name box and a button below the list.
  The OS file control already has its own "New folder" command
  (`HackFileMenuItems.NewFolder`): it creates "New folder" (or "New folder (2)"),
  selects it and puts it in rename, like the explorer's context menu. It was only
  reachable through that menu (right click, long press on mobile), which the user
  does not see.

## Decision

1. **Button in the dialog's toolbar.** `FileDialogBrowser` shows a "New folder"
   button (icon and label; icon only on a narrow dialog) in its toolbar, between
   the breadcrumb and the view modes, when the dialog allows
   `HackFileViewFeatures.CreateFolder` and the file control is shown. It runs the
   control's command (`IHackFileView.ExecuteAsync(HackFileMenuItems.NewFolder)`),
   so the folder is created with the app's rights, then put in rename. The button
   is disabled when `CanExecute` refuses it. It works the same on the desktop and
   on mobile (ADR 0171's full-screen dialog keeps the toolbar).
2. **One way to create.** `FolderSelectDialog` shows its name box only when the
   platform's list replaces the control (no provider of `hackeros.file-view`):
   that list only browses. `FileDialogBrowser` reports it with a new
   `FileControlChanged` parameter (`EventCallback<bool>`, public API of
   `HackerOs.Platform.Blazor`, in `PublicAPI.Unshipped.txt`).
3. **The Save dialog** already allowed `CreateFolder`: it gets the same button.
4. **Code Editor** passes `AllowCreateFolder = true` for "Open Folder": the folder
   of a new project can be created there.
5. The SDK does not change: `AllowCreateFolder` keeps its `false` default. Each
   app chooses; WebServer and Code Editor turn it on.

## Consequences

- The dialog does not open a second dialog (a text input would wait behind the
  active dialog): the name is typed in the list, as in the explorer.
- Tests: `FileDialogBrowserTests` (the button is there when the dialog allows it,
  absent from an Open dialog, `FileControlChanged`) and UI.E2E
  `CodeEditorOpenFolderTests` (desktop and mobile: New folder, rename, Select).
