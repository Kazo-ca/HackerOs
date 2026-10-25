# ADR 0065: OS clipboard and text sharing with the host

## Status

Accepted on 2026-10-16.

- **Context:** decision validated by Benoit: a single clipboard, owned by
  the OS, and text sharing with the real machine "like VirtualBox Guest
  Additions". Requested as "ADR 0060"; that number was already taken
  (system administration adjustments), hence 0065.
- **Completes** ADR 0056 §12 (file clipboard), without contradicting it:
  `HackClipboard`, the `x-special/gnome-copied-files` format and the
  `clipboard.read` / `clipboard.write` capabilities stay as they are.
- **Supersedes** ADR 0060 §5: Diagnostic no longer copies through `BrowserClipboard`
  but through `HackClipboard` (so it declares `clipboard.write` again), and
  `BrowserClipboard` is marked `[Obsolete]` (ADR 0019).

## Context

The `rebuild/4-file-explorer` work item delivered `SystemClipboard`, a per-session
selection in Platform Core, shared by all the apps of the session.
Two gaps remained:

- text copied in HackerOS did not reach the clipboard of the real
  machine, except through `BrowserClipboard`, a direct output that bypassed the
  OS clipboard and any policy;
- a paste from the real machine did not enter the OS clipboard:
  an app reading `HackClipboard` did not see what the user
  had just copied elsewhere, and files copied on the host could not be pasted
  into the explorer.

A real guest system under VirtualBox solves this with a Guest Additions
service that copies the text in both directions, and is configurable
("bidirectional", "guest to host", "host to guest", "disabled").

## Decision

### 1. A single clipboard, owned by the OS

- `SystemClipboard` (Platform Core) remains the only clipboard: one entry
  per session (`SystemClipboardEntry`), in memory only, lost on
  reboot and emptied on logout.
- The entry is **multi-format**: `text/plain`, `x-special/gnome-copied-files`
  (ADR 0056 §12), and any other MIME type through `SetTargets` (extensible).
- Each change receives a **sequence number** per session
  (`GetSequenceNumber`, like Windows' `GetClipboardSequenceNumber`) and raises
  the `Changed` event.
- The entry records its **origin** (`SystemClipboardOrigin`): `Guest` (an app through
  `HackClipboard`), `GuestNative` (native copy in a text field or the
  terminal), `Host` (paste from the real machine).
- Apps access it only through `HackClipboard` and `IAppClipboardGateway`, with
  `clipboard.read` / `clipboard.write`. The old in-memory class of the SDK
  (unshared `AppClipboardGateway`) has already been replaced by the Platform Core
  gateway to this service; nothing changes for apps.

### 2. Output to the host

- When an app sets an entry containing `text/plain`, the
  `HostClipboardBridge` copies it through `navigator.clipboard.writeText`. The call
  is made during the transient activation of the gesture that copied (Ctrl+C, click):
  the browser does not ask for permission.
- A file list goes out as text paths (its
  `text/plain` target), never in its GNOME format.
- A browser refusal (no focus, no API) is logged; the OS
  clipboard is not touched.

### 3. Input from the host

- The module listens for the `paste` event on the document, in the capture phase,
  before any app. `clipboardData` is readable without permission.
- If the pasted text differs from the last text the OS knows to be present on the host
  (written by it or already received), it becomes the current entry, origin `Host`.
  It is never sent back to the host.
- `navigator.clipboard.readText` is **not** called on the normal path (it
  would ask for permission).
- Pasted files (`clipboardData.files`) are recorded by name in the
  `application/x-hackeros-host-files` target, and the `FileView` that receives the paste
  imports them into the current folder through the same path as the drop of
  host files (`DropRequested`, then `FileTransfer.ImportAsync`). For
  this, Ctrl+V in a `FileView` follows the `paste` event, which arrives after
  the clipboard update, and no longer `keydown` (with a 250 ms fallback
  for browsers that do not raise `paste` outside a field).

### 4. Setting `/etc/hackeros/clipboard.config`

- Key `sharing`: `bidirectional` (default), `guest-to-host`, `host-to-guest`,
  `disabled`. Layered document of ADR 0044: machine default in
  `/etc/hackeros/clipboard.config`, override in
  `~/.config/hackeros/clipboard.config`, lockable with `[locked]`.
- The name follows the convention of OS documents (`<document>.config`, ADR
  0044), and not `clipboard.conf` as in the request.
- The file is installed commented at first boot and listed by
  `OsSettingsDocumentRegistry` (Settings can open it through
  `HackSettings.OpenSystemAsync`). A change applies without rebooting.
- The refused direction is enforced in the page, during the event:
  - host to guest refused, or stale host text: a paste into a
    HackerOS text field receives the OS clipboard text instead
    of the host's (`insertText`), and files are not
    imported;
  - guest to host refused: a native copy does not modify the host's
    clipboard, and the bridge writes nothing.

### 5. No encryption

Internal data is not encrypted: the key would necessarily be in the
WASM, hence readable. Instead, it never leaves the OS's memory;
only the text goes to the host, and only if the setting allows it.

### 6. Minimal JavaScript

A `hostClipboard.js` module in the `wwwroot/` of
`HackerOs.Infrastructure.Browser`, imported by `BrowserHostClipboardChannel`
(contract `IHostClipboardChannel` in Simulation.Abstractions). No host
references it. The logic (origins, direction, sequence, session) stays in C# in
`HostClipboardBridge`, testable without a browser; the module keeps only the state
needed to decide synchronously during the event.

## Consequences

- Text copied by any app (Diagnostic, explorer, editor)
  reaches the real machine, and a paste from the real machine is visible
  through `HackClipboard.GetTextAsync`.
- A native copy in a HackerOS field also becomes the current entry,
  as on a real desktop.
- On the first paste after startup, the OS does not yet know the host's
  content: a never-seen text is taken as new. With `bidirectional`,
  this has no visible effect (the OS already wrote its text to the host); with
  `host-to-guest`, this first paste can replace a file list
  cut in the OS. This is the "last change wins" behavior of
  VirtualBox.
- Diagnostic declares `clipboard.write` (normal capability).
- `BrowserClipboard` remains for one version, `[Obsolete]`, then will be removed.

## Rejected alternatives

- **Reading the host through `navigator.clipboard.readText`** on each paste or at
  intervals: requires permission (Firefox, Safari), and background reading
  would reveal to the OS what the user copies elsewhere without any gesture
  on their part.
- **Encrypting the clipboard in memory**: no real protection, the key
  being in the same process.
- **Keeping `BrowserClipboard` as the official output**: two clipboards,
  and a `sharing` setting that any app can bypass.
- **Deciding everything in C#**: a paste or a copy can only be modified
  during their dispatch, synchronously; a .NET round trip arrives
  too late.

## References

- ADR 0019 (SDK versioning), ADR 0044 (settings in files, `[locked]`),
  ADR 0055 (SDK boundary), ADR 0056 §12, ADR 0058 §1, ADR 0060 §5.
- [`clipboard.md`](../clipboard.md), [`file-explorer.md`](../file-explorer.md).
