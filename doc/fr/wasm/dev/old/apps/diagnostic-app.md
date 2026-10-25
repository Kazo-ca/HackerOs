# HackerOS IndexedDB Diagnostic Application (`org.hackeros.diagnostic`)

## Purpose

`Apps/System/HackerOs.Apps.Diagnostic/` provides a comprehensive browser storage inspector and diagnostic application for HackerOS. It enables developers and AI agents to query, inspect, filter, copy, and export all 12 IndexedDB object stores, while demonstrating how UNIX/Linux inode file metadata (`fsEntries`) correlates with directory link hierarchies (`fsLinks`) to form canonical virtual file paths.

## Architecture & Store Structure

IndexedDB database `hackeros` (schema version 2) stores 12 object stores:
- **`users`**: User account records (`LocalUser`).
- **`groups`**: User group records (`LocalGroup`).
- **`sessions`**: Active session state records.
- **`settings`**: User and app settings documents.
- **`fsEntries`**: File system entry metadata (inodes storing entry ID, kind, owner, content hash, permissions, timestamps).
- **`fsLinks`**: Directory hierarchy links mapping `(parentId, name)` to `entryId`.
- **`fsContent`**: Deduplicated content chunks keyed by SHA-256 hash.
- **`catalog`**: Installed app manifests and enablement status.
- **`grants`**: Immutably granted capabilities per user/app.
- **`audit`**: Append-only security audit log entries.
- **`diagnostics`**: Bounded diagnostic logs.
- **`syncMetadata`**: Installation ID and local policy revision bookkeeping.

## Virtual Path Resolution Algorithm

Because `fsEntries` acts like a Linux inode table without storing absolute paths, `IndexedDbInspectorService` reconstructs canonical paths by traversing `fsLinks` directory trees:
1. Loads both `fsEntries` and `fsLinks` in a single atomic `BackupRestore` read transaction.
2. Constructs a parent link lookup: `linkMap[entryId] = (parentId, name)`.
3. For each `entryId` in `fsEntries`, traverses upwards from child to parent until reaching the root entry (`00000000-0000-0000-0000-000000000001`).
4. Reverses and joins path segments to produce resolved canonical virtual paths (e.g. `/home/Admin/Documents/notes.txt`).

## Key Features

- **Store Data Inspection**: Interactive store sidebar allowing instant switching between all 12 IndexedDB object stores.
- **Path Resolution Table**: `fsEntries` view displays entry metadata alongside dynamically resolved virtual file paths.
- **Copy JSON to Clipboard**: One-click copying of the formatted database export JSON for instant AI agent inspection.
- **Save JSON File**: Saves a complete formatted database dump to `/home/{username}/indexeddb-dump.json`.
- **Search & Filter**: Real-time filtering across store items by name, ID, path, or owner.

## Completed Task Checklist

- [x] Create project file `HackerOs.Apps.Diagnostic.csproj` with MudBlazor and Infrastructure references.
- [x] Create manifest `app.manifest.json` declaring reverse-domain ID `org.hackeros.diagnostic`.
- [x] Implement `IndexedDbInspectorService.cs` with `BackupRestore` transaction and path resolver.
- [x] Implement `DiagnosticWindow.razor` & `DiagnosticWindow.razor.css` with Gothic/Hacker dark theme UI.
- [x] Register `HackerOs.Apps.Diagnostic` in `HackerOs.Ecosystem.csproj`.
- [x] Verify project compilation and test suite execution.
