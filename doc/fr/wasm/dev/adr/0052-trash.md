> [English version](../../../../en/wasm/dev/adr/0052-trash.md)

# ADR 0052 : Corbeille

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 3 — Shell, commandes et corbeille
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 7.4,
  décision D15, point de détail de la section 11 sur `rm`).
- **Complète :** ADR 0043 : `HackFile.DeleteAsync` et
  `HackDirectory.DeleteAsync` restent des suppressions définitives, comme
  `System.IO` ; la corbeille est une API à part, `HackTrash`.
- **S'appuie sur :** ADR 0051 (`rm` et la nouvelle commande `trash` vivent dans
  `HackerOs.Commands`).

## Contexte

Aucune corbeille n'existe. `rm` supprime définitivement. L'explorateur
(`FileExplorerWindow.DeleteSelectedAsync`) appelle
`IAppFileSystemGateway.DeleteAsync` récursif sur la sélection, sans
confirmation ni retour possible. Le menu de `FileView` propose « Delete » avec
une icône de corbeille.

## Décision

### 1. Format : spécification freedesktop.org

La corbeille d'un utilisateur est `$HOME/.local/share/Trash/`, avec la
structure de la spécification « Trash » de freedesktop.org (version 1.0) :

- `files/<nom>` : l'élément supprimé, fichier ou dossier entier, déplacé tel
  quel (contenu, droits, propriétaire et dates conservés).
- `info/<nom>.trashinfo` : un fichier texte par élément :

  ```ini
  [Trash Info]
  Path=/home/benoit/Documents/rapport%20final.txt
  DeletionDate=2026-10-10T14:03:27
  ```

  `Path` est le chemin absolu d'origine, encodé comme une URL (RFC 2396,
  `/` non encodé). `DeletionDate` est l'heure locale de l'horloge simulée, au
  format `YYYY-MM-DDThh:mm:ss`.
- Les dossiers sont créés à la demande avec les droits `0700`.
- Pas de cache `directorysizes` dans cette phase.

**Noms.** `<nom>` est le nom d'origine. S'il est déjà pris dans `info/`, on
essaie `nom.2.ext`, `nom.3.ext`… (pour `rapport.txt` : `rapport.2.txt`). Comme
le demande la spécification, le fichier `.trashinfo` est créé **d'abord**, en
création exclusive, pour réserver le nom ; puis l'élément est déplacé dans
`files/`. Si le déplacement échoue, le `.trashinfo` est supprimé et l'erreur
remonte. Au listage, un `.trashinfo` sans élément dans `files/` est ignoré et
nettoyé ; un élément de `files/` sans `.trashinfo` est listé avec une origine
inconnue.

### 2. Ce qui peut aller à la corbeille

- Les éléments du système de fichiers principal (IndexedDB), quel que soit
  leur dossier, dès que l'appelant a le droit de les supprimer (écriture sur
  le dossier parent, droits du fichier et capacités de l'app). Ils vont dans
  la corbeille de l'**utilisateur effectif** : `sudo rm /etc/x` envoie dans
  `/root/.local/share/Trash/`.
- Les montages virtuels (`/proc`, `/dev`) refusent : `HackIOException`
  « Cannot move to trash: virtual file system ».
- Un élément déjà dans la corbeille (`~/.local/share/Trash/...`) est supprimé
  définitivement au lieu d'être déplacé.
- Le dossier personnel lui-même, `/` et la corbeille elle-même sont refusés.
- Pas de limite de taille ni de purge automatique dans cette phase. La
  corbeille compte dans l'espace disque comme n'importe quel fichier.

### 3. API `HackTrash` dans le SDK

`Shared/HackerOs.AppSdk/IO/HackTrash.cs`, statique, asynchrone, contexte
ambiant de l'ADR 0043, mêmes exceptions `Hack*` :

```csharp
public static class HackTrash
{
    public static Task<HackTrashItem> MoveToTrashAsync(string path, CancellationToken cancellationToken = default);
    public static Task<IReadOnlyList<HackTrashItem>> GetItemsAsync(CancellationToken cancellationToken = default);
    public static Task<string> RestoreAsync(HackTrashItem item, string? destinationPath = null, CancellationToken cancellationToken = default);
    public static Task DeleteAsync(HackTrashItem item, CancellationToken cancellationToken = default);
    public static Task EmptyAsync(CancellationToken cancellationToken = default);
}

public sealed record HackTrashItem(
    string Name,                 // nom dans files/ (identifiant stable)
    string? OriginalPath,        // null si le .trashinfo manque
    DateTimeOffset? DeletedAt,
    bool IsDirectory,
    long Length);                // taille du fichier, ou somme pour un dossier
```

- `MoveToTrashAsync` : chemin manquant → `HackFileNotFoundException`.
- `RestoreAsync` : remet l'élément à `destinationPath`, sinon à son origine ;
  recrée les dossiers parents manquants ; un élément déjà présent à la
  destination → `HackIOException` (« already exists »), sans rien écraser.
  Renvoie le chemin restauré et supprime le `.trashinfo`.
- `DeleteAsync` supprime définitivement un élément ; `EmptyAsync` vide tout.
- **Aucune nouvelle capacité :** la corbeille est un ensemble de fichiers du
  dossier personnel. Les capacités `filesystem.*` existantes et les droits
  des fichiers décident, comme pour `HackFile`.
