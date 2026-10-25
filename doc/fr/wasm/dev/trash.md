> [English version](../../../en/wasm/dev/trash.md)

# Corbeille

L'API `HackTrash` et le format `.trashinfo` destinés aux apps sont dans [`trash-api.md`](../framework/trash-api.md).

Phase 3 du plan de refonte ([`ecosystem-rebuild-plan.md`](plans/ecosystem-rebuild-plan.md),
section 7.4), décidée par l'[ADR 0052](adr/0052-trash.md). Chantier
`rebuild/3-trash`.

## Objectif

Une suppression par `rm` ou par l'explorateur de fichiers devient réversible. La
corbeille suit la spécification « Trash » 1.0 de freedesktop.org, comme les
bureaux Linux : ce sont de simples fichiers du dossier personnel, donc
`ls ~/.local/share/Trash/files` fonctionne et les droits sont ceux des fichiers.

## Format sur disque

- Le `.trashinfo` est créé **d'abord**, en création exclusive, pour réserver le
  nom ; puis l'élément est déplacé. Si le déplacement échoue, le `.trashinfo` est
  supprimé et l'erreur remonte.
- Un élément supprimé sur un autre appareil arrive ici par la synchronisation
  (ADR 0101 §1, [`synchronization.md`](synchronization.md)) : son `.trashinfo`
  porte les clés d'extension `X-HackerOS-Origin=deleted-on-another-device` et
  `X-HackerOS-Device=<appareil>`, que les autres implémentations ignorent.
  `HackTrashItem.DeletedOnAnotherDevice` le dit aux apps. Mettre à la corbeille
  est un renommage synchronisé ; vider la corbeille envoie les suppressions aux
  autres appareils. Les éléments mis à la corbeille par la synchro restent sur
  cet appareil.

## Règles

Les montages virtuels (`/proc`, `/dev`) sont détectés par le `CrossMountOperation` du déplacement ; rien n'est laissé dans `info/`.

Pas de limite de taille ni de purge automatique : la corbeille compte dans
l'espace disque comme n'importe quel fichier. Pas de cache `directorysizes`.

## Code du format `.trashinfo`

Le format `.trashinfo` (lecture tolérante, écriture, noms `nom.N.ext`) est dans
`HackTrashInfo.cs` (interne au SDK).

## Commandes

Dans `Apps/Commands/HackerOs.Commands` (ADR 0051).

### `rm`

`rm [-r|-R] [-f] [--permanent] <fichier…>` (`Rm/RmCommand.cs`) :

- Par défaut, met à la corbeille. Sortie silencieuse.
- Un dossier demande `-r` (`rm: cannot remove 'd': Is a directory`), puis part
  entier à la corbeille.
- `-f` ignore les éléments manquants (et l'absence d'opérande) ; les autres
  erreurs restent affichées.
- `--permanent` : suppression définitive, le comportement d'avant.
- Options courtes groupées (`-rf`), `--` termine les options, option inconnue →
  `rm: invalid option -- 'x'`.
- `/proc` ou `/dev` : `rm: cannot remove '/proc/x': Cannot move to trash: virtual file system`.
- `sudo rm /etc/x` envoie dans `/root/.local/share/Trash/`.

### `trash`

Nouvelle commande système (`Trash/TrashCommand.cs`, id
`org.hackeros.commands.trash`), inspirée de `trash-cli` :

| Sous-commande | Effet |
| --- | --- |
| `trash put [-f] <fichier…>` | Comme `rm`, mais un dossier n'a pas besoin de `-r` (comme `trash-put`). `--permanent` est refusé. |
| `trash list` | Une ligne par élément : `AAAA-MM-JJ hh:mm:ss  /chemin/d/origine`, plus ancien d'abord. Quand le nom dans la corbeille diffère du nom d'origine, il est ajouté entre crochets (`[rapport.2.txt]`) pour servir à `trash rm`. Un élément supprimé sur un autre appareil finit par `(deleted on another device)`. |
| `trash restore <nom\|chemin d'origine> [destination]` | Cherche d'abord par nom, puis par chemin d'origine ; si plusieurs éléments viennent du même chemin, le plus récent. |
| `trash rm <nom…>` | Suppression définitive d'éléments, par nom. |
| `trash empty` | Vide la corbeille. |

Sans argument, `trash` affiche l'aide sur la sortie d'erreur et renvoie 1.

## Explorateur de fichiers

