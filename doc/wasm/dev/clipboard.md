# Presse-papiers de l'OS et partage avec l'hôte

## Objet

Mise en œuvre de l'[ADR 0065](adr/0065-os-clipboard-and-host-bridge.md), qui
complète l'ADR 0056 §12. HackerOS a **un seul presse-papiers**, tenu par l'OS
et partagé par toutes les apps de la session ; un pont recopie son texte vers
le presse-papiers de la vraie machine et y reprend ce que l'utilisateur colle,
comme les Guest Additions de VirtualBox. L'usage du presse-papiers dans
l'explorateur (couper, copier, coller, conflits de noms) est décrit dans
[`file-explorer.md`](file-explorer.md).

## Architecture

```text
App ──► HackClipboard ──► IAppClipboardGateway ──► AppClipboardGateway ──► SystemClipboard (une entrée par session)
                                                       clipboard.read / .write          │ Changed, séquence, origine
                                                                                          ▼
              /etc/hackeros/clipboard.config ──► HostClipboardBridge (Platform Core)
                                                       │ IHostClipboardChannel / IHostClipboardSink
                                                       ▼
                                   BrowserHostClipboardChannel ──► hostClipboard.js ──► navigator.clipboard.writeText
                                   (Infrastructure.Browser)        paste / copy / cut ◄── vraie machine
```

| Pièce | Où | Rôle |
|---|---|---|
| `SystemClipboard`, `SystemClipboardEntry`, `SystemClipboardOrigin` | `Platform/HackerOs.Platform.Core/Clipboard/` | Le presse-papiers : entrée multi-formats par session, numéro de séquence, origine (`Guest`, `GuestNative`, `Host`), vidé à la déconnexion. En mémoire seulement. |
| `ClipboardSettingsDocuments`, `ClipboardSharingMode` | idem | Document `clipboard` (clé `sharing`) et sens permis. |
| `HostClipboardBridge` | idem | Recopie le texte vers l'hôte, applique les collages de l'hôte et les copies natives, relit le réglage à chaud. Toute la logique testable. |
| `IHostClipboardChannel`, `IHostClipboardSink` | `Shared/HackerOs.Simulation.Abstractions/Clipboard/` | Contrat entre le pont et la page. |
| `BrowserHostClipboardChannel` | `Infrastructure/HackerOs.Infrastructure.Browser/Interop/` | Importe `hostClipboard.js` et relaie ses rappels. |
| `hostClipboard.js` | `Infrastructure/HackerOs.Infrastructure.Browser/wwwroot/` | Écoute `paste` (capture, document), `copy` et `cut` (bulle, window) ; appelle `writeText`. |
| `FileView.attachPasteTarget` | `Apps/System/HackerOs.Apps.FileExplorer/FileControl/FileView.razor.js` | Ctrl+V par l'événement `paste` ; fichiers de l'hôte importés comme un dépôt. |

