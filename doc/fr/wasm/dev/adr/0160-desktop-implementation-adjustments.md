> [English version](../../../../en/wasm/dev/adr/0160-desktop-implementation-adjustments.md)

# ADR 0160 : bureau, ajustements d'implémentation

## Statut

Accepté le 2026-10-20.

- **Chantier :** `desk/5-desktop` (plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §12,
  chantier 5).
- **Ajuste :** l'ADR 0152 (§1 hôte du bureau, §2 grille, §3 positions et
  options, §4 icônes système, §5 raccourcis, §6 menu, §8 Paramètres). Ses
  décisions restent vraies et sont livrées telles quelles ; cet ADR fixe ce
  qu'elles laissaient ouvert.

## Contexte

L'ADR 0152 décide que le bureau affiche `~/Desktop` par le contrôle de
fichiers en **mode Bureau**, avec des positions mémorisées dans
`~/.config/hackeros/desktop.config`, les icônes Dossier perso et Corbeille, des
raccourcis `.desktop` et la portée `Desktop` du menu. En l'écrivant, plusieurs
points ont dû être fixés : qui exécute le bureau et avec quels droits, ce que
le contrat du contrôle porte pour le mode Bureau, où vit le calcul de la
grille, comment le menu Démarrer crée un raccourci. Le guide d'utilisation est
[`desktop.md`](../desktop.md).

## Décision

### 1. Le bureau est un processus : l'app système `org.hackeros.desktop`

Le contrôle de fichiers s'exécute avec les droits de l'app qui l'affiche
(ADR 0148 §4) : il lui faut un contexte d'app. Le bureau est donc une app
système, **Bureau** (`org.hackeros.desktop`, projet `HackerOs.Apps.Desktop`),
cachée du menu Démarrer, à instance unique, qui n'utilise que le SDK public.
C'est le rôle d'`explorer.exe` sous Windows ou de `xfdesktop` sous Xfce.

- La coquille de bureau (`DesktopShell`) la démarre à l'ouverture de la
  session (`DesktopSurfaceHost`, dans `DesktopArea.BackgroundContent`) par le
  chemin normal (`AppLifecycleOrchestrator.LaunchAsync`) : un vrai processus,
  visible dans `ps` et le gestionnaire des tâches, avec les capacités de son
  manifeste pour l'utilisateur de la session.
- La coquille rend son composant d'entrée **hors de toute fenêtre**, dans la
  couche de fond, avec son contexte et une portée de menus en cascade, comme
  `WindowAppRenderer` le fait dans une fenêtre. Avant le lancement,
  `WindowLaunchCoordinator.ReserveShellSurface(appId)` réserve l'app comme
  surface de la coquille : `PresentAsync` ne lui ouvre jamais de fenêtre, quel
  que soit celui qui la lance (réservation comptée, libérée quand la coquille
  se ferme).
- `kill` ou « Fin de tâche » arrête le bureau : les icônes disparaissent. S'il
  **plante** (`Faulted`), la coquille le relance une fois, comme Windows
  relance l'Explorateur ; arrêté volontairement, il revient à la connexion
  suivante.
- Sans l'app (hôte qui ne la référence pas, app désactivée), le fond reste vide.
- La couche de fond devient interactive : `DesktopArea.BackgroundInteractive`
  (nouveau paramètre, `false` par défaut) la rend cliquable et lisible par les
  lecteurs d'écran, et laisse passer les clics de la couche des fenêtres là où
  il n'y a pas de fenêtre.

### 2. Le contrat du mode Bureau

`HackFileView` (ADR 0155 §2) gagne, sans rien changer pour les autres modes :

| Membre | Rôle |
| --- | --- |
| `HackFileViewMode.Desktop` | Grille de bureau. |
| `DesktopOptions` (`HackDesktopOptions`) | Taille des icônes, réorganisation automatique, alignement, tri, icônes système affichées. |
| `DesktopPositions` | Case de chaque élément, par nom (`::home`, `::trash` pour les icônes système). |
| `DesktopPositionsChanged` | Les positions à enregistrer, après un déplacement ou quand un élément nouveau a pris une case. |

L'hôte (l'app Bureau) lit et écrit le fichier ; le fournisseur dessine, place
et déplace. Un fournisseur tiers du contrôle (ADR 0156) reçoit les mêmes
paramètres. Les types sont dans `HackerOs.AppSdk.Files` (sans Blazor) :
`HackDesktopCell`, `HackDesktopIconSize`, `HackDesktopSort`,
`HackDesktopOptions`, `HackDesktopItems`, `HackDesktopLayout`.