- Chaque mise à la corbeille, restauration et suppression définitive est
  écrite au niveau `Info` dans le journal de l'app (ADR 0045).

### 4. `rm` et la commande `trash`

- `rm fichier` : met à la corbeille. Un dossier demande `-r` (`rm: cannot
  remove 'd': Is a directory`), puis part **entier** à la corbeille. `-f`
  ignore les éléments manquants. Sortie silencieuse, comme `rm`.
- `rm --permanent` : suppression définitive, comportement actuel.
- `rm` sur `/proc` ou `/dev` affiche l'erreur de la section 2.
- Nouvelle commande `trash` (même projet, app système), inspirée de
  `trash-cli` :
  - `trash put <fichier…>` : comme `rm` ;
  - `trash list` : `DeletionDate  Path`, trié par date ;
  - `trash restore <nom|chemin d'origine> [destination]` : quand le chemin
    d'origine désigne plusieurs éléments, le plus récent ;
  - `trash rm <nom>` : suppression définitive d'un élément ;
  - `trash empty` : vide la corbeille.

### 5. Explorateur de fichiers

- « Delete » et la touche Suppr mettent la sélection à la corbeille, sans
  confirmation (l'action est réversible).
- Maj+Suppr et une entrée « Delete permanently » suppriment définitivement,
  après une confirmation.
- Une entrée « Trash » dans le panneau latéral ouvre une vue de la
  corbeille : colonnes nom, emplacement d'origine, date de suppression,
  taille ; actions « Restore », « Delete permanently » et « Empty trash »
  (avec confirmation). Cette vue lit `HackTrash.GetItemsAsync`, pas le
  dossier brut.
- Couper, copier, coller et glisser-déposer vers la corbeille restent en
  phase 4 (section 8.2 du plan).

## Conséquences

- Une suppression par `rm` ou l'explorateur devient réversible, avec le même
  format que les bureaux Linux.
- La corbeille est de simples fichiers : `ls ~/.local/share/Trash/files`
  fonctionne, et ses droits sont ceux du dossier personnel.
- Les scripts ou apps qui comptaient sur `rm` pour libérer l'espace doivent
  utiliser `rm --permanent` ou vider la corbeille.

## Chantiers d'implémentation de la phase 3

Tableau commun aux ADR 0050, 0051 et 0052. Chaque branche part de
`rebuild/base` et sa PR vise `rebuild/base`.

| Branche | Contenu | Dépend de |
|---|---|---|
| `rebuild/3-commands-merge` | ADR 0051 section 1 : projet `HackerOs.Commands`, déplacement des 28 commandes, `manifests/<appId>.json`, une `ProjectReference` dans l'hôte, solution et projets de tests mis à jour, anciens projets supprimés. Aucun changement de comportement. | — |
| `rebuild/3-process-environment` | ADR 0050 sections 5 et 6 : `IProcessEnvironmentTable`, arguments, répertoire et environnement à la création (héritage du parent), environnement de connexion et `/etc/environment`, `/etc/hostname`, `/proc/<pid>/cmdline`, `environ`, `cwd`, `HackEnvironment`, contexte ambiant relié au répertoire du processus. | — |
| `rebuild/3-shell-interpreter` | ADR 0050 sections 1 à 4 et 7 : `ShellParser` (arbre), expansion, `ShellPipe`, redirections, `&&`, `ShellInterpreter` et `ShellSession` dans Platform Core, flux sur `AppLaunchRequest`, sortie au fil de l'eau dans le terminal, `ExecuteCommandIntent` par l'interpréteur. Les noms codés en dur du terminal restent provisoirement. | `3-process-environment` |
| `rebuild/3-trash` | ADR 0052 : `HackTrash`, `rm` vers la corbeille et `--permanent`, commande `trash`, vue Corbeille et Maj+Suppr dans l'explorateur. | `3-commands-merge` |
| `rebuild/3-shell-commands` | ADR 0051 sections 2 à 5 : `IShellSession`, capacité `shell.control` (classée normale), commandes `cd`, `pwd`, `clear`, `help`, `export`, `unset`, `env`, `sudo` du catalogue, séquences d'effacement dans le terminal, retrait de tous les noms codés en dur, complétion depuis le catalogue, alias statiques de `alias`. | `3-shell-interpreter`, `3-commands-merge` |

Ordre : `3-commands-merge` et `3-process-environment` en parallèle ; puis
`3-trash` (dès que `3-commands-merge` est mergé) en parallèle avec
`3-shell-interpreter` ; enfin `3-shell-commands`.

## Références

- [Trash specification 1.0](https://specifications.freedesktop.org/trash-spec/1.0/),
  freedesktop.org.
- ADR 0043 (API `Hack*`), ADR 0045 (journaux), ADR 0050, ADR 0051.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 7.4 et 11.
- `Apps/System/HackerOs.Apps.FileExplorer/FileExplorerWindow.razor`,
  `Apps/Commands/HackerOs.Commands.Rm/RmCommand.cs`,
  `Shared/HackerOs.AppSdk/IO/HackFile.cs`.
