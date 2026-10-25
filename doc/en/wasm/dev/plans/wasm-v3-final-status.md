# Bilan de la migration wasm v3

## Objet

Ce document clôt le plan [`wasm-v3-completion-plan.md`](wasm-v3-completion-plan.md)
(chantier `v3/16-final`, 2026-10-17). Il dit ce qui a été livré, comment la
branche `v3/base` a été vérifiée de bout en bout, et ce qui reste ouvert :
lacunes connues et questions posées à Benoit sans réponse. Il remplace, pour
l'état courant, [`old/implementation-status.md`](../old/implementation-status.md).

L'analyse d'origine, [`doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md`](wasm-v3-migration-analyse.md),
porte maintenant une section 24 qui confronte sa définition du succès à cet
état.

## Ce qui a été livré

Les seize chantiers sont fusionnés dans `v3/base` (PR #152 à #167) :

| Chantier | PR | Décisions | Document |
|---|---|---|---|
| `v3/1-adr` | #152 | ADR 0094 à 0106 | — |
| `v3/2-game-removal` | #154 | ADR 0092 | — |
| `v3/3-window-model` | #157 | ADR 0095 | [`window-model.md`](../../framework/window-model.md) |
| `v3/4-intents` | #160 | ADR 0096 | [`app-intents.md`](../../framework/app-intents.md) |
| `v3/5-services` | #159 | ADR 0097, 0107 | [`linux-commands.md`](../linux-commands.md) (`systemctl`) |
| `v3/6-sdk-versioning` | #161 | ADR 0098 §1-7 | [`sdk-packages.md`](../../framework/sdk-packages.md) |
| `v3/7-storage-backup` | #163 | ADR 0099, 0110 | [`system-backup.md`](../system-backup.md) |
| `v3/8-settings-security` | #155 | ADR 0100 | [`sensitive-settings.md`](../sensitive-settings.md) |
| `v3/9-sync` | #156 | ADR 0101 | [`synchronization.md`](../synchronization.md) |
| `v3/10-server-proxy` | #158 | ADR 0102, 0108 | [`server-network-relay.md`](../server-network-relay.md) |
| `v3/11-apps` | #162 | ADR 0103 | [`hack-paint.md`](../hack-paint.md), [`theme-editor.md`](../theme-editor.md) |
| `v3/12-localization` | #164 | ADR 0104, 0111 | [`localization.md`](../../framework/localization.md) |
| `v3/13-accessibility` | #165 | ADR 0105, 0112 | [`accessibility.md`](../accessibility.md) |
| `v3/14-trimming` | #166 | ADR 0106, 0113 | [`trimmed-publish.md`](../trimmed-publish.md) |
| `v3/15-sdk-freeze` | #167 | ADR 0098 §8, 0114 | [`building-on-hackeros.md`](../../framework/building-on-hackeros.md) |
| `v3/16-final` | ce chantier | aucune | ce document |

En chiffres, sur `v3/base` : 14 apps fenêtrées système, 68 commandes du
shell, 6 apps d'exemple, SDK 1.0.0 (environ 940 types publics listés dans les
fichiers `PublicAPI.Shipped.txt`), interface en anglais et en français,
publication Release élaguée de 26,5 Mio de `.wasm` (50,9 Mio avant).

Les douze décisions différées de l'analyse (section 20) ont toutes un statut
écrit dans l'ADR 0094 : neuf tranchées, trois reportées au plan
« installation d'apps au runtime » (format et signature des paquets, isolation
des apps non fiables, redémarrage après installation).

## Vérification finale

Faite le 2026-10-17 sur `v3/base` 46e3d6c (avec les seules modifications de
documentation de ce chantier), dans un conteneur Linux, .NET SDK 10.0.402,
Chromium de `/opt/pw-browsers`. GitHub Actions est en panne dans tout le dépôt
(aucun exécuteur) : cette suite locale fait foi.

