> [English version](../../en/wasm/features.md)

# HackerOS : présentation et fonctionnalités

## En bref

HackerOS est un **système d'exploitation complet qui s'exécute dans le
navigateur**. Il offre un bureau avec ses fenêtres, une interface mobile, un
système de fichiers persistant, des comptes utilisateurs, un terminal avec un
vrai shell et plus de 70 commandes Linux, un réseau simulé avec ses propres
sites web, et une suite d'applications de base. Il fonctionne hors ligne et
s'installe comme une application, sans rien d'autre qu'un navigateur récent.

HackerOS est aussi une **plateforme de développement**. Son SDK public, publié
sur NuGet, permet d'écrire en C# des applications fenêtrées, des commandes du
shell, des services et des sites web qui tournent dans l'OS. Le développeur
profite de tout ce que l'OS fournit déjà (fenêtres, fichiers, réseau,
sécurité, thèmes, langues, mobile) et se concentre sur son application.

HackerOS est écrit en C# avec Blazor WebAssembly (.NET 10). La version
actuelle est la **2.1.0**. Essayez-le sur <https://hackos.kazo.ca/>.

## Le but de l'écosystème

L'écosystème HackerOS veut qu'un développeur **écrive son application une
seule fois, en C#, et qu'elle tourne partout où HackerOS tourne** : en ligne,
hors ligne, sur son propre serveur ou intégrée dans un autre produit. L'OS
fournit l'environnement complet (bureau, mobile, fichiers, terminal, réseau,
comptes), et l'application se distribue ensuite par le **Hack Store**, le
magasin d'applications de HackerOS.

L'écosystème se compose de :

- **HackerOS**, l'OS lui-même (ce dépôt) ;
- **le SDK HackerOS**, les paquets NuGet `HackerOs.*` qui permettent de
  construire sur l'OS depuis un autre dépôt ;
