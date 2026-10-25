# Trash

The `HackTrash` API and the `.trashinfo` format intended for apps are in [`trash-api.md`](../framework/trash-api.md).

Phase 3 of the rebuild plan ([`ecosystem-rebuild-plan.md`](plans/ecosystem-rebuild-plan.md),
section 7.4), decided by [ADR 0052](adr/0052-trash.md). Work item
`rebuild/3-trash`.

## Goal

A deletion through `rm` or the file explorer becomes reversible. The trash
follows the freedesktop.org "Trash" 1.0 specification, like Linux desktops: it
consists of plain files in the home folder, so `ls ~/.local/share/Trash/files`
works and the permissions are those of the files.

## On-disk format

- The `.trashinfo` is created **first**, with exclusive creation, to reserve the
  name; then the item is moved. If the move fails, the `.trashinfo` is deleted
  and the error bubbles up.
- An item deleted on another device arrives here through synchronization
  (ADR 0101 §1, [`synchronization.md`](synchronization.md)): its `.trashinfo`
  carries the extension keys `X-HackerOS-Origin=deleted-on-another-device` and
  `X-HackerOS-Device=<appareil>`, which other implementations ignore.
  `HackTrashItem.DeletedOnAnotherDevice` tells apps. Moving to the trash is a
  synchronized rename; emptying the trash sends the deletions to the other
  devices. Items moved to the trash by the sync stay on this device.

## Rules

Virtual mounts (`/proc`, `/dev`) are detected by the move's `CrossMountOperation`; nothing is left in `info/`.

No size limit or automatic purge: the trash counts in disk space like any other
file. No `directorysizes` cache.

## `.trashinfo` format code

The `.trashinfo` format (tolerant reading, writing, `nom.N.ext` names) is in
`HackTrashInfo.cs` (internal to the SDK).

## Commands

In `Apps/Commands/HackerOs.Commands` (ADR 0051).

### `rm`

`rm [-r|-R] [-f] [--permanent] <fichier…>` (`Rm/RmCommand.cs`):

- By default, moves to the trash. Silent output.
- A folder requires `-r` (`rm: cannot remove 'd': Is a directory`), then goes
  to the trash whole.
- `-f` ignores missing items (and the absence of an operand); other errors are
  still displayed.
- `--permanent`: permanent deletion, the previous behavior.
- Short options can be grouped (`-rf`), `--` ends the options, unknown option →
  `rm: invalid option -- 'x'`.
- `/proc` or `/dev`: `rm: cannot remove '/proc/x': Cannot move to trash: virtual file system`.
- `sudo rm /etc/x` sends to `/root/.local/share/Trash/`.

### `trash`

New system command (`Trash/TrashCommand.cs`, id
`org.hackeros.commands.trash`), inspired by `trash-cli`:

| Subcommand | Effect |
| --- | --- |
| `trash put [-f] <fichier…>` | Like `rm`, but a folder does not need `-r` (like `trash-put`). `--permanent` is rejected. |
| `trash list` | One line per item: `AAAA-MM-JJ hh:mm:ss  /chemin/d/origine`, oldest first. When the name in the trash differs from the original name, it is appended in brackets (`[rapport.2.txt]`) to be used by `trash rm`. An item deleted on another device ends with `(deleted on another device)`. |
| `trash restore <nom\|chemin d'origine> [destination]` | Looks first by name, then by original path; if several items come from the same path, the most recent. |
| `trash rm <nom…>` | Permanent deletion of items, by name. |
| `trash empty` | Empties the trash. |

With no argument, `trash` prints help on the error output and returns 1.

## File explorer

- **FileView** (`Shared/HackerOs.AppSdk.FileView` at the time, in the explorer since SDK 2.0, `FileControl/`) receives a
  `DeleteRequested` parameter (`EventCallback<FileViewDeleteRequestedEventArgs>`).
  When the host supplies it, the Delete key, Shift+Delete and the context menu
  hand the deletion over to it (`Items`, `Permanent`) instead of deleting
  themselves; the context menu then adds "Delete permanently". The `Deleting`
  event (cancellable) is always raised before. Without `DeleteRequested`,
  FileView keeps the old behavior (permanent deletion), for example in the code
  editor.
