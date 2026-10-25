# Corbeille

Phase 3 du plan de refonte ([`ecosystem-rebuild-plan.md`](plans/ecosystem-rebuild-plan.md),
section 7.4), décidée par l'[ADR 0052](adr/0052-trash.md). Chantier
`rebuild/3-trash`.

## Objectif

Une suppression par `rm` ou par l'explorateur de fichiers devient réversible. La
corbeille suit la spécification « Trash » 1.0 de freedesktop.org, comme les
bureaux Linux : ce sont de simples fichiers du dossier personnel, donc
`ls ~/.local/share/Trash/files` fonctionne et les droits sont ceux des fichiers.

## Format sur disque

```
~/.local/share/Trash/          (0700, créé à la demande)
├── files/                     (0700) éléments déplacés tels quels
│   ├── rapport.txt
│   └── rapport.2.txt          second élément du même nom
└── info/                      (0700)
    ├── rapport.txt.trashinfo  (0600)
    └── rapport.2.txt.trashinfo
```

```ini
[Trash Info]
Path=/home/benoit/Documents/rapport%20final.txt
DeletionDate=2026-10-10T14:03:27
```

- `Path` : chemin absolu d'origine, encodé comme une URL segment par segment
  (`/` non encodé).
- `DeletionDate` : heure locale de l'horloge simulée (`IAppClockGateway.UtcNow`
  converti dans le fuseau local), à la seconde, sans décalage.
- Noms en collision : `nom.2.ext`, `nom.3.ext`… ; un fichier caché sans
  extension (`.bashrc`) devient `.bashrc.2`. Un nom est libre s'il n'existe ni
  dans `info/` ni dans `files/`.
- Le `.trashinfo` est créé **d'abord**, en création exclusive, pour réserver le
  nom ; puis l'élément est déplacé. Si le déplacement échoue, le `.trashinfo` est
  supprimé et l'erreur remonte.
- Au listage, un `.trashinfo` sans élément est supprimé ; un élément sans
  `.trashinfo` est listé avec une origine inconnue.
- Un élément supprimé sur un autre appareil arrive ici par la synchronisation
  (ADR 0101 §1, [`synchronization.md`](synchronization.md)) : son `.trashinfo`
  porte les clés d'extension `X-HackerOS-Origin=deleted-on-another-device` et
  `X-HackerOS-Device=<appareil>`, que les autres implémentations ignorent.
  `HackTrashItem.DeletedOnAnotherDevice` le dit aux apps. Mettre à la corbeille
  est un renommage synchronisé ; vider la corbeille envoie les suppressions aux
  autres appareils. Les éléments mis à la corbeille par la synchro restent sur
  cet appareil.

### Corbeille de l'utilisateur effectif

La corbeille est celle de l'utilisateur **effectif** : sous `sudo`, c'est
`/root/.local/share/Trash/`. Pour le savoir, `IAppExecutionContext` expose
`IsRootElevated` (membre par défaut `false`, rempli par
`AppExecutionContextFactory` à partir du lancement élevé de l'ADR 0042).
`HomeDirectory` ne change pas sous `sudo` (le `~` du shell reste celui de
l'utilisateur).

## Règles

| Cas | Comportement |
| --- | --- |
| Élément du système de fichiers principal | Déplacé dans `files/` (contenu, droits, propriétaire et dates conservés), si l'appelant a le droit de le supprimer (écriture sur le dossier parent, capacités). |
| `/proc`, `/dev` (montages virtuels) | Refus : `HackIOException` « Cannot move to trash: virtual file system ». Détecté par le `CrossMountOperation` du déplacement ; rien n'est laissé dans `info/`. |
| Élément déjà dans la corbeille | Supprimé définitivement. |
| `/`, le dossier personnel, un dossier qui contient la corbeille (`~/.local`, la corbeille elle-même) | Refus : `HackIOException`, code `ProtectedEntry`. |
| Lien symbolique | Le lien lui-même part à la corbeille, pas sa cible. |

Pas de limite de taille ni de purge automatique : la corbeille compte dans
l'espace disque comme n'importe quel fichier. Pas de cache `directorysizes`.

## API `HackTrash`

`Shared/HackerOs.AppSdk/IO/HackTrash.cs`. Classe statique, contexte ambiant de
l'ADR 0043 (`HackExecutionContext.Current`), exceptions `Hack*`. Aucune
nouvelle capacité : les capacités `filesystem.*` et les droits décident, comme
pour `HackFile`.

```csharp
HackTrashItem item = await HackTrash.MoveToTrashAsync("Documents/rapport.txt");
IReadOnlyList<HackTrashItem> items = await HackTrash.GetItemsAsync();   // plus ancien d'abord
string restoredTo = await HackTrash.RestoreAsync(item);                 // ou RestoreAsync(item, "~/ailleurs.txt")
await HackTrash.DeleteAsync(item);                                      // suppression définitive d'un élément
await HackTrash.EmptyAsync();                                           // vide tout
string root = HackTrash.TrashDirectory;                                 // ~/.local/share/Trash ou /root/...
```

| Membre | Détail |
| --- | --- |
| `MoveToTrashAsync(path)` | Chemin manquant → `HackFileNotFoundException`. Renvoie l'élément créé (`Name` = nom dans `files/`). |
| `GetItemsAsync()` | Trié par date de suppression puis par nom ; vide si la corbeille n'existe pas. |
| `RestoreAsync(item, destination?)` | Destination, sinon le `Path` lu dans le `.trashinfo` (puis `item.OriginalPath`). Recrée les dossiers parents manquants. Destination occupée → `HackIOException` « already exists », rien n'est écrasé. Origine inconnue sans destination → `HackIOException`. Supprime le `.trashinfo`. |
| `DeleteAsync(item)` | Supprime l'élément et son `.trashinfo` ; ni l'un ni l'autre → `HackFileNotFoundException`. |
| `EmptyAsync()` | Supprime tout le contenu de `files/` et `info/` (les dossiers restent). |
| `TrashDirectory` | Chemin canonique de la corbeille de l'utilisateur effectif. |

`HackTrashItem(Name, OriginalPath, DeletedAt, IsDirectory, Length)` : `Name` est
l'identifiant stable ; `Length` est la taille du fichier ou la somme des
fichiers du dossier. Seul `Name` sert à retrouver l'élément.

Chaque mise à la corbeille, restauration et suppression définitive est écrite au
niveau `Information` dans le journal de l'app (`/var/log/apps/<appId>.log`,
ADR 0045).

`HackFile.DeleteAsync` et `HackDirectory.DeleteAsync` restent des suppressions
définitives, comme `System.IO`.

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
