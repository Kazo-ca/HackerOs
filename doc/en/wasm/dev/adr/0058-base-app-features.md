# ADR 0058: Base app features

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 4: complete SDK and base apps
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 8.2,
  decision D22).
- **Builds on:** ADR 0052 (trash), ADR 0050 and 0051 (shell), ADR 0055
  (public SDK only), ADR 0056 (SDK APIs), ADR 0057 (`/proc`).
- Contains the table of phase 4 work items (ADR 0055 to 0058).

## Context

For each base app, the plan sets what exists and must stay, and what
is missing compared to the legacy (`src/apps/`). State observed on `rebuild/base`
(dfd9c47):

| App | Exists | Missing |
|---|---|---|
| Explorer (`HackerOs.Apps.FileExplorer`) | navigation, history, creation, renaming, trash (ADR 0052), zip, properties, "open with", upload and download; drag and drop of an item **onto a folder** in the view (`FileView.OnItemDropAsync`: move, Ctrl to copy) | cut, copy, paste; drop onto the view background, the tree or the breadcrumb; drop of files coming from the host system |
| Task manager (`HackerOs.Apps.SystemMonitor`) | Overview, Processes, Services, Hardware tabs; kill; service start mode | performance with charts, disk, network (legacy tabs: `performance`, `disk`, `network`), per-process detail, filter, multiple selection |
| Text editor (`HackerOs.Apps.TextEditor`) | open, save, save as, search (`TextEditorFindEngine`), conflict detection | replace, replace all (the legacy had them) |
| Console (`HackerOs.Apps.Terminal`, `TerminalPanel`) | history, current directory, Tab completion of **command names** (phase 3) | path completion, multiple tabs |

## Decision

The user interfaces stay in English, like the rest of the apps. Each app
uses only the SDK (ADR 0055).

### 1. File explorer

**Cut, copy, paste.**

- Shortcuts Ctrl+X, Ctrl+C, Ctrl+V; "Cut", "Copy", "Paste" entries
  in the context menu and the toolbar. "Paste" is grayed out when the
  clipboard does not contain files.
- Cut and copy write the selection to the OS clipboard through
  `HackClipboard.SetFileDropListAsync` (ADR 0056, section 12). Another
  explorer window, or another app, can therefore paste.
- Cut items are displayed dimmed while they are in the
  clipboard. Cutting deletes nothing before the paste.
- Paste targets the current folder, or the folder the context menu
  applies to ("Paste Into Folder").
- Copy: `HackFile.CopyAsync` / recursive copy of a folder. Cut:
  `HackFile.MoveAsync` / `HackDirectory.MoveAsync`, then the clipboard is
  emptied if everything succeeded. Pasting a cut item into its own folder
  does nothing.
- **Conflicting names**, like Nautilus: on copy, `report.txt` becomes
  `report (copy).txt`, then `report (copy 2).txt`... On move, a
  window asks "Replace", "Keep both" (same naming rule) or "Skip",
  with "Apply to all".
- Copying or moving a folder into itself or into one of its descendants
  is refused before any write.
- An error on one item does not stop the others; a summary of failures
  is displayed at the end (notification), with the reason
  (`HackUnauthorizedAccessException`, read-only system like `/proc`...).

**Drag and drop**, in addition to what exists:

- Drop onto the view **background**: to the current folder.
- Drop onto a node of the **tree** or a segment of the **breadcrumb**: to that
  folder.
- Between two explorer windows: the
  `application/vnd.hackeros.file-drag+json` format of `FileView` is already readable by
  any instance; the same rules apply.
- Onto the Trash view: sent to the trash (ADR 0052).
- Modifiers: move by default, **Ctrl** to copy (current rule of
  `FileView`). The cursor indicates it during hover.
- **Host system files** (dragged from the user's real
  desktop): uploaded into the target folder through the existing
  upload path. Name conflicts as for paste.
- These additions live in `HackerOs.AppSdk.FileView` when they concern the
  view (background, tree), so that any app that displays a `FileView` benefits.

### 2. Task manager

Rewritten on `HackProcess`, `HackServiceController` and `HackDriveInfo`
(ADR 0056, 0057). No more injection of `IProcessManager`, `AppCatalog`,
`AppLifecycleOrchestrator` or `IEventBus`.

- **Tabs:** Processes, Performance, Disk, Network, Services. The current
  "Hardware & System Info" tab becomes a section of Performance
  (contents of `/proc/cpuinfo` and `/proc/meminfo`). The Overview tab disappears:
  its summary moves to the top of Performance.
