> [English version](../../../en/wasm/dev/rename-migration.md)

# Migration des données au renommage (HackerOS → Kazo OS)

## But

Le chantier 5 du renommage ([ADR 0228](adr/0228-rename-to-kazoos.md)) a aussi
renommé ce que l'OS écrit sur le disque : bases IndexedDB, clés de stockage,
ids d'apps, chemins de configuration. Un navigateur qui a fait tourner une
version d'avant 3.0.0, ou une sauvegarde `.kosbak` faite avant 3.0.0, contient
donc encore les anciens noms. [ADR 0230](adr/0230-rename-migration-by-previous-version.md)
décide de les migrer : au démarrage, avant que l'OS ouvre sa base, et à la
restauration d'une sauvegarde, avant de l'écrire. Après la migration, plus
aucune donnée enregistrée ne porte l'ancien nom, sauf ce que l'utilisateur a
écrit lui-même dans ses documents.

## Architecture

Tout est dans `Infrastructure/Kazo.Os.Infrastructure.Browser/Rename/` :

| Élément | Rôle |
|---|---|
| `RenamedNames` | La table des noms (règles du chantier 5, dans le même ordre) : « HackerOS » → « Kazo OS », `HackerOs.` → `Kazo.Os.`, `Hack*`/`Hacker*` → `Kazo*`, `hackeros` → `kazoos`, `hackweb` → `kazoweb`, `hack-paint` → `kazo-paint`, `hackpkg` → `kazopkg`, `.hackapp` → `.kazoapp`..., puis les noms courts du chantier 9 : `--hos-*` → `--kos-*`, `data-hos-*` → `data-kos-*`, classes `hos-*` → `kos-*`, `.hosbak` → `.kosbak` (un thème ou un réglage enregistré avec un jeton du système de design reste valable). Chaque règle est idempotente. Dit aussi si une base est ancienne (`hackeros`, `hackeros-*`, `hackweb.*`) et si une version précède 3.0.0. |
| `RenamedOsDatabaseRewriter` | Réécrit les enregistrements de la base de l'OS (fonction pure, sur du JSON) et liste les écritures à faire. |
| `RenamedStorageMigration` | Le déroulé au démarrage, sur une interface `IRenamedStorageBrowser` (un faux dans les tests). |
| `BrowserRenamedStorage` + `wwwroot/renamedStorage.js` | Le navigateur : liste et copie des bases, clés `localStorage`/`sessionStorage`, caches. Le JavaScript ne fait que ce que C# ne peut pas atteindre ; les décisions sont en C#. |

L'appel est fait par `LoadInstalledAppsAsync` (`OS/Kazo.Os.Ecosystem.Host/InstalledAppsBoot.cs`),
que chaque hôte navigateur appelle entre `Build()` et `RunAsync()` : c'est le
premier point du démarrage où la page tourne et où rien n'a encore ouvert la
base. Un échec est journalisé et le démarrage continue ; rien n'est marqué,
donc la migration recommence au démarrage suivant.

## Version précédente

Les versions d'avant 3.0.0 n'enregistraient pas leur version dans le
navigateur. La version qui a écrit les données est maintenant gardée dans
`localStorage`, clé `kazoos.osVersion` :

- présente et ≥ 3.0.0 (une préversion de 3.0.0 compte comme 3.0.0) : rien à
  faire, sans même lister les bases ;
- absente, et une ancienne base, une clé ou un cache à l'ancien nom existe : la
  migration s'applique ;
- absente sur un navigateur neuf : la version courante est seulement
  enregistrée.

Une sauvegarde porte maintenant `osVersion` (dans la partie couverte par le
SHA-256). Une sauvegarde dont la base s'appelle `hackeros`, sans `osVersion` ou
avec une version antérieure, est migrée.

## Ce qui est réécrit

1. **Bases IndexedDB.** Chaque ancienne base est copiée sous son nouveau nom
   (`hackeros` → `kazoos`, `hackeros-host-folders` → `kazoos-host-folders`,
   `hackweb.<app>` → `kazoweb.<app>`) : même version, magasins, index et
   enregistrements, tous écrits dans la transaction de mise à niveau qui crée la
   nouvelle base (une copie interrompue ne laisse aucune base). Le clonage
   structuré d'IndexedDB garde les valeurs que JSON ne sait pas porter (les
   `FileSystemDirectoryHandle` des dossiers de l'hôte). L'ancienne base est
   ensuite supprimée. Le contenu des bases de sites n'est pas réécrit : ce sont
   les données des sites.
2. **Clés** `localStorage` et `sessionStorage` à l'ancien nom : renommées, leur
   valeur réécrite ; si la nouvelle clé existe déjà, elle gagne.
3. **Caches** (Cache Storage) à l'ancien nom : supprimés, l'OS les remplit de
   nouveau.
