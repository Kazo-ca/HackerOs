> [Version française](../../../../fr/wasm/dev/adr/0063-embeddable-app-components.md)

# ADR 0063: embeddable app components, registered with the OS

## Status

Accepted on 2026-10-11.

- **Plan context:** after the rebuild, request from Benoit on 2026-10-11:
  the code editor must have a dock at the bottom with tabs (status,
  terminal, errors) and, for the terminal, "reuse the control of the
  terminal application. Terminal must register its control in the OS,
  respecting an interface to control it from the application; set the current
  directory, read output, send commands".
- **Completes** ADR 0047 (manifest as a C# class): `AppDefinition` and
  `AppManifest` gain `Components`.
- **Supersedes** the sentence of ADR 0062 §1 about the editor's "terminal
  panel": this panel becomes a tabbed dock (Status, Terminal,
  Problems) and its terminals are those of the Terminal app, embedded by
  contract, instead of `TerminalPanel` referenced directly.
- **Respects** ADR 0055: the editor uses only the public SDK; it no longer
  references `HackerOs.AppSdk.Terminal`.

## Context

Until now, an app that wanted a terminal referenced the library
`HackerOs.AppSdk.Terminal` and drew a `TerminalPanel` itself. The
editor's terminal and the console's had the same code, but
no link to the Terminal app: nothing made it possible to drive it
(change folder, launch a command, read the output), and another app
would have had to redo the same thing.

Linux desktops handle this case with registered components: KDE loads
Konsole's KPart in Kate or Dolphin (the host app only knows the
`TerminalInterface` interface), GNOME embeds the VTE widget. This is the
model retained.

## Decision

### 1. An app registers components in its manifest

`AppDefinition.Components` lists `AppComponent(Contract, Type)` entries. The
generated manifest carries `components: [{ contract, assembly, type }]`.

- The **contract** is a stable name in lowercase words separated by dots
  (`hackeros.terminal`). It designates an SDK interface (the controller) and an
  options record.
- Only `Window` apps can register components; a contract is
  registered only once per app; the type lives in the entry point's
  assembly, which lazy loading already retrieves
  (`AppManifestValidator`).
- `components` is **omitted** when the app has none (`null` in
  `AppManifest`): existing manifests keep their bytes.
- The schema, the allow list of `AppManifestJsonSerializer`, the test
  canonical form and the validator are updated together (common-pitfalls §2).

### 2. The component inherits from `HackEmbeddableComponent`

The component derives from `HackEmbeddableComponent` (`HackerOs.AppSdk.Blazor.Components`),
which derives from `HackComponentBase`. It receives:

- the context of the **host** window (cascading parameter `AppContext`): it
  runs in the host's process, with its capabilities, like a
  library loaded into the process;
- `Options`: the options record of the contract, chosen by the host;
- `ControllerReady`: the callback that hands the controller to the host once the
  component is ready.

### 3. The OS finds the provider, the host asks for a contract

`IHackComponentHost.ResolveAsync(contract)` (implemented by
`HackComponentHost` in `Platform.Blazor`, registered in the ecosystem):

1. lists the **installed** apps that register the contract, system apps
   first, then by identifier;
2. retrieves the app's descriptor with
   `AppLifecycleOrchestrator.EnsureDescriptorAsync`, which loads its assembly
   like a launch;
3. checks that the type exists, is not abstract and derives from
   `HackEmbeddableComponent`;
4. returns the type, or the reason why nothing is available. A
   provider that fails to load leaves room for the next one.

The host displays `<HackEmbeddedComponent TController="..." Contract="...">`: the
component resolves the contract, renders the provided component and converts the
received controller to `TController`. Without a provider (Terminal app
uninstalled, test host), it displays the reason or the `Unavailable`
fragment.

### 4. The first contract: `hackeros.terminal`

`HackComponentContracts.Terminal` designates `IHackTerminal` and
`HackTerminalOptions` (`HackerOs.AppSdk.Components`). The Terminal app
registers it with `TerminalEmbed`, which renders the same `TerminalPanel` as its
window; `TerminalPanel` implements `IHackTerminal`:

| Member | What it does, as a person at the keyboard would |
| --- | --- |
| `RunCommandAsync(line)` | The line is put in the terminal's input queue (after the keys already typed), displayed at the prompt, executed; the line being typed is set aside then restored (like zsh's push-line). Returns `$?` and the output, without control sequences. |
| `SetWorkingDirectoryAsync(path)` | Launches a visible `cd` (path quoted if needed). |
| `SendInputAsync(text)` | The keyboard bytes: text, `\r` for Enter, `\u0003` for Ctrl+C. |
| `ReadOutput(max)` | The last 256 KiB displayed: committed lines, command outputs, terminal messages; not the redraws of the line. |
| `ClearAsync`, `FocusAsync` | `clear`, focus. |
| `WorkingDirectory`, `Title`, `IsRunning`, `IsBusy` | Shell state. |
| `OutputReceived`, `CommandCompleted`, `StateChanged`, `Exited` | Events, raised on the render thread. |

An interrupted command (Ctrl+C or canceled token) ends with status 130,
as in bash.

### 5. The editor's dock

The editor replaces its terminal panel with a tabbed dock:

- **Status**: facts about the editor (folder, file, language, size, tabs,
  terminals) and a timestamped log of messages (200 at most); errors
  are also still displayed above the editor, other messages go to
  the status bar;
- **Terminal**: one `hsh` per tab, embedded by contract; "Open in
  Integrated Terminal" in the explorer moves the active terminal with a
  visible `cd`, or opens a new one if it is busy;
- **Problems**: empty for now (no diagnostics without compilation).

The dock is hidden, not destroyed, when closed: its shells keep running.
Ctrl+J shows or hides it; Ctrl+` goes to the Terminal tab.

## Consequences

- A third-party app can embed a terminal through the public SDK, with the
  `shell.host` capability (the shell is its child).
- Uninstalling the Terminal app removes the terminal from the editor, which displays
  why: this is the behavior of a component provided by another app.
- Other contracts can follow (image viewer, file view)
  without changing this mechanism.

## Rejected alternatives

- **Referencing `HackerOs.AppSdk.Terminal` in each app** (what exists): no
  control, and nothing is registered with the OS.
- **A global DI service per component**: each component would have to be
  registered in the ecosystem and all apps loaded at startup; the
  manifest keeps lazy loading.
- **`DynamicComponent`**: its trimming annotations fail the build
  (IL2111); `HackEmbeddedComponent` opens the component itself.
