> [English version](../../../../en/wasm/dev/adr/0179-per-screen-taskbars-and-other-screen-menus.md)

# ADR 0179 : une barre des tâches par écran, menus « Autre écran » et voile d'identification

## Statut

Accepté le 2026-10-28. Reprend les réponses de Benoit du 2026-10-28 : une barre
des tâches par écran avec un réglage pour la barre de l'écran principal, les
deux menus de clic droit avec le sous-menu « Autre écran », et le voile
d'identification (chaque écran montre son propre numéro, l'écran survolé en
surbrillance). Complète l'[ADR 0178](0178-extended-screens-in-display-surfaces.md)
(écrans étendus), l'[ADR 0150](0150-os-context-menu.md) (menu contextuel de
l'OS) et l'[ADR 0164](0164-app-icons-and-window-icon-context-menu.md) (menu de
l'icône de fenêtre). Plan : [`multi-monitor-plan.md`](../plans/multi-monitor-plan.md).

## Contexte

- La barre des tâches (`HackerOs.Taskbar.Blazor.Taskbar`), le menu Démarrer et
  la zone de notification n'existent qu'une fois, dans l'onglet principal.
  `TaskbarWindowSourceAdapter` lui donne toutes les fenêtres de `WindowRuntime`.
- Le menu de fenêtre (ADR 0095 §3, `WindowManagerOverlays`) est une liste à un
  seul niveau : Restaurer, Réduire, Déplacer, Redimensionner, Agrandir, Ancrer à
  gauche/droite, Plein écran, Fermer. Il s'ouvre au clic droit sur l'icône
  (ADR 0164) et par `Alt+Space` / `Alt+F3`. Le bouton Agrandir/Restaurer n'a pas
  de menu.
- Le menu contextuel de l'OS (ADR 0150) sait déjà afficher des sous-menus et se
  parcourt au clavier.

## Décision

1. **Une barre des tâches par écran.** Chaque écran rend sa barre, avec son
   bouton Démarrer et son horloge.
   - La barre d'un écran secondaire montre les fenêtres de son écran.
   - La barre de l'écran 1 suit le réglage `primary-taskbar` : `all` (toutes les
     fenêtres, valeur par défaut, comme aujourd'hui avec un seul écran) ou `own`
     (seulement les fenêtres de l'écran 1).
   - Cliquer sur le bouton d'une fenêtre d'un autre écran l'active sur son écran,
     sans la déplacer.
   - Le menu Démarrer s'ouvre sur l'écran de la barre cliquée. Une app lancée
     depuis lui ouvre sa fenêtre sur cet écran. Une fenêtre ouverte autrement
     (commande, autre app) s'ouvre sur l'écran de la fenêtre active.
   - La zone de notification et les notifications restent sur l'écran 1.
2. **Une couche d'overlays par écran.** Le menu de fenêtre, le menu contextuel,
   le sélecteur de fenêtres et le déplacement au clavier s'affichent sur l'écran
   où l'utilisateur agit. Les dialogues sécurisés (approbation, ADR 0049)
   restent sur l'écran 1, et un écran secondaire montre alors un voile qui
   renvoie vers l'écran 1.
3. **Le menu de fenêtre passe par le menu contextuel de l'OS** (ADR 0150), pour
   avoir des sous-menus. Ses entrées, ses raccourcis affichés et son accès au
   clavier ne changent pas. Au clic droit sur l'icône :
   - Restaurer, Réduire, Déplacer, Redimensionner, Agrandir, Ancrer à gauche,
     Ancrer à droite, Plein écran, Fermer (comme aujourd'hui) ;
   - séparateur ;
   - **Autre écran ▸** : Écran 1, Écran 2, … ; séparateur ; **Nouvel écran**.
4. **Nouveau menu au clic droit sur le bouton Agrandir/Restaurer** :
   - Ancrer à gauche, Ancrer à droite, Plein écran, avec les raccourcis Linux
     affichés (`Super+Left`, `Super+Right`, …) ;
   - séparateur ;
   - **Autre écran ▸** (le même sous-menu).

   Le clic gauche sur ce bouton ne change pas (ADR 0177).
5. **Sous-menu « Autre écran »** :
   - Il liste tous les écrans par numéro. L'écran de la fenêtre est affiché mais
     désactivé, pour que les numéros restent à la même place.
   - **Nouvel écran** ouvre la surface pendant le clic (geste exigé par le
     navigateur), puis y déplace la fenêtre (ADR 0178 §4). Si le navigateur
     bloque la fenêtre surgissante, la notification « fenêtre bloquée » des
     surfaces explique comment l'autoriser.
   - Le sous-menu existe aussi avec un seul écran : « Écran 1 » désactivé, puis
     « Nouvel écran ». Il n'existe pas sur mobile.
6. **Voile d'identification.** Quand le pointeur survole « Écran N » (ou que le
   clavier y met le focus), chaque **autre** écran affiche :
   - un voile noir à 33 % d'opacité qui couvre tout l'écran ;
   - au centre, un carré noir avec le numéro de **cet** écran en blanc ;
   - sur l'écran N, le carré est en surbrillance (bordure de la couleur
     d'accent du thème).

   L'écran qui affiche le menu ne reçoit pas de voile, pour garder le menu
   utilisable. Le voile disparaît quand le survol ou le focus quitte l'élément,
   quand le menu se ferme, et **dès que le pointeur passe sur le voile**
   (garde-fou contre un voile resté bloqué). Il ne prend jamais de clic.
7. **Réglages en fichier** (règle « les réglages sont des fichiers ») :
   `/etc/hackeros/screens.config` (valeurs par défaut du système) et
   `~/.config/hackeros/screens.config` (utilisateur) :

   ```ini
   [taskbar]
   primary-taskbar=all      ; all | own

   [screens]
   ; écrans de la dernière session, pour « Restaurer les écrans »
   ; et la position retenue en mode repli (ADR 0178 §4)
   open=2
   2=1920,0,1920,1080
   ```

   La clé `open` (écrans à rouvrir) et le sens des lignes numérotées (place gardée
   même après la fermeture) sont précisés par l'[ADR 0180](0180-screen-service-placement-and-memory.md).

   **Paramètres ▸ Écrans** : liste des écrans ouverts (numéro et nom du
   moniteur quand le navigateur le donne), boutons Identifier, Nouvel écran,
   Restaurer les écrans et Plein écran, état de la permission
   `window-management`, et le choix de `primary-taskbar`.

## Conséquences

- La barre des tâches reçoit un filtre par écran ; `HackerOs.Taskbar.Blazor`
  n'a pas d'autre changement public.
- Le menu de fenêtre change de composant. Les tests du menu (`WindowKeyboardCommands`,
  `WindowAppIconTests`) doivent rester verts sans changer leurs attentes.
- Le menu contextuel de l'OS reçoit un signal interne « entrée en surbrillance »
  pour déclencher le voile ; aucune API du SDK ne change.

## Vérification

- Tests unitaires : filtre de la barre (`all` / `own`), contenu des deux menus
  (écran courant désactivé, Nouvel écran), lecture de `screens.config`.
- UI.E2E : clic droit sur l'icône et sur Agrandir, sous-menu au clavier, voile
  affiché au survol puis retiré au départ du pointeur, à la fermeture du menu et
  quand le pointeur passe sur le voile ; deux barres, chacune ses fenêtres.

## Tâches

- [x] Une couche d'overlays par écran et le voile des dialogues sécurisés (chantier 4, [`secondary-screen-desktop.md`](../secondary-screen-desktop.md)).
- [x] Barres des tâches par écran et réglage `primary-taskbar` (chantier 5, [`per-screen-taskbars.md`](../per-screen-taskbars.md)).
- [x] Menu de fenêtre par le menu contextuel de l'OS, menu du bouton Agrandir,
  sous-menu « Autre écran », voile (chantier 6, [`other-screen-menus.md`](../other-screen-menus.md)).
- [x] Paramètres ▸ Écrans et `screens.config` (chantier 7, [`screens-settings.md`](../screens-settings.md)).