| Étape | Résultat |
|---|---|
| `dotnet build HackerOs.sln`, Debug puis Release | réussi, 0 avertissement, 0 erreur |
| Tests unitaires et d'intégration, 40 projets (`--no-build -c Debug`) | 3 397 réussis, 0 échec (dont `HackerOs.SdkCompatibility.Tests` 5/5, `HackerOs.Platform.Core.Tests` 1 125/1 125, `HackerOs.Tools.ManifestGenerator.Tests` 67/67) |
| `HackerOs.E2E.Tests` | 16/16 |
| `HackerOs.UI.E2E.Tests` | 24/24 |
| `HackerOs.Templates.E2E.Tests` (dont l'hôte qui charge l'app de compatibilité 1.0.0) | 6/6 |
| `HackerOs.Pwa.E2E.Tests` | 5/6 au premier passage, puis 6/6 deux fois de suite après la correction ci-dessous |
| `build/publish-trimmed.sh` (porte de fusion) | PASS : 134 fichiers `.wasm`, 26,4 Mio (8,0 Mio Brotli) ; connexion, chaque app fenêtrée du menu Démarrer, tube dans le pool de workers, Intranet, site Blazor d'exemple, redémarrage hors ligne |
| `build/verify-hosts.sh` (trois hôtes) | PASS : Ecosystem publié (mesures actives, coupées au build, coupées par réglage), `test/test`, `HackerOs.Server` |

Seule correction de code de ce chantier : le test PWA
`Reload_fully_offline_boots_the_app_and_preserves_a_saved_file` cliquait la
ligne du fichier une seule fois ; quand la liste de l'explorateur se
rechargeait juste après la navigation, la sélection tombait et le bouton
« Download » restait désactivé. `PwaTestSupport.AssertFileContentAsync` reprend
la boucle de `SelectFileExplorerEntryAsync` de `HackerOs.UI.E2E.Tests` : cliquer
jusqu'à ce que la ligne soit sélectionnée. C'était l'échec « hors ligne »
relevé sur `v3/base` depuis `v3/12`. Sans IPv6, la suite se replie sur
`127.0.0.2` depuis `v3/7` ([`pwa-cache-recovery.md`](../pwa-cache-recovery.md)).

## Série v3.1 et SDK 1.1