- **Refresh:** `/proc` is read every second, like
  `top`, suspended when the window is minimized. Rates are
  differences between two reads.
- **Processes:** columns PID, name, user, state, CPU, memory,
  disk read and write per second; sort by column; text **filter**
  on name, app id and PID; **multiple selection** (Ctrl, Shift, Ctrl+A);
  "End task" on the selection, with a confirmation that lists the processes.
  A denial (`process.manage` missing for a process) is reported per
  process, without stopping the others.
- **Process detail** (double click or side panel): complete `status`,
  command line, current directory, environment if readable,
  `io` counters, parent and child processes.
- **Performance:** chart of total CPU and, optionally, per core; chart
  of memory used; 60 seconds of history.
- **Disk:** used and free space of each `HackDriveInfo`, chart of
  read and write rates from `/proc/diskstats`.
- **Network:** chart of received and sent rates per interface from
  `/proc/net/dev`, packet and error counters.
- **Services:** what exists (list, start, stop, start mode),
  on `HackServiceController`.
- **Charts:** native Blazor components in SVG with isolated CSS, without a
  chart library (`AGENTS.md` guidelines on MudBlazor). The
  tabs themselves stay `MudTabs`.

### 3. Text editor

- **Find and replace panel**, opened by Ctrl+H (Ctrl+F opens search
  alone, as today): "Replace with" field, "Replace" and
  "Replace All" buttons.
- Options shared with search: "Match case" and "Whole word".
  No regular expressions in this phase.
- "Replace" replaces the selected occurrence if it matches, then
  selects the next one; otherwise it first selects the next one.
- "Replace All" replaces all occurrences in **a single
  modification** of the document and displays their count ("12 replacements").
- The logic lives in `TextEditorFindEngine` (`Replace`, `ReplaceAll`), tested
  without a UI.
- A replacement marks the document as modified; the detection of write
  conflicts on save does not change.

### 4. Console

**Tab completion**, done by the shell (`IHackShell.CompleteAsync`,
ADR 0056 section 7), like `bash` and not by the terminal emulator:

- First word of a command (start of line, after `|` or `&&`): names of
  installed commands and their aliases.
- Other words: paths, relative to the shell's directory, with `~` expanded.
  A folder is completed with a trailing `/`. Hidden files are
  offered only if the prefix starts with `.`.
- A single candidate: it is inserted, followed by a space (or the `/` of a
  folder). Several: the longest common prefix is inserted; a second Tab
  lists the candidates below the prompt, like `bash`.
- An unreadable path (rights, capability) offers nothing, without error.

**Tabs:**

- Each tab is a distinct shell (`HackShell.StartAsync`), therefore a distinct
  **process**, child of the terminal's process, with its own
  directory, environment, input history and `$?`.
- Ctrl+Shift+T opens a tab in the current tab's directory;
  Ctrl+Shift+W closes the tab (and terminates its shell and commands);
  Ctrl+PgUp and Ctrl+PgDn switch tabs; "+" button and close icon on
  each tab.
- Tab title: name of the running command, otherwise name of the current
  folder (`~` for the home folder).
- `exit` closes the tab; closing the last tab closes the window.
- The CodeEditor terminal panel stays at a single shell.

## Consequences

- The four base apps reach parity with the legacy and
  exceed it on the trash, pasting between windows, real disk
  and network measurements, and console tabs.
- The drag-and-drop additions in `FileView` benefit the other apps that
  display it (file pickers, CodeEditor).
- Completion in the shell makes it available to any third-party terminal
  emulator.

## Rejected alternatives

- **Completion in `TerminalPanel`** (like that of command names
  today): each terminal emulator would have to redo it, and it would not
  see the shell's directory and aliases without new access.
- **Chart library** (MudBlazor Charts, Chart.js): three
  simple curves do not justify a dependency or JavaScript.
- **Regular expressions in "Replace"**: useful, but absent from the legacy;
  postponed to keep the panel simple.

## Phase 4 implementation work items

Table shared by ADR 0055, 0056, 0057 and 0058. Each branch starts from
`rebuild/base` and its PR targets `rebuild/base`. Each work item that removes a
violation of ADR 0055 removes the exemption of the project concerned in the same
change, and documents its APIs in `docs/app-sdk.md` (created by
`4-sdk-boundary`).

