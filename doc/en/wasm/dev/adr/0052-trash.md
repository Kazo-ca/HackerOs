> [Version française](../../../../fr/wasm/dev/adr/0052-trash.md)

# ADR 0052: Trash

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 3, Shell, commands and trash
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 7.4,
  decision D15, detail point of section 11 on `rm`).
- **Complements:** ADR 0043: `HackFile.DeleteAsync` and
  `HackDirectory.DeleteAsync` remain permanent deletions, like
  `System.IO`; the trash is a separate API, `HackTrash`.
- **Builds on:** ADR 0051 (`rm` and the new `trash` command live in
  `HackerOs.Commands`).

## Context

No trash exists. `rm` deletes permanently. The explorer
(`FileExplorerWindow.DeleteSelectedAsync`) calls a recursive
`IAppFileSystemGateway.DeleteAsync` on the selection, without
confirmation and with no way back. The `FileView` menu offers "Delete" with
a trash icon.

## Decision

### 1. Format: freedesktop.org specification

A user's trash is `$HOME/.local/share/Trash/`, with the
structure of the freedesktop.org "Trash" specification (version 1.0):

- `files/<name>`: the deleted item, a file or an entire folder, moved as
  is (content, permissions, owner and dates preserved).
- `info/<name>.trashinfo`: one text file per item:

  ```ini
  [Trash Info]
  Path=/home/benoit/Documents/rapport%20final.txt
  DeletionDate=2026-10-10T14:03:27
  ```

  `Path` is the original absolute path, encoded like a URL (RFC 2396,
  `/` not encoded). `DeletionDate` is the local time of the simulated clock, in
  the format `YYYY-MM-DDThh:mm:ss`.
- Folders are created on demand with permissions `0700`.
- No `directorysizes` cache in this phase.

**Names.** `<name>` is the original name. If it is already taken in `info/`,
`name.2.ext`, `name.3.ext`... are tried (for `rapport.txt`: `rapport.2.txt`). As
the specification requires, the `.trashinfo` file is created **first**, with
exclusive creation, to reserve the name; then the item is moved into
`files/`. If the move fails, the `.trashinfo` is deleted and the error
propagates. When listing, a `.trashinfo` with no item in `files/` is ignored and
cleaned up; an item in `files/` with no `.trashinfo` is listed with an unknown
origin.

### 2. What can go to the trash

