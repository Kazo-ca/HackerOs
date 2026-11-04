> [English version](../../../../en/wasm/dev/adr/0230-rename-migration-by-previous-version.md)

# ADR 0230 : migration du renommage selon la version précédente

## Statut

Accepté le 2026-11-04 (demandé par Benoit, série `rename/base`, branche
`rename/6a-adr-version-migration`).

- **Complète :** [ADR 0228](0228-rename-to-kazoos.md) et
  [ADR 0229](0229-kazo-os-display-name-and-new-address.md).
- **Remplace :** le point « Données des utilisateurs » de l'ADR 0228 §2 (copie
  base par base au premier démarrage) et le contenu du chantier 6 de
  l'ADR 0228 §3.

## Contexte

Benoit (2026-11-04) : « On modifie aussi ce qui est inscrit sur le disque. Plus
aucune mention de HackOs. Nous n'avons pas vraiment d'utilisateur, donc aucune
migration n'est obligatoire mais par principe, lors de la première exécution, ou
lors d'un restore, on recherche la version utilisée précédemment, et si elle est
plus basse que la nouvelle version 3.0.0, nous allons rechercher toute mention à
HackerOs et la remplacer par kazo os ou kazo.os ou kazo dépendamment du
contexte. »

Le chantier 5 a donc renommé aussi les noms enregistrés : bases IndexedDB
`kazoos*` et `kazoweb.*`, clés `localStorage`, ids d'apps `org.kazoos.*`,
chemins `/etc/kazoos`..., section de configuration `KazoOs`.

## Décision

1. **Quand.** La migration tourne avant le démarrage de l'OS, dans deux cas :
   au premier démarrage d'un navigateur qui a des données d'une version
   antérieure, et à la restauration d'une sauvegarde.
2. **Version précédente.** L'OS lit la version qui a écrit les données : la
   version enregistrée par le suivi des versions (ADR 0174) dans l'ancien
   stockage, ou celle de l'en-tête de la sauvegarde. Si elle est inférieure à
   `3.0.0`, ou absente alors qu'une ancienne base `hackeros*` existe, la
   migration s'applique ; sinon rien ne se passe.
3. **Quoi.** Toute mention de l'ancien nom est remplacée selon le contexte, avec
   la table de l'ADR 0228 §1 et les noms du chantier 5 :
   - noms de bases IndexedDB (`hackeros*` → `kazoos*`, `hackweb.*` →
     `kazoweb.*`) : la base est copiée sous le nouveau nom, puis l'ancienne est
     supprimée ;
   - clés `localStorage` et `sessionStorage` ;
   - chemins du système de fichiers virtuel (`/etc/hackeros` → `/etc/kazoos`,
     `~/.config/hackeros` → `~/.config/kazoos`, `/etc/hackweb` →
     `/etc/kazoweb`...) ;
   - ids enregistrés (`org.hackeros.*` → `org.kazoos.*`, ids de thèmes, de
     curseurs, de sons, contrats `hackeros.*`) et extensions de fichiers ;
   - contenu texte des fichiers de configuration de l'OS : « HackerOS » →
     « Kazo OS », `HackerOs.` → `Kazo.Os.`, `HackerOs`/`hackeros` →
     `KazoOs`/`kazoos`, `hackweb` → `kazoweb`.
   Les fichiers de l'utilisateur hors configuration (documents, `~/` sauf
   `~/.config`) ne sont pas réécrits.
4. **Rejouable.** La migration marque sa fin (version 3.0.0 enregistrée sous le
   nouveau nom). Interrompue, elle reprend au démarrage suivant ; une base déjà
   copiée n'est pas recopiée.
5. **Sauvegarde.** Une sauvegarde `.hosbak` faite avant 3.0 se restaure : la
   restauration applique la même migration à son contenu avant de l'écrire.
6. **Tests.** Les fixtures enregistrées `Tests/fixtures/indexeddb` (bases
   `hackeros`) ne changent pas : les tests de mise à niveau vérifient qu'elles
   arrivent sous les nouveaux noms, sans aucune mention de l'ancien nom dans
   les données migrées.

## Conséquences

- Un utilisateur d'avant 3.0 garde ses comptes, fichiers et réglages sur la
  même adresse ; sur `os.kazo.ca` il part d'une machine vide (ADR 0229 §3) ou
  restaure une sauvegarde, migrée à la restauration.
- Après la migration, plus aucune donnée enregistrée ne porte l'ancien nom,
  sauf ce que l'utilisateur a écrit lui-même dans ses documents.