- **FileExplorerWindow** wires `DeleteRequested`:
  - "Delete" (toolbar, menu, Delete key) moves the selection to the trash,
    without confirmation;
  - Shift+Delete and "Delete permanently" ask for confirmation (Yes/No), then
    delete with `HackFile`/`HackDirectory.DeleteAsync`;
  - errors (permissions, `/proc`) are displayed in the error strip.
- A "Places" side panel offers **Home** and **Trash**. "Trash" displays
  `TrashView` (`Apps/System/HackerOs.Apps.FileExplorer/TrashView.razor`) in
  place of the list; FileView stays mounted (hidden) and finds its folder again
  on return.
- **TrashView** (derives from `HackComponentBase`, hence in the explorer's
  ambient context) reads `HackTrash.GetItemsAsync`, never the raw folder, and
  displays name, original location, deletion date and size. Actions:
  "Restore" (or double click, Enter), "Delete permanently" (or Delete key) and
  "Empty trash", the last two after confirmation. Ctrl+click for multiple
  selection. After a restore, the explorer refreshes FileView.
- **Drop on the trash** (phase 4, `rebuild/4-file-explorer`): dragging items
  onto the "Trash" entry of the Places panel or onto the Trash view sends them
  to the trash (`FileTransfer.MoveToTrashAsync`, today `HackFileOperations.MoveToTrashAsync`). See
  [`file-explorer.md`](file-explorer.md).

Fix along the way: `Shared/HackerOs.AppSdk.FileView/_Imports.razor` now imports
`Microsoft.AspNetCore.Components.Web`. Without it, `FileView.razor` emitted
`@oncontextmenu` as a literal HTML attribute and opening the explorer displayed
"An unhandled error has occurred" (seen when testing in the browser).

Cut, copy, paste and drag-and-drop to the trash remain in phase 4 (section 8.2
of the plan).

## Tests

- `Tests/HackerOs.AppSdk.Tests/IO/HackTrashTests.cs`: `.trashinfo` format,
  `0700`/`0600` permissions, colliding names, whole folders and size, refusals
  (root, home folder, trash), item already in the trash, virtual mount without
  residual `.trashinfo`, `sudo` to `/root`, listing (orphans, unknown origin,
  sort), restore (parents recreated, destination, never overwriting), deletion,
  emptying and log.
- `Tests/HackerOs.Commands.Wave5.Tests/RmCommandTests.cs`: `rm` (trash,
  `--permanent`, `-r`, `-f`, unknown option, `/proc`, revision conflict) and
  `trash` (put, list, restore by path and by name, rm, empty, help).
- `Tests/HackerOs.Apps.FileExplorer.FileControl.Tests/FileViewTests.cs`: `DeleteRequested`
  receives the items and the `Permanent` flag.
- `Tests/HackerOs.Ecosystem.Tests`: 41 built-in apps, 29 commands (45 and 33 since `3-shell-commands`).
- Test doubles: `FakeAppLoggingGateway` (new) in
  `HackerOs.Tests.Support`; `MinimalAppExecutionContext` accepts
  `isRootElevated`.

## Task list

- [x] `HackTrash`, `HackTrashItem` and the `.trashinfo` format (`HackTrashInfo`).
- [x] `IAppExecutionContext.IsRootElevated` for the effective user's trash.
- [x] `rm` to the trash, `rm --permanent`, `-r` required for a folder, grouped options.
- [x] `trash` command (put, list, restore, rm, empty).
- [x] `FileView.DeleteRequested`, Shift+Delete, "Delete permanently" entry.
- [x] Explorer: trash by default, confirmation for permanent deletion, "Places" panel, `TrashView`.
- [x] SDK, command, FileView tests and ecosystem counters.
- [x] Documentation (this file, index, plan, `hack-file-api.md`).
