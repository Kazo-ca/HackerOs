> [Version française](../../../fr/wasm/framework/clipboard-api.md)

# Clipboard: API for apps

This document describes the clipboard as an app sees it: `KazoClipboard` (text and file lists), the file list format, and what goes out to the real machine. The OS shared clipboard, the bridge to the host and the `clipboard.config` setting are in [`dev/clipboard.md`](../dev/clipboard.md).

## Apps

Nothing new: `KazoClipboard` remains the only door (see
the next section).

```csharp
await KazoClipboard.SetTextAsync(json);                  // arrive aussi sur la vraie machine
string? text = await KazoClipboard.GetTextAsync();       // y compris un texte collé depuis la vraie machine
```

An app never talks to `navigator.clipboard`. `BrowserClipboard`
(`Kazo.Os.AppSdk.Blazor`), which bypassed the setting, was removed at the SDK 1.0
freeze (ADR 0114 §3).

## Clipboard (`KazoClipboard`)

```csharp
await KazoClipboard.SetFileDropListAsync(["~/rapport.txt", "Documents"], KazoClipboardFileOperation.Cut);
KazoClipboardFileList? files = await KazoClipboard.GetFileDropListAsync();   // Operation + Paths absolus
bool canPaste = await KazoClipboard.ContainsFileDropListAsync();
string? text = await KazoClipboard.GetTextAsync();   // une liste de fichiers se lit comme ses chemins
await KazoClipboard.SetTextAsync("bonjour");
await KazoClipboard.ClearAsync();
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
