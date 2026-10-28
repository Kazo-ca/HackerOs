> [Version française](../../../../fr/wasm/dev/adr/0181-host-folder-mounted-as-a-network-drive.md)

# ADR 0181: a folder of the computer mounted as a network drive

## Status

Accepted on 2026-10-28. Proposed the same day at Benoit's request ("add the ability to
mount a local folder of the computer […] I would like it to be mounted as a network drive
in HackOs"). The choices marked **(decided by Benoit)** were asked to him in the thread;
he chose the recommendation for all six questions (2026-10-28).

Complements [ADR 0008](0008-virtual-filesystem-model.md) (file system model),
[ADR 0046](0046-virtual-proc-and-dev-filesystems.md) (`/proc` and `/dev` mounts) and
[ADR 0057](0057-proc-metrics-for-task-manager.md), section 7 (`HackDriveInfo`).

## Context

- Chrome and Edge on a computer have the *File System Access API*:
  `window.showDirectoryPicker({ mode: 'readwrite' })` opens the computer's folder dialog
  and returns a `FileSystemDirectoryHandle`. Once the user agrees (a browser prompt),
  the page reads, creates, renames and deletes files in that folder. The handle can be
  stored in IndexedDB (it is cloneable); after a reload, `queryPermission` answers
  `prompt` and `requestPermission` needs a new user gesture, unless the user chose
  "Allow on every visit" (Chrome 122 and later).
- Firefox, Safari and Chrome on Android have no `showDirectoryPicker`.
- The HackerOS file system routes each path to a provider (`IFileSystemProvider`)
  through `FileSystemMountRouter`: `/` (IndexedDB, `hda`), `/proc` and `/dev`. The table
  was fixed at boot; there was no mounting or unmounting at run time, and no `mount`
  command.
- `FileSystemService` already refuses a move between two mounts (`CrossMountOperation`)
  and the SDK (`HackFile.Move`) then falls back to copy and delete. `HackDriveInfo` lists
  the mounts that have a size; `HackDriveType` only knew `Fixed`.
- Decisions already made: file permissions are real; advanced system rights go through
  a special approval dialog; base apps only use the public SDK
  ([ADR 0055](0055-base-apps-on-public-sdk-only.md)).

## Decision

1. **Dynamic mounts.** `IDynamicFileSystemMountRouter` (new, extends
   `IFileSystemMountRouter`) adds `Mount(FileSystemMount)`, `Unmount(VirtualPath)` and
   the `MountsChanged` event; `FileSystemMountRouter` implements it by replacing its
   immutable table under a lock. Nothing can be mounted on an existing mount, inside a
   mount other than `/`, or around a mount. The fixed mounts (`/`, `/proc`, `/dev`)
   cannot be unmounted.
2. **`hostfs` provider.** `HostFolderFileSystemProvider` (Platform.Core) implements
   `IFileSystemProvider` over an `IHostFolder` (Simulation.Abstractions). In the browser,
   `BrowserHostFolderAccess` (Infrastructure.Browser) gives one per
   `FileSystemDirectoryHandle`, with `hostFolder.js`. Mapping:
   - files and folders only; names HackerOS refuses are skipped;
   - read with `getFile()`, write with `createWritable()` (the browser writes a temporary
     file and swaps it in on close: writing a file is atomic);
   - rename and move with `FileSystemHandle.move()` when the browser has it, otherwise
     copy then delete; copying a folder goes file by file;
   - a file's revision comes from its modification time and size (the folder can change
     outside HackerOS); a folder's revision is an in-memory counter; ids are stable (a
     SHA-256 hash of the path); revisions are checked as on the `/` disk;
   - owner = the user who mounted, group = their primary group, fixed modes `0700`
     (folders) and `0600` (files): the browser does not show the computer's real
     permissions. `chmod` answers `UnsupportedOperation`; the mount root cannot be
     deleted (`ProtectedEntry`);
   - changes made outside HackerOS are not watched: the file explorer reads the folder
     again when it is opened.
