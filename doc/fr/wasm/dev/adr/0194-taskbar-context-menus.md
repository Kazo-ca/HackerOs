> [English version](../../../../en/wasm/dev/adr/0194-taskbar-context-menus.md)

# ADR 0194 : menus contextuels de la barre des tâches

## Statut

Accepté le 2026-10-29, chantier 4 de la série barre des tâches (issue
[#242](https://github.com/Kazo-ca/HackerOs/issues/242), points 8 et 9 des décisions
prises avec Benoit le 2026-10-29). S'appuie sur le menu contextuel de l'OS
([ADR 0150](0150-os-context-menu.md)), le menu de fenêtre avec « Autre écran ▸ »
([ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md) §3-5) et les barres
par écran (ADR 0179 §1). Mise en œuvre : [`taskbar-context-menus.md`](../taskbar-context-menus.md).

## Contexte

- La barre des tâches (`HackerOs.Taskbar.Blazor`) est un paquet sans dépendance vers
  l'OS : elle reçoit ses sources et ses commandes par des interfaces
  (`ITaskbarWindowSource`, `ITaskbarCommandDispatcher`, `ITaskbarLauncher`…). Elle n'a
  aucun menu contextuel : un clic droit montre le menu du navigateur.
- Le menu de fenêtre existe (`WindowManagerOverlays`) : Agrandir/Restaurer, Minimiser,
  Déplacer, Taille, Ancrer à gauche/droite, Plein écran, Fermer, puis « Autre écran ▸ ».
  Il s'ouvre par l'icône de la barre de titre et par `Alt+Espace`.
- Le menu contextuel de l'OS (ADR 0150) s'ouvre sur tout élément qui porte les
  attributs donnés par `ContextMenuScope.Attach`. Son script gère déjà le clic droit,
  l'appui long tactile, la touche Menu et `Maj+F10`, sur l'écran 1 et sur les écrans
  secondaires.
- Il n'existe pas de page « Barre des tâches » dans Paramètres. Le seul réglage de la
  barre, `primary-taskbar` (ADR 0179 §7), est sur la page « Écrans ».

## Décision

1. **Le paquet de la barre reste neutre.** Il reçoit un nouveau paramètre
   `ContextMenus` (`ITaskbarContextMenus`) qui donne, pour la barre et pour le bouton
   de chaque fenêtre, des attributs HTML à poser sur l'élément. La barre ne sait pas ce
   qu'ils contiennent. Sans ce paramètre, rien ne change. C'est un ajout à l'API
   publique du paquet (`PublicAPI.Unshipped.txt`), sans rupture.
2. **L'OS utilise son menu contextuel.** Le bureau (`DesktopShell`) donne à chaque
   barre (une par écran) un objet qui attache deux menus de l'OS :
   `taskbar-window-menu` (objet attaché : la fenêtre) et `taskbar-menu` (objet attaché :
   l'écran de la barre). Le clic droit, l'appui long, la touche Menu et `Maj+F10`
   marchent donc partout, comme sur le bureau.
3. **Clic droit sur le bouton d'une fenêtre.** Le menu contient, dans l'ordre :
   - « Nouvelle fenêtre » : lance l'app de la fenêtre une nouvelle fois, sur l'écran
     de cette barre. L'entrée est désactivée quand l'app n'accepte qu'une instance
     (`SingleInstancePerUser`) ou n'est plus dans le catalogue ;
   - les entrées du menu de fenêtre, les mêmes que celles de la barre de titre, avec
     leurs raccourcis et leur état (désactivées quand elles ne s'appliquent pas) ;
   - « Autre écran ▸ », avec « Nouvel écran ».
   Le même code construit le menu de la barre de titre et celui de la barre (un seul
   constructeur, `WindowMenuDefinitions`). Le clic droit n'active pas la fenêtre,
   comme sous Windows.
4. **Clic droit sur la barre, hors d'un bouton de fenêtre.** Le menu contient :
   - « Gestionnaire des tâches » : ouvre le moniteur système
     (`org.hackeros.system-monitor`) sur l'écran de cette barre ;
   - « Afficher le bureau » : minimise toutes les fenêtres de cet écran ;
   - « Fenêtres en cascade » et « Fenêtres en mosaïque » : rangent les fenêtres de cet
     écran (point 5) ;
   - « Paramètres de la barre des tâches » : ouvre Paramètres sur la page « Écrans »,
     qui porte le réglage de la barre.
   Les entrées qui n'ont rien à faire (aucune fenêtre visible, app absente du catalogue)
   sont désactivées. Le bouton Démarrer et l'horloge sont dans la barre : un clic droit
   dessus ouvre aussi ce menu.
5. **Cascade et mosaïque.** Seules comptent les fenêtres de premier niveau de cet
   écran : visibles (pas minimisées), sans propriétaire, et non bloquées par une
   boîte de dialogue modale. Une fenêtre agrandie est d'abord restaurée, une fenêtre
   ancrée quitte son ancrage.
   - **Cascade** : de l'arrière vers l'avant, chaque fenêtre garde sa taille et se
     place en escalier depuis le coin de la zone de travail, avec le pas de
     l'ouverture des fenêtres (28 px) sur 8 crans au plus, comme
     `WindowLaunchCoordinator`.
   - **Mosaïque** : une grille de `ceil(√n)` colonnes et du nombre de lignes
     nécessaire couvre la zone de travail. Une fenêtre redimensionnable prend sa
     case ; les autres se placent au coin de leur case. La taille minimale de chaque
     fenêtre reste respectée.
   L'ordre de profondeur ne change pas, et chaque fenêtre rangée enregistre sa
   géométrie comme après un déplacement à la main (ADR 0116).
6. **Textes.** Les nouveaux textes vont dans les ressources de
   `HackerOs.Platform.Blazor` (anglais et français), avec ceux du menu de fenêtre.

## Conséquences

- Un clic droit sur la barre ne montre plus le menu du navigateur.
- Le paquet de la barre gagne une interface publique ; elle sera dans la prochaine
  version mineure du paquet.
- « Afficher le bureau » ne revient pas en arrière au second clic (Windows le fait) :
  on rouvre les fenêtres par leur bouton. Un retour en arrière pourra venir plus tard.
- Les aperçus au survol (chantier 5) pourront donner le même menu sur un clic droit de
  l'aperçu, avec les mêmes attributs.

## Alternatives écartées

- **Un menu dessiné par la barre elle-même.** Il aurait fallu refaire dans le paquet
  le placement, le clavier, l'appui long et les sous-menus du menu de l'OS, et le
  paquet aurait dépendu de l'OS.
- **Des rappels (`EventCallback`) de la barre vers le bureau.** Le bureau aurait dû
  ouvrir le menu lui-même et refaire la touche Menu, `Maj+F10` et l'appui long, que
  le script du menu de l'OS gère déjà.
- **Une nouvelle page « Barre des tâches » dans Paramètres.** Elle n'aurait qu'un
  réglage, déjà sur la page « Écrans ».
