> [Version française](../../../fr/wasm/framework/file-operations-api.md)

# File operations: `HackFileOperations` API

This document describes `HackFileOperations`, the SDK API that copies, moves, imports and sends files to the trash, with its naming, conflict and stop rules. The explorer's file control, which relies on these operations, is in [`dev/file-explorer.md`](../dev/file-explorer.md).

## Transfers (`HackFileOperations`, formerly `FileTransfer`)

```csharp
HackFileTransferResult copied = await HackFileOperations.CopyAsync(paths, "/home/benoit/Backup");
HackFileTransferResult moved = await HackFileOperations.MoveAsync(paths, destination, Dialogs.AsFileConflictResolver(AppContext));
HackFileTransferResult imported = await HackFileOperations.ImportAsync(hostFiles, destination);
HackFileTransferResult trashed = await HackFileOperations.MoveToTrashAsync(paths);
string? summary = moved.DescribeFailures("move");   // « Could not move 'x': permission denied. »
```

Rules:

| Case | Behavior |
|---|---|
| Copy, name taken (including pasting into the same folder) | `name (copy).ext`, then `name (copy 2).ext`...; a name already suffixed continues the series. No separate extension for a folder or a hidden file (`.bashrc (copy)`). |
| Move, name taken | "Replace" window (deletes the destination, folder included), "Keep both" (copy name), "Skip"; "Apply to all" applies to the rest of the operation. Closing the window = "Skip". |
| Cut then paste into the original folder | Nothing. |
| Folder onto itself or a descendant | Refused before any write; the other items continue. |
| Host files | Written like a copy (copy name if taken), 256 MiB at most per file. |
| Error | One reason per item (`permission denied`, `read-only file system`, `no longer exists`...), without stopping the others. |
| Stop requested (ADR 0069: canceled token, `kill`, "End task", disconnection) | Between two items (`HackProcess.CheckpointAsync`, which also yields to the interface): the current item, being atomic, finishes; the following ones are reported "stopped before this item" and `FileTransferResult.Cancelled` is true. Nothing is half copied or silently lost. The explorer window makes the OS wait until the current item is done. |

After pasting cut items with no failure, the clipboard is emptied.