4. **Base de l'OS** (`RenamedOsDatabaseRewriter`), en une seule transaction :
   - **noms de fichiers** (`fsLinks`) dans une zone système, c'est-à-dire hors
     de `/home`, `/root`, `/tmp`, `/mnt` et `/media`, plus les dossiers
     cachés de chaque compte (`~/.config`, `~/.local`, `~/.hackeros`...) : `/etc/hackeros` → `/etc/kazoos`,
     `~/.config/hackeros` → `~/.config/kazoos`, `/var/log/hackweb` →
     `/var/log/kazoweb`. `~/Documents/hackeros` garde son nom. Un nom déjà pris
     n'est jamais écrasé ;
   - **fichiers de configuration** (`/etc/**`, `~/.config/**`) en texte UTF-8 :
     contenu réécrit, enregistré sous son nouveau SHA-256 (morceaux de 256 Kio),
     taille mise à jour ; les anciens morceaux sont supprimés quand plus aucun
     fichier ne les utilise. Les documents ne sont jamais réécrits ;
   - **tous les autres enregistrements** (catalogue, droits, réglages, sessions,
     journaux, métadonnées des fichiers, cibles des liens symboliques dans une
     zone système) : chaque chaîne est réécrite (ids d'apps, de thèmes, de sons,
     capacités, chemins), sauf les secrets (`credential`), l'adressage du
     contenu (`contentHash`, `dataBase64`) et les noms que les personnes ont
     choisis (`loginName`, `displayName`). Un enregistrement dont la clé change
     (l'id d'une app du catalogue) est supprimé sous l'ancienne clé.
5. **Fin.** La version courante est écrite sous `kazoos.osVersion`.

La restauration d'une sauvegarde d'avant 3.0.0 passe par la même étape 4 dans
`IndexedDbBackupService` (validation, puis réécriture avant l'écriture) : la
vérification du SHA-256 se fait sur le contenu d'origine, la restauration écrit
le contenu migré.

## Reprise

`kazoos.renameMigration` (`localStorage`) garde, pendant la migration, la liste
des bases déjà copiées. Une migration interrompue recommence au démarrage
suivant : une base notée n'est pas recopiée, seulement supprimée ; une base du
nouveau nom que la migration n'a pas créée (faite par un autre build) n'est
jamais écrasée, l'ancienne est alors gardée et un avertissement est journalisé.
Toutes les réécritures sont idempotentes.

## Serveur

Un serveur installé avant 3.0.0 (volume Docker, dossier de données) a
`hackeros.db`. Au démarrage, `LegacyDatabaseFile.MoveIfNeeded`
(`Server/Kazo.Os.Server/Data/`) renomme `hackeros.db` (et ses fichiers `-wal`,
`-shm`, `-journal`) en `kazoos.db` quand la base configurée s'appelle
`kazoos.db`, n'existe pas encore et que `hackeros.db` est à côté.

## Décisions

- La version précédente est lue dans `localStorage` et non dans la base : elle
  doit être connue avant d'ouvrir une base, et les versions d'avant 3.0.0 n'en
  écrivaient aucune.
- La copie des bases est faite en JavaScript (clonage structuré) ; la réécriture
  des enregistrements est faite en C#.
- Les fixtures `Tests/fixtures/indexeddb` restent telles qu'enregistrées (base
  `hackeros`) : elles servent de données d'une version d'avant 3.0.0.

## Tests

- `Kazo.Os.Infrastructure.Browser.Tests` : `RenamedNamesTests` (table des noms,
  idempotence, versions), `RenamedOsDatabaseRewriterTests` (fixtures v2 et v3
  sans ancien nom hors documents, configuration réécrite sous un nouveau
  hachage, documents gardés, morceaux partagés gardés, secrets et noms choisis
  intacts, seconde passe sans changement), `RenamedStorageMigrationTests`
  (bases, clés et caches, version enregistrée, arrêt immédiat après 3.0.0,
  reprise sans recopie, échec repris au démarrage suivant, base existante
  jamais écrasée, sauvegarde d'avant 3.0.0 migrée et sauvegarde falsifiée
  refusée), `IndexedDbUpgradeFixtureCatalogTests` (les fixtures nomment une base
  d'avant 3.0.0 que la migration envoie vers `kazoos`).
- `Kazo.Os.Server.Tests` : `LegacyDatabaseFileTests`.
- `Kazo.Os.UI.E2E.Tests` : `IndexedDbUpgradeFixtureTests` démarre l'OS sur les
  bases `hackeros` v2 et v3 et vérifie, en plus de la connexion, du fichier et
  du réglage, que `hackeros` a disparu et qu'aucun enregistrement de `kazoos`
  ne contient d'ancien nom.

## Liste des tâches

- [x] Table des noms `RenamedNames`.
- [x] Réécriture de la base de l'OS (`RenamedOsDatabaseRewriter`).
- [x] Migration au démarrage, reprise, version `kazoos.osVersion`.
- [x] Sauvegardes d'avant 3.0.0 migrées à la restauration ; `osVersion` dans les nouvelles sauvegardes.
- [x] `hackeros.db` renommé par le serveur.
- [x] Tests unitaires et E2E.
