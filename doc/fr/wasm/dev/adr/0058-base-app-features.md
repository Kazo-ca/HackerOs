> [English version](../../../../en/wasm/dev/adr/0058-base-app-features.md)

# ADR 0058 : Fonctionnalités des apps de base

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 4 — SDK complet et apps de base
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 8.2,
  décision D22).
- **S'appuie sur :** ADR 0052 (corbeille), ADR 0050 et 0051 (shell), ADR 0055
  (SDK public uniquement), ADR 0056 (API du SDK), ADR 0057 (`/proc`).
- Contient le tableau des chantiers de la phase 4 (ADR 0055 à 0058).

## Contexte

Le plan fixe, pour chaque app de base, ce qui existe et doit rester, et ce qui
manque par rapport au legacy (`src/apps/`). État constaté sur `rebuild/base`
(dfd9c47) :

| App | Existe | Manque |
|---|---|---|
| Explorateur (`HackerOs.Apps.FileExplorer`) | navigation, historique, création, renommage, corbeille (ADR 0052), zip, propriétés, « ouvrir avec », envoi et téléchargement ; glisser-déposer d'un élément **sur un dossier** de la vue (`FileView.OnItemDropAsync` : déplacer, Ctrl pour copier) | couper, copier, coller ; dépôt sur le fond de la vue, l'arbre ou le fil d'Ariane ; dépôt de fichiers venant du système hôte |
| Gestionnaire de tâches (`HackerOs.Apps.SystemMonitor`) | onglets Overview, Processes, Services, Hardware ; kill ; mode de démarrage des services | performance avec graphiques, disque, réseau (onglets du legacy : `performance`, `disk`, `network`), détail par processus, filtre, sélection multiple |
| Éditeur de texte (`HackerOs.Apps.TextEditor`) | ouvrir, enregistrer, enregistrer sous, recherche (`TextEditorFindEngine`), détection des conflits | remplacer, tout remplacer (le legacy les avait) |
| Console (`HackerOs.Apps.Terminal`, `TerminalPanel`) | historique, répertoire courant, complétion des **noms de commandes** par Tab (phase 3) | complétion des chemins, plusieurs onglets |

## Décision

Les interfaces restent en anglais, comme le reste des apps. Chaque app
n'utilise que le SDK (ADR 0055).

### 1. Explorateur de fichiers

**Couper, copier, coller.**

- Raccourcis Ctrl+X, Ctrl+C, Ctrl+V ; entrées « Cut », « Copy », « Paste »
  dans le menu contextuel et la barre d'outils. « Paste » est grisé quand le
  presse-papiers ne contient pas de fichiers.
- Couper et copier écrivent la sélection dans le presse-papiers de l'OS par
  `HackClipboard.SetFileDropListAsync` (ADR 0056, section 12). Une autre
  fenêtre de l'explorateur, ou une autre app, peut donc coller.
- Les éléments coupés s'affichent atténués tant qu'ils sont dans le
  presse-papiers. Couper ne supprime rien avant le collage.
- Coller vise le dossier courant, ou le dossier sur lequel porte le menu
  contextuel (« Paste Into Folder »).
- Copier : `HackFile.CopyAsync` / copie récursive d'un dossier. Couper :
  `HackFile.MoveAsync` / `HackDirectory.MoveAsync`, puis le presse-papiers est
  vidé si tout a réussi. Coller un élément coupé dans son propre dossier ne
  fait rien.
- **Noms en conflit**, comme Nautilus : en copie, `rapport.txt` devient
  `rapport (copy).txt`, puis `rapport (copy 2).txt`… En déplacement, une
  fenêtre demande « Replace », « Keep both » (même règle de nom) ou « Skip »,
  avec « Apply to all ».
- Copier ou déplacer un dossier dans lui-même ou dans un de ses descendants
  est refusé avant toute écriture.
- Une erreur sur un élément n'arrête pas les autres ; un résumé des échecs
  s'affiche à la fin (notification), avec la raison
  (`HackUnauthorizedAccessException`, système en lecture seule comme `/proc`…).

**Glisser-déposer**, en complément de l'existant :