3. **Network drive.** The mount shows as a network drive: `HackDriveType.Network` (new,
   like `System.IO.DriveType.Network`), `DriveFormat` = `hostfs`, `VolumeLabel` = name of
   the computer's folder; `/proc/mounts` shows `host:<name> <point> hostfs
   rw,nosuid,nodev 0 0` (a space is written `\040`, as on Linux). The browser gives
   neither the size nor the free space: `TotalSize` and `AvailableFreeSpace` are 0 and
   `df` prints `-`. `HackDriveInfo.IsReady` is false until the browser's permission is
   granted.
4. **Where (decided by Benoit).** A drive is mounted on a direct child of `/mnt`. With no chosen
   point, it is `/mnt/<folder name>` (then `-2`, `-3`… when the name is taken), created
   at mount (`0700`, owned by the user) and removed at unmount. A chosen point must be
   missing or an empty folder of the user. `/mnt` is created at boot (`0755`, root).
   Outside the home folder, the drive is part of neither the sync nor the backup
   (section 9).
5. **Who (decided by Benoit).** Any user mounts a folder for themselves, like a Windows network
   drive: their choice in the browser's dialog is already an explicit approval. The
   mount point belongs to them; other accounts cannot enter it (mode `0700`). Only the
   owner and an administrator unmount or reconnect a drive.
6. **App access (decided by Benoit).** The ordinary file rules apply, as for `/tmp`: outside the
   home folder, an app needs `filesystem.system.read` or `filesystem.system.write` (the
   file explorer and the commands have them), or goes through the open and save
   dialogs. No new capability.
7. **Mounting needs a gesture.** The SDK gets `HackDriveInfo.IsHostFolderSupportedAsync`,
   `MountHostFolderAsync(string? mountPoint = null)`, `ReconnectAsync(string)` and
   `UnmountAsync(string)`, through `IAppDriveGateway` (default members, so existing
   implementations do not break), down to the OS's `NetworkDriveService` and, for an app
   in a worker, through the worker protocol (`drv.hostsup`, `drv.mount`,
   `drv.reconnect`, `drv.umount`). The dialog opens on the main page; the call must
   follow a click or a key press (user activation), otherwise it fails with "user
   activation required". Errors: `NotSupportedException` (browser),
   `UnauthorizedAccessException` (not the owner, permission refused), `IOException`
   (invalid mount point, nothing mounted), `InvalidOperationException` (no gesture,
   nobody signed in).
8. **Persistence.** The user's drive list is a real file,
   `~/.local/state/hackeros/network-drives.json` (`0600`; mount point, name, handle key):
   device-local state, never synced, because the handles only work in this browser. The
   handles are in the IndexedDB database `hackeros-host-folders` (a handle is not JSON).
   When the user signs in, each drive is mounted again; when the browser answers
   `prompt`, it is mounted **disconnected** (decided by Benoit): it shows in the file explorer
   with "Reconnect", and every operation answers `ProviderUnavailable` until the click.
   A drive whose handle is gone is removed from the list. When the user signs out, their
   drives are unmounted (the list and the handles stay).
9. **Out of sync and backup.** A drive's content is never synced with the server nor
   included in the OS backup ([ADR 0099](0099-os-backup-and-low-storage.md)): it is
   already on the computer, the sync only walks the home folder, and the backup only the
   IndexedDB database.
10. **Delete (decided by Benoit).** Deleting on a drive is permanent, after confirmation, with no
    trash, like on a Windows network drive: the file explorer switches to a permanent
    delete as soon as a chosen item is on a drive.
11. **File explorer.** The (desktop) sidebar gets a "Network drives" section: one item
    per drive (greyed when disconnected; a click reconnects), an eject button to
    "Disconnect", and "Connect a network drive…". The mobile file explorer is not
    changed.
12. **Terminal.** `mount` (or `mount -l`) lists `/proc/mounts` as
    `source on point type type (options)`; `mount -t hostfs [point]` opens the dialog
    (Enter counts as the gesture); `mount -o remount <point>` reconnects;
    `umount <point>` unmounts. Exit codes follow util-linux: 1 for usage, 32 for a
    failure. `df` shows a network drive with `host:<name>` and `-` sizes.
13. **Browsers without the API (decided by Benoit).** On Firefox, Safari and mobile, "Connect a
    network drive" is hidden, and `mount -t hostfs` answers "hostfs: not supported by
    this browser".

## Questions asked to Benoit

Benoit's answer (2026-10-28): the recommendation for all six.

1. Where to mount: `/mnt/<name>` (recommended), `/media/<user>/<name>` or
   `~/Réseau/<name>`.
2. Who can mount: any user for themselves (recommended) or administrators only.
3. App access: ordinary file rules (recommended) or a dedicated capability granted by
   the special approval dialog.
4. Delete: permanent with confirmation (recommended) or a copy in the trash.
5. Browsers without the API: feature hidden (recommended) or read-only import (a copy,
   not a mount).
6. After a reload: drive disconnected with "Reconnect" (recommended) or a request at
   sign-in.

## Consequences

- Only Chrome and Edge on a computer mount a folder.
- The handles only live on the page's main thread; apps in a worker go through the file
  service, as for the `/` disk.
- Operations on a large folder are slower than on IndexedDB (file-by-file copy, no
  transaction over several files: an interrupted folder copy leaves a partial copy).
- Public API: `IDynamicFileSystemMountRouter`, `IHostFolder`, `IHostFolderAccess` and
  their types, `HostFolderFileSystemProvider`, `NetworkDriveService`,
  `HackDriveType.Network`, `HackDriveInfo.IsHostFolderSupportedAsync`,
  `MountHostFolderAsync`, `ReconnectAsync`, `UnmountAsync`, and the matching default
  members of `IAppDriveGateway`, in the `PublicAPI.Unshipped.txt` files (for SDK 2.1).
- Tests: router (mount, unmount, refusals), provider over a fake in-memory folder (move
  with and without `move()`, revisions, protected root, revoked permission), drive
  service (mount, list, restore connected or not, unmount, owner, refused mount points,
  `/proc/mounts`), SDK, worker protocol, `mount`, `umount` and `df` commands. A
  Playwright browser test that mounts an origin private folder (OPFS,
  `navigator.storage.getDirectory()`, which has the same interface with no dialog,
  through `globalThis.__hackerOsPickHostFolder`) is still to be written.
