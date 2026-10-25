> [English version](../../../../en/wasm/dev/adr/0159-app-menu-entries-implementation-adjustments.md)

# ADR 0159 : entrées de menu des apps, ajustements d'implémentation

## Statut

Accepté le 2026-10-20.

- **Chantier :** `desk/4-app-menu-entries` (plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §12,
  chantier 4).
- **Ajuste :** l'ADR 0151 (§2 titres, §3 inscription, §4.4 `afterGroup`, §5
  vérifications, §6 réglage, §8 dernier point). Ses autres décisions restent
  vraies et sont livrées telles quelles.

## Contexte

L'ADR 0151 décide comment une app ajoute des entrées aux menus des fichiers :
une méthode `[RegisterFileContextMenu]`, exécutée au build pour les apps
intégrées et à l'installation pour les autres, un dossier
`/etc/hackeros/context-menus.d` lu au démarrage, un placement par `first`,
`after`, `afterGroup` et `parent`, la capacité `shell.context-menu` et un
réglage pour masquer les entrées. En l'implémentant, quelques points ont dû
être précisés ou corrigés. Cet ADR les consigne ; le guide d'utilisation est
[`context-menu.md`](../../framework/context-menu.md) (section « Entrées des apps »).

## Décision

### 1. Une seule inscription : au build, pour toutes les apps

Toutes les apps de l'écosystème sont compilées dans l'hôte
(`HackerOs.App.Build`) : il n'existe pas d'installation de paquet à
l'exécution où l'OS exécuterait la méthode (ADR 0151 §3.2). Le générateur de
manifeste l'exécute donc pour **chaque** app, au build, et écrit les entrées
dans `manifest.json` (`contextMenus`, omise sans entrée) puis dans
`BuiltInApplications.json`. Une app installée plus tard par un paquet suivra
le même chemin : son `manifest.json` porte déjà ses entrées, l'OS n'exécute
jamais son code pour un menu.

Le générateur :

- cherche la méthode **déclarée sur la classe `[AppManifest]` elle-même**
  (pas une méthode héritée), par le nom complet de l'attribut ; il en faut
  une au plus, `static void M(RegisterFileContextMenuContext context)` ;
- l'exécute **deux fois** et compare les deux résultats : une méthode qui
  dépend de l'heure, d'un fichier ou d'un service est refusée ;
- refuse toute l'inscription si la méthode lève une exception (rien de
  partiel, ADR 0151 §2).

### 2. Le dossier `/etc/hackeros/context-menus.d`

Au démarrage et à chaque changement des apps installées
(`AppInstallationService.Changed`), `FileContextMenuRegistry` aligne le
dossier sur les manifestes, puis le relit :

- un fichier `<appId>.json` par app qui a des entrées : `schemaVersion`,
  `appId`, `appVersion`, `rank`, `source` (empreinte SHA-256 des entrées) et
  `entries` ;
- le fichier n'est réécrit que si l'empreinte ou la version de l'app change ;
  il garde alors son **rang**. Une nouvelle app prend le rang suivant, dans
  l'ordre d'activation du catalogue ; c'est l'« ordre d'installation » de
  l'ADR 0151 §4.6 ;
- le fichier d'une app désinstallée pour toute la machine, ou inconnue du
  catalogue, est retiré ; une app désactivée ou désinstallée pour
  l'utilisateur garde son fichier, ses entrées sont seulement masquées ;
- un fichier illisible, qui nomme une autre app, dont l'app ne demande pas
  `shell.context-menu`, ou qui enfreint une règle de l'ADR 0151 §5, est
  ignoré et signalé dans `/var/log/syslog` (il n'y a pas d'installation à
  refuser, ADR 0151 §5 dernière phrase) ;
- chaque écriture est une opération système auditée de l'OS.

### 3. Titres : `Resources/AppManifest.resx` en anglais

`title` est une clé de ressource. Le texte anglais vit dans le fichier
**neutre** `Resources/AppManifest.resx`, les traductions dans
`Resources/AppManifest.<culture>.resx` (ADR 0104). Le générateur copie chaque
titre dans le manifeste, par culture (`titles: [{culture, text}]`, l'anglais
obligatoire). Dans un projet qui déclare plusieurs apps, la clé est préfixée
de l'identifiant de l'app, comme les autres clés du manifeste. Ces clés ne
sont pas des chaînes de manifeste localisées au sens de l'ADR 0104 : le
générateur de localisation les ignore. Le menu choisit le titre de la culture
de la session, puis de sa culture parente, puis l'anglais.

### 4. Le menu appliqué par l'OS, quel que soit le fournisseur

Le fournisseur du contrôle de fichiers (ADR 0156, 0158) pose
`HackMenuDefinition.FileMenu` (`HackFileMenuContext` : portée, éléments,
dossier affiché) sur le menu qu'il ouvre. `ContextMenuService` ajoute les
entrées des apps **après** le `Opening` du menu, donc après le
`ContextMenuOpening` de l'hôte (ADR 0151 §8, dernier point : l'hôte ajuste
le menu par défaut, pas les entrées des apps). Un fournisseur tiers qui ne
pose pas `FileMenu` n'a pas les entrées des apps ; le contrat
`hackeros.file-view` le demande ([`embedded-components.md`](../../framework/embedded-components.md)).
Une erreur en ajoutant les entrées est écrite dans le journal et le menu
s'ouvre avec ses entrées par défaut.

