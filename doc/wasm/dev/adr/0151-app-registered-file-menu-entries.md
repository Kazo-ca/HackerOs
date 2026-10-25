# ADR 0151 : entrées de menu de fichiers inscrites par les apps

## Statut

Accepté le 2026-10-19.

- **Décision de Benoit :** plan [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)
  validé le 2026-10-19, questions Q4 et Q4 bis (inscription à l'installation,
  résultat lu au démarrage, comme le registre de Windows), Q7 (filtre `types`
  et nombre d'éléments), Q8 (`after` et `afterGroup`), Q9 (ordre
  d'installation), Q10 (sous-menus par `parent`), Q11 (`selection`), Q12
  (groupe commun), Q13 (`target`), Q15 (capacité et réglage), Q16
  (identifiants courts), Q17 (« Ouvrir avec » en sous-menu), Q18 (portée
  `Desktop`), Q19 (titre = clé de ressource).
- **S'appuie sur :** l'ADR 0150 (modèle du menu), l'ADR 0096 (intents définis
  par les apps), l'ADR 0121 (associations par utilisateur), l'ADR 0104
  (localisation du manifeste).

## Contexte

Les entrées « UnZip Here » et « Publish as a web site… » sont codées dans
l'explorateur. Une autre app ne peut rien ajouter au menu d'un fichier. Benoit
veut le modèle de Windows : chaque app déclare ses entrées, l'OS les range
dans les menus du contrôle de fichiers (fichier, dossier, vide, bureau), sans
exécuter le code de l'app pour construire un menu.

## Décision

### 1. Menus par défaut et identifiants

Le contrôle de fichiers construit quatre menus sur le modèle de l'ADR 0150 :
`File`, `Directory`, `Background` (le vide) et `Desktop` (= `Background` plus
les entrées du bureau, ADR 0152). Leur contenu est celui du plan §7. Les
identifiants sont courts et stables, avec des constantes dans le SDK
(`HackFileMenuItems.Open` = `open`, `OpenWith` = `open-with`, `Cut`, `Copy`,
`Paste`, `PasteInto`, `Rename`, `Delete`, `DeletePermanently`, `NewFolder`,
`Refresh`, `Properties`, `View`, `Sort`, `Background`). Ils font partie de la
promesse SemVer du SDK (ADR 0114 §2, « observable par une app »).

« Ouvrir avec » est toujours un sous-menu : les apps qui déclarent le type
(associations de l'ADR 0121, défaut de l'utilisateur en tête), un séparateur,
puis « Choisir une autre application… », qui ouvre le dialogue fourni
(ADR 0148 §6) avec « Toujours utiliser cette app ».

### 2. Déclaration

Une méthode statique de la classe `[AppManifest]`, marquée
`[RegisterFileContextMenu]`, reçoit un `RegisterFileContextMenuContext` et
appelle `Register(…)` :

| Paramètre | Rôle | Défaut |
|---|---|---|
| `title` | Clé de ressource de l'app (`Resources/AppManifest.<culture>.resx`, ADR 0104) | requis |
| `name` | Identifiant unique dans l'app | requis |
| `target` | `"self"` ou l'identifiant d'une autre app qui déclare l'intent | `"self"` |
| `intent` | Intent défini par l'app cible (ADR 0096) ; absent sur une entrée parent | aucun |
| `icon` | `HackIcons.FromImage(asset)` ou `HackIcons.Vector(bibliothèque, nom)` | aucune |
| `scope` | `FileMenuScope.File`, `Directory`, `Background`, `Desktop`, combinables | `File` |
| `types` | Extensions (`.zip`), types de média (`application/zip`, `image/*`), `*` | tous |
| `selection` | `MenuSelection.All`, `Each`, `Single` | `All` |
| `first`, `after`, `afterGroup`, `parent` | Position (§4) | aucune |

Le contexte **n'enregistre que les appels** : il ne donne accès à aucun
service, fichier ou réseau. Une méthode qui lève une exception fait échouer
l'inscription de toutes les entrées de l'app (rien de partiel).

### 3. Inscription à l'installation

1. **Apps intégrées** : le générateur de manifeste
   (`HackerOs.Tools.ManifestGenerator`) appelle la méthode **au build** et
   écrit le résultat dans `manifest.json` (propriété facultative
   `contextMenus`, omise sans entrée) et `BuiltInApplications.json`. Le
   schéma, la liste blanche de `AppManifestJsonSerializer`, le canonique de
   test et le validateur changent ensemble (common-pitfalls §2).
2. **Apps installées plus tard** : l'OS exécute la méthode **une fois à
   l'installation** (et à chaque mise à jour de l'app), dans le contexte qui
   enregistre.
3. Le résultat est écrit dans `/etc/hackeros/context-menus.d/<appId>.json`
   (`root`, `0644`) : les entrées, les titres EN et FR copiés depuis les
   ressources, et le **rang d'installation** (entier croissant). Les apps
   intégrées y sont écrites au premier démarrage et à chaque mise à jour de
   l'OS, dans l'ordre de `BuiltInApplications.json`.
4. Le démarrage lit ce dossier : **aucun code d'app ne tourne pour construire
   un menu**. Un fichier illisible est ignoré et signalé dans
   `/var/log/syslog`.
5. Désinstaller l'app retire son fichier ; une app désactivée (ADR 0048) voit
   ses entrées masquées sans retirer le fichier.