Le pont est enregistré par `AddHackerOsEcosystem` (portée par circuit, comme
tout ce qui dépend d'`IJSRuntime`) et démarré par `App.razor` après le
chargement des préférences ; un échec n'empêche jamais le démarrage.

## Usage et API

### Apps

Rien de nouveau : `HackClipboard` reste la seule porte (voir
[`file-explorer.md`](file-explorer.md#presse-papiers-hackclipboard)).

```csharp
await HackClipboard.SetTextAsync(json);                  // arrive aussi sur la vraie machine
string? text = await HackClipboard.GetTextAsync();       // y compris un texte collé depuis la vraie machine
```

Une app ne parle jamais à `navigator.clipboard`. `BrowserClipboard`
(`HackerOs.AppSdk.Blazor`), qui contournait le réglage, a été retiré au gel du
SDK 1.0 (ADR 0114 §3).

### OS

```csharp
clipboard.SetTargets(session, new Dictionary<string, string>
{
    ["text/plain"] = "bonjour",
    ["text/html"] = "<b>bonjour</b>"
}, SystemClipboardOrigin.Guest);
long sequence = clipboard.GetSequenceNumber(session);   // +1 à chaque changement, effacement compris
SystemClipboardEntry? entry = clipboard.GetEntry(session);
```

### Réglage

`/etc/hackeros/clipboard.config` (installé commenté au premier démarrage) :

```ini
sharing=bidirectional
```

| Valeur | Invité → hôte | Hôte → invité |
|---|---|---|
| `bidirectional` (défaut) | oui | oui |
| `guest-to-host` | oui | non |
| `host-to-guest` | non | oui |
| `disabled` | non | non |

Un utilisateur surcharge dans `~/.config/hackeros/clipboard.config`, sauf si
l'administrateur verrouille :

```ini
sharing=disabled
[locked]
sharing=true
```

## Règles

| Cas | Comportement |
|---|---|
| Une app copie du texte ou des fichiers | L'entrée devient courante ; son `text/plain` (les chemins pour des fichiers) part vers l'hôte si `sharing` le permet. Les autres formats ne sortent jamais. |
| Collage avec un texte nouveau pour l'OS | Le texte devient l'entrée courante (origine `Host`), sans écho vers l'hôte. |
| Collage du texte que l'OS a lui-même écrit | Rien ne change ; le collage natif suit son cours. |
| Collage de fichiers de l'hôte dans l'explorateur | Noms notés sous `application/x-hackeros-host-files` ; la `FileView` les importe dans le dossier courant (noms de copie si pris, 256 Mio par fichier). Un nouveau Ctrl+V les réimporte tant que le presse-papiers n'a pas changé côté OS. |
| Hôte → invité refusé, ou texte de l'hôte périmé, collage dans un champ | Le champ reçoit le texte de l'OS (ou rien), jamais celui de l'hôte ; le terminal aussi. |
| Copie native (champ de texte, terminal) | Devient l'entrée courante (origine `GuestNative`). Si invité → hôte est refusé, l'hôte n'est pas modifié. |
| Navigateur qui refuse `writeText` | Journal `clipboard: host clipboard call failed` (Debug) ; l'OS garde son entrée. |
| Déconnexion | L'entrée est effacée et la page oublie le texte. |

## Décisions clés

Voir l'[ADR 0065](adr/0065-os-clipboard-and-host-bridge.md) : un seul
presse-papiers propriétaire, pas de `readText` (pas d'autorisation), pas de
chiffrement (clé forcément dans le WASM), sens décidé dans la page pendant
l'événement, fichier `clipboard.config` plutôt que `clipboard.conf` pour suivre
la convention de l'ADR 0044.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Clipboard/SystemClipboardTests.cs` :
  séquence, formats multiples, origine, copie de l'entrée, effacement.
- `Tests/HackerOs.Platform.Core.Tests/Clipboard/ClipboardSettingsDocumentsTests.cs` :
  valeurs de `sharing`, fichier installé, surcharge utilisateur, `[locked]`.
- `Tests/HackerOs.Platform.Core.Tests/Clipboard/HostClipboardBridgeTests.cs` :
  écriture vers l'hôte, liste de fichiers en chemins, collage sans écho,
  fichiers de l'hôte, copie native, chaque mode, verrou, changement à chaud,
  déconnexion, refus du navigateur, absence de session.
- `Tests/HackerOs.Apps.FileExplorer.FileControl.Tests/FileViewDropAndClipboardTests.cs` :
  Ctrl+V par l'événement `paste`, fichiers de l'hôte collés.

## Liste des tâches

- [x] ADR 0065 (0060 était pris).
- [x] `SystemClipboard` : entrée multi-formats, numéro de séquence, origine.
- [x] Document `clipboard.config` (clé `sharing`, `[locked]`), installé au premier démarrage, listé par `OsSettingsDocumentRegistry`.
- [x] `HostClipboardBridge` et contrat `IHostClipboardChannel`.
- [x] `BrowserHostClipboardChannel` et `hostClipboard.js` dans Infrastructure.Browser.
- [x] Enregistrement dans `AddHackerOsEcosystem`, démarrage dans `App.razor`.
- [x] `FileView` : Ctrl+V par `paste`, fichiers de l'hôte collés importés comme un dépôt.
- [x] Diagnostic copie par `HackClipboard` (`clipboard.write`) ; `BrowserClipboard` `[Obsolete]`, puis retiré en 1.0 (ADR 0114).
- [x] Tests et documentation.