- Items of the main file system (IndexedDB), whatever
  their folder, as long as the caller has the right to delete them (write on
  the parent folder, file permissions and the app's capabilities). They go into
  the trash of the **effective user**: `sudo rm /etc/x` sends to
  `/root/.local/share/Trash/`.
- Virtual mounts (`/proc`, `/dev`) refuse: `HackIOException`
  "Cannot move to trash: virtual file system".
- An item already in the trash (`~/.local/share/Trash/...`) is deleted
  permanently instead of being moved.
- The home folder itself, `/` and the trash itself are refused.
- No size limit or automatic purge in this phase. The
  trash counts toward disk space like any other file.

### 3. `HackTrash` API in the SDK

`Shared/HackerOs.AppSdk/IO/HackTrash.cs`, static, asynchronous, ambient
context of ADR 0043, same `Hack*` exceptions:

```csharp
public static class HackTrash
{
    public static Task<HackTrashItem> MoveToTrashAsync(string path, CancellationToken cancellationToken = default);
    public static Task<IReadOnlyList<HackTrashItem>> GetItemsAsync(CancellationToken cancellationToken = default);
    public static Task<string> RestoreAsync(HackTrashItem item, string? destinationPath = null, CancellationToken cancellationToken = default);
    public static Task DeleteAsync(HackTrashItem item, CancellationToken cancellationToken = default);
    public static Task EmptyAsync(CancellationToken cancellationToken = default);
}

public sealed record HackTrashItem(
    string Name,                 // name in files/ (stable identifier)
    string? OriginalPath,        // null if the .trashinfo is missing
    DateTimeOffset? DeletedAt,
    bool IsDirectory,
    long Length);                // file size, or sum for a folder
```

- `MoveToTrashAsync`: missing path → `HackFileNotFoundException`.
- `RestoreAsync`: puts the item back at `destinationPath`, otherwise at its origin;
  recreates missing parent folders; an item already present at the
  destination → `HackIOException` ("already exists"), overwriting nothing.
  Returns the restored path and deletes the `.trashinfo`.
- `DeleteAsync` permanently deletes an item; `EmptyAsync` empties everything.
- **No new capability:** the trash is a set of files in the
  home folder. The existing `filesystem.*` capabilities and file permissions
  decide, as for `HackFile`.
- Every move to the trash, restore and permanent deletion is
  written at `Info` level in the app's log (ADR 0045).

### 4. `rm` and the `trash` command

- `rm file`: moves to the trash. A folder requires `-r` (`rm: cannot
  remove 'd': Is a directory`), then goes to the trash **whole**. `-f`
  ignores missing items. Silent output, like `rm`.
- `rm --permanent`: permanent deletion, the current behavior.
- `rm` on `/proc` or `/dev` prints the error from section 2.
- New `trash` command (same project, system app), inspired by
  `trash-cli`:
  - `trash put <file...>`: like `rm`;
  - `trash list`: `DeletionDate  Path`, sorted by date;
  - `trash restore <name|original path> [destination]`: when the original
    path designates several items, the most recent;
  - `trash rm <name>`: permanent deletion of an item;
  - `trash empty`: empties the trash.

### 5. File explorer

- "Delete" and the Delete key send the selection to the trash, without
  confirmation (the action is reversible).
- Shift+Delete and a "Delete permanently" entry delete permanently,
  after a confirmation.
- A "Trash" entry in the side panel opens a trash view:
  columns name, original location, deletion date,
  size; actions "Restore", "Delete permanently" and "Empty trash"
  (with confirmation). This view reads `HackTrash.GetItemsAsync`, not the
  raw folder.
- Cut, copy, paste and drag-and-drop to the trash remain in
  phase 4 (section 8.2 of the plan).

## Consequences

- A deletion by `rm` or the explorer becomes reversible, with the same
  format as Linux desktops.
- The trash is plain files: `ls ~/.local/share/Trash/files`
  works, and its permissions are those of the home folder.
- Scripts or apps that relied on `rm` to free up space must
  use `rm --permanent` or empty the trash.

## Phase 3 implementation work items

Table shared by ADRs 0050, 0051 and 0052. Each branch starts from
`rebuild/base` and its PR targets `rebuild/base`.

| Branch | Content | Depends on |
|---|---|---|
| `rebuild/3-commands-merge` | ADR 0051 section 1: `HackerOs.Commands` project, move of the 28 commands, `manifests/<appId>.json`, one `ProjectReference` in the host, solution and test projects updated, old projects deleted. No behavior change. | n/a |
| `rebuild/3-process-environment` | ADR 0050 sections 5 and 6: `IProcessEnvironmentTable`, arguments, directory and environment at creation (inherited from the parent), login environment and `/etc/environment`, `/etc/hostname`, `/proc/<pid>/cmdline`, `environ`, `cwd`, `HackEnvironment`, ambient context tied to the process's directory. | n/a |
| `rebuild/3-shell-interpreter` | ADR 0050 sections 1 to 4 and 7: `ShellParser` (tree), expansion, `ShellPipe`, redirections, `&&`, `ShellInterpreter` and `ShellSession` in Platform Core, streams on `AppLaunchRequest`, output as produced in the terminal, `ExecuteCommandIntent` through the interpreter. The terminal's hard-coded names remain temporarily. | `3-process-environment` |
| `rebuild/3-trash` | ADR 0052: `HackTrash`, `rm` to the trash and `--permanent`, `trash` command, Trash view and Shift+Delete in the explorer. | `3-commands-merge` |
| `rebuild/3-shell-commands` | ADR 0051 sections 2 to 5: `IShellSession`, `shell.control` capability (classified normal), catalog commands `cd`, `pwd`, `clear`, `help`, `export`, `unset`, `env`, `sudo`, clear sequences in the terminal, removal of all hard-coded names, completion from the catalog, static aliases of `alias`. | `3-shell-interpreter`, `3-commands-merge` |

Order: `3-commands-merge` and `3-process-environment` in parallel; then
`3-trash` (as soon as `3-commands-merge` is merged) in parallel with
`3-shell-interpreter`; finally `3-shell-commands`.

## References

- [Trash specification 1.0](https://specifications.freedesktop.org/trash-spec/1.0/),
  freedesktop.org.
- ADR 0043 (`Hack*` API), ADR 0045 (logs), ADR 0050, ADR 0051.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 7.4 and 11.
- `Apps/System/HackerOs.Apps.FileExplorer/FileExplorerWindow.razor`,
  `Apps/Commands/HackerOs.Commands.Rm/RmCommand.cs`,
  `Shared/HackerOs.AppSdk/IO/HackFile.cs`.
