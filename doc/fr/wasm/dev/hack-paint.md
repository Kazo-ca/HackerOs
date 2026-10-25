# Hack Paint

App de dessin `org.hackeros.hack-paint` (`Apps/System/HackerOs.Apps.HackPaint`,
désinstallable). Chantier `v3/11-apps`,
[ADR 0103](adr/0103-hack-paint-image-files-and-theme-editor.md) §1 : les images
vivent dans les fichiers de l'OS.

## Objet

Dessiner (pinceau, couleur, taille, zoom, grille, déplacement, recadrage,
rotation, annuler et rétablir) et **ouvrir, enregistrer, enregistrer sous** des
images du système de fichiers simulé.

## Formats

| Action | Formats |
|---|---|
| Ouvrir | PNG, JPEG, BMP, GIF (première image), WebP |
| Enregistrer | PNG par défaut ; JPEG quand le nom finit par `.jpg`/`.jpeg` (qualité 10 à 100, 92 par défaut, image aplatie sur du blanc) |

- Taille maximale : 4 096 × 4 096 pixels et 16 Mio par fichier (ADR 0094).
  L'en-tête est lu en C# (`ImageFileFormat.TryReadHeader`) **avant** tout
  décodage : un fichier trop grand, endommagé ou d'un autre format donne un
  message, sans figer l'app.
- Le fichier enregistré porte son type de média (`image/png`, `image/jpeg`) et
  est créé en `0644`.

## Utilisation

- Barre d'outils : *New*, *Open…*, *Save*, *Save As…*, qualité JPEG ; erreurs
  dans un bandeau, chemin du fichier dans la barre d'état.
- Raccourcis : Ctrl+N, Ctrl+O, Ctrl+S, Ctrl+Maj+S, Ctrl+Z, Ctrl+Y (le module
  JS empêche le navigateur de les prendre).
- Titre de la fenêtre : `nom.png — Hack Paint`, précédé de `*` quand l'image a
  des changements non enregistrés, posé par `SetWindowTitleAsync` (ADR 0117 §4)
  et affiché par le gestionnaire de fenêtres (barre de titre, barre des tâches,
  `` Alt+` ``). Il n'est plus répété dans l'app ; « ouverture… /
  enregistrement… » s'affiche dans la barre d'état.
- Fermer avec des changements non enregistrés demande confirmation
  (`IWindowCloseGuard`, boîte Oui/Non).
- Ouverture par l'explorateur : Hack Paint déclare `FileHandlers` pour les cinq
  types (actions `open` et `edit`) et `file-associations.json` en fait le
  gestionnaire par défaut de `edit` pour `.png .jpg .jpeg .bmp .gif .webp`
  (ADR 0096). « Ouvrir avec » et un chemin passé au lancement ouvrent aussi le
  fichier.
- Fenêtre 960 × 680, minimum 600 × 420 (`AppDefinition.Window`, ADR 0095).

## Architecture

| Pièce | Rôle |
|---|---|
| `ImageFileFormat` | formats, limites, lecture de l'en-tête (PNG IHDR, GIF, BMP, WebP VP8/VP8L/VP8X, JPEG SOF), format d'enregistrement d'après le nom |
| `PaintFileStore` | lecture (`ReadAsync`, `Inspect`) et écriture (`WriteAsync`) par la passerelle de fichiers de l'app |
| `PaintCanvasState` | pixels RGBA, historique, `IsDirty`/`MarkSaved` |
| `HackPaintWindow.razor(.cs)` | fenêtre, dialogues du SDK (`OpenFileAsync`, `SaveFileAsync`), garde de fermeture |
| `HackPaintWindow.razor.js` | dessin sur le canvas ; `decodeImage` (`createImageBitmap` → RGBA, erreurs rendues comme codes `decode-failed` / `too-large` que le C# traduit) et `encodeImage` (`toBlob`) ; jamais d'accès aux fichiers |

Les octets sont lus et écrits en C# par `IAppFileSystemGateway`, comme
l'éditeur de texte : un fichier choisi dans un dialogue hors du dossier
personnel n'est accessible que par le jeton remis par le dialogue, que `HackFile`
ne sait pas porter. Le JS ne reçoit que des octets et rend des pixels.

### Calculs hors de la fenêtre (ADR 0140)

Hack Paint n'a pas de filtre d'image. Ses opérations sur toute l'image
(rotation, recadrage) restent dans la fenêtre plutôt que dans une tâche
`HackBackground` ([`background-jobs.md`](../framework/background-jobs.md)) : ce sont des
passes de copies sur le tampon, qui peut faire 64 Mio (4096 × 4096 en RGBA),
quatre fois ce qu'une entrée de tâche peut porter en JSON (16 Mio), et le
retour coûterait plus que la passe. Un futur filtre passera par une tâche et un
fichier temporaire.

## Langues (ADR 0104)

Barre d'outils, barre d'état, dialogues et erreurs viennent de
`Resources/Strings.resx` / `Strings.fr.resx`. `PaintFileStore` écrit ses
messages dans la langue du processus qui l'appelle
(`HackLocalization.GetLocalizer<Strings>()`) ; le module JavaScript ne renvoie
que des codes d'erreur. Le titre garde le nom de produit « Hack Paint » ; seul
« Untitled » devient « Sans titre ».

## Tâches

- [x] Lecture de l'en-tête et limites avant décodage.
- [x] Ouvrir (dialogue, explorateur, argument de lancement), décoder par le navigateur.
- [x] Enregistrer, Enregistrer sous, PNG et JPEG avec qualité.
- [x] Titre avec `*`, confirmation à la fermeture.
- [x] Titre posé dans la fenêtre par le SDK 1.1 (chantier `v3.1/3-window-title`, E2E
  `HackPaintWindowTitleTests`).
- [x] `FileHandlers` et association par défaut `edit` des images.
- [x] Tests (`HackerOs.Apps.HackPaint.Tests`) : en-têtes des cinq formats,
  fichiers tronqués, limites, format d'enregistrement, suivi des changements, titre.
- [ ] Scans axe : chantier `v3/13`.
