> [English version](../../../en/wasm/memory/README.md)

# Mémoire du projet HackerOS

État du projet et conventions de travail au **2026-10-30**. Ce dossier sert de
point de reprise : un nouveau contributeur (humain ou agent) y trouve ce qui
est fait, comment on travaille et ce qu'il ne faut pas refaire. Il est mis à
jour à la fin de chaque série ou quand une convention change.

## Où en est le projet

- **Code actif** : `wasm2/HackerOs/` (OS simulé en Blazor WebAssembly, .NET 10,
  solution `HackerOs.sln`). `src/` est l'ancienne version, gardée comme
  référence.
- **Version** : HackerOS et le SDK sont en **2.1.0** (`build/Versions.props`).
  Le SDK 2.0.0 a retiré `HackerOs.AppSdk.FileView` et refuse les apps 1.x
  ([ADR 0161](../dev/adr/0161-sdk-2-0-implementation-adjustments.md)).
  La 2.1.0 (2026-10-26) est la première version publiée automatiquement
  (tag, GitHub Release, nuget.org ; ADR 0173) ; elle a versé dans `Shipped`
  tous les ajouts depuis la 2.0.
- **Séries terminées et fusionnées dans `main`** :

| Série | Contenu | Bilan |
| --- | --- | --- |
| Refonte, phases 1 à 4 | API fichier `Hack*`, manifeste en classe C#, réglages et journaux en fichiers, `/proc` et `/dev`, shell POSIX, corbeille, capacités dangereuses, apps de base sur le SDK public | [`ecosystem-rebuild-plan.md`](../dev/plans/ecosystem-rebuild-plan.md) |
| Phase 5 | Mesure CPU et mémoire, « Not responding », arrêt propre, processus en Web Workers | [`execution-isolation-plan.md`](../dev/plans/execution-isolation-plan.md) |
| Phase web | ASP.Hack (MVC, vues `.razor`/`.cshtml`), sites Blazor, sites statiques, rendu HTML du navigateur, fenêtres détachées | [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md) |
| V3 (`v3/1` à `v3/16`) | Fin de la migration wasm v3, SDK 1.0 gelé, publication trimmée | [`wasm-v3-final-status.md`](../dev/plans/wasm-v3-final-status.md) |
| V3.1 | Géométrie et titre des fenêtres, SDK 1.1 | [`v3-1-series-plan.md`](../dev/plans/v3-1-series-plan.md) |
| EXEC (`exec/*`) | Limites de processus, OOM killer, passerelles distantes, boîte « ne répond pas », `HackBackground`, OS servi par `HackerOs.Server`, isolation cross-origin, SDK 1.2 | [`execution-followups-plan.md`](../dev/plans/execution-followups-plan.md) |
| DESK (`desk/0` à `desk/6`) | Contrôle de fichiers fourni par l'OS, menu contextuel central, entrées de menu des apps, bureau, SDK 2.0 | [`desktop-and-context-menu-plan.md`](../dev/plans/desktop-and-context-menu-plan.md) |
| Multi-moniteur (`multi-monitor/0` à `multi-monitor/9`) | Bureau étendu : un écran par surface d'affichage, `DisplayId` et zone de travail par écran, fenêtres déplacées par portail, service d'écrans (Chrome/Edge, repli ailleurs), une barre des tâches par écran, menus « Autre écran » et voile d'identification, Paramètres ▸ Écrans, glisser entre écrans ; aucun changement du SDK des apps | [`multi-monitor-plan.md`](../dev/plans/multi-monitor-plan.md) |
| File Explorer (`explorer/1` à `explorer/9`) | Barre latérale redimensionnable, état vide dans la liste, glisser vers l'ordinateur, raccourcis, lecteurs réseau suivis, erreurs en `MessageBox` ; le `.zip` ouvert comme dossier est fait dans `todo/base` (`todo/10-zip-as-folder`, issue #241) | [ADR 0185](../dev/adr/0185-file-explorer-improvements.md), [`file-explorer.md`](../dev/file-explorer.md) |
| Invité (`guest/*`) | Démarrage en invité quand aucun compte n'existe, élévation par simple confirmation, « Créer mon compte » (migrer ou supprimer l'invité), crédits à la connexion | [ADR 0187](../dev/adr/0187-guest-start-and-sign-in-credits.md), [`guest-account.md`](../dev/guest-account.md) |

