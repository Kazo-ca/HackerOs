# ADR 0090 : recherche d'un chemin en une seule requête au stockage

## Statut

Accepté le 2026-10-17, avec le chantier `web/15-settings-perf`.

- **Complète :** le contrat des fournisseurs du système de fichiers
  (`IFileSystemProvider`) et le résolveur de chemins `FileSystemPathResolver`
  (documentés dans [`old/virtual-filesystem.md`](../old/virtual-filesystem.md)
  et [`old/indexeddb-filesystem.md`](../old/indexeddb-filesystem.md)).
  Ne remplace aucune décision : les permissions, les liens symboliques, les
  points de montage et les révisions restent appliqués exactement comme avant.

## Contexte

Écrire un réglage dans `~/.config/hackeros` prenait environ 2 secondes dans le
navigateur (constaté pendant la PR #133). Mesure dans Chromium, version Release
de l'hôte Ecosystem, clic sur « Mobile » dans le panneau de l'horloge (premier
enregistrement du choix, création du fichier comprise) :

| | Transactions IndexedDB | Clic → shell mobile affiché | Avec le processeur ralenti ×4 |
|---|---|---|---|
| Avant | 604 | 845 ms | 2 000 ms |
| Après | 51 | 235 ms | 1 010 ms |

Les réglages restent de vrais fichiers soumis aux permissions (ADR 0044). La
lenteur venait de la recherche des chemins :

1. `IndexedDbFileSystemReader.ResolveAsync` lisait chaque composant d'un chemin
   avec deux transactions (le lien `(parentId, nom)`, puis l'entrée), en
   repartant de `/`. Chaque transaction est un aller-retour C# → JavaScript
   → IndexedDB (environ 4 ms, 20 ms sur un appareil lent).
2. `FileSystemPathResolver` fait un `lstat` de **chaque préfixe** du chemin
   pour suivre les liens et vérifier le droit de traverser. Avec le point 1,
   résoudre `/home/<user>/.config/hackeros/<doc>.config` coûtait
   3 + 5 + 7 + 9 + 11 = 35 transactions, avant même l'autorisation et
   l'opération elle-même. Une écriture de réglage lit puis écrit plusieurs
   fichiers : plus de 600 transactions.

## Décision

### 1. Le stockage IndexedDB parcourt un chemin dans une seule transaction

`indexedDb.js` gagne l'opération `resolvePath` : à partir de l'entrée racine,
elle lit lien puis entrée pour chaque nom, **dans la même transaction en
lecture seule**, et renvoie la liste des entrées trouvées (arrêt avant le
premier nom absent). Un lien vers une entrée absente termine la liste par un
marqueur `{ danglingLink, entryId }`, que le C# signale comme corruption
(`InvalidDataException`, comme avant). C'est le `namei` d'un noyau : un seul
parcours, un instantané cohérent.

`IndexedDbFileSystemReader.ResolveAsync` s'en sert pour tout chemin autre que
`/` ; la nouvelle méthode `ResolveComponentsAsync` rend la liste complète.

### 2. Capacité facultative `IFileSystemPathWalkProvider`

Contrat ajouté dans `HackerOs.Simulation.Abstractions.FileSystem` :

```csharp
public interface IFileSystemPathWalkProvider
{
    ValueTask<FileSystemResult<IReadOnlyList<FileSystemEntrySnapshot>>> StatComponentsAsync(
        VirtualPath path, FileSystemAuthorizationContext context, CancellationToken cancellationToken = default);
}
```

Elle rend les métadonnées de chaque composant sans suivre de lien ; une liste
plus courte que le chemin veut dire que le composant suivant n'existe pas.
`IndexedDbFileSystemProvider` l'implémente avec l'opération du point 1. Les
autres fournisseurs (`/proc`, `/dev`, mémoire, montages de test) ne changent pas.

### 3. Le résolveur l'utilise quand tout le chemin est sur un même montage

Au début de chaque passe, si le chemin a au moins deux composants, que tous
ses préfixes appartiennent au même montage et que ce fournisseur implémente
`IFileSystemPathWalkProvider`, `FileSystemPathResolver` demande le parcours en
une requête puis applique **les mêmes règles** sur la liste : droit de
traverser, `NotDirectory`, liens suivis (un lien n'a pas d'enfant, le parcours
s'arrête dessus et la passe suivante repart de la cible), limite de 40 liens et
boucles. Sinon, ou si le parcours échoue, il revient au `lstat` par préfixe,
qui rapporte l'erreur exacte comme avant.

## Conséquences

- Une opération sur un fichier coûte trois requêtes au stockage (résolution,
  autorisation, opération) au lieu de dizaines, quelle que soit la profondeur.
  Tout l'OS en profite (démarrage, explorateur, shell), pas seulement les
  réglages.
- `/proc/diskstats` compte une lecture de 4 Kio par parcours de chemin au lieu
  d'une par composant : plus proche d'un vrai noyau, dont le cache des entrées
  de répertoire évite de relire le disque à chaque préfixe.
- Pas de cache des métadonnées : un autre onglet ou une synchronisation qui
  modifie IndexedDB reste vu immédiatement, aucune invalidation à gérer.
- Reste après ce chantier (mesuré, non traité) : environ 100 ms entre le clic et
  la première écriture, dues aux relectures du document (lecture avant
  `SetManyAsync`, relecture après écriture, relecture déclenchée par la
  surveillance du fichier) ; au ralenti ×4, la moitié du temps restant est le
  rendu du shell mobile, pas le disque.
