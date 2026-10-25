# Clipboard: API for apps

This document describes the clipboard as an app sees it: `HackClipboard` (text and file lists), the file list format, and what goes out to the real machine. The OS shared clipboard, the bridge to the host and the `clipboard.config` setting are in [`dev/clipboard.md`](../dev/clipboard.md).

## Apps

Nothing new: `HackClipboard` remains the only door (see
the next section).

```csharp
await HackClipboard.SetTextAsync(json);                  // arrive aussi sur la vraie machine
string? text = await HackClipboard.GetTextAsync();       // y compris un texte collé depuis la vraie machine
```

An app never talks to `navigator.clipboard`. `BrowserClipboard`
(`HackerOs.AppSdk.Blazor`), which bypassed the setting, was removed at the SDK 1.0
freeze (ADR 0114 §3).

## Clipboard (`HackClipboard`)

```csharp
await HackClipboard.SetFileDropListAsync(["~/rapport.txt", "Documents"], HackClipboardFileOperation.Cut);
HackClipboardFileList? files = await HackClipboard.GetFileDropListAsync();   // Operation + Paths absolus
bool canPaste = await HackClipboard.ContainsFileDropListAsync();
string? text = await HackClipboard.GetTextAsync();   // une liste de fichiers se lit comme ses chemins
await HackClipboard.SetTextAsync("bonjour");
await HackClipboard.ClearAsync();
AppContext.Clipboard.Changed += OnClipboardChanged;   // une autre app a coupé ou copié
```

Content of the `x-special/gnome-copied-files` target, like GNOME Files:

```text
cut
file:///home/benoit/rapport.txt
file:///home/benoit/Documents
```

`IAppClipboardGateway.GetFileReferencesAsync` and `SetFileReferencesAsync`, old
forms of "copy" marked `[Obsolete]` by ADR 0056 §12, are removed at the SDK 1.0
freeze (ADR 0114 §3).

## Rules for apps

When an app copies text or files, the entry becomes current for the OS; its `text/plain` (the paths for files) goes out to the real machine if `sharing` allows it. Other formats never go out. The setting and the other cases are in [`dev/clipboard.md`](../dev/clipboard.md).
