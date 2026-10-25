# Recherche des chemins du système de fichiers

## But

Trouver l'entrée d'un chemin (`/home/<user>/.config/hackeros/<doc>.config`)
coûtait des dizaines d'allers-retours vers IndexedDB : écrire un réglage prenait
environ 2 secondes sur un appareil lent. Ce document décrit comment un chemin est
maintenant recherché en une requête, et comment le mesurer. Décision :
[ADR 0090](adr/0090-filesystem-path-lookup-in-one-request.md).

## Mesures

Hôte Ecosystem publié en Release, servi en statique, Chromium sans tête,
profil neuf, clic sur « Mobile » dans le panneau de l'horloge (le choix est écrit
dans `~/.config/hackeros/ui-platform-preference.config`, création du fichier
comprise), jusqu'à l'apparition du bouton « Back » du shell mobile. Trois essais,
2026-10-17 :

| | Transactions IndexedDB | Durée | Processeur ralenti ×4 (CDP) |
|---|---|---|---|
| `web/base` avant | 604 | 837 à 850 ms | 1 975 à 2 010 ms |
| Une transaction par recherche (étape 1) | 95 | 260 à 360 ms | |
| Parcours par le résolveur (étape 2) | 51 | 231 à 242 ms | 997 à 1 028 ms |

Le compte des transactions enveloppe `IDBDatabase.prototype.transaction` par un
script injecté avant le chargement de la page. Le script de mesure n'est pas
versionné ; il suit le parcours de `ClockPanelMobileToggleTests`.

## Architecture

```
FileSystemService.WriteAsync(path)
 ├─ FileSystemPathResolver.ResolveAsync        1 requête : StatComponentsAsync (parcours)
 ├─ AuthorizeAsync → StatAsync(path)           1 requête : resolvePath
 └─ IndexedDbFileSystemProvider.WriteAsync     1 requête : resolvePath, puis contenu et métadonnées
```

| Élément | Projet | Rôle |
|---|---|---|
| `resolvePath` (`indexedDb.js`) | `Infrastructure.Browser/wwwroot` | lit lien puis entrée pour chaque nom dans une seule transaction en lecture seule ; rend les entrées trouvées |
| `IndexedDbFileSystemReader.ResolveComponentsAsync` | `Infrastructure.Browser/FileSystem` | appelle `resolvePath`, convertit les entrées, signale un lien orphelin comme corruption |
| `IndexedDbFileSystemReader.ResolveAsync` | idem | `/` : une lecture de l'entrée racine ; sinon la dernière entrée du parcours, ou `null` |
| `IFileSystemPathWalkProvider` | `Simulation.Abstractions/FileSystem` | capacité facultative d'un fournisseur : métadonnées de chaque composant, sans suivre de lien |
| `IndexedDbFileSystemProvider.StatComponentsAsync` | `Infrastructure.Browser/FileSystem` | l'implémente pour la racine IndexedDB |
| `FileSystemPathResolver.TryWalkAsync` | `Platform.Core/FileSystem` | demande le parcours quand tous les préfixes sont sur le même montage ; sinon `lstat` par préfixe |

## Règles conservées

- Le droit de traverser (`x`) de chaque répertoire intermédiaire, `NotDirectory`,
  les liens suivis (absolus ou relatifs), la limite de 40 liens et la détection
  des boucles s'appliquent sur la liste rendue, exactement comme avec les `lstat`
  successifs.
- Un chemin qui traverse un point de montage (`/proc`, `/dev`, montage de test)
  est vérifié préfixe par préfixe, comme avant.
- Un parcours en échec (stockage, annulation) retombe sur les `lstat` par
  préfixe, qui rapportent l'erreur précise.
- Aucun cache : chaque recherche relit IndexedDB, donc un autre onglet ou une
  synchronisation est vu tout de suite.

## Tests

- `Tests/HackerOs.Infrastructure.Browser.Tests/IndexedDbFileSystemReaderTests.cs` :
  une seule transaction `resolvePath` pour un chemin profond, nom absent,
  liste partielle, lien orphelin, comptage disque.
- `Tests/HackerOs.Infrastructure.Browser.Tests/IndexedDbFileSystemProviderTests.cs` :
  scénarios du fournisseur adaptés au parcours en une transaction.
- `Tests/HackerOs.Platform.Core.Tests/FileSystem/FileSystemPathResolverTests.cs` :
  parcours en une requête, premier composant absent, droit de traverser et
  redémarrage sur un lien, chemin qui traverse un montage.
- Les suites E2E (`HackerOs.E2E.Tests`, `HackerOs.UI.E2E.Tests`) exercent
  `resolvePath` dans un vrai navigateur à chaque démarrage.
- Effet de bord sur les tests UI : le système de fichiers plus rapide a révélé
  une course dans `E2ESupport.SelectFileExplorerEntryAsync` (le clic sur une
  ligne arrivait pendant le rechargement de la liste après une navigation, qui
  efface la sélection). L'aide clique de nouveau tant que la ligne n'est pas
  restée sélectionnée.

## Liste des tâches

- [x] Mesurer l'écriture d'un réglage dans un vrai navigateur (604 transactions, 845 ms).
- [x] Opération `resolvePath` dans `indexedDb.js` (une transaction par recherche).
- [x] `IndexedDbFileSystemReader` sur `resolvePath`, tests adaptés.
- [x] Contrat `IFileSystemPathWalkProvider` et implémentation IndexedDB.
- [x] `FileSystemPathResolver` : parcours en une requête sur un même montage, repli par préfixe.
- [x] Mesurer après (51 transactions, 235 ms ; ×4 : 2,0 s → 1,0 s).
- [x] ADR 0090, index de la doc, notes dans les documents archivés.
- [ ] Non traité : relectures redondantes du document de réglages autour d'une écriture (environ 100 ms).