- **FileView** (`Shared/HackerOs.AppSdk.FileView` à l'époque, dans l'explorateur depuis le SDK 2.0, `FileControl/`) reçoit un paramètre
  `DeleteRequested` (`EventCallback<FileViewDeleteRequestedEventArgs>`). Quand
  l'hôte le fournit, la touche Suppr, Maj+Suppr et le menu contextuel lui
  confient la suppression (`Items`, `Permanent`) au lieu de supprimer eux-mêmes ;
  le menu contextuel ajoute alors « Delete permanently ». L'événement
  `Deleting` (annulable) est toujours levé avant. Sans `DeleteRequested`,
  FileView garde l'ancien comportement (suppression définitive), par exemple dans
  l'éditeur de code.
- **FileExplorerWindow** branche `DeleteRequested` :
  - « Delete » (barre d'outils, menu, Suppr) met la sélection à la corbeille,
    sans confirmation ;
  - Maj+Suppr et « Delete permanently » demandent une confirmation (Yes/No),
    puis suppriment avec `HackFile`/`HackDirectory.DeleteAsync` ;
  - les erreurs (droits, `/proc`) s'affichent dans la bande d'erreur.
- Un panneau latéral « Places » propose **Home** et **Trash**. « Trash » affiche
  `TrashView` (`Apps/System/HackerOs.Apps.FileExplorer/TrashView.razor`) à la place
  de la liste ; FileView reste monté (caché) et retrouve son dossier au retour.
- **TrashView** (dérive de `HackComponentBase`, donc dans le contexte ambiant de
  l'explorateur) lit `HackTrash.GetItemsAsync` — jamais le dossier brut — et
  affiche nom, emplacement d'origine, date de suppression et taille. Actions :
  « Restore » (ou double-clic, Entrée), « Delete permanently » (ou Suppr) et
  « Empty trash », ces deux dernières après confirmation. Ctrl+clic pour une
  sélection multiple. Après une restauration, l'explorateur rafraîchit FileView.
- **Dépôt sur la corbeille** (phase 4, `rebuild/4-file-explorer`) : glisser des
  éléments sur l'entrée « Trash » du panneau Places ou sur la vue Corbeille les
  envoie à la corbeille (`FileTransfer.MoveToTrashAsync`, aujourd'hui `HackFileOperations.MoveToTrashAsync`). Voir
  [`file-explorer.md`](file-explorer.md).

Correctif au passage : `Shared/HackerOs.AppSdk.FileView/_Imports.razor` importe
désormais `Microsoft.AspNetCore.Components.Web`. Sans lui, `FileView.razor`
émettait `@oncontextmenu` comme un attribut HTML littéral et l'ouverture de
l'explorateur affichait « An unhandled error has occurred » (vu au test dans le
navigateur).

Couper, copier, coller et glisser-déposer vers la corbeille restent en phase 4
(section 8.2 du plan).

## Tests

- `Tests/HackerOs.AppSdk.Tests/IO/HackTrashTests.cs` : format `.trashinfo`, droits
  `0700`/`0600`, noms en collision, dossiers entiers et taille, refus (racine,
  dossier personnel, corbeille), élément déjà dans la corbeille, montage virtuel
  sans `.trashinfo` résiduel, `sudo` vers `/root`, listage (orphelins, origine
  inconnue, tri), restauration (parents recréés, destination, jamais
  d'écrasement), suppression, vidage et journal.
- `Tests/HackerOs.Commands.Wave5.Tests/RmCommandTests.cs` : `rm` (corbeille,
  `--permanent`, `-r`, `-f`, option inconnue, `/proc`, conflit de révision) et
  `trash` (put, list, restore par chemin et par nom, rm, empty, aide).
- `Tests/HackerOs.Apps.FileExplorer.FileControl.Tests/FileViewTests.cs` : `DeleteRequested`
  reçoit les éléments et le drapeau `Permanent`.
- `Tests/HackerOs.Ecosystem.Tests` : 41 apps intégrées, 29 commandes (45 et 33 depuis `3-shell-commands`).
- Doubles de test : `FakeAppLoggingGateway` (nouveau) dans
  `HackerOs.Tests.Support` ; `MinimalAppExecutionContext` accepte
  `isRootElevated`.

## Liste des tâches

- [x] `HackTrash`, `HackTrashItem` et le format `.trashinfo` (`HackTrashInfo`).
- [x] `IAppExecutionContext.IsRootElevated` pour la corbeille de l'utilisateur effectif.
- [x] `rm` vers la corbeille, `rm --permanent`, `-r` obligatoire pour un dossier, options groupées.
- [x] Commande `trash` (put, list, restore, rm, empty).
- [x] `FileView.DeleteRequested`, Maj+Suppr, entrée « Delete permanently ».
- [x] Explorateur : corbeille par défaut, confirmation pour le définitif, panneau « Places », `TrashView`.
- [x] Tests SDK, commandes, FileView et compteurs de l'écosystème.
- [x] Documentation (ce fichier, index, plan, `hack-file-api.md`).
