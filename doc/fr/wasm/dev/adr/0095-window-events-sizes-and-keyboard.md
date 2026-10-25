> [English version](../../../../en/wasm/dev/adr/0095-window-events-sizes-and-keyboard.md)

# ADR 0095 : événements de fenêtre pour les apps, tailles déclarées et commandes clavier

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour le chantier `v3/3-window-model`).
Le §3 suit la réponse de Benoit du 2026-10-17 : touches Linux quand le clavier
est verrouillé, plus un jeu de remplacement toujours actif.

- **Complète :** [ADR 0009](0009-window-runtime-strategy.md) (gestionnaire de
  fenêtres en C#, « les commandes clavier restent en C# », critère 6 de la preuve
  sans touches choisies) et [ADR 0006](0006-seal-window-component-lifecycle.md)
  (crochets `OnApp*` de `WindowAppBase`).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  ligne `v3/3-window-model` ; manques R7 et R8.

## Contexte

- `WindowRuntime` (`Platform/HackerOs.Windowing.Core/WindowRuntime.cs`) produit
  déjà les événements `WindowFocused`, `WindowResized`, `WindowVisualStateChanged`
  (Normal, Minimized, Maximized), mais seuls le bureau, la barre des tâches et la
  surface de fenêtre s'y abonnent, pour se redessiner. Aucune app ne les reçoit.
- `WindowLaunchCoordinator` (`Platform/HackerOs.Platform.Blazor/Windows/WindowLaunchCoordinator.cs:73-77`)
  ouvre toute fenêtre en 800×560 avec un minimum de 360×240, sans maximum. Ni
  `AppDefinition`, ni `AppManifest`, ni `manifest.schema.v1.json` n'ont de champ
  de taille.
- Le bureau ne gère qu'Échap (`DesktopShell.razor:356`). Il n'existe pas de
  service de raccourcis globaux.
- Dans un onglet de navigateur, le système hôte capture Alt+Tab, Super et souvent
  Alt+F4 (qui ferme le navigateur sous Windows) avant la page. Seule l'API
  Keyboard Lock (`navigator.keyboard.lock()`, en plein écran, Chromium) permet de
  les recevoir.

## Décision

### 1. Événements de fenêtre dans `WindowAppBase`

`WindowAppBase` reçoit de nouveaux crochets virtuels, appelés par le framework
dans le même ordre que les événements de `WindowRuntime`, sur le fil de rendu de
la fenêtre :

| Crochet | Appelé quand |
|---|---|
| `OnWindowActivated()` / `OnWindowDeactivated()` | la fenêtre prend ou perd le focus |
| `OnWindowStateChanged(HackWindowState previous, HackWindowState current)` | réduction, agrandissement, restauration |
| `OnWindowResized(HackWindowSize size)` | fin d'un redimensionnement (pas à chaque pixel : un seul appel quand le pointeur est relâché ou après une commande clavier) |

- `HackWindowState` (`Normal`, `Minimized`, `Maximized`) et `HackWindowSize`
  (largeur et hauteur de la zone cliente en pixels CSS) sont des types du SDK
  (`HackerOs.AppSdk.Blazor`), jamais les types de `HackerOs.Windowing.*`
  (ADR 0055).
- Chaque crochet a sa variante `…Async`. Une exception dans un crochet est
  journalisée dans `/var/log/apps/<appId>.log` et n'empêche pas la transition :
  le gestionnaire de fenêtres reste maître de l'état (ADR 0009).
- Une app réduite continue de tourner, comme sous un vrai gestionnaire de
  fenêtres ; c'est à elle de ralentir son travail si elle le souhaite, dans
  `OnWindowStateChanged`.
- Les fenêtres détachées (ADR 0078, 0081) appellent les mêmes crochets à partir
  des événements `focus`, `blur` et `resize` de la fenêtre du navigateur qui les
  porte.

### 2. Tailles déclarées par l'app

- `AppDefinition` reçoit une propriété facultative `Window` de type
  `WindowOptions` :
  `DefaultWidth`, `DefaultHeight`, `MinWidth`, `MinHeight`, `MaxWidth?`,
  `MaxHeight?`, `Resizable` (vrai par défaut).
