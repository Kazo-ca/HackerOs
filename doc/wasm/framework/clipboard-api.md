# Presse-papiers : API pour les apps

Ce document décrit le presse-papiers tel qu'une app le voit : `HackClipboard` (texte et listes de fichiers), le format de liste de fichiers et ce qui sort vers la vraie machine. Le presse-papiers partagé de l'OS, le pont vers l'hôte et le réglage `clipboard.config` sont dans [`dev/clipboard.md`](../dev/clipboard.md).

## Apps

Rien de nouveau : `HackClipboard` reste la seule porte (voir
la section suivante).

```csharp
await HackClipboard.SetTextAsync(json);                  // arrive aussi sur la vraie machine
string? text = await HackClipboard.GetTextAsync();       // y compris un texte collé depuis la vraie machine
```

Une app ne parle jamais à `navigator.clipboard`. `BrowserClipboard`
(`HackerOs.AppSdk.Blazor`), qui contournait le réglage, a été retiré au gel du
SDK 1.0 (ADR 0114 §3).

## Presse-papiers (`HackClipboard`)

```csharp
await HackClipboard.SetFileDropListAsync(["~/rapport.txt", "Documents"], HackClipboardFileOperation.Cut);
HackClipboardFileList? files = await HackClipboard.GetFileDropListAsync();   // Operation + Paths absolus
bool canPaste = await HackClipboard.ContainsFileDropListAsync();
string? text = await HackClipboard.GetTextAsync();   // une liste de fichiers se lit comme ses chemins
await HackClipboard.SetTextAsync("bonjour");
await HackClipboard.ClearAsync();
AppContext.Clipboard.Changed += OnClipboardChanged;   // une autre app a coupé ou copié
```

Contenu de la cible `x-special/gnome-copied-files`, comme GNOME Files :

```text
cut
file:///home/benoit/rapport.txt
file:///home/benoit/Documents
```

`IAppClipboardGateway.GetFileReferencesAsync` et `SetFileReferencesAsync`, anciennes
formes de « copier » marquées `[Obsolete]` par l'ADR 0056 §12, sont retirées au gel
du SDK 1.0 (ADR 0114 §3).

## Règles pour les apps

Quand une app copie du texte ou des fichiers, l'entrée devient courante pour l'OS ; son `text/plain` (les chemins pour des fichiers) part vers la vraie machine si `sharing` le permet. Les autres formats ne sortent jamais. Le réglage et les autres cas sont dans [`dev/clipboard.md`](../dev/clipboard.md).