### 4. Placement

1. Les entrées par défaut forment leurs groupes (§1, plan §7).
2. `first: true` : un **groupe commun en tête** du menu, suivi d'un
   séparateur ; plusieurs apps y partagent ce groupe.
3. `after: "X"` : juste après X, **dans le groupe de X**, sans séparateur.
4. `afterGroup: "X"` : un **nouveau groupe** juste après le groupe de X.
5. Sans position : le **groupe commun des apps**, juste avant le groupe de
   « Propriétés ».
6. Plusieurs entrées à la même place : **ordre d'installation** des apps, puis
   ordre de déclaration dans l'app.
7. `X` désigne un identifiant par défaut (§1) ou le `name` d'une entrée de la
   même app. Une ancre absente du menu courant (par exemple `paste-into` sur
   un fichier) envoie l'entrée dans le groupe commun.
8. `parent: "n"` : l'entrée va dans le sous-menu de l'entrée `n` de la même
   app ; `n` n'a pas d'intent ; profondeur totale 3 au plus (ADR 0150 §2).
   `first`, `after` et `afterGroup` sont refusés sur une entrée qui a un
   `parent`.
9. Les filtres (`scope`, `types`, `selection`), les apps désactivées et les
   entrées masquées s'appliquent **avant** le placement ; les séparateurs se
   replient ensuite (ADR 0150 §3).

### 5. Vérifications

Le générateur (au build) et l'OS (à l'installation) vérifient : `name`
unique dans l'app ; `parent` existant, de la même app et sans intent ;
profondeur ≤ 3 ; `after`/`afterGroup` qui visent un identifiant par défaut ou
une entrée de la même app ; une seule position par entrée ; `target` égal à
`"self"` ou à une app qui déclare l'intent ; `intent` présent sur toute entrée
sans enfant ; `title` présent dans les ressources EN. Une erreur est un
diagnostic `HOSAPP` au build et un refus de l'installation de l'app.

### 6. Capacité et réglage

- La capacité **`shell.context-menu`** (normale) est exigée pour inscrire des
  entrées. Sans elle, le générateur produit un diagnostic et l'OS ignore les
  entrées.
- Paramètres ▸ Applications liste les entrées de chaque app et permet de les
  masquer pour l'utilisateur, dans `~/.config/hackeros/context-menus.config`
  (`[hidden]`, `<appId>.<name>=true`). Masquer une entrée parente masque ses
  enfants.

### 7. Exécution

- `selection: All` : un lancement avec tous les chemins ; `Each` : un
  lancement par élément ; `Single` : l'entrée est masquée si plusieurs
  éléments sont choisis.
- L'app cible reçoit l'intent : `AppContext.Intent`, `IntentId` = l'intent
  déclaré, charge utile JSON `{"paths":["…"],"scope":"File"}`. Une instance
  unique déjà ouverte le reçoit par `IntentReceived` (ADR 0096).
- Sans intent déclaré par la cible (`target: "self"` sur une app qui n'en a
  pas encore), l'OS passe les arguments de lancement
  `["<intent>", "<chemin1>", "<chemin2>", …]`.
- `target` est revérifié au clic : si l'app cible n'est plus installée ou ne
  déclare plus l'intent, l'entrée est masquée.
- L'app est lancée **avec ses propres droits** : l'entrée de menu ne lui donne
  accès à aucun fichier qu'elle n'aurait pas. Une app qui ne peut pas lire un
  chemin reçu reçoit l'erreur habituelle de `HackFile`.

### 8. Entrées existantes migrées

- L'explorateur inscrit « Extraire ici » (`types: [".zip"]`,
  `target: "self"`, intent défini par l'explorateur).
- Web Server Manager (`org.hackeros.web-server-manager`) inscrit « Publier
  comme site web » (portée `Directory`, `target: "self"`, `selection:
  Single`, avec un intent qu'il déclare pour publier un dossier). Son
  lancement actuel avec le dossier en premier argument (ADR 0077 §3) reste
  accepté. L'entrée quitte donc l'explorateur pour l'app qui fait le travail.
- Elles apparaissent donc aussi sur le bureau. `ZipFileContextMenuProvider` et
  `PublishSiteContextMenuProvider` disparaissent.
- Un hôte peut encore ajuster le menu de son instance par l'événement
  `ContextMenuOpening` de `IHackFileView`, avec des callbacks (ADR 0150 §4).

## Conséquences

- Construire un menu ne charge aucune app : le coût est la lecture d'un
  dossier au démarrage.
- Une entrée d'app est visible partout où le contrôle de fichiers est affiché
  (explorateur, bureau, dialogues, toute app hôte).
- Le chantier 4 du plan livre le générateur, `context-menus.d`, le placement,
  la capacité, la page de Paramètres et la migration des deux entrées.
- [`capabilities.md`](../../framework/capabilities.md) gagne `shell.context-menu`.

## Alternatives écartées

- **Exécuter le code de l'app au démarrage** : charge toutes les apps et
  ralentit le démarrage ; Benoit a choisi l'inscription à l'installation.
- **Un groupe par app sans position** : menu haché de séparateurs ; Benoit a
  choisi un groupe commun.

## Références

- ADR 0048, 0096, 0104, 0114, 0121, 0148, 0150, 0152.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §7
  et §8, [`app-intents.md`](../../framework/app-intents.md),
  [`localization.md`](../../framework/localization.md).