Les identifiants des entrées dans le menu sont `app:<appId>/<name>`. Au clic,
l'OS lance la cible par `AppLifecycleOrchestrator` ; l'expéditeur de l'intent
est l'OS (`org.hackeros.shell`), pas l'app qui montre le menu. Dans l'espace
vide (portées `background` et `desktop`), le chemin transmis est le dossier
affiché.

### 5. Filtres

- Un fichier est accepté par la portée `file` et les types ; un dossier par
  la portée `directory` et, si des types sont déclarés, par `*` ou
  `inode/directory`. Une sélection mélangée demande les deux portées.
- Le menu de l'espace vide du bureau est le menu de l'espace vide plus celui
  du bureau (ADR 0152) : une entrée `background` y paraît aussi, une entrée
  `desktop` n'y paraît que là.
- Une entrée enfant inscrite sans portée prend celle de son parent ; un
  enfant ne peut pas paraître là où son parent ne paraît pas.

### 6. `afterGroup` : un groupe par ancre

Plusieurs entrées `afterGroup: "X"` (de la même app ou d'apps différentes)
partagent **un seul** nouveau groupe après celui de X, dans l'ordre de rang
puis de déclaration, plutôt qu'un groupe chacune. Une entrée ancrée à une
entrée de sa propre app déclarée plus loin est placée après elle ; une ancre
absente du menu envoie l'entrée au groupe commun (ADR 0151 §4.7).

### 7. Diagnostics

| Code | Cas |
|---|---|
| `HOSAPP0026` | Méthode mal formée, plusieurs méthodes, exception, deux exécutions différentes, titre absent de `AppManifest.resx`, règle de l'ADR 0151 §5 |
| `HOSAPP0027` | Entrées sans la capacité `shell.context-menu` |
| `HOSAPP0028` | (hôte) `target` vise une app absente de l'hôte ou qui ne déclare pas l'intent |

### 8. Le réglage : Paramètres ▸ Menus des fichiers

Le réglage de l'ADR 0151 §6 est un onglet à part, **Menus des fichiers**,
plutôt qu'une section de Paramètres ▸ Applications : il liste les entrées de
toutes les apps, groupées par app, sous-menus sous leur parent, avec une case
par entrée. Il passe par une API publique du SDK, `HackFileMenuEntries`
(`GetEntriesAsync`, `SetHiddenAsync`), qui exige `apps.manage`, comme
`HackComponentProviders` ; le choix est celui de l'utilisateur de la session
(`~/.config/hackeros/context-menus.config`), aucun administrateur n'est
demandé. Masquer une entrée inconnue est refusé ; la réafficher reste
possible pour nettoyer une ligne restée.

### 9. Entrées migrées

- « Extraire ici » (`extract-here`) : `types: [".zip", "application/zip"]`,
  `after: "open"`, intent `org.hackeros.file-explorer.extract-here.v1`.
  L'explorateur s'ouvre sur le dossier de l'archive et l'extrait dans un
  dossier de son nom (`<nom>.d` pour un fichier `application/zip` sans
  extension).
- « Publier comme site web… » (`publish-site`) : portée `Directory`,
  `selection: Single`, intent `org.hackeros.web-server-manager.publish-site.v1`,
  reçu au lancement ou par `IntentReceived`.
- Les entrées que l'explorateur ajoutait lui-même dans son
  `ContextMenuOpening` disparaissent.

## Conséquences

- La capacité `shell.context-menu` est une capacité normale du catalogue
  ([`capabilities.md`](../../framework/capabilities.md)).
- Les entrées sont les mêmes dans l'explorateur, les dialogues de fichiers,
  toute app qui affiche `HackFileView` et, au chantier 5, le bureau.
- Le SDK gagne `RegisterFileContextMenuAttribute`,
  `RegisterFileContextMenuContext`, `MenuSelection`, `HackFileMenuIntent`,
  `HackFileMenuContext`, `HackMenuDefinition.FileMenu` et
  `HackFileMenuEntries` (non livrés, pour le SDK 2.0 du chantier 6).

## Alternatives écartées

- **Exécuter la méthode au démarrage de l'OS** : refusé par l'ADR 0151.
- **Un groupe par entrée `afterGroup`** : hache le menu de séparateurs pour
  des entrées voulues au même endroit.
- **Une section de Paramètres ▸ Applications** : obligerait à ouvrir chaque
  app pour retrouver une entrée ; une liste unique répond mieux à « qu'est-ce
  qui encombre mon menu ? ».

## Références

- ADR 0104, 0150, 0151, 0152, 0153, 0154, 0155, 0156, 0158.
- [`context-menu.md`](../../framework/context-menu.md),
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md),
  [`app-intents.md`](../../framework/app-intents.md).
