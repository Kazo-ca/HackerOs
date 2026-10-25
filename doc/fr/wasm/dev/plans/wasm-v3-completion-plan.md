# Fin de la migration wasm v3 : plan des chantiers

**Statut :** proposé le 2026-10-17, **validé par Benoit le 2026-10-17**
(réponses aux cinq questions, section 7). Le premier chantier est `v3/1-adr`.
**Terminé le 2026-10-17** : les seize chantiers sont fusionnés dans `v3/base` ;
le bilan, la vérification finale et ce qui reste ouvert sont dans
[`wasm-v3-final-status.md`](wasm-v3-final-status.md).

Ce plan termine ce que l'analyse d'origine
[`doc/fr/wasm/dev/plans/wasm-v3-migration-analyse.md`](wasm-v3-migration-analyse.md)
(2026-08-01) demandait et qui n'est toujours pas fait. Il s'appuie sur deux
relevés faits le 2026-10-16 sur `web/base` (07c9c77) et revérifiés le
2026-10-17 sur `web/base` (34952e2) : rien n'a changé pour ces points pendant la
phase web.

- `/mnt/project-files/wasm-v3/wasm-v3-remaining-work.md` (23 manques numérotés,
  cités ici « R1 » à « R23 ») ;
- `/mnt/project-files/audits/wasm-v3-migration-analysis-gaps.md`.

## 1. Décision préalable : **Aucun jeu sur ce repository**

Ce dépôt construit **la plateforme** : l'OS simulé, son SDK et ses apps de base
([ADR 0135](../adr/0135-no-game-in-this-repository.md), qui remplace l'ADR 0092 et l'ADR 0023).

Conséquences pour ce plan :

- la vague 6 de l'analyse est **retirée** ; elle n'est ni à faire ici ni
  comptée comme un manque ;
- l'ancien code de domaine hors périmètre (trois projets et une capacité) est
  retiré par le chantier `v3/2` ;
- un autre dépôt qui utilise l'écosystème passe par les **points d'extension
  génériques du SDK** ;
- la capacité d'un autre dépôt à consommer l'écosystème (paquets, versions,
  gabarits) devient un objectif de ce plan : chantiers `v3/6` et `v3/15`.

## 2. Ce qui n'est pas repris

| Point | Pourquoi |
|---|---|
| Vague 6 (`P4-W6-*`) | Hors périmètre (section 1). |
| Site ShopZone (`ecommerce-controller.ts`) | Hors périmètre (section 1) ; il n'était même pas enregistré dans `src/`. |
| Portées de réglages app/appareil, itinérance, synchro des réglages | Remplacé par l'ADR 0044. |
| Rejet atomique d'un document de réglages invalide | Remplacé par l'ADR 0044 §5. |
| Activer/désactiver une app | Remplacé par désinstaller/réinstaller (ADR 0048). |
| Permissions accordées par utilisateur | Remplacé par l'ADR 0049. |
| Interface de récupération si Paramètres et Terminal sont désactivés (décision différée n°12) | Sans objet : les apps système ne se désinstallent pas (ADR 0048). Constaté dans l'ADR du chantier `v3/1`. |
| Checklist manuelle d'accessibilité | Écartée par Benoit ; seuls des tests automatisés (`v3/13`). |
| Suites de la phase 5 (quotas et tueur OOM, apps à fenêtre dans des workers, workers sur l'hôte Server, COOP/COEP) | Plan séparé (fil « Plan : suites de l'étape 5 »), à faire **après** cette migration (Benoit, 2026-10-17). |
| Installation d'apps au runtime (R1, R2, décisions différées n°2 et n°10) | Plan séparé plus tard (question 1). |

## 3. Principes

1. **Mimic the Metal.** La langue suit `LANG` et `/etc/locale.conf` ; une
   suppression synchronisée est une pierre tombale ; un service a un état
   `active`/`failed` comme sous systemd ; une sauvegarde est une archive qu'on
   peut copier.