- Dépôt sur le **fond** de la vue : vers le dossier courant.
- Dépôt sur un nœud de l'**arbre** ou un segment du **fil d'Ariane** : vers ce
  dossier.
- Entre deux fenêtres de l'explorateur : le format
  `application/vnd.hackeros.file-drag+json` de `FileView` est déjà lisible par
  toute instance ; les mêmes règles s'appliquent.
- Sur la vue Corbeille : envoi à la corbeille (ADR 0052).
- Modificateurs : déplacer par défaut, **Ctrl** pour copier (règle actuelle de
  `FileView`). Le curseur l'indique pendant le survol.
- **Fichiers du système hôte** (glissés depuis le bureau réel de
  l'utilisateur) : envoi dans le dossier cible par le chemin d'envoi
  existant. Conflits de noms comme pour le collage.
- Ces ajouts vivent dans `HackerOs.AppSdk.FileView` quand ils concernent la
  vue (fond, arbre), pour que toute app qui affiche une `FileView` en profite.

### 2. Gestionnaire de tâches

Réécrit sur `HackProcess`, `HackServiceController` et `HackDriveInfo`
(ADR 0056, 0057). Plus d'injection de `IProcessManager`, `AppCatalog`,
`AppLifecycleOrchestrator` ou `IEventBus`.

- **Onglets :** Processes, Performance, Disk, Network, Services. L'onglet
  « Hardware & System Info » actuel devient une section de Performance
  (contenu de `/proc/cpuinfo` et `/proc/meminfo`). L'onglet Overview disparaît :
  son résumé passe en tête de Performance.
- **Rafraîchissement :** lecture de `/proc` toutes les secondes, comme
  `top`, suspendue quand la fenêtre est réduite. Les débits sont des
  différences entre deux lectures.
- **Processes :** colonnes PID, nom, utilisateur, état, CPU, mémoire,
  lecture et écriture disque par seconde ; tri par colonne ; **filtre** texte
  sur nom, id d'app et PID ; **sélection multiple** (Ctrl, Maj, Ctrl+A) ;
  « End task » sur la sélection, avec confirmation qui liste les processus.
  Un refus (`process.manage` absent pour un processus) est signalé par
  processus, sans arrêter les autres.
- **Détail d'un processus** (double clic ou panneau latéral) : `status`
  complet, ligne de commande, répertoire courant, environnement si lisible,
  compteurs `io`, processus parent et enfants.
- **Performance :** graphique du CPU total et, au choix, par cœur ; graphique
  de la mémoire utilisée ; 60 secondes d'historique.
- **Disk :** espace occupé et libre de chaque `HackDriveInfo`, graphique des
  débits de lecture et d'écriture de `/proc/diskstats`.
- **Network :** graphique des débits reçus et émis par interface de
  `/proc/net/dev`, compteurs de paquets et d'erreurs.
- **Services :** l'existant (liste, démarrer, arrêter, mode de démarrage),
  sur `HackServiceController`.
- **Graphiques :** composants Blazor natifs en SVG avec CSS isolé, sans
  bibliothèque de graphiques (consignes de `AGENTS.md` sur MudBlazor). Les
  onglets eux-mêmes restent des `MudTabs`.

### 3. Éditeur de texte

- **Panneau Rechercher et remplacer**, ouvert par Ctrl+H (Ctrl+F ouvre la
  recherche seule, comme aujourd'hui) : champ « Replace with », boutons
  « Replace » et « Replace All ».
- Options partagées avec la recherche : « Match case » et « Whole word ».
  Pas d'expressions régulières dans cette phase.
- « Replace » remplace l'occurrence sélectionnée si elle correspond, puis
  sélectionne la suivante ; sinon il sélectionne d'abord la suivante.
- « Replace All » remplace toutes les occurrences en **une seule
  modification** du document et affiche leur nombre (« 12 replacements »).
- La logique vit dans `TextEditorFindEngine` (`Replace`, `ReplaceAll`), testée
  sans interface.
- Un remplacement rend le document modifié ; la détection des conflits
  d'écriture à l'enregistrement ne change pas.

### 4. Console

**Complétion par Tab**, faite par le shell (`IHackShell.CompleteAsync`,
ADR 0056 section 7), comme `bash` et non par l'émulateur de terminal :

- Premier mot d'une commande (début de ligne, après `|` ou `&&`) : noms des
  commandes installées et de leurs alias.
- Autres mots : chemins, relatifs au répertoire du shell, avec `~` développé.
  Un dossier est complété avec un `/` final. Les fichiers cachés ne sont
  proposés que si le préfixe commence par `.`.
- Un seul candidat : il est inséré, suivi d'une espace (ou du `/` d'un
  dossier). Plusieurs : le plus long préfixe commun est inséré ; un second Tab
  liste les candidats sous l'invite, comme `bash`.
- Un chemin illisible (droits, capacité) ne propose rien, sans erreur.

**Onglets :**

- Chaque onglet est un shell distinct (`HackShell.StartAsync`), donc un
  **processus** distinct, enfant du processus du terminal, avec son propre
  répertoire, son environnement, son historique de saisie et son `$?`.
- Ctrl+Maj+T ouvre un onglet dans le répertoire de l'onglet courant ;
  Ctrl+Maj+W ferme l'onglet (et termine son shell et ses commandes) ;
  Ctrl+PgPréc et Ctrl+PgSuiv changent d'onglet ; bouton « + » et croix sur
  chaque onglet.
- Titre de l'onglet : nom de la commande en cours, sinon nom du dossier
  courant (`~` pour le dossier personnel).
- `exit` ferme l'onglet ; fermer le dernier onglet ferme la fenêtre.
- Le panneau terminal de CodeEditor reste à un seul shell.

## Conséquences

- Les quatre apps de base atteignent la parité avec le legacy et le
  dépassent sur la corbeille, le collage entre fenêtres, les mesures réelles
  de disque et de réseau et les onglets de la console.
- Les ajouts de glisser-déposer dans `FileView` profitent aux autres apps qui
  l'affichent (sélecteurs de fichiers, CodeEditor).
- La complétion dans le shell la rend disponible à tout émulateur de
  terminal tiers.

## Alternatives écartées

- **Complétion dans `TerminalPanel`** (comme celle des noms de commandes
  aujourd'hui) : chaque émulateur de terminal devrait la refaire, et elle ne
  verrait pas le répertoire et les alias du shell sans nouvel accès.
- **Bibliothèque de graphiques** (MudBlazor Charts, Chart.js) : trois
  courbes simples ne justifient pas une dépendance ni du JavaScript.
- **Expressions régulières dans « Replace »** : utile, mais absent du legacy ;
  repoussé pour garder le panneau simple.

## Chantiers d'implémentation de la phase 4

Tableau commun aux ADR 0055, 0056, 0057 et 0058. Chaque branche part de
`rebuild/base` et sa PR vise `rebuild/base`. Chaque chantier qui supprime une
violation de l'ADR 0055 retire l'exemption du projet concerné dans le même
changement, et documente ses API dans `docs/app-sdk.md` (créé par
`4-sdk-boundary`).

| Branche | Contenu | Dépend de |
|---|---|---|
| `rebuild/4-sdk-boundary` | ADR 0055 sections 1 à 3 et 6 : cible MSBuild `HOSAPP0002` / `HOSSDK0001`, contrôle `HOSAPP0003` dans le générateur de manifeste, test de sources `IJSRuntime`, liste d'exemptions des projets en défaut, retrait des références `Platform.Core` inutilisées (Calculator, HackPaint, IconViewer, TextEditor, Browser). Création de `docs/app-sdk.md`. | — |
| `rebuild/4-sdk-user-and-apps` | ADR 0056 sections 1 à 3 et ADR 0055 section 5 : `HackUser`, `HackEnvironment.GetFolderPath`, `HackApps`, `HackAppManagement`, agent d'authentification de l'OS. Migration de FileExplorer, TextEditor, CodeEditor (utilisateur) et de l'onglet « Installed Apps » de Paramètres. | `4-sdk-boundary` |
| `rebuild/4-sdk-network` | ADR 0056 section 6 : `IAppNetworkGateway`, `HackDns`, `HackPing`, `HackHttpClient`, `HackTcpClient`, repli réel dans Platform Core, `ShellErrorText` dans le SDK. Migration de Browser, `ping`, `curl`, `cat`, `nmap` ; `HackerOs.Commands` sans `Platform.Core` ni `Server.Contracts`. | `4-sdk-boundary` |
| `rebuild/4-sdk-shell-host` | ADR 0056 section 7 : capacité `shell.host`, `HackShell`, `IHackShell` (sans la complétion des chemins), `HackCredentialPrompt` ; `TerminalPanel` et `HackerOs.AppSdk.Terminal` sans `Platform.Core` ; `IsSystemApp="true"` pour CodeEditor. | `4-sdk-boundary` |
| `rebuild/4-sdk-system-admin` | ADR 0056 sections 8 à 11 : `HackSettings.OpenSystemAsync`, `HackPermissionAdministration`, `HackServerConnection`, `HackSystem`, `HackStorageDiagnostics`, capacités `permissions.manage`, `server.connection.manage`, `system.power`, `system.storage.inspect`. Migration du reste de Paramètres et de Diagnostic. | `4-sdk-user-and-apps` |
| `rebuild/4-proc-metrics` | ADR 0057 : comptage dans la passerelle fichier, la persistance et la passerelle réseau ; `/proc/stat`, `/proc/<pid>/io`, `/proc/diskstats`, `/proc/net/dev`, `/proc/mounts` ; `StorageCapacityBytes` ; `HackDriveInfo`. Mise à jour de `virtual-proc-and-dev.md`. | `4-sdk-network` |
| `rebuild/4-file-explorer` | ADR 0056 section 12 et ADR 0058 section 1 : `HackClipboard` et opération couper/copier, couper-copier-coller, conflits de noms, glisser-déposer sur le fond, l'arbre, le fil d'Ariane, la corbeille et depuis l'hôte. | `4-sdk-user-and-apps` |
| `rebuild/4-text-editor` | ADR 0058 section 3 : panneau Rechercher et remplacer, `Replace` et `ReplaceAll` dans `TextEditorFindEngine`. | `4-sdk-user-and-apps` |
| `rebuild/4-terminal` | ADR 0058 section 4 : `IHackShell.CompleteAsync` (commandes et chemins), onglets de la console. | `4-sdk-shell-host` |
| `rebuild/4-task-manager` | ADR 0056 sections 4 et 5 et ADR 0058 section 2 : `HackProcess`, `HackServiceController`, réécriture de SystemMonitor (onglets, graphiques, détail, filtre, sélection multiple). | `4-proc-metrics`, `4-sdk-user-and-apps` |
| `rebuild/4-sdk-boundary-final` | ADR 0055 section 6 : suppression du mécanisme d'exemption, vérification que tout `Apps/` et tout le SDK passent sans exemption, plan et index de la documentation à jour. | tous les autres chantiers de la phase 4 |

Ordre :

1. `4-sdk-boundary` seul.
2. En parallèle : `4-sdk-user-and-apps`, `4-sdk-network`, `4-sdk-shell-host`.
3. Dès que leur dépendance est mergée, en parallèle :
   `4-sdk-system-admin`, `4-file-explorer` et `4-text-editor` (après
   `4-sdk-user-and-apps`), `4-proc-metrics` (après `4-sdk-network`),
   `4-terminal` (après `4-sdk-shell-host`).
4. `4-task-manager` (après `4-proc-metrics` et `4-sdk-user-and-apps`).
5. `4-sdk-boundary-final`.

## Références

- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 8.2.
- Legacy : `src/apps/file-explorer.ts`, `src/apps/system-monitor.ts`,
  `src/apps/text-editor.ts`, `src/apps/terminal.ts`.
- ADR 0016, 0050, 0051, 0052, 0055, 0056, 0057.
- `Shared/HackerOs.AppSdk.FileView/FileView.razor.cs`,
  `Apps/System/HackerOs.Apps.TextEditor/TextEditorFindEngine.cs`,
  `Shared/HackerOs.AppSdk.Terminal/TerminalPanel.razor`.