La série v3.1 ([`v3-1-series-plan.md`](v3-1-series-plan.md)) est fusionnée
dans `v3.1/base` : géométrie des fenêtres (PR #170, #173), titre de fenêtre
(PR #174), fusion de `v3/base` avec `passwd` et les associations par
utilisateur (PR #175), puis publication du SDK **1.1.0** (`v3.1/4-sdk-1-1` :
`Unshipped` versé dans `Shipped`, voir [`sdk-packages.md`](../../framework/sdk-packages.md#sdk-11)).
L'app de compatibilité compilée en 1.0.0 se charge et tourne sur l'OS 1.1.

Vérification complète refaite le 2026-10-17 sur la branche du chantier (même
conteneur, .NET SDK 10.0.402) :

| Étape | Résultat |
|---|---|
| `dotnet build HackerOs.sln`, Debug puis Release | réussi, 0 avertissement, 0 erreur |
| Tests unitaires et d'intégration, 40 projets (`--no-build -c Debug`) | 3 510 réussis, 0 échec (dont `HackerOs.SdkCompatibility.Tests` 5/5) |
| `HackerOs.E2E.Tests` | 16/16 |
| `HackerOs.UI.E2E.Tests` | 27/27 |
| `HackerOs.Templates.E2E.Tests` (dont l'hôte qui charge l'app 1.0.0) | 6/6 |
| `HackerOs.Pwa.E2E.Tests` | 6/6 |
| `build/publish-trimmed.sh` | PASS : 134 fichiers `.wasm`, 26,6 Mio (8,0 Mio Brotli), chaque app fenêtrée, pool de workers, Intranet, site Blazor, redémarrage hors ligne |
| `build/verify-hosts.sh` (trois hôtes) | PASS : Ecosystem (trois modes de mesure), `test/test`, `HackerOs.Server` |

Pendant la fusion de `v3/base`, l'escalade de `kill -9` a échoué une fois
(test instable connu, voir plus bas) ; elle passe dans la série complète.

## Ce qui reste ouvert

### Lacunes connues

La série v3.1 ([`v3-1-series-plan.md`](v3-1-series-plan.md)) a traité la
géométrie des fenêtres (ADR 0116, 0122) et le titre de fenêtre (ADR 0117,
0123) ; les associations par utilisateur (ADR 0121) et `passwd` (ADR 0120) ont
été faits par les fils `v3/4` et `v3/8` dans `v3/base`, puis ramenés dans
`v3.1/base` (PR #175). Le SDK 1.1.0 les publie tous (voir « Série v3.1 »
plus bas).

| Lacune | Origine | Effet |
|---|---|---|
| ~~La géométrie des fenêtres est enregistrée mais jamais relue : une fenêtre ne rouvre pas à sa dernière position.~~ Corrigé par `v3.1/2-window-geometry` (ADR 0116 et 0122 : taille et état agrandi, pas la position ; [`window-geometry.md`](../window-geometry.md)). | `v3/3` | Écart avec l'ADR 0095, résolu. |
| Une touche pressée dans une iframe (navigateur, sites) reste dans l'iframe ; les raccourcis de l'OS n'y marchent pas. | `v3/3` | Choix d'implémentation, non validé. |
| ~~Pas de couche utilisateur pour les associations de fichiers et de schémas.~~ Comblée par `v3/4b` (ADR 0121, [`user-file-associations.md`](../user-file-associations.md)). « Afficher dans le dossier » demande la capacité `apps.launch`. | `v3/4` | Fermée pour les associations. |
| `hackwebd` démarre à la connexion, pas au boot. | `v3/5` | Question ouverte ; un passage au boot demande son ADR. |
| `hackbackup` exige `sudo` ; une restauration en mode fusion redémarre aussi la machine ; pas de fixture IndexedDB v1 (jamais commitée). | `v3/7` | Documenté dans [`system-backup.md`](../system-backup.md). |
| ~~Pas de commande `passwd` ni de réinitialisation d'un mot de passe par un administrateur.~~ Comblée par `v3/8` (PR #172, ADR 0120, [`sensitive-settings.md`](../sensitive-settings.md)). | `v3/8` | Fermée. |
| Sur un même appareil, le curseur de synchronisation est commun à tous les utilisateurs. | `v3/9` | Documenté dans [`synchronization.md`](../synchronization.md). |
| Le SDK n'a pas d'API pour changer le titre d'une fenêtre ; Hack Paint affiche le nom du fichier dans l'app. | `v3/11` | Corrigé par `v3.1/3-window-title` (ADR 0117, 0123) : `SetWindowTitleAsync`, publié dans le SDK 1.1.0 par `v3.1/4-sdk-1-1`. Fermée. |
| Les menus internes de Monaco (Code Editor) restent en anglais. | `v3/12` | Question ouverte (défaut : anglais). |
| Une app compilée contre `0.9.0-preview` est refusée par l'OS 1.0. | `v3/15` | Voulu (autre version majeure) ; une app d'un autre dépôt se reconstruit contre 1.0.0. |
| Installation d'apps au runtime. | plan, question 1 | Reportée à un plan à part. |

### Questions posées à Benoit sans réponse

Chaque fil a appliqué l'option recommandée en attendant. Les réponses se
donnent dans le fil du chantier.

| Chantier | Question | Défaut appliqué |
|---|---|---|
| ~~`v3/4`~~ | ~~Ajouter des associations de fichiers propres à chaque utilisateur ?~~ Répondue par Benoit : faites (PR #171, ADR 0121). | — |
| `v3/5` | `hackwebd` doit-il démarrer au boot ? (Garder à la connexion / Boot + recharge des sites utilisateur / Boot + seconde instance sous l'utilisateur) | Garder à la connexion |
| `v3/5` | Comment créer le compte de service `hackeros-svc` ? (Compte intégré / Magasin d'utilisateurs IndexedDB / Magasin + `/etc/passwd` généré) | Compte intégré (ADR 0107) |
| ~~`v3/8`~~ | ~~Ajouter `passwd` et la réinitialisation par un administrateur ?~~ Répondue par Benoit : faits (PR #172, ADR 0120). | — |
| `v3/10` | D'où le serveur tient-il l'accord `network.real.access` d'une app ? (Liste du serveur / Accords poussés par l'OS / Vrai réseau coupé pour toutes les apps) | Liste du serveur (`Proxy:GrantedApps`) |
| `v3/12` | 1. Nom français « Gestionnaire de serveur web » (garder / nom anglais) ; 2. boutons d'accès d'un site au stockage (suivent la langue / forcer le français) ; 3. menus de Monaco (anglais / pack de langue, plusieurs centaines de Kio) ; 4. noms des thèmes intégrés (anglais / traduits) | 1A 2A 3A 4A |
| `v3/13` | 1. La page Accessibilité modifie l'utilisateur seul ou aussi « Tous les utilisateurs » ; 2. le contraste élevé recouvre les aperçus de thèmes ; 3. « Réduire les animations » en Auto/Activé/Désactivé ou simple interrupteur | 1A 2A 3A |
| `v3/14` | Garder les assemblies HackerOS entières dans la publication élaguée, ou tout élaguer plus tard (~280 appels d'interop à rendre sûrs, ~0,4 Mio Brotli de gain) ? | Garder entières (ADR 0113) |
| `v3/15` | Retirer les trois API `[Obsolete]` avant la 1.0 ? | Retirer (fait) |
| `v3/15` | Quels paquets la promesse de compatibilité 1.0 couvre-t-elle ? (SDK seulement / tout) | SDK seulement (ADR 0114) |

### Tests rouges ou instables connus

Ils échouaient ou vacillaient déjà sur `v3/base` avant ce chantier ; aucun
n'a échoué pendant cette vérification, et aucun n'est une régression de la
migration :

- `A_command_that_spins_at_once` (`HackerOs.Platform.WorkerHost.Tests`) échouait
  à chaque exécution sur `v3/base` en cours de journée ; il passe ici, sous une
  machine moins chargée, ce qui le range parmi les tests sensibles à la charge ;
- l'escalade de `kill -9`, `CommandParityTests` (`zip` puis `unzip`),
  `CpuAccountingOverheadTests` et `AppInstallationService` sont instables sous
  charge ;
- le test hors ligne de `HackerOs.Pwa.E2E.Tests` échouait sur `v3/base`
  depuis `v3/12` ; il est corrigé par ce chantier (voir plus haut) ;
- `HackerOs.Pwa.E2E.Tests` publie l'OS avec les versions `1.0.0-pwatest-v1` et
  `-v2` ; le générateur réécrit alors les `manifest.json` commités des apps
  avec `sdkCompatibility.minimumVersion` = `1.0.0-pwatest-v2` (85 fichiers). Il
  faut annuler ces modifications (`git checkout -- wasm2/HackerOs/Apps`) avant
  de commiter après cette suite ;
- le banc UI des fenêtres détachées démarre parfois mal quand les suites
  s'enchaînent.

## Tâches de ce chantier

- [x] Relire le plan §4 et recenser les questions restées ouvertes dans les fils `v3/3` à `v3/15`.
- [x] Compiler toute la solution en Debug et en Release.
- [x] Lancer les tests unitaires et d'intégration de chaque projet.
- [x] Lancer les suites E2E en série, la porte de fusion (`build/publish-trimmed.sh`, compatibilité du SDK) et `build/verify-hosts.sh` (trois hôtes).
- [x] Ajouter le statut final à `doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md` (section 24).
- [x] Mettre à jour `old/implementation-status.md`, `project-overview.md`, `sdk-reference.md`, le plan et l'index.
- [x] Série v3.1 (`v3.1/4-sdk-1-1`) : lacunes fermées marquées, questions répondues, vérification complète refaite après le SDK 1.1.
