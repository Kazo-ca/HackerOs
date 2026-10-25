# ADR 0110 : ajustements de la mise en œuvre de la sauvegarde, de l'espace de stockage et des fixtures IndexedDB

## Statut

Accepté le 2026-10-17 (chantier `v3/7-storage-backup`).

- **Complète :** [ADR 0099](0099-os-backup-and-low-storage.md). Remplace une
  phrase de son §4 (fixture de la version 1).
- Numéro : `0107` (`v3/5`) et `0108` (`v3/10`, renuméroté après une
  collision) sont pris ; ce chantier prend `0110` pour laisser `0109` à un
  chantier parallèle et éviter une nouvelle collision.

## Contexte

La mise en œuvre de l'ADR 0099 a rencontré quatre points que l'ADR ne
tranche pas, ou qu'il décrit d'une façon impossible à réaliser.

## Décision

1. **Pas de fixture de la version 1.** La version 1 du schéma IndexedDB n'a
   jamais existé dans l'historique du dépôt : le premier commit (`fdca68e`)
   est déjà en version 2. `Tests/fixtures/indexeddb/` contient donc les
   versions 2 (`e46e897`) et 3 (`7a20401`), produites en démarrant l'OS de ces
   commits. L'étape vers la version 1 du plan de migration reste couverte :
   elle tourne à chaque démarrage sur une base vide. La règle « tout chantier
   qui augmente `CurrentVersion` ajoute le vidage de la version précédente »
   s'applique à partir de la version 2 et est vérifiée par un test sans
   navigateur (`IndexedDbUpgradeFixtureCatalogTests`).
2. **Une fusion redémarre aussi.** L'ADR décrit le redémarrage pour le
   remplacement. La fusion ferme aussi la session et recharge la page : l'OS en
   marche garde en mémoire des chemins, des réglages et des comptes que la
   restauration vient de changer sous lui.
3. **L'entrée d'audit d'une restauration attend hors de la base.** Un
   remplacement écrase `/var/log/auth.log` lui-même. L'OS garde l'entrée dans
   le `localStorage` de l'hôte pendant la restauration et l'écrit dans
   `auth.log` au démarrage suivant, avec le résultat (`success` ou
   `failure:<raison>`).
4. **`hackbackup` exige `sudo`** : sans root, la commande s'arrête
   (`must be run as root`) au lieu d'ouvrir la demande d'administrateur
   graphique, comme un outil système. Le sélecteur de fichiers s'ouvre
   **avant** la demande d'administrateur, parce que le navigateur ne l'ouvre
   que juste après un geste de l'utilisateur.

## Conséquences

- [`system-backup.md`](../system-backup.md), [`building-and-testing.md`](../building-and-testing.md)
  et [`pwa-cache-recovery.md`](../pwa-cache-recovery.md) décrivent le
  comportement réel.
- Aucun autre ADR ne change.