- **le dépôt modèle** [HackerOs-AppTemplate](https://github.com/Kazo-ca/HackerOs-AppTemplate),
  point de départ d'une nouvelle application ;
- **le catalogue** [HackerOs-AppMarketPlace](https://github.com/Kazo-ca/HackerOs-AppMarketPlace),
  la liste relue des applications que le Hack Store propose (en cours de
  développement) ;
- **les applications** elles-mêmes, chacune dans son propre dépôt.

## Pourquoi développer pour ou avec HackerOS

- **Une API familière.** L'API fichier ressemble à `System.IO` (`HackFile`,
  `HackDirectory`, `HackPath`…) et une application se décrit par une simple
  classe C# (`[AppManifest]`). Un développeur .NET est à l'aise tout de suite.
- **Tout est déjà fourni.** Fenêtres, menus contextuels, dialogues de
  fichiers, presse-papiers, notifications, thèmes, traduction anglais et
  français, accessibilité, clavier virtuel, interface mobile : l'application
  les reçoit sans rien coder.
- **Aucun serveur à maintenir.** L'OS et ses applications tournent entièrement
  dans le navigateur de l'utilisateur, y compris hors ligne. Un serveur
  optionnel ajoute la synchronisation et l'accès au vrai réseau quand on le
  veut.
- **Sécurité par conception.** Chaque application déclare les capacités dont
  elle a besoin (réseau réel, fichiers d'autres utilisateurs…). Les capacités
  sensibles demandent l'accord d'un administrateur, comme sur un vrai système.
- **Un comportement de vrai Linux.** Tout est fichier : réglages dans `/etc`
  et `~/.config`, journaux dans `/var/log`, processus dans `/proc`. Une
  application s'intègre avec le shell, les permissions et les outils existants.
- **Un SDK stable et versionné.** Les paquets suivent une version unique, la
  surface publique est contrôlée à chaque build, et la compatibilité des
  applications est vérifiée par des tests.
- **Des outils de développement complets.** Modèles `dotnet new`, dépôt
  modèle, débogage dans Visual Studio, tests unitaires et de bout en bout,
  publication automatique.
- **Une distribution intégrée.** Une application s'installe depuis son dépôt
  GitHub en une commande, ou en un clic depuis le Hack Store.

## Façons d'utiliser et de déployer HackerOS

| Usage | Pour qui | Description |
| --- | --- | --- |
| **En ligne** | Tout le monde | Ouvrir <https://hackos.kazo.ca/>. HackerOS s'installe comme une application (PWA) et fonctionne ensuite hors ligne. |
| **Site statique hébergé chez soi** | Organisations, enseignants | Chaque version publie une archive `hackeros-wasm-<version>.zip` dans les [GitHub Releases](https://github.com/Kazo-ca/HackerOs/releases). Elle se dépose sur n'importe quel hébergement statique (GitHub Pages, serveur web). Voir [`github-pages-deployment.md`](dev/github-pages-deployment.md). |
| **Avec le serveur HackerOS** | Organisations, utilisateurs avancés | Le serveur ASP.NET Core optionnel sert l'OS et ajoute les comptes synchronisés entre appareils et le relais vers le vrai réseau. Voir [`server-hosting.md`](dev/server-hosting.md). |
| **Intégré dans son propre produit** | Développeurs, entreprises | Les paquets NuGet et le modèle `dotnet new hackeros-host` permettent de composer son propre hôte avec ses propres applications. Voir [`building-on-hackeros.md`](framework/building-on-hackeros.md). |
| **Développer des applications** | Développeurs | Paquets NuGet du SDK, modèles `dotnet new`, dépôt modèle, puis installation au terminal avec `hackpkg`. Voir [`sdk-developer-guide.md`](framework/sdk-developer-guide.md). |
| **Serveur local installable** | Utilisateurs avancés | Chaque version publie le serveur HackerOS pour Windows, Linux et macOS (`hackeros-server-<version>-<système>`) et l'image Docker `ghcr.io/kazo-ca/hackeros-server`. Il s'installe comme service sur son ordinateur ou sur une machine du réseau local, sans être exposé à internet, et l'OS en ligne s'y relie par un code d'appairage (Paramètres > Connexion au serveur > Serveur local). Voir [`installable-server.md`](dev/installable-server.md). |

## Fonctionnalités disponibles

Cette section liste ce qui est publié dans la branche `main` et en ligne.

### Bureau et fenêtres

- **Bureau** avec icônes, raccourcis et menus contextuels, comme sur un OS de
  bureau. ([`desktop.md`](dev/desktop.md))
- **Fenêtres** déplaçables, redimensionnables, agrandies ou ancrées sur les
  bords de l'écran, avec des animations sobres pour ouvrir, fermer, minimiser
  vers la barre, agrandir et ancrer, coupées par « Réduire les animations ».
  ([`window-model.md`](framework/window-model.md), [`window-animations.md`](dev/window-animations.md))
- **Barre des tâches et menu Démarrer** avec les applications épinglées et les
  fenêtres ouvertes. Un clic droit sur la barre donne le menu de la fenêtre,
  « Nouvelle fenêtre », le gestionnaire des tâches, « Afficher le bureau » et
  les fenêtres en cascade ou en mosaïque. ([`taskbar-context-menus.md`](dev/taskbar-context-menus.md))
  Survoler le bouton d'une fenêtre montre un aperçu en direct ; s'y attarder
  montre la fenêtre seule (« peek »). ([`taskbar-window-previews.md`](dev/taskbar-window-previews.md))
  Le menu Démarrer montre l'utilisateur connecté et son bouton Marche/arrêt :
  se déconnecter ou redémarrer. ([ADR 0195](dev/adr/0195-start-menu-power-button.md))
- **Bulles de notification** : chaque notification de l'OS ou d'une app
  s'affiche quelques secondes au-dessus de la barre (en haut sur téléphone),
  puis reste dans le panneau de l'horloge. Une mise à jour prête y arrive avec
  son bouton « Redémarrer ». ([`notification-toasts.md`](dev/notification-toasts.md))
- **Plusieurs écrans** : chaque écran supplémentaire s'ouvre dans sa propre
  fenêtre du navigateur, avec sa barre des tâches, et l'OS s'en souvient. On
  glisse une fenêtre d'un écran à l'autre. ([`screen-service.md`](dev/screen-service.md),
  [`per-screen-taskbars.md`](dev/per-screen-taskbars.md),
  [`drag-between-screens.md`](dev/drag-between-screens.md))
- **Fenêtres détachées** : une application peut afficher son contenu dans une
  vraie fenêtre séparée du navigateur. ([`detached-display-windows.md`](dev/detached-display-windows.md))

### Interface mobile

- **Écran d'accueil façon téléphone** avec pages de raccourcis, dock et tiroir
  d'applications. ([`mobile-home-screen.md`](dev/mobile-home-screen.md))
- **Applications adaptées au tactile**, dont l'explorateur de fichiers et des
  dialogues en plein écran.
- **Clavier virtuel** fourni par l'OS.

### Fichiers

- **Système de fichiers persistant** gardé dans le navigateur, avec dossiers
  personnels, permissions et propriétaires comme sous Linux.
  ([`project-overview.md`](dev/project-overview.md#système-de-fichiers))
- **Explorateur de fichiers** avec vues, recherche, glisser-déposer,
  téléversement et téléchargement. ([`file-explorer.md`](dev/file-explorer.md))
- **Explorateur amélioré** : panneau de gauche redimensionnable, bulles au
  survol, taille totale des dossiers, aperçu du contenu des dossiers, menu
  Compresser, Télécharger, Copier le chemin et Envoyer vers, glisser hors du
  navigateur pour télécharger (Chrome et Edge), lecteurs réseau rafraîchis
  tout seuls. ([ADR 0185](dev/adr/0185-file-explorer-improvements.md))
- **Archive `.zip` ouverte comme un dossier** dans l'explorateur, en lecture
  seule : un fichier ouvert est extrait dans `/tmp`, glisser hors de l'archive
  extrait. ([ADR 0185](dev/adr/0185-file-explorer-improvements.md),
  [`file-explorer.md`](dev/file-explorer.md))
- **Corbeille** avec restauration. ([`trash.md`](dev/trash.md))
- **Applications par défaut** par type de fichier, propres à chaque
  utilisateur. ([`user-file-associations.md`](dev/user-file-associations.md))
- **Dossier de l'ordinateur monté comme lecteur réseau** : un vrai dossier de
  l'ordinateur apparaît dans l'OS (Chrome et Edge sur ordinateur).
  ([ADR 0181](dev/adr/0181-host-folder-mounted-as-a-network-drive.md))
- **Sauvegarde et restauration** de toute la machine dans une archive
  téléchargée. ([`system-backup.md`](dev/system-backup.md))

### Terminal et shell

- **Terminal** complet, avec couleurs et copier-coller.
  ([`terminal-emulator.md`](dev/terminal-emulator.md))
- **Shell POSIX** : variables, tuyaux, redirections, scripts, conditions et
  boucles. ([`shell-interpreter.md`](dev/shell-interpreter.md))
- **Plus de 70 commandes Linux** : `ls`, `cp`, `grep`, `nano`, `sudo`, `ps`,
  `top`, `systemctl`, `zip`, `curl`, `ping`, `nmap` et bien d'autres.
  ([`linux-commands.md`](dev/linux-commands.md))

### Applications de base

- **Éditeur de texte** et **éditeur de code** avec coloration syntaxique.
  ([`text-editor.md`](dev/text-editor.md), [`code-editor.md`](dev/code-editor.md))
- **Navigateur web** pour les sites du réseau simulé et, avec un serveur
  HackerOS connecté, les vrais sites par son proxy (cookies et connexion aux
  comptes compris) ; une icône de la barre d'adresse dit d'où vient la page.
  ([`browser-html-rendering.md`](dev/browser-html-rendering.md), [ADR 0210](dev/adr/0210-browser-real-sites-through-the-proxy.md))
- **Paramètres** : comptes, apparence, écrans, langue, accessibilité, système.
  ([`settings-app.md`](dev/settings-app.md))
- **Gestionnaire des tâches** et moniteur système (processus, mémoire,
  processeur). ([`task-manager.md`](dev/task-manager.md))
- **HackPaint** (dessin), **Calculatrice**, **Éditeur de thèmes**,
  **Visionneuse d'icônes**, **Journal des erreurs**, **Diagnostic** et
  **Gestionnaire de serveur web**. ([`hack-paint.md`](dev/hack-paint.md),
  [`theme-editor.md`](dev/theme-editor.md))

### Réseau et web

- **Réseau simulé** avec noms de domaine, ports, `ping` et HTTP.
  ([`sdk-network.md`](framework/sdk-network.md))
- **Sites web internes** en `.hack`, servis depuis l'OS : sites statiques,
  sites ASP.Hack (contrôleurs, vues Razor) et sites Blazor.
  ([`static-sites.md`](dev/static-sites.md),
  [`asp-hack-developer-guide.md`](framework/asp-hack-developer-guide.md),
  [`blazor-sites.md`](framework/blazor-sites.md))
- **Accès au vrai réseau** par le serveur optionnel, pour les applications
  autorisées. ([`server-network-relay.md`](dev/server-network-relay.md))
- **Serveur local installable** : le serveur HackerOS s'installe comme service
  (Windows, Linux, macOS, Docker) sur son ordinateur ou sur une machine du
  réseau local en HTTPS, et l'OS, même en ligne sur GitHub Pages, s'y relie
  par un code d'appairage. ([`installable-server.md`](dev/installable-server.md),
  [ADR 0191](dev/adr/0191-installable-local-server.md))

### Comptes et sécurité

- **Plusieurs comptes** avec mot de passe, administrateurs et `sudo`. Un
  administrateur ajoute et retire des utilisateurs.
  ([ADR 0169](dev/adr/0169-administrator-adds-and-removes-users.md))
- **Démarrer en invité** : essayer l'OS sans créer de compte, puis
  « Créer mon compte » en gardant ou non les fichiers de l'invité ; crédits
  sur l'écran de connexion. ([`guest-account.md`](dev/guest-account.md),
  [ADR 0187](dev/adr/0187-guest-start-and-sign-in-credits.md))
- **Clés d'accès (passkeys)** : connexion par empreinte, visage, NIP de
  l'appareil ou clé de sécurité. ([`passkeys.md`](dev/passkeys.md))
- **Capacités des applications** et approbation des capacités sensibles par un
  administrateur. ([`capabilities.md`](framework/capabilities.md),
  [`dangerous-capability-approval.md`](dev/dangerous-capability-approval.md))
- **Réglages sensibles** protégés et chiffrés.
  ([`sensitive-settings.md`](dev/sensitive-settings.md))
- **Synchronisation entre appareils** quand l'OS est connecté au serveur
  optionnel. ([`synchronization.md`](dev/synchronization.md))

### Système

- **Processus** avec priorités, limites et signaux ; les commandes tournent
  dans des Web Workers pour ne pas bloquer l'interface.
  ([`worker-processes.md`](dev/worker-processes.md),
  [`process-limits.md`](dev/process-limits.md))
- **Mesure du processeur et de la mémoire** par processus, et OOM killer quand
  la mémoire manque. ([`resource-accounting.md`](dev/resource-accounting.md),
  [`oom-killer.md`](dev/oom-killer.md))
- **Fenêtres qui ne répondent plus** détectées, avec l'offre de les fermer.
  ([`process-priority-and-unresponsive-windows.md`](dev/process-priority-and-unresponsive-windows.md))
- **`/proc` et `/dev`** virtuels, journaux dans `/var/log`.
  ([`virtual-proc-and-dev.md`](dev/virtual-proc-and-dev.md),
  [`logging.md`](dev/logging.md))
- **Annonce des nouvelles versions** : une session ouverte apprend qu'une
  nouvelle version est disponible. ([ADR 0174](dev/adr/0174-announce-a-new-version-to-open-sessions.md))
- **Nouveautés depuis la dernière visite** : la page Paramètres ▸ Nouveautés liste
  les changements par version avec un badge **Nouveau** ; une notification après la
  connexion et une pastille sur le menu Démarrer signalent ce qui n'a pas été vu.
  La même liste écrit les notes de la GitHub Release, des paquets NuGet et
  [`CHANGELOG-fr.md`](../../../CHANGELOG-fr.md).
  ([`change-list.md`](dev/change-list.md), [ADR 0186](dev/adr/0186-whats-new-change-list.md))

### Personnalisation

- **Thèmes** de l'OS, et éditeur pour créer les siens.
  ([`theme-files.md`](framework/theme-files.md))
- **Fonds d'écran en image** : une image PNG, JPEG ou WebP (4 livrées avec
  l'OS, les siennes ou celles d'un dossier partagé), pour tous les écrans ou
  un écran à la fois, dans Paramètres ▸ Apparence.
  ([ADR 0202](dev/adr/0202-desktop-wallpaper-images.md), [`wallpapers.md`](dev/wallpapers.md))
- **Anglais et français**, la langue suit la locale de l'utilisateur.
  ([`localization.md`](framework/localization.md))
- **Accessibilité** : préférences d'affichage et d'animation, navigation au
  clavier. ([`accessibility.md`](dev/accessibility.md))
- **Heure locale et fuseau horaire** : l'horloge, `date`, `ls -l` et les apps
  affichent l'heure dans le fuseau de l'utilisateur, automatique ou choisi dans
  Paramètres ▸ Date et heure. ([`time-zone.md`](framework/time-zone.md))
- **Thèmes de curseurs** : pointeur de tout l'OS en images PNG, Système,
  HackerOS Néon, Gothique Pourpre ou vos propres thèmes, choisi dans
  Paramètres ▸ Apparence ▸ Curseurs. ([`cursor-themes.md`](framework/cursor-themes.md))
- **Packs d'icônes** : des images PNG remplacent les icônes des applications,
  des dossiers et des fichiers partout dans l'OS, choisies dans Paramètres ▸
  Apparence ; pack d'exemple « Néon » fourni. ([`icon-packs.md`](dev/icon-packs.md),
  [ADR 0203](dev/adr/0203-icon-packs.md))
- **Sons du système** : notifications, avertissements, erreurs, ouverture et
  fermeture de session. Deux jeux intégrés (HackerOS et Doux), ses propres jeux
  en OGG ou MP3, volume et coupure dans Paramètres ▸ Sons.
  ([`system-sounds.md`](dev/system-sounds.md), [ADR 0204](dev/adr/0204-os-event-sounds.md))

### Pour les développeurs

- **SDK public sur NuGet** (`HackerOs.*`, version 2.1.0) : API fichier,
  fenêtres, terminal, réseau, tâches de fond, presse-papiers, dialogues,
  composants Blazor. ([`sdk-reference.md`](framework/sdk-reference.md),
  [`sdk-packages.md`](framework/sdk-packages.md))
- **Modèles `dotnet new`** pour une application fenêtrée, une commande, un
  service, un site, un hôte complet ou un dépôt d'application entier, visibles
  dans Visual Studio et Rider.
  ([`sdk-developer-guide.md`](framework/sdk-developer-guide.md))
- **Installation d'une application depuis son dépôt GitHub** avec la commande
  `hackpkg`. ([ADR 0182](dev/adr/0182-install-apps-from-github.md),
  [`app-installation.md`](dev/app-installation.md)) Ses images, scripts et Web
  Workers sont servis à la même adresse que pour une application compilée dans
  l'OS : une application qui enveloppe un projet JavaScript marche une fois
  installée. ([ADR 0206](dev/adr/0206-serve-installed-app-assets.md))
- **Sources de confiance et installation pour soi** : une application d'une
  source de confiance s'installe pour soi sans administrateur ; une source non
  fiable, une application de la liste noire ou une installation pour tous
  passent par une validation en deux étapes (connexion d'un administrateur puis
  fenêtre qui montre l'application, sa source et ses capacités). Sources et
  liste noire dans Paramètres ▸ Sources et liste noire et `hackpkg sources` /
  `hackpkg policy`. ([ADR 0189](dev/adr/0189-app-sources-trust-and-per-user-install.md),
  [ADR 0208](dev/adr/0208-hack-store-sources-trust-build.md))
- **Hack Store** : le magasin d'applications de HackerOS, épinglé par défaut
  dans le menu Démarrer, sur le bureau et le mobile. Accueil par catégories,
  recherche, fiche (captures, description, taille, licence, capacités,
  confiance), installation pour soi ou pour tous, ouverture, mise à jour,
  désinstallation, onglet Installées et « Redémarrer pour terminer ». Il
  vérifie les mises à jour à l'ouverture de session puis toutes les 6 heures,
  avec un badge et une notification ; `hackpkg install <appId>` lit les mêmes
  catalogues. ([ADR 0188](dev/adr/0188-hack-store-and-app-catalog.md),
  [ADR 0212](dev/adr/0212-hack-store-app-build.md))
- **Packs de thèmes** : l'onglet Thèmes du Hack Store installe des packs
  `.hacktheme` (thèmes, fonds d'écran, icônes, sons et curseurs), pour soi sans
  administrateur ou pour tous avec la validation en deux étapes. Un pack ne
  contient aucun code (pas de SVG, aucune adresse distante, tailles limitées)
  et ses parties apparaissent dans Paramètres ▸ Apparence.
  ([ADR 0190](dev/adr/0190-theme-packs.md),
  [ADR 0213](dev/adr/0213-theme-packs-build.md))
- **Modèle de dépôt d'application `hackeros-solution`** : application, hôte
  et tests prêts à lancer, sous-module de l'OS pour le débogage dans ses
  sources, paquets NuGet en production, CI et soumission au catalogue du Hack
  Store par un tag. ([ADR 0176](dev/adr/0176-app-repository-template.md),
  [ADR 0207](dev/adr/0207-solution-template-implementation.md),
  [`app-repository.md`](framework/app-repository.md))
- **Versions publiées automatiquement** : GitHub Release, paquets NuGet et
  archive de l'OS à chaque nouvelle version. ([`releases.md`](dev/releases.md))
- **Tests navigateur chaque nuit** : les suites Playwright tournent la nuit sur
  `main` quand il a changé ; un échec ouvre une issue.
  ([`ci-workflows.md`](dev/ci-workflows.md#tests-navigateur-chaque-nuit))

## En cours de développement

Cette section liste les fonctionnalités prévues, avec leur décision
d'architecture (ADR) et leur issue de suivi. Quand une ADR n'est pas encore
dans `main`, le lien mène à sa PR.

### Bureau, fenêtres et fichiers

| Fonctionnalité | Description | ADR | Issue |
| --- | --- | --- | --- |
| Changer d'utilisateur | Ouvrir une autre session sans fermer la première. Reste grisé dans le menu Marche/arrêt par choix de Benoit : une session à la fois, Se déconnecter pour changer de compte. | [0195](dev/adr/0195-start-menu-power-button.md), [0201](dev/adr/0201-switch-user-stays-greyed.md) | [#290](https://github.com/Kazo-ca/HackerOs/issues/290) |

### Hack Store

| Fonctionnalité | Description | ADR | Issue |
| --- | --- | --- | --- |
| Catalogue des applications | Format du catalogue, vérification et publication des applications proposées. | [0188](dev/adr/0188-hack-store-and-app-catalog.md) | [AppMarketPlace #1](https://github.com/Kazo-ca/HackerOs-AppMarketPlace/issues/1) |
| Hack Store, étapes suivantes | Ajout sans redémarrage, signature des paquets, installation transactionnelle. | [0188](dev/adr/0188-hack-store-and-app-catalog.md) | [#251](https://github.com/Kazo-ca/HackerOs/issues/251) |

### Personnalisation

| Fonctionnalité | Description | ADR | Issue |
| --- | --- | --- | --- |
| Vérification des packs de thèmes par le catalogue | La CI du catalogue vérifie les entrées `kind: "theme-pack"` comme l'OS à l'installation. | [0213](dev/adr/0213-theme-packs-build.md) | [AppMarketPlace #4](https://github.com/Kazo-ca/HackerOs-AppMarketPlace/issues/4) |
| Fond d'écran sur mobile et menu « Définir comme fond d'écran » | L'image de fond sur l'écran d'accueil mobile, et une entrée du menu contextuel des images. Les fonds d'écran du bureau sont disponibles. | [0202](dev/adr/0202-desktop-wallpaper-images.md) | [#297](https://github.com/Kazo-ca/HackerOs/issues/297), [#298](https://github.com/Kazo-ca/HackerOs/issues/298) |

### Réseau et serveur

| Fonctionnalité | Description | ADR | Issue |
| --- | --- | --- | --- |

### Applications optionnelles portées

Des applications open source connues, adaptées à HackerOS, chacune dans son
propre dépôt. Elles ne font pas partie de l'OS : elles s'installeront à part,
par `hackpkg` puis par le Hack Store.

| Application | Description | Dépôt | Issue |
| --- | --- | --- | --- |
| Chess | Échecs, basés sur chess.js ; première application prévue du catalogue. | [HackerOs-App-Chess](https://github.com/Kazo-ca/HackerOs-App-Chess) | [#2](https://github.com/Kazo-ca/HackerOs-App-Chess/issues/2), [#3](https://github.com/Kazo-ca/HackerOs-App-Chess/issues/3) |
| Webamp | Lecteur de musique dans le style de Winamp. | [HackerOs-App-Webamp](https://github.com/Kazo-ca/HackerOs-App-Webamp) | À venir |
| IRC | Client de discussion IRC (KiwiIRC). | [HackerOs-App-IRC](https://github.com/Kazo-ca/HackerOs-App-IRC) | À venir |
| EmulatorJS | Émulateur de consoles rétro. | [HackerOs-App-EmulatorJS](https://github.com/Kazo-ca/HackerOs-App-EmulatorJS) | À venir |
| BoxedWine | Exécuter d'anciens programmes Windows dans l'OS. | [HackerOs-App-BoxedWine](https://github.com/Kazo-ca/HackerOs-App-BoxedWine) | À venir |

## Garder ce document à jour

Toute PR vers `main` qui livre, modifie ou planifie une fonctionnalité met à
jour ce document **dans les deux langues** :

- une fonctionnalité livrée passe de « En cours de développement » à
  « Fonctionnalités disponibles » ;
- une nouvelle issue `TODO` ou une nouvelle ADR de fonctionnalité ajoute une
  ligne dans « En cours de développement » ;
- le lien vers la PR d'une ADR est remplacé par le lien vers l'ADR une fois
  celle-ci fusionnée.

Le modèle de PR ([`.github/pull_request_template.md`](../../../.github/pull_request_template.md))
contient une case pour le rappeler.