| Branch | Content | Depends on |
|---|---|---|
| `rebuild/4-sdk-boundary` | ADR 0055 sections 1 to 3 and 6: MSBuild target `HOSAPP0002` / `HOSSDK0001`, check `HOSAPP0003` in the manifest generator, `IJSRuntime` source test, exemption list of the projects in violation, removal of unused `Platform.Core` references (Calculator, HackPaint, IconViewer, TextEditor, Browser). Creation of `docs/app-sdk.md`. | - |
| `rebuild/4-sdk-user-and-apps` | ADR 0056 sections 1 to 3 and ADR 0055 section 5: `HackUser`, `HackEnvironment.GetFolderPath`, `HackApps`, `HackAppManagement`, OS authentication agent. Migration of FileExplorer, TextEditor, CodeEditor (user) and of the "Installed Apps" tab of Settings. | `4-sdk-boundary` |
| `rebuild/4-sdk-network` | ADR 0056 section 6: `IAppNetworkGateway`, `HackDns`, `HackPing`, `HackHttpClient`, `HackTcpClient`, real fallback in Platform Core, `ShellErrorText` in the SDK. Migration of Browser, `ping`, `curl`, `cat`, `nmap`; `HackerOs.Commands` without `Platform.Core` or `Server.Contracts`. | `4-sdk-boundary` |
| `rebuild/4-sdk-shell-host` | ADR 0056 section 7: capability `shell.host`, `HackShell`, `IHackShell` (without path completion), `HackCredentialPrompt`; `TerminalPanel` and `HackerOs.AppSdk.Terminal` without `Platform.Core`; `IsSystemApp="true"` for CodeEditor. | `4-sdk-boundary` |
| `rebuild/4-sdk-system-admin` | ADR 0056 sections 8 to 11: `HackSettings.OpenSystemAsync`, `HackPermissionAdministration`, `HackServerConnection`, `HackSystem`, `HackStorageDiagnostics`, capabilities `permissions.manage`, `server.connection.manage`, `system.power`, `system.storage.inspect`. Migration of the rest of Settings and of Diagnostic. | `4-sdk-user-and-apps` |
| `rebuild/4-proc-metrics` | ADR 0057: counting in the file gateway, the persistence and the network gateway; `/proc/stat`, `/proc/<pid>/io`, `/proc/diskstats`, `/proc/net/dev`, `/proc/mounts`; `StorageCapacityBytes`; `HackDriveInfo`. Update of `virtual-proc-and-dev.md`. | `4-sdk-network` |
| `rebuild/4-file-explorer` | ADR 0056 section 12 and ADR 0058 section 1: `HackClipboard` and cut/copy operation, cut-copy-paste, name conflicts, drag and drop onto the background, the tree, the breadcrumb, the trash and from the host. | `4-sdk-user-and-apps` |
| `rebuild/4-text-editor` | ADR 0058 section 3: Find and replace panel, `Replace` and `ReplaceAll` in `TextEditorFindEngine`. | `4-sdk-user-and-apps` |
| `rebuild/4-terminal` | ADR 0058 section 4: `IHackShell.CompleteAsync` (commands and paths), console tabs. | `4-sdk-shell-host` |
| `rebuild/4-task-manager` | ADR 0056 sections 4 and 5 and ADR 0058 section 2: `HackProcess`, `HackServiceController`, rewrite of SystemMonitor (tabs, charts, detail, filter, multiple selection). | `4-proc-metrics`, `4-sdk-user-and-apps` |
| `rebuild/4-sdk-boundary-final` | ADR 0055 section 6: removal of the exemption mechanism, verification that all of `Apps/` and the whole SDK pass without exemption, plan and documentation index up to date. | all the other phase 4 work items |

Order:

1. `4-sdk-boundary` alone.
2. In parallel: `4-sdk-user-and-apps`, `4-sdk-network`, `4-sdk-shell-host`.
3. As soon as their dependency is merged, in parallel:
   `4-sdk-system-admin`, `4-file-explorer` and `4-text-editor` (after
   `4-sdk-user-and-apps`), `4-proc-metrics` (after `4-sdk-network`),
   `4-terminal` (after `4-sdk-shell-host`).
4. `4-task-manager` (after `4-proc-metrics` and `4-sdk-user-and-apps`).
5. `4-sdk-boundary-final`.

## References

- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 8.2.
- Legacy: `src/apps/file-explorer.ts`, `src/apps/system-monitor.ts`,
  `src/apps/text-editor.ts`, `src/apps/terminal.ts`.
- ADR 0016, 0050, 0051, 0052, 0055, 0056, 0057.
- `Shared/HackerOs.AppSdk.FileView/FileView.razor.cs`,
  `Apps/System/HackerOs.Apps.TextEditor/TextEditorFindEngine.cs`,
  `Shared/HackerOs.AppSdk.Terminal/TerminalPanel.razor`.
