> [Version française](../../../fr/wasm/framework/trash-api.md)

# Trash: `.trashinfo` format and `KazoTrash` API

This document describes what an app sees of the trash: the SDK's `KazoTrash` API, the rules that apply to its operations, and the `.trashinfo` format that apps and other implementations read or write. The place of the trash in the OS, the `rm` and `trash` commands, the explorer and synchronization are in [`dev/trash.md`](../dev/trash.md).

## On-disk format

```
~/.local/share/Trash/          (0700, created on demand)
├── files/                     (0700) items moved as they are
│   ├── rapport.txt
│   └── rapport.2.txt          second item with the same name
└── info/                      (0700)
    ├── rapport.txt.trashinfo  (0600)
    └── rapport.2.txt.trashinfo
```

```ini
[Trash Info]
Path=/home/benoit/Documents/rapport%20final.txt
DeletionDate=2026-10-10T14:03:27
```

- `Path`: original absolute path, encoded like a URL segment by segment
  (`/` not encoded).
- `DeletionDate`: local time of the simulated clock (`IAppClockGateway.UtcNow`
  converted to the local time zone), to the second, with no offset.
- Colliding names: `name.2.ext`, `name.3.ext`...; a hidden file without an
  extension (`.bashrc`) becomes `.bashrc.2`. A name is free if it exists neither
  in `info/` nor in `files/`.

- When listing, a `.trashinfo` without an item is deleted; an item without a
  `.trashinfo` is listed with an unknown origin.
- An item deleted on another device carries, in its `.trashinfo`, the extension keys `X-KazoOS-Origin=deleted-on-another-device` and `X-KazoOS-Device=<device>`, which other implementations ignore. `KazoTrashItem.DeletedOnAnotherDevice` reports it to apps.

## Trash of the effective user

The trash is that of the **effective** user: under `sudo`, it is
`/root/.local/share/Trash/`. To find out, `IAppExecutionContext` exposes
`IsRootElevated` (default member `false`, filled in by
`AppExecutionContextFactory` from the elevated launch of ADR 0042).
`HomeDirectory` does not change under `sudo` (the shell's `~` remains that of
the user).

## Rules

| Case | Behavior |
| --- | --- |
| Item on the main file system | Moved into `files/` (content, permissions, owner and dates kept), if the caller has the right to delete it (write on the parent folder, capabilities). |
| `/proc`, `/dev` (virtual mounts) | Refused: `KazoIOException` "Cannot move to trash: virtual file system". Detected by the move's `CrossMountOperation`; nothing is left in `info/`. |
| Item already in the trash | Deleted permanently. |
| `/`, the home folder, a folder that contains the trash (`~/.local`, the trash itself) | Refused: `KazoIOException`, code `ProtectedEntry`. |
| Symbolic link | The link itself goes to the trash, not its target. |

## `KazoTrash` API

`Shared/Kazo.Os.AppSdk/IO/KazoTrash.cs`. Static class, ambient context of
ADR 0043 (`KazoExecutionContext.Current`), `Kazo*` exceptions. No
new capability: the `filesystem.*` capabilities and the permissions decide, as
for `KazoFile`.

```csharp
KazoTrashItem item = await KazoTrash.MoveToTrashAsync("Documents/rapport.txt");
IReadOnlyList<KazoTrashItem> items = await KazoTrash.GetItemsAsync();   // plus ancien d'abord
string restoredTo = await KazoTrash.RestoreAsync(item);                 // ou RestoreAsync(item, "~/ailleurs.txt")
await KazoTrash.DeleteAsync(item);                                      // suppression définitive d'un élément
await KazoTrash.EmptyAsync();                                           // vide tout
string root = KazoTrash.TrashDirectory;                                 // ~/.local/share/Trash ou /root/...
```

| Member | Detail |
| --- | --- |
| `MoveToTrashAsync(path)` | Missing path → `KazoFileNotFoundException`. Returns the item created (`Name` = name in `files/`). |
| `GetItemsAsync()` | Sorted by deletion date then by name; empty if the trash does not exist. |
| `RestoreAsync(item, destination?)` | Destination, otherwise the `Path` read from the `.trashinfo` (then `item.OriginalPath`). Recreates missing parent folders. Destination occupied → `KazoIOException` "already exists", nothing is overwritten. Unknown origin without a destination → `KazoIOException`. Deletes the `.trashinfo`. |
| `DeleteAsync(item)` | Deletes the item and its `.trashinfo`; neither of them → `KazoFileNotFoundException`. |
| `EmptyAsync()` | Deletes all the content of `files/` and `info/` (the folders remain). |
| `TrashDirectory` | Canonical path of the effective user's trash. |

`KazoTrashItem(Name, OriginalPath, DeletedAt, IsDirectory, Length)`: `Name` is
the stable identifier; `Length` is the size of the file or the sum of the
files in the folder. Only `Name` is used to find the item.

Each move to the trash, restoration and permanent deletion is written at
`Information` level in the app's log (`/var/log/apps/<appId>.log`,
ADR 0045).

`KazoFile.DeleteAsync` and `KazoDirectory.DeleteAsync` remain permanent
deletions, like `System.IO`.
