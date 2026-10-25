> [English version](../../../../en/wasm/dev/adr/0094-deferred-decisions-status-and-maximum-file-size.md)

# ADR 0094 : état des décisions différées de la migration v3 et taille maximale des fichiers

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`).

- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md), ligne `v3/1-adr`.
- **Source :** `doc/fr/wasm/dev/plans/wasm-v3-migration-analyse.md`, §20 « Deferred Decisions »
  et case `[ ] Resolve Phase 0 deferred decisions in architecture decision records`
  de la §22 ; manque R22 du relevé du 2026-10-16.

## Contexte

L'analyse de migration v3 (2026-08-01) a laissé douze décisions « à prendre avant
leur phase », chacune devant devenir un ADR court. Plusieurs ont été tranchées
depuis par des ADR qui ne les citent pas, une est devenue sans objet, et
certaines restent ouvertes. Faute de table, le relevé du 2026-10-16 les comptait
toutes comme manquantes.

## Décision

1. **Table de correspondance.** Chaque décision différée a désormais un statut
   écrit :

   | N° | Décision différée | Statut |
   |---|---|---|
   | 1 | Version LTS de .NET et navigateurs pris en charge | **Tranchée par le code** : `net10.0` dans tous les projets, SDK `10.0.100` épinglé par `global.json` (`rollForward: latestFeature`). Navigateurs : Chromium (cible des tests E2E et PWA) ; Firefox et Safari en meilleur effort, sans test automatisé. |
   | 2 | Format, signature et révocation des paquets d'apps | **Reportée** au plan « installation d'apps au runtime » (réponse 1 de Benoit, 2026-10-17). |
   | 3 | Apps chargées au runtime non fiables et isolation | **Reportée** au même plan. L'isolation actuelle est celle de la phase 5 (Web Workers, ADR 0070). |
   | 4 | Grammaire du shell | **Tranchée** : ADR 0050 puis ADR 0064 (shell POSIX). |
   | 5 | Découpage, déduplication et taille maximale d'un fichier | **Tranchée** par `FileContentStoragePolicy`, constatée au point 2 ci-dessous. |
   | 6 | Quota IndexedDB, export et restauration côté utilisateur | **Tranchée** par l'[ADR 0099](0099-os-backup-and-low-storage.md) (chantier `v3/7`). |
   | 7 | Algorithmes de conflit par domaine de synchro et profondeur d'historique hors ligne | **Tranchée** par l'ADR 0025 (table par domaine) et l'[ADR 0101](0101-synchronized-deletions-and-conflicts.md) (chantier `v3/9`). |
   | 8 | Utilisateurs locaux et identités synchronisées | **Tranchée** : ADR 0024 et 0028. |
   | 9 | Réglages sensibles et chiffrement côté client | **Tranchée** par l'[ADR 0100](0100-sensitive-settings-and-protected-write-audit.md) (chantier `v3/8`). |
   | 10 | Redémarrage de la PWA après une installation au runtime | **Reportée** au plan « installation d'apps au runtime ». |
   | 11 | Fenêtre de support et dépréciation du SDK, mise à jour des paquets | **Tranchée** par l'ADR 0019 (SemVer, `[Obsolete]` une version mineure avant retrait) et l'[ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md) (chantier `v3/6`). |
   | 12 | Interface de récupération quand Paramètres et Terminal sont désactivés | **Sans objet** : il n'y a plus de désactivation (ADR 0048), et une app système ne se désinstalle pas. Paramètres et Terminal sont des apps système, donc toujours présents. |

2. **Taille maximale d'un fichier.** La règle déjà appliquée par
   `Infrastructure/HackerOs.Infrastructure.Browser/Schema/FileContentStoragePolicy.cs`
   devient la règle documentée :
   - un fichier fait au plus **16 Mio** (`DefaultMaxFileSizeBytes`) ;
   - son contenu est découpé en blocs d'au plus **256 Kio**
     (`DefaultMaxChunkSizeBytes`), adressés par leur SHA-256 et dédupliqués ;
   - un bloc qui n'est plus référencé est gardé 30 jours avant d'être supprimé.

   Une écriture qui dépasserait 16 Mio est déjà refusée par
   `IndexedDbFileContentRepository` (ligne 67), mais avec une erreur générique.
   Le chantier `v3/7` lui donne un code propre, `FileSystemErrorCode.FileTooLarge`
   (l'équivalent de `EFBIG` sous Linux), à côté de `NoSpace` (`ENOSPC`, voir
   l'ADR 0099). Changer ces valeurs demande un
   nouvel ADR, parce qu'elles fixent le format stocké dans IndexedDB et le
   protocole de blocs de la synchro (ADR 0030).

3. Les décisions reportées (n° 2, 3 et 10) ne sont plus des manques de la
   migration v3 : elles appartiennent au plan d'installation au runtime.

## Conséquences

- La case §22 de l'analyse peut être cochée par `v3/16-final`, avec un renvoi à
  cet ADR.
- `sdk-developer-guide.md` et `sdk-reference.md` (section `HackFile`) citent la
  limite de 16 Mio quand le chantier qui ajoute l'erreur « fichier trop gros »
  au SDK (`v3/7`, avec `ENOSPC`) les met à jour.