2. **SDK public seulement** pour les apps de base (ADR 0055) ; toute nouvelle
   possibilité passe par une façade `Hack*` documentée dans `sdk-reference.md`.
3. **Pensé pour un dépôt tiers.** Chaque ajout au SDK doit pouvoir servir à
   un autre dépôt sans rien savoir de lui.
4. **Les trois hôtes restent opérationnels** après chaque chantier
   (`build/verify-hosts.sh`) et l'Ecosystem marche sans serveur.
5. **ADR avant le code**, documentation et index `docs/README.md` à jour dans
   chaque chantier.
6. **Porte de fusion :** compilation et tests verts en local, tant que GitHub
   Actions ne tourne plus (blocage de facturation côté compte, hors de ce plan).

## 4. Chantiers

Branche d'intégration : `v3/base`, créée depuis `web/base` après la fusion de
`web/12` à `web/15` (question 5). Benoit décide quand `web/base` revient dans
`main`. Chaque chantier part
de `v3/base`, s'appelle `v3/<n>-<sujet>` et sa PR vise `v3/base`. Rien n'est
fusionné dans `main` sans Benoit.

| Branche | Contenu | Manques couverts | Dépend de |
|---|---|---|---|
| `v3/1-adr` | ADR des chantiers 3 à 15 (numéros pris à partir du premier libre). Constate aussi les décisions différées sans objet (n°12) et la taille maximale des fichiers déjà fixée par `FileContentStoragePolicy` (16 Mio, blocs de 256 Kio, analyse §20.5). | R22 (en partie) | validation |
| `v3/2-game-removal` | Retrait de l'ancien code de domaine hors périmètre (trois projets) de la solution et des règles de la frontière du SDK, et de sa capacité (32 → 31 capacités) ; docs (`project-overview`, `capabilities`, `sdk-reference`, `app-sdk`). | — | `1` |
| `v3/3-window-model` | Événements de fenêtre dans `WindowAppBase` (focus, perte du focus, réduction, agrandissement, restauration, redimensionnement) ; tailles initiale, minimale et maximale déclarées dans `AppDefinition` au lieu de `360×240` en dur (`WindowLaunchCoordinator.cs:77`) ; opérations au clavier de l'ADR 0009 : Alt+Tab, Alt+F4, Super+flèches, déplacer et redimensionner au clavier (menu de fenêtre Alt+Espace). | R7, R8 | `1` |
| `v3/4-intents` | `RevealFileIntent` réel (l'explorateur ouvre le dossier et sélectionne le fichier) ; `Edit` atteignable par `IAppIntentGateway` ; intents `OpenUri` (navigateur par défaut, `xdg-open`) et `ShowAppSettings` ; intents déclarés par les apps et routés par le dispatcher au lieu de `intent.unknown` ; commande `xdg-open`. | R6 | `1` |
| `v3/5-services` | Santé des services (`ServiceHealth` : `active`, `activating`, `failed`, raison) remontée par `HackServiceController`, `systemctl status` et le gestionnaire de tâches ; démarrage au boot de l'OS (avant la connexion) ou à la connexion, déclaré dans le manifeste ; message clair quand la désinstallation d'une app arrête un processus actif. | R9, R23 | `1` |
| `v3/6-sdk-versioning` | Versions sur les bibliothèques du SDK ; paquets NuGet du SDK et de la plateforme dans un flux local `artifacts/local-nupkg` pour qu'un autre dépôt construise une app ou un OS ; plage `SdkCompatibility` vérifiée au chargement (`AppCatalog`) ; noms de commandes en double rejetés au build et au chargement ; hash Sha256 des assets vérifiés ; `BuildProfileManifest` retiré (remplacé par l'ADR 0047/0048) ; gabarits `dotnet new` (app à fenêtre, commande, service, site ASP.Hack). | R3, R4, R5, R20, R21 (en partie) | `1` |
| `v3/7-storage-backup` | Sauvegarde et restauration de l'OS exposées (`IndexedDbBackupService` enregistré, page dans Paramètres ou Diagnostic, commande `hackbackup`, archive dans `~`) ; comportement « espace faible » (`IsLowSpace` : notification, écriture refusée avec `ENOSPC`, `df`) ; fixtures IndexedDB des versions précédentes et test de montée de version ; cache PWA corrompu détecté et rechargé, test de quota. | R13, R14, R15 | `1` |
| `v3/8-settings-security` | Valeurs `SettingSensitivity` masquées à la lecture par un non-administrateur et dans les exports ; chiffrement des réglages sensibles (décision différée n°9, ADR) ; journal d'audit des écritures protégées dans `/etc` (`/var/log/audit.log`, façon `auditd`). | R16, R17, R22 (n°9) | `1` |
| `v3/9-sync` | Suppressions synchronisées (pierres tombales au lieu de `IsTombstone: false` en dur) ; résolution des conflits appelée et proposée à l'utilisateur ; synchro périodique ; client du domaine AppCatalog. | R18 | `1` |
| `v3/10-server-proxy` | Relais du serveur optionnel : corps des requêtes HTTP transmis, flux TCP, UDP, contrôle du grant côté serveur, quotas et politique dans `appsettings` (tout autoriser, désactiver les quotas), avertissements au démarrage. | R19 | `1` |
| `v3/11-apps` | Hack Paint ouvre et enregistre des images dans les fichiers de l'OS (PNG, dialogues, « Ouvrir avec ») ; éditeur de thèmes porté depuis `src/` (édition, aperçu, documentation, thème enregistré dans `~/.config`) ; revérification des points ouverts du Code Editor (assets hors ligne, gros fichiers, nettoyage des workers). | migration §2 | `1`, `3` |
| `v3/12-localization` | `IStringLocalizer` dans les apps et la plateforme, ressources `.resx` ; langue de l'utilisateur dans `~/.config/locale.conf`, langue du système dans `/etc/locale.conf`, variable `LANG` dans l'environnement des processus (ADR 0050) ; passerelle de langue dans `IAppExecutionContext` ; `Localizations` du manifeste utilisé (nom des apps dans le lanceur) ; traduction des apps de base et de la plateforme ; correction de `docs/old/localization.md`, qui décrit un état inexistant. | R10 | `3`, `4`, `5`, `7`, `11` |
| `v3/13-accessibility` | Préférences d'accessibilité en fichiers (contraste élevé, taille du texte, mouvement réduit) lues par le thème et exposées par le SDK ; scans axe ajoutés pour Terminal, Browser, Calculator, System Monitor et les apps des chantiers 7 et 11 ; parcours au clavier testé en E2E. | R11 | `3`, `12` |
| `v3/14-trimming` | `PublishTrimmed=True` dans l'Ecosystem, avertissements de trimming corrigés dans tous les projets livrés, publication Release trimmée testée en E2E (boot, apps, sites). AOT hors périmètre. | R12 | `2` à `13` |
| `v3/15-sdk-freeze` | `PublicAPI.Shipped.txt` pour toutes les bibliothèques du SDK ; gel du SDK 1.0 ; test d'une app compilée contre une version antérieure du SDK ; guide « construire sur HackerOS depuis un autre dépôt ». | R21 | `14` |
| `v3/16-final` | Vérification finale (trois hôtes, toutes les suites), statut ajouté à `doc/fr/wasm/dev/plans/wasm-v3-migration-analyse.md`, mise à jour de `docs/old/implementation-status.md`, `project-overview.md`, `sdk-reference.md`. | — | tous |

