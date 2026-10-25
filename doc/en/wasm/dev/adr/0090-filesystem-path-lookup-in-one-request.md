# ADR 0090: path lookup in a single storage request

## Status

Accepted on 2026-10-17, with the `web/15-settings-perf` work item.

- **Completes:** the contract of file system providers
  (`IFileSystemProvider`) and the path resolver `FileSystemPathResolver`
  (documented in [`old/virtual-filesystem.md`](../old/virtual-filesystem.md)
  and [`old/indexeddb-filesystem.md`](../old/indexeddb-filesystem.md)).
  Replaces no decision: permissions, symbolic links, mount
  points and revisions are applied exactly as before.

## Context

Writing a setting in `~/.config/hackeros` took about 2 seconds in the
browser (observed during PR #133). Measurement in Chromium, Release build of the
Ecosystem host, click on "Mobile" in the clock panel (first
save of the choice, file creation included):

| | IndexedDB transactions | Click → mobile shell displayed | With CPU throttled ×4 |
|---|---|---|---|
| Before | 604 | 845 ms | 2,000 ms |
| After | 51 | 235 ms | 1,010 ms |

Settings remain real files subject to permissions (ADR 0044). The
slowness came from path lookup:

1. `IndexedDbFileSystemReader.ResolveAsync` read each component of a path
   with two transactions (the `(parentId, name)` link, then the entry),
   starting again from `/`. Each transaction is a C# → JavaScript
   → IndexedDB round trip (about 4 ms, 20 ms on a slow device).
2. `FileSystemPathResolver` does an `lstat` of **each prefix** of the path
   to follow links and check the right to traverse. With point 1,
   resolving `/home/<user>/.config/hackeros/<doc>.config` cost
   3 + 5 + 7 + 9 + 11 = 35 transactions, before even the authorization and
   the operation itself. A setting write reads then writes several
   files: more than 600 transactions.

## Decision

### 1. IndexedDB storage walks a path in a single transaction

`indexedDb.js` gains the `resolvePath` operation: starting from the root entry,
it reads link then entry for each name, **in the same read-only
transaction**, and returns the list of entries found (stopping before the
first missing name). A link to a missing entry ends the list with a
`{ danglingLink, entryId }` marker, which the C# reports as corruption
(`InvalidDataException`, as before). It is a kernel's `namei`: a single
walk, a consistent snapshot.

`IndexedDbFileSystemReader.ResolveAsync` uses it for any path other than
`/`; the new method `ResolveComponentsAsync` returns the full list.

### 2. Optional capability `IFileSystemPathWalkProvider`

Contract added in `HackerOs.Simulation.Abstractions.FileSystem`:

```csharp
public interface IFileSystemPathWalkProvider
{
    ValueTask<FileSystemResult<IReadOnlyList<FileSystemEntrySnapshot>>> StatComponentsAsync(
        VirtualPath path, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default);
}
```

It returns the metadata of each component without following links; a list
shorter than the path means that the next component does not exist.
`IndexedDbFileSystemProvider` implements it with the operation of point 1. The
other providers (`/proc`, `/dev`, memory, test mounts) do not change.

### 3. The resolver uses it when the whole path is on one mount

At the start of each pass, if the path has at least two components, all
its prefixes belong to the same mount and that provider implements
`IFileSystemPathWalkProvider`, `FileSystemPathResolver` requests the walk in
one request then applies **the same rules** on the list: right to
traverse, `NotDirectory`, links followed (a link has no children, the walk
stops on it and the next pass starts again from the target), limit of 40 links and
loops. Otherwise, or if the walk fails, it falls back to `lstat` per prefix,
which reports the exact error as before.

## Consequences

- An operation on a file costs three storage requests (resolution,
  authorization, operation) instead of dozens, whatever the depth.
  The whole OS benefits (startup, explorer, shell), not only
  settings.
- `/proc/diskstats` counts one 4 KiB read per path walk instead of one
  per component: closer to a real kernel, whose directory entry cache
  avoids rereading the disk for each prefix.
- No metadata cache: another tab or a synchronization that
  modifies IndexedDB is still seen immediately, no invalidation to manage.
- Remaining after this work item (measured, not addressed): about 100 ms between the click and
  the first write, due to rereads of the document (read before
  `SetManyAsync`, reread after write, reread triggered by the
  file watcher); at ×4 throttling, half of the remaining time is the
  rendering of the mobile shell, not the disk.