### 3. La grille : `HackDesktopLayout`, fonction pure du SDK

Le placement est une fonction pure et testée du SDK, pour que tout fournisseur
place les icônes de la même façon :

- cases en **colonnes de haut en bas, puis de gauche à droite** ; le nombre de
  colonnes et de lignes vient de la taille du bureau mesurée par le
  fournisseur et de la taille des cellules (petite 76 × 84, moyenne 92 × 100,
  grande 116 × 128 pixels) ;
- un élément sans position prend la **première case libre** ; l'ordre des
  éléments nouveaux est : icônes système, puis le tri de `sort` (dossiers
  d'abord) ;
- deux éléments enregistrés sur la même case : le second va à la case libre
  suivante, et c'est enregistré ;
- une position hors de l'écran (bureau rétréci) n'est **pas réécrite** :
  l'élément est montré sur la case libre la plus proche de sa position si
  `align-to-grid=true`, sinon sur la dernière case libre (ADR 0152 §3) ;
- un nom absent du dossier n'est pas enregistré, donc oublié au prochain
  enregistrement ;
- **réorganisation automatique** : les éléments sont rangés sans trou dans
  l'ordre de leurs positions enregistrées (les nouveaux à la fin, dans l'ordre
  du tri) ; glisser une icône l'insère à la case visée, les autres se
  décalent ;
- **déplacement** : les éléments glissés gardent leur écart avec celui qu'on
  tient ; une case occupée envoie l'élément à la case libre suivante.

« Trier par » est une action : elle écrit `sort` et efface `[positions]` ; tout
devient nouveau et se range dans l'ordre du tri.

### 4. `desktop.config`

- Un **fichier de l'utilisateur seulement**, `~/.config/hackeros/desktop.config`,
  comme le fichier de configuration d'un bureau Linux sous `~/.config/` : pas
  de document de l'OS ni de couche `/etc` (le bureau est propre à chaque
  utilisateur, et `[positions]` n'a pas de schéma). Le format est lu et écrit
  par `HackDesktopSettings` (SDK, `HackerOs.AppSdk.Files`) : `Parse`,
  `WithOptions`, `WithPositions`, `ReadAsync`, `SaveOptionsAsync`,
  `SavePositionsAsync`, avec les droits de l'app ambiante.
- `[view]` (`icon-size`, `auto-arrange`, `align-to-grid`, `sort`, `show-home`,
  `show-trash`) est réécrit ligne à ligne (`ConfigDocumentEditor`) : les
  commentaires et les clés inconnues restent. `[positions]` est réécrit en
  entier, à la fin du fichier. Une ligne ou une valeur fausse est ignorée et
  le défaut s'applique ; le dossier est créé s'il manque.
- L'app Bureau suit `~/.config/hackeros` (`HackFileSystemWatcher`) et se
  redessine quand le fichier change (Paramètres, un éditeur, `nano`).
- Une clé est un nom de fichier ; `%`, `=`, `\`, `#`, `;`, `[`, `]`, les
  caractères de contrôle et les espaces de bord y sont encodés en `%XX`.
  La valeur est `colonne,ligne`, à partir de 0.

### 5. Icônes système

Le **fournisseur** dessine Dossier perso et Corbeille en mode Bureau selon
`ShowHome` et `ShowTrash`, avec leurs menus (Ouvrir, Propriétés ; Ouvrir,
Vider la corbeille, Propriétés) et leurs dépôts (dans `~`, à la corbeille).
Ouvrir la Corbeille lance l'explorateur avec l'argument `trash:///`, comme
Nautilus. L'icône de la Corbeille est pleine quand `~/.local/share/Trash/files`
contient un élément. Ces menus ne portent pas `HackMenuDefinition.FileMenu` :
les entrées des apps (ADR 0151) ne s'y ajoutent pas.

### 6. Raccourcis `.desktop`

- `HackDesktopEntry` (SDK) lit et écrit le format : `[Desktop Entry]`,
  `Type=Application`, `Name`, `Exec=<appId> [arguments]` (arguments cités
  comme le dit la spécification freedesktop), `Icon` facultatif.
  `CreateShortcutAsync` écrit `<Nom>.desktop`, `<Nom> (2).desktop`… dans un
  dossier, avec les droits du contexte ambiant.
- Le **fournisseur** les montre partout (bureau, explorateur, dialogues) avec
  le nom et l'icône de l'app, sans extension ; les ouvrir lance l'app
  (`IAppIntentGateway.LaunchAsync`) avec ses arguments ; une app absente du
  catalogue de l'utilisateur grise l'icône, et l'ouvrir l'explique. Un fichier
  mal formé reste un fichier texte. Un hôte qui prend l'ouverture
  (`Opening.Handled`, un dialogue Ouvrir) garde la main.
