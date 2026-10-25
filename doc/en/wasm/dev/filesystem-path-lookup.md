> [Version française](../../../fr/wasm/dev/filesystem-path-lookup.md)

# Filesystem path lookup

## Goal

Finding the entry for a path (`/home/<user>/.config/hackeros/<doc>.config`)
cost dozens of round trips to IndexedDB: writing a setting took about 2
seconds on a slow device. This document describes how a path is now looked up
in one request, and how to measure it. Decision:
[ADR 0090](adr/0090-filesystem-path-lookup-in-one-request.md).

## Measurements

Ecosystem host published in Release, served statically, headless Chromium,
fresh profile, click on "Mobile" in the clock panel (the choice is written to
`~/.config/hackeros/ui-platform-preference.config`, file creation included),
until the mobile shell's "Back" button appears. Three runs, 2026-10-17:

| | IndexedDB transactions | Duration | CPU throttled ×4 (CDP) |
|---|---|---|---|
| `web/base` before | 604 | 837 to 850 ms | 1,975 to 2,010 ms |
| One transaction per lookup (step 1) | 95 | 260 to 360 ms | |
| Walk through the resolver (step 2) | 51 | 231 to 242 ms | 997 to 1,028 ms |

The transaction count wraps `IDBDatabase.prototype.transaction` with a script
injected before the page loads. The measurement script is not versioned; it
follows the flow of `ClockPanelMobileToggleTests`.

## Architecture

```
FileSystemService.WriteAsync(path)
 ├─ FileSystemPathResolver.ResolveAsync        1 request: StatComponentsAsync (walk)
 ├─ AuthorizeAsync → StatAsync(path)           1 request: resolvePath
 └─ IndexedDbFileSystemProvider.WriteAsync     1 request: resolvePath, then content and metadata
```

| Element | Project | Role |
|---|---|---|
| `resolvePath` (`indexedDb.js`) | `Infrastructure.Browser/wwwroot` | reads link then entry for each name in a single read-only transaction; returns the entries found |
| `IndexedDbFileSystemReader.ResolveComponentsAsync` | `Infrastructure.Browser/FileSystem` | calls `resolvePath`, converts the entries, reports an orphan link as corruption |
| `IndexedDbFileSystemReader.ResolveAsync` | same | `/`: one read of the root entry; otherwise the last entry of the walk, or `null` |
| `IFileSystemPathWalkProvider` | `Simulation.Abstractions/FileSystem` | optional provider capability: metadata of each component, without following links |
| `IndexedDbFileSystemProvider.StatComponentsAsync` | `Infrastructure.Browser/FileSystem` | implements it for the IndexedDB root |
| `FileSystemPathResolver.TryWalkAsync` | `Platform.Core/FileSystem` | requests the walk when all prefixes are on the same mount; otherwise `lstat` per prefix |

## Rules kept

- The traverse permission (`x`) of each intermediate directory, `NotDirectory`,
  followed links (absolute or relative), the 40-link limit and loop detection
  apply to the returned list, exactly as with successive `lstat` calls.
- A path that crosses a mount point (`/proc`, `/dev`, test mount) is checked
  prefix by prefix, as before.
- A failing walk (storage, cancellation) falls back to per-prefix `lstat`
  calls, which report the precise error.
- No cache: each lookup rereads IndexedDB, so another tab or a sync is seen
  immediately.

## Tests

- `Tests/HackerOs.Infrastructure.Browser.Tests/IndexedDbFileSystemReaderTests.cs`:
  a single `resolvePath` transaction for a deep path, missing name, partial
  list, orphan link, disk count.
- `Tests/HackerOs.Infrastructure.Browser.Tests/IndexedDbFileSystemProviderTests.cs`:
  provider scenarios adapted to the one-transaction walk.
- `Tests/HackerOs.Platform.Core.Tests/FileSystem/FileSystemPathResolverTests.cs`:
  one-request walk, missing first component, traverse permission and restart
  on a link, path crossing a mount.
- The E2E suites (`HackerOs.E2E.Tests`, `HackerOs.UI.E2E.Tests`) exercise
  `resolvePath` in a real browser on every startup.
- Side effect on the UI tests: the faster filesystem revealed a race in
  `E2ESupport.SelectFileExplorerEntryAsync` (the click on a row arrived while
  the list was reloading after a navigation, which clears the selection). The
  helper clicks again until the row has stayed selected.

## Task list

- [x] Measure writing a setting in a real browser (604 transactions, 845 ms).
- [x] `resolvePath` operation in `indexedDb.js` (one transaction per lookup).
- [x] `IndexedDbFileSystemReader` on `resolvePath`, tests adapted.
- [x] `IFileSystemPathWalkProvider` contract and IndexedDB implementation.
- [x] `FileSystemPathResolver`: one-request walk on a single mount, per-prefix fallback.
- [x] Measure after (51 transactions, 235 ms; ×4: 2.0 s → 1.0 s).
- [x] ADR 0090, docs index, notes in the archived documents.
- [ ] Not handled: redundant rereads of the settings document around a write (about 100 ms).