- Valeurs par défaut quand rien n'est déclaré : les valeurs actuelles
  (800×560, minimum 360×240, sans maximum, redimensionnable). Aucune app
  existante ne change donc d'aspect.
- Le générateur écrit l'objet `window` dans `manifest.json` ; le schéma v1
  l'ajoute comme champ facultatif (ajout compatible). Le validateur refuse
  `window` sur une app qui n'est pas de genre `Window`, une taille négative ou
  nulle, un minimum plus grand que le défaut ou le défaut plus grand que le
  maximum (nouveaux codes `manifest.window.*`).
- `WindowLaunchCoordinator` lit ces valeurs. La taille initiale est ramenée à la
  zone de travail si l'écran est plus petit ; le minimum, lui, est respecté, et
  la fenêtre déborde alors comme sur un petit écran réel. L'agrandissement
  ignore le maximum déclaré (comportement des gestionnaires de fenêtres Linux
  courants), sauf si `Resizable` est faux : le bouton Agrandir est alors masqué.
- L'interface mobile (plein écran) ignore ces tailles.

### 3. Commandes clavier du gestionnaire de fenêtres

Les raccourcis sont tenus par un service C# de la plateforme
(`WindowKeyboardCommands`), branché sur le gestionnaire `keydown` du bureau, et
lus dans un fichier de réglages, comme sous un vrai bureau Linux :
`/etc/hackeros/keybindings.config` (défaut machine) et
`~/.config/hackeros/keybindings.config` (choix de l'utilisateur), résolus par
`LayeredConfigResolver` (ADR 0044).

Deux jeux de touches sont fournis par défaut :

| Action | Touches Linux (actives quand le clavier est verrouillé) | Touches de remplacement (toujours actives) |
|---|---|---|
| Passer à la fenêtre suivante / précédente (sélecteur affiché tant qu'Alt est tenu) | Alt+Tab / Alt+Maj+Tab | Alt+` / Alt+Maj+` |
| Fermer la fenêtre (passe par la confirmation de fermeture, ADR 0006) | Alt+F4 | Alt+Q |
| Menu de la fenêtre | Alt+Espace | Alt+F3 |
| Déplacer au clavier (flèches, Entrée valide, Échap annule) | Alt+F7 | Alt+F7 |
| Redimensionner au clavier | Alt+F8 | Alt+F8 |
| Réduire | Super+Bas (depuis l'état normal) | Alt+F9 |
| Agrandir / restaurer | Super+Haut / Super+Bas | Alt+F10 |
| Coller à gauche / à droite | Super+Gauche / Super+Droite | Alt+Maj+Gauche / Alt+Maj+Droite |

- Les touches de remplacement reprennent celles de GNOME là où le navigateur ne
  les intercepte pas.
- Les touches Linux sont actives seulement quand l'OS a obtenu le verrouillage
  du clavier : en plein écran, la plateforme appelle `navigator.keyboard.lock()`
  par un module JS de la bibliothèque de fenêtres (seule exception JS, l'API
  n'existant pas en C#) ; hors plein écran ou sans l'API, elles restent au
  système hôte.
- Toutes les actions sont aussi dans le menu de la fenêtre, pour qu'elles
  restent atteignables sans raccourci (critère 6 de l'ADR 0009).
- Une app qui gère elle-même une touche (par exemple le Terminal) la reçoit
  d'abord seulement pour les touches qui ne sont pas des commandes de fenêtre ;
  les commandes de fenêtre passent toujours avant, comme sous X11 avec un
  gestionnaire de fenêtres qui a saisi ces touches.

## Conséquences

- `v3/3` ajoute les crochets, `WindowOptions`, le champ `window` du schéma,
  le service de raccourcis, le fichier `keybindings.config` et des tests
  unitaires (ordre des crochets, validation) et E2E (Alt+` et Alt+Q dans un
  onglet).
- `v3/6` touche aussi `AppDefinition` : le second chantier fusionné reprend
  l'autre avant (plan, §4).
- `sdk-reference.md`, `sdk-developer-guide.md`, `app-sdk.md` et la doc du
  schéma du manifeste décrivent les crochets et `Window`.
- `v3/13` teste ces parcours clavier avec axe et Playwright.
