# OS clipboard and sharing with the host

The clipboard API for apps (`HackClipboard`, file lists, GNOME format) is in [`clipboard-api.md`](../framework/clipboard-api.md).

## Purpose

Implementation of [ADR 0065](adr/0065-os-clipboard-and-host-bridge.md), which
completes ADR 0056 §12. HackerOS has **a single clipboard**, held by the OS
and shared by all apps of the session; a bridge copies its text to the real
machine's clipboard and picks up what the user pastes there, like VirtualBox
Guest Additions. Clipboard use in the explorer (cut, copy, paste, name
conflicts) is described in [`file-explorer.md`](file-explorer.md).

## Architecture

```text
App ──► HackClipboard ──► IAppClipboardGateway ──► AppClipboardGateway ──► SystemClipboard (one entry per session)
                                                       clipboard.read / .write          │ Changed, sequence, origin
                                                                                          ▼
              /etc/hackeros/clipboard.config ──► HostClipboardBridge (Platform Core)
                                                       │ IHostClipboardChannel / IHostClipboardSink
                                                       ▼
                                   BrowserHostClipboardChannel ──► hostClipboard.js ──► navigator.clipboard.writeText
                                   (Infrastructure.Browser)        paste / copy / cut ◄── real machine
```

| Piece | Where | Role |
|---|---|---|
| `SystemClipboard`, `SystemClipboardEntry`, `SystemClipboardOrigin` | `Platform/HackerOs.Platform.Core/Clipboard/` | The clipboard: multi-format entry per session, sequence number, origin (`Guest`, `GuestNative`, `Host`), cleared on logout. In memory only. |
| `ClipboardSettingsDocuments`, `ClipboardSharingMode` | same | `clipboard` document (`sharing` key) and allowed direction. |
| `HostClipboardBridge` | same | Copies text to the host, applies host pastes and native copies, rereads the setting live. All the testable logic. |
| `IHostClipboardChannel`, `IHostClipboardSink` | `Shared/HackerOs.Simulation.Abstractions/Clipboard/` | Contract between the bridge and the page. |
| `BrowserHostClipboardChannel` | `Infrastructure/HackerOs.Infrastructure.Browser/Interop/` | Imports `hostClipboard.js` and relays its callbacks. |
| `hostClipboard.js` | `Infrastructure/HackerOs.Infrastructure.Browser/wwwroot/` | Listens to `paste` (capture, document), `copy` and `cut` (bubble, window); calls `writeText`. |
| `FileView.attachPasteTarget` | `Apps/System/HackerOs.Apps.FileExplorer/FileControl/FileView.razor.js` | Ctrl+V through the `paste` event; host files imported like a drop. |

The bridge is registered by `AddHackerOsEcosystem` (circuit scope, like
everything that depends on `IJSRuntime`) and started by `App.razor` after the
preferences are loaded; a failure never prevents startup.

## Usage and API

### OS

```csharp
clipboard.SetTargets(session, new Dictionary<string, string>
{
    ["text/plain"] = "bonjour",
    ["text/html"] = "<b>bonjour</b>"
}, SystemClipboardOrigin.Guest);
long sequence = clipboard.GetSequenceNumber(session);   // +1 on every change, clearing included
SystemClipboardEntry? entry = clipboard.GetEntry(session);
```

### Setting

`/etc/hackeros/clipboard.config` (installed commented out at first startup):

```ini
sharing=bidirectional
```

| Value | Guest → host | Host → guest |
|---|---|---|
| `bidirectional` (default) | yes | yes |
| `guest-to-host` | yes | no |
| `host-to-guest` | no | yes |
| `disabled` | no | no |

A user overrides it in `~/.config/hackeros/clipboard.config`, unless the
administrator locks it:

```ini
sharing=disabled
[locked]
sharing=true
```

## Rules

| Case | Behavior |
|---|---|
| An app copies text or files | The entry becomes current; its `text/plain` (the paths for files) goes to the host if `sharing` allows it. Other formats never leave. |
| Paste with text new to the OS | The text becomes the current entry (origin `Host`), with no echo to the host. |
| Paste of text the OS itself wrote | Nothing changes; the native paste proceeds. |
| Paste of host files in the explorer | Names noted under `application/x-hackeros-host-files`; the `FileView` imports them into the current folder (copy names if taken, 256 MiB per file). A new Ctrl+V reimports them as long as the clipboard has not changed on the OS side. |
| Host → guest refused, or stale host text, paste into a field | The field receives the OS text (or nothing), never the host's; the terminal too. |
| Native copy (text field, terminal) | Becomes the current entry (origin `GuestNative`). If guest → host is refused, the host is not modified. |
| Browser that refuses `writeText` | Log `clipboard: host clipboard call failed` (Debug); the OS keeps its entry. |
| Logout | The entry is cleared and the page forgets the text. |

## Key decisions

See [ADR 0065](adr/0065-os-clipboard-and-host-bridge.md): a single owning
clipboard, no `readText` (no authorization), no encryption (the key would
necessarily be in the WASM), direction decided in the page during the event,
`clipboard.config` file rather than `clipboard.conf` to follow the ADR 0044
convention.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Clipboard/SystemClipboardTests.cs`:
  sequence, multiple formats, origin, entry copy, clearing.
- `Tests/HackerOs.Platform.Core.Tests/Clipboard/ClipboardSettingsDocumentsTests.cs`:
  `sharing` values, installed file, user override, `[locked]`.
- `Tests/HackerOs.Platform.Core.Tests/Clipboard/HostClipboardBridgeTests.cs`:
  write to the host, file list as paths, paste without echo, host files,
  native copy, each mode, lock, live change, logout, browser refusal, absence
  of a session.
- `Tests/HackerOs.Apps.FileExplorer.FileControl.Tests/FileViewDropAndClipboardTests.cs`:
  Ctrl+V through the `paste` event, host files pasted.

## Task list

- [x] ADR 0065 (0060 was taken).
- [x] `SystemClipboard`: multi-format entry, sequence number, origin.
- [x] `clipboard.config` document (`sharing` key, `[locked]`), installed at first startup, listed by `OsSettingsDocumentRegistry`.
- [x] `HostClipboardBridge` and `IHostClipboardChannel` contract.
- [x] `BrowserHostClipboardChannel` and `hostClipboard.js` in Infrastructure.Browser.
- [x] Registration in `AddHackerOsEcosystem`, startup in `App.razor`.
- [x] `FileView`: Ctrl+V through `paste`, pasted host files imported like a drop.
- [x] Copy diagnostic through `HackClipboard` (`clipboard.write`); `BrowserClipboard` `[Obsolete]`, then removed in 1.0 (ADR 0114).
- [x] Tests and documentation.