- **Glisser une app du menu Démarrer** pose le type
  `application/vnd.hackeros.app+json` (`{"appId":…}`) ; le contrôle de fichiers
  l'accepte partout comme un dépôt qui crée un raccourci dans le dossier visé
  (sur le bureau : à la case du dépôt).
- **« Envoyer vers ▸ Bureau (créer un raccourci) »** : le menu Démarrer a
  désormais le menu de l'OS sur chaque app (Ouvrir, Épingler / Désépingler,
  Envoyer vers ▸ Bureau). La coquille envoie l'intent
  `org.hackeros.desktop.create-shortcut.v1` (`{"appId":…}`) à l'instance du
  bureau, qui écrit le fichier avec ses propres droits. L'entrée n'apparaît que
  si le bureau tourne.

### 7. Menu du bureau

En mode Bureau, le fond a la portée `Desktop` (ADR 0159 §5 : les entrées
`background` et `desktop` des apps s'y ajoutent). Le fournisseur donne le menu
du vide (Nouveau dossier, Coller, Actualiser, Propriétés) ; l'app Bureau y
ajoute par `ContextMenuOpening` :

- « Affichage ▸ » : Grandes / Moyennes / Petites icônes, Réorganiser
  automatiquement, Aligner sur la grille, Afficher Dossier perso, Afficher
  Corbeille (cases à cocher) ;
- « Trier par ▸ » : Nom, Taille, Type, Date de modification ;
- « Arrière-plan… » : lance Paramètres avec `--page=appearance` ;
- « Paramètres du bureau… » : lance Paramètres avec `--page=desktop`.

Ouvrir un dossier du bureau ouvre une fenêtre de l'explorateur, pas une
navigation dans le bureau.

### 8. Paramètres ▸ Bureau

Un onglet **Bureau**, après Apparence, règle `[view]` du fichier de
l'utilisateur par `HackDesktopSettings` (taille des icônes, réorganisation
automatique, alignement, icônes système, tri) et peut oublier les positions.
Paramètres accepte `--page=appearance` et `--page=desktop`. L'index de
l'onglet « Application » (`--app=`) est désormais compté à partir des onglets
fixes et de Sauvegarde (il était décalé d'un depuis l'onglet Menus des
fichiers).

### 9. Mobile

`MobileShell` ne démarre pas le bureau : pas d'icônes (ADR 0152 §7).

## Conséquences

- Une app de plus dans l'écosystème et ses modèles d'hôte ; le bureau est
  chargé à la connexion, avec l'assembly de l'explorateur (fournisseur du
  contrôle).
- Nouvelles API non livrées (`PublicAPI.Unshipped.txt`) dans `AppSdk`
  (`HackDesktop*`, `HackDesktopEntry`, `HackDesktopSettings`, `HackAppIcons`,
  `HackFileMenuItems.EmptyTrash`), `AppSdk.Blazor`, `AppSdk.FileView`,
  `Windowing.Blazor` et `Platform.Blazor`, publiées avec le SDK 2.0
  (chantier 6). `empty-trash` rejoint les entrées par défaut connues du
  manifeste (`AppContextMenus.DefaultItemIds`).
- [`settings-files.md`](../../framework/settings-files.md) gagne `desktop.config`,
  [`embedded-components.md`](../../framework/embedded-components.md) les paramètres du mode
  Bureau.

## Alternatives écartées

- **Un contexte d'app fabriqué par la coquille, sans processus** : un manifeste
  hors catalogue casse les passerelles qui lisent le catalogue, et le bureau
  serait invisible dans `ps`.
- **Le bureau dans l'app Explorateur (deux manifestes dans un projet)** : un
  projet à plusieurs apps change les chemins du manifeste et des ressources de
  l'explorateur pour un gain nul.
- **Le fournisseur écrit `desktop.config` lui-même** : un fournisseur tiers
  devrait connaître le format ; l'hôte garde le fichier, le fournisseur ne voit
  que des positions.
- **Des positions en pixels** : l'ADR 0152 fixe des cases ; une case reste
  juste quand la taille des icônes change.

## Références

- ADR 0037, 0043, 0044, 0048, 0148, 0150, 0151, 0152, 0154, 0155, 0156, 0158,
  0159.
- [`desktop.md`](../desktop.md),
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md),
  [`trash.md`](../trash.md), [`settings-files.md`](../../framework/settings-files.md).