### Ordre et parallélisme

1. `v3/1-adr`.
2. **Vague 1**, en parallèle (zones de code distinctes) : `2`, `3`, `4`, `5`,
   `6`, `7`, `8`, `9`, `10`. Recommandé : quatre fils à la fois au plus, en
   commençant par `2`, `3`, `6` et `7`. `3` et `6` touchent tous deux
   `AppDefinition` : le second à fusionner reprend l'autre avant.
3. **Vague 2** : `11` (après `3`), puis `12`, qui touche toutes les chaînes
   affichées et passe donc après les chantiers qui en ajoutent.
4. `13`, puis `14`, puis `15`, puis `16`, l'un après l'autre (chacun touche
   l'ensemble de la solution).

Un autre dépôt peut commencer dès la fusion de `v3/6` (paquets en version
préliminaire) ; il passe au SDK 1.0 après `v3/15`.

### ADR prévus (écrits par `v3/1`)

Fenêtres (événements, tailles, clavier ; complète l'ADR 0009) ; intents ajoutés
et intents d'app ; santé et démarrage des services ; versions, paquets et
vérifications du manifeste (complète l'ADR 0019) ; sauvegarde et espace faible ;
réglages sensibles chiffrés et audit ; suppressions synchronisées (complète les
ADR 0025 et 0030) ; relais TCP/UDP du serveur ; localisation ; préférences
d'accessibilité ; trimming ; décisions différées sans objet et taille maximale
des fichiers. Les numéros sont attribués au moment de l'écriture (premier
numéro libre ; plusieurs fils écrivent des ADR en parallèle).

**ADR écrits par `v3/1` (2026-10-17) :**

| Chantier | ADR |
|---|---|
| `v3/1-adr` | [0094](../adr/0094-deferred-decisions-status-and-maximum-file-size.md) décisions différées et taille maximale des fichiers |
| `v3/2-game-removal` | [0092](../adr/0092-game-in-a-separate-repository.md) (déjà écrit), remplacé par l'ADR 0135 |
| `v3/3-window-model` | [0095](../adr/0095-window-events-sizes-and-keyboard.md) |
| `v3/4-intents` | [0096](../adr/0096-added-intents-and-app-defined-intents.md) |
| `v3/5-services` | [0097](../adr/0097-service-health-and-startup.md) |
| `v3/6-sdk-versioning` | [0098](../adr/0098-sdk-versions-packages-and-manifest-checks.md) §1 à §7 |
| `v3/7-storage-backup` | [0099](../adr/0099-os-backup-and-low-storage.md) |
| `v3/8-settings-security` | [0100](../adr/0100-sensitive-settings-and-protected-write-audit.md) |
| `v3/9-sync` | [0101](../adr/0101-synchronized-deletions-and-conflicts.md) |
| `v3/10-server-proxy` | [0102](../adr/0102-server-network-relay-tcp-udp-and-policy.md) |
| `v3/11-apps` | [0103](../adr/0103-hack-paint-image-files-and-theme-editor.md) |
| `v3/12-localization` | [0104](../adr/0104-localization.md) |
| `v3/13-accessibility` | [0105](../adr/0105-accessibility-preferences.md) |
| `v3/14-trimming` | [0106](../adr/0106-trimmed-release-publish.md) |
| `v3/15-sdk-freeze` | [0098](../adr/0098-sdk-versions-packages-and-manifest-checks.md) §8 |
| `v3/16-final` | aucun |

Deux écarts au plan, décidés dans ces ADR : la sauvegarde est téléchargée sur
l'hôte au lieu d'être écrite dans `~` (une archive de l'OS dépasse la limite de
16 Mio par fichier, ADR 0099 §1) ; il n'y a pas de client du domaine de synchro
`app_catalog`, le choix d'apps de l'utilisateur voyageant déjà comme fichier de
`~/.config` (ADR 0101 §3). Le prochain numéro libre est 0109 (0107 réservé par `v3/5`, 0108 : ajustements du relais, `v3/10`).

## 5. Hors périmètre

- Installation d'apps au runtime, format et signature des paquets, magasin
  d'apps : reportés à un plan à part (question 1).
- Suites de la phase 5 : plan séparé.
- AOT, compilation d'apps dans l'OS.

## 6. Risques

- **Localisation** : chantier le plus long (toutes les chaînes de la plateforme
  et des 13 apps système). Peut être découpé en un fil d'infrastructure puis un
  fil par groupe d'apps.
- **Trimming** : les assemblies chargées paresseusement et la réflexion du
  générateur de manifestes peuvent casser en Release seulement ; c'est pour ça
  que le chantier vient tard, une fois les API stables, et qu'il ajoute un test
  E2E sur la publication trimmée.
- **Tests sans CI** : la porte est locale (section 3, principe 6). Les suites
  PWA tournent en conteneur depuis `v3/7` : sans IPv6, leur harnais se replie
  sur `127.0.0.2` ([`building-and-testing.md`](../building-and-testing.md)).

## 7. Décisions (questions du 2026-10-17)

Réponses de Benoit du 2026-10-17 05:52Z.

| # | Question | Réponse |
|---|---|---|
| 1 | Installation d'apps au runtime (paquets, signature, transaction, migrations de données) | **Plus tard**, dans un plan à part. |
| 2 | Comment un autre dépôt consomme l'écosystème | « Pas important » : on garde la recommandation, **paquets NuGet** (flux local, puis GitHub Packages quand la CI remarchera). |
| 3 | Langues de la localisation | **Anglais et français**, anglais par défaut. |
| 4 | Porter l'éditeur de thèmes de `src/` | **Oui**, comme app système sur le SDK public. |
| 5 | Branche d'intégration | **`v3/base`**, créée depuis `web/base` une fois `web/12` à `web/15` fusionnés ; Benoit décide quand ramener `web/base` vers `main`. |

## 8. Liste des tâches

### Préparation
- [x] Relevé de ce qui reste (2026-10-16), revérifié sur `web/base` 34952e2.
- [x] Décision préalable consignée (ADR 0092, remplacé par l'ADR 0135 : **Aucun jeu sur ce repository**).
- [x] Ce plan.
- [x] Validation du plan et réponses aux questions par Benoit (2026-10-17).
- [x] Création de `v3/base` depuis `web/base` (après `web/12` à `web/15`).

### Chantiers
- [x] `v3/1-adr` (ADR 0094 à 0106)
- [x] `v3/2-game-removal`
- [x] `v3/3-window-model` ([`window-model.md`](../../framework/window-model.md))
- [x] `v3/4-intents` ([`app-intents.md`](../../framework/app-intents.md))
- [x] `v3/5-services` (ADR 0097, ajusté par l'ADR 0107)
- [x] `v3/6-sdk-versioning` (ADR 0098 §1-7, [`sdk-packages.md`](../../framework/sdk-packages.md))
- [x] `v3/7-storage-backup` ([`system-backup.md`](../system-backup.md) ; archive téléchargée sur l'hôte, pas dans `~`, ADR 0099)
- [x] `v3/8-settings-security` ([`sensitive-settings.md`](../sensitive-settings.md))
- [x] `v3/9-sync` ([`synchronization.md`](../synchronization.md))
- [x] `v3/10-server-proxy` ([`server-network-relay.md`](../server-network-relay.md), ADR 0108)
- [x] `v3/11-apps` ([`hack-paint.md`](../hack-paint.md), [`theme-editor.md`](../theme-editor.md))
- [x] `v3/12-localization` (ADR 0104, ADR 0111, [`localization.md`](../../framework/localization.md))
- [x] `v3/13-accessibility` ([`accessibility.md`](../accessibility.md), ADR 0112)
- [x] `v3/14-trimming` ([`trimmed-publish.md`](../trimmed-publish.md), ADR 0113 ; assemblies HackerOS gardées entières)
- [x] `v3/15-sdk-freeze` (ADR 0114, [`building-on-hackeros.md`](../../framework/building-on-hackeros.md))
- [x] `v3/16-final` (bilan : [`wasm-v3-final-status.md`](wasm-v3-final-status.md))