- **Depuis DESK** (sur `main`, octobre 2026) : redémarrage proposé quand le
  build change sous la page (ADR 0162), aperçu local du déplacement des
  fenêtres (ADR 0163), icônes des apps et menu de l'icône de fenêtre
  (ADR 0164), accueil et tiroir mobiles (ADR 0165, 0166), publication GitHub
  Pages combinée PWA + legacy (ADR 0167), paquets NuGet mis à jour.
- **Clés d'accès et comptes** (octobre 2026, PR vers `main`) : connexion par
  clé d'accès WebAuthn et trousseau format 2 à emplacements (ADR 0168,
  [`passkeys.md`](../dev/passkeys.md)), ajout et suppression de comptes par un
  administrateur (ADR 0169). Ajouts au SDK dans `PublicAPI.Unshipped.txt`
  (prochaine mineure).
- **Explorateur mobile** (octobre 2026, PR #212 vers `main`) : point d'entrée
  mobile des apps honoré, explorateur de fichiers mobile, dialogues en plein
  écran sur mobile, un toucher ouvre (`HackFileView.OpenOnTap`) (ADR 0171).
- **Depuis le multi-moniteur** (sur `main`, 28 au 30 octobre 2026) : dossier de
  l'ordinateur monté comme lecteur réseau (ADR 0181), `hackpkg install` depuis
  GitHub (ADR 0182), présentation des fonctionnalités `features.md` (ADR 0192),
  heure locale et fuseau horaire (ADR 0193, chantier 2 de l'issue #242),
  menus contextuels de la barre des tâches (ADR 0194, chantier 4 de #242).
  Les ADR du Hack Store (0188 à 0190) sont acceptées, rien n'est codé (issues
  `TODO` #246 à #257). Le serveur local installable (ADR 0191, issue #267) est
  codé dans la série `todo/base` (chantier `todo/14-installable-server`,
  [`installable-server.md`](../dev/installable-server.md)) ; restent à essayer
  en vrai les scripts Windows et macOS et l'image Docker.
- **Bouton Marche/arrêt dans le menu Démarrer** (ADR 0195, PR #291, fusionnée
  le 2026-10-30) : « Changer d'utilisateur » reste grisé, choix de
  Benoit le 2026-10-30 (ADR 0201, issue #290 ; prototype non fusionné sur la
  branche `switch-user/simultaneous-sessions-prototype`).
- **Thèmes de curseurs** (ADR 0205, série `todo/base`, chantier
  `todo/9-cursor-themes`, issue #257) : dossiers `cursor.theme` de PNG dans
  `/etc/hackeros/cursors/` et `~/.config/hackeros/cursors/`, `cursors.config`,
  thèmes intégrés Système (défaut), HackerOS Néon et Gothique Pourpre,
  appliqués à toute la surface par `cursorTheme.js`, apps comprises. Fusionné
  dans `todo/base` (PR #301) ; Benoit a confirmé le 2026-10-31 le thème par
  défaut Système et la portée partout.
- **Série `todo/base`** (2026-10-30, toutes les issues `TODO` en parallèle,
  une PR finale vers `main`, branches `todo/<n>-<sujet>`, ADR 0197 à 0211
  réservées) : chantier 1 du Hack Store fait, modèle
  `hackeros-solution` et workflow de publication vers le catalogue (ADR 0207,
  [`app-repository.md`](../framework/app-repository.md), issue #246). Le
  dépôt `HackerOs-AppTemplate` ne sera régénéré qu'avec un sous-module sur la
  release qui contient ce modèle (PR en brouillon dans ce dépôt).
  Fonds d'écran en image faits aussi ([ADR 0202](../dev/adr/0202-desktop-wallpaper-images.md),
  issue #254, [`wallpapers.md`](../dev/wallpapers.md)).
  L'ADR 0206 est prise par l'issue #247 (`wwwroot/` des apps installées servi
  par le service worker, [ADR 0206](../dev/adr/0206-serve-installed-app-assets.md)).
- **Packs d'icônes** (ADR 0203, issue `TODO` #255, série `todo/base`) : packs
  PNG pour les apps et les fichiers dans `/usr/share/icons` et
  `~/.local/share/icons`, choisis dans Paramètres ▸ Apparence (document de
  réglages `icons`, clé `iconPackId`) ; le pack Néon intégré est installé
  d'office. Voir [`icon-packs.md`](../dev/icon-packs.md).
- **Hack Store 3, sources de confiance** (ADR 0208, issue `TODO` #248, série
  `todo/base`) : installation pour soi dans `~/.local/share/hackeros/apps`,
  liste noire `/etc/hackeros/app-policy.config`, validation en deux étapes
  (`AppInstallApprovalOverlay`), `hackpkg --user/--system`, `sources add/remove`,
  `policy`, Paramètres ▸ Sources et liste noire. Le catalogue charge au
  démarrage les apps personnelles de tous et les masque par session (question
  posée à Benoit). Voir [`app-installation.md`](../dev/app-installation.md).
- **Bulles de notification** (ADR 0197, chantier 1 de #242, série
  `todo/base`) : bulles des notifications et « Update ready » en
  notification persistante avec « Redémarrer ». Voir
  [`notification-toasts.md`](../dev/notification-toasts.md).
- **Animations des fenêtres** (ADR 0198, chantier 3 de #242, branche
  `todo/12-window-animations` vers `todo/base`) : ouvrir, fermer, minimiser vers
  la barre de son écran, restaurer, agrandir, snap ; variables
  `--hos-window-anim-*` ([`window-animations.md`](../dev/window-animations.md)).
- **Sons du système** (ADR 0204, issue #256, série `todo/base`, branche
  `todo/8-os-sounds`) : jeux de sons freedesktop.org dans `/usr/share/sounds`
  (jeux intégrés `hackeros` et `soft` écrits par `FileSystemSeeder`) et
  `~/.local/share/sounds`, `sound.config`, Paramètres ▸ Sons. Pas d'API SDK pour
  jouer un son (question Q2 à Benoit). Voir [`system-sounds.md`](../dev/system-sounds.md).
- **Aperçus de la barre des tâches** (ADR 0199, chantier 5 de l'issue #242,
  série `todo/`, branche `todo/13-taskbar-previews` vers `todo/base`) : copie
  réduite en direct d'une fenêtre au survol de son bouton, et peek. Numéro 0199
  pris dans la réserve 0197 à 0205 de la série `todo/`.
- **Vrais sites dans le navigateur** (ADR 0210, série `todo/base`, issue #266) :
  le Navigateur est une app système avec `network.real.access` ; avec un
  serveur connecté, il ouvre les vrais sites par le proxy, cookies compris.
  Restent le proxy public et la Wayback Machine.
- **ADR** : 0001 à 0171 et 0173 à 0195 existent sur `main` (quelques numéros sautés ;
  0141 et 0172 n'ont pas servi). Repères récents : 0173 publication automatique,
  0174 annonce de nouvelle version, 0175 « Nouveau dossier » des dialogues, 0176
  dépôt template d'apps, 0177 barre de titre des fenêtres agrandies, 0178 à 0180,
  0183 et 0184 multi-moniteur, 0181 lecteur réseau, 0182 `hackpkg`, 0185 File
  Explorer, 0186 nouveautés ([`change-list.md`](../dev/change-list.md)), 0187
  invité et crédits, 0188 à 0190 Hack Store ([`hackeros-store.md`](../dev/plans/hackeros-store.md)),
  0191 serveur local installable, 0192 `features.md` et exception à l'ADR 0135,
  0193 heure locale, 0194 menus de la barre des tâches, 0195 bouton
  Marche/arrêt du menu Démarrer ; série `todo/base` : 0197 bulles de
  notification, 0201 « Changer d'utilisateur » reste grisé, 0203 packs d'icônes, 0207 modèle `hackeros-solution`, 0208 sources de confiance du Hack Store (les
  autres numéros jusqu'à 0211 sont réservés par ses chantiers). 0196 est prise
  par la PR #294 (point des notifications non lues). **Prochain numéro libre :
  0212.** Avant d'en
  prendre un, vérifier `dev/adr/` sur `main` et les PR ouvertes.

## Conventions de travail

Les règles de travail avec Benoit (langue, questions, issues `TODO`, noms de
branche, fusion vers `main`, numéros d'ADR, entrées de changement) sont dans la
section **IV** de [`AGENTS.md`](../../../../AGENTS.md), que Claude Code lit par
[`CLAUDE.md`](../../../../CLAUDE.md). En résumé :

- **Langue** : on répond à Benoit en français.
- **À faire** : « ajouter à la liste des choses à faire » = une issue GitHub
  avec le label `TODO` et une section « Dépendances » ; « qu'est-ce qu'il reste
  à faire » = les issues `TODO` ouvertes d'abord, puis la doc.
- **Aucun jeu sur ce repository** ([ADR 0135](../dev/adr/0135-no-game-in-this-repository.md)) :
  ni code, ni mention dans la doc, les plans, les ADR ou la mémoire.
- **Branches** : noms descriptifs (`taskbar/4-context-menus`), jamais
  `claude/project-thread-…`. Une branche d'intégration `<série>/base` par série, une branche
  `<série>/<n>-<sujet>` par chantier, PR vers la branche d'intégration. Les ADR
  d'une phase sont écrits avant son code.
- **`main`** : seul Benoit fusionne dans `main` et décide quand une branche
  d'intégration y va.
- **Questions** : on peut les noter dans un ADR, mais on les pose toujours à
  Benoit directement, avec des choix, le contexte et les implications.
- **Documentation** : écrite en français dans `doc/fr/`, traduite en anglais dans `doc/en/` (mêmes dossiers, mêmes noms de fichiers, en anglais) ; chaque changement met à jour les deux langues. Les readme à la racine : `readme.md` en anglais, `readme-fr.md` en français, chacun avec un lien vers l'autre en première ligne. `dev/` pour le
  développement de l'OS, `framework/` pour créer des apps et des sites,
  `memory/` pour ce dossier. Chaque changement met à jour la doc touchée et
  l'index [`../README.md`](../README.md) ; les docs remplacées vont dans
  `dev/old/`. Un ADR n'est jamais modifié : un nouvel ADR le remplace.
- **Barre de fusion** : build et tests locaux verts (suites E2E une à la fois),
  `build/publish-trimmed.sh` et la compatibilité du SDK
  (`Tests/fixtures/sdk-compat`) quand ils sont concernés. Les GitHub Actions
  tournent de nouveau depuis le 2026-10-24 et vérifient chaque PR vers `main`
  et `*/base` (tests en échec relancés une fois ; Playwright chaque nuit sur `main`
  si `main` a changé, un échec ouvre une issue) : [`../dev/ci-workflows.md`](../dev/ci-workflows.md).
- **SDK** : l'API publique est suivie par `PublicAPI.Shipped.txt` /
  `PublicAPI.Unshipped.txt` ; un ajout passe par `Unshipped` jusqu'à la
  publication suivante (`build/tools/ship-public-api.sh`).
- **Publier** : changer `HackerOsVersion` dans `build/Versions.props` et
  fusionner dans `main` ; le workflow `Release HackerOS` crée le tag, la
  Release GitHub et pousse sur nuget.org ([`releases.md`](../dev/releases.md),
  ADR 0173).

## Décisions et contexte hors du code

| Note | Contenu |
| --- | --- |
| [`decisions-not-built.md`](decisions-not-built.md) | Décisions prises par Benoit en discussion et pas encore codées ni écrites ailleurs : fenêtres et barre des tâches (chantiers restants de #242), vrais sites dans le navigateur (#266 : proxy public et Wayback restants), apps tierces portées, apps hébergées par le serveur, idées tirées de daedalOS, éditeurs. |
| [`known-test-failures.md`](known-test-failures.md) | Échecs de tests préexistants connus (à nommer dans une PR, pas à cacher) et astuces d'environnement récentes. |

## Environnement et pièges

Les contournements de build (NuGet, conteneur Linux, Playwright) et les tests
instables connus sont regroupés dans [`../dev/pitfalls.md`](../dev/pitfalls.md)
et [`../dev/building-and-testing.md`](../dev/building-and-testing.md).

## Notes historiques

Notes écrites pendant les premières phases ; les chemins et nombres qu'elles
citent datent de leur écriture.

| Note | Contenu |
| --- | --- |
| [`blazor-window-manager-bugs.md`](blazor-window-manager-bugs.md) | Leçons de l'ancien gestionnaire de fenêtres Blazor (appel de base oublié dans `OnAfterRenderAsync`, état de la solution v3 en août 2026). |
| [`manifest-serializer-notes.md`](manifest-serializer-notes.md) | Sérialiseur strict de `AppManifest` et fixture canonique. |
| [`p2-idb-008-filesystem-repository.md`](p2-idb-008-filesystem-repository.md) | Analyse des transactions du dépôt de fichiers IndexedDB (phase 2). |
