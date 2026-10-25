# Corbeille : format `.trashinfo` et API `HackTrash`

Ce document décrit ce qu'une app voit de la corbeille : l'API `HackTrash` du SDK, les règles qui s'appliquent à ses opérations, et le format `.trashinfo` que les apps et les autres implémentations lisent ou écrivent. La place de la corbeille dans l'OS, les commandes `rm` et `trash`, l'explorateur et la synchronisation sont dans [`dev/trash.md`](../dev/trash.md).

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

- Au listage, un `.trashinfo` sans élément est supprimé ; un élément sans
  `.trashinfo` est listé avec une origine inconnue.
- Un élément supprimé sur un autre appareil porte, dans son `.trashinfo`, les clés d'extension `X-HackerOS-Origin=deleted-on-another-device` et `X-HackerOS-Device=<appareil>`, que les autres implémentations ignorent. `HackTrashItem.DeletedOnAnotherDevice` le signale aux apps.

## Corbeille de l'utilisateur effectif

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

