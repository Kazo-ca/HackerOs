> [English version](../../../en/wasm/framework/window-model.md)

# Modèle de fenêtre : événements, tailles déclarées et clavier

Chantier `v3/3-window-model` du [plan de fin de migration wasm v3](../dev/plans/wasm-v3-completion-plan.md),
mise en œuvre de l'[ADR 0095](../dev/adr/0095-window-events-sizes-and-keyboard.md).

## Objet

- Une app fenêtrée apprend ce qui arrive à sa fenêtre : elle gagne ou perd le focus, elle est
  réduite, agrandie ou restaurée, elle change de taille.
- Une app fenêtrée déclare la taille de sa fenêtre (initiale, minimale, maximale, redimensionnable)
  dans sa classe `AppDefinition` au lieu du 800×560 / 360×240 codé en dur.
- Le gestionnaire de fenêtres se pilote au clavier comme un bureau Linux : changer de fenêtre,
  fermer, menu de fenêtre, déplacer et redimensionner au clavier, réduire, agrandir, ancrer à
  gauche ou à droite, plein écran.
- Une app fenêtrée écrit le titre de sa fenêtre (`document — App`), affiché par le gestionnaire de
  fenêtres (ADR 0117, §4).

## 1. Événements de fenêtre (ADR 0095 §1)

### Pour l'app

`WindowAppBase` (SDK Blazor) expose :

| Membre | Rôle |
| --- | --- |
| `OnWindowActivated[Async]()` | La fenêtre reçoit le focus. |
| `OnWindowDeactivated[Async]()` | La fenêtre perd le focus (autre fenêtre, réduction, plus aucune fenêtre au premier plan). |
| `OnWindowStateChanged[Async](previous, current)` | `KazoWindowState` : `Normal`, `Minimized`, `Maximized`. |
| `OnWindowResized[Async](KazoWindowSize size)` | Taille de la zone client (sans la barre de titre), en pixels CSS, **une seule fois à la fin** d'un redimensionnement (geste à la souris, clavier, agrandissement, restauration, changement de la zone de travail). |
| `IsWindowActive`, `WindowState`, `WindowSize` | Dernier état connu, à lire dans le rendu. |
| `TrackDisplaySurface(IDisplaySurface)` | Fait suivre une fenêtre détachée (ADR 0078/0081) : son focus, sa perte de focus et sa taille déclenchent les mêmes crochets. `KazoWindowSize.DisplaySurfaceId` indique alors la surface. Le résultat se libère quand la surface est rattachée. |

Ordre garanti :

- les crochets d'une fenêtre passent dans une file à elle, un à la fois, dans l'ordre des faits ;
- un changement d'état précède le redimensionnement qu'il provoque (agrandir donne
  `OnWindowStateChanged(Normal, Maximized)` puis `OnWindowResized`) ;
- quand l'app est rendue dans une fenêtre déjà agrandie ou déjà au premier plan, elle reçoit
  `OnWindowStateChanged(Normal, état actuel)` puis `OnWindowActivated` ;
- la version synchrone est appelée avant la version `Async`, sur le répartiteur du composant, dans
  le contexte ambiant de l'app (les façades `Kazo*` marchent) ; un nouveau rendu suit.

Un crochet qui lève une exception est écrit dans le journal de l'app
(`/var/log/apps/<appId>.log`, niveau `Error`, préfixe `window:`) ; la transition de la fenêtre a
déjà eu lieu et continue.

```razor
@inherits WindowAppBase

<p class="@(IsWindowActive ? "active" : "idle")">@WindowState</p>

@code {
    protected override Task OnWindowDeactivatedAsync() => PauseAnimationAsync();
    protected override void OnWindowResized(KazoWindowSize size) => _columns = (int)(size.Width / 120);
}
```

### Architecture

- `WindowRuntime` (Windowing.Core) produit déjà des faits (`WindowFocusedEvent`,
  `WindowVisualStateChangedEvent`, `WindowViewportChangedEvent`, `WindowClosedEvent`). Il publie
  maintenant chaque lot par l'événement `EventsApplied`, avant `StateChanged`.
- Écrans : une fenêtre a un `DisplayId` et chaque écran sa zone de travail ; changer d'écran
  appelle `OnWindowResized` si la taille change (ADR 0178, [`window-runtime-displays.md`](../dev/window-runtime-displays.md)). La fenêtre
  n'est pas recréée : son élément passe dans la couche de son écran ([`window-portal.md`](../dev/window-portal.md)).
- Nouveau fait : `CompleteWindowResizeCommand` → `WindowResizeCompletedEvent(id, bounds)`. Le
  chrome (`WindowChrome.razor.js`) l'envoie quand un geste de redimensionnement se termine ; le
  clavier l'envoie quand on valide un redimensionnement.
- `WindowAppEventDispatcher` (Platform.Blazor, un par session) traduit ces faits en appels de
  `IWindowAppEventTarget` (interface du SDK, implémentée par `WindowAppBase`). `WindowAppRenderer`
  y inscrit le composant de l'app. La taille est mesurée dans le navigateur
  (`IWindowClientAreaProbe`, `windowClientArea.js`, deux images après le rendu) ; à défaut, elle
  est estimée depuis la taille de la fenêtre moins le chrome.
- `DesktopShell` et `MobileShell` passent le répartiteur à `WindowAppRenderer`. Sur mobile, les
  fenêtres sont plein écran : les crochets d'activation et d'état restent valables.

## 2. Tailles déclarées (ADR 0095 §2)

### Gestes au pointeur (ADR 0163)

Les attributs `data-window-x/y/width/height` utilisent la culture invariante,
même quand le bureau est en français. Le déplacement donne le focus au début,
projette une translation CSS locale pendant le geste et transmet un seul delta
final à C# au relâchement. Le runtime applique alors ses contraintes ; la position
n'est jamais enregistrée sur disque (ADR 0122). Le redimensionnement garde les
contraintes C# en direct, avec un échantillon en cours et le plus récent en attente
au lieu d'une file par mouvement. La capture perdue ou annulée termine le geste
sur le dernier delta valide ; retirer le composant libère les écouteurs et la capture.

Cette projection temporaire amende l'ADR 0009 sans déplacer l'état du runtime
en JavaScript : voir l'[ADR 0163](../dev/adr/0163-window-pointer-preview-and-invariant-coordinates.md).

Au relâchement, l'aperçu vise une destination absolue et sa translation soustrait
la position CSS courante du cadre. Ainsi, un rendu de la nouvelle position avant
le retour de l'interop ne peut pas additionner deux fois le déplacement.
`ReportGestureCompletedAsync("move")`, appelé après le retour de la commande,
projette la position C# courante (contraintes comprises) et retire l'aperçu dans
une même tâche JavaScript (`completeWindowMove`). Cela couvre aussi un retour
interop précédant la projection `WindowHost.OnAfterRenderAsync`, sans image à
l'ancienne position. Aucun mouvement supplémentaire ni écriture disque n'est émis.

### Déclaration par l'app

```csharp
public override WindowOptions? Window => new()
{
    DefaultWidth = 720, DefaultHeight = 520,
    MinWidth = 480, MinHeight = 360,
    MaxWidth = null, MaxHeight = null,   // pas de limite
    Resizable = true,
};
```

- `WindowOptions` (App.Abstractions) : tailles extérieures (barre de titre comprise) en pixels
  CSS. Sans déclaration : `WindowOptions.Default`, 800×560 et minimum 360×240, comme avant.
- Manifeste : objet facultatif `window` (`defaultWidth`, `defaultHeight`, `minWidth`, `minHeight`,
  `maxWidth`, `maxHeight`, `resizable`, et depuis le SDK 3.1 `style`, `showIcon`, `maximizeBox`,
  `minimizeBox`, `showInTaskbar`, `modal`, voir §6), produit par le générateur et absent si l'app ne déclare
  rien. Un champ omis prend sa valeur par défaut. Le schéma `manifest.schema.v1.json` interdit
  `window` hors des apps de genre `window`.
- Codes du validateur :

| Code | Règle |
| --- | --- |
| `manifest.window.forbidden` | `window` sur une app qui n'est pas de genre `window`. |
| `manifest.window.size.invalid` | Une taille nulle ou négative. |
| `manifest.window.minimum.exceedsDefault` | Minimum plus grand que la taille initiale. |
| `manifest.window.default.exceedsMaximum` | Taille initiale plus grande que le maximum. |

- Ouverture (`WindowLaunchCoordinator.InitialBounds`) : taille initiale réduite à la zone de
  travail, mais jamais sous le minimum ; une fenêtre dont le minimum dépasse l'écran déborde,
  comme sur un vrai petit écran (`WindowRuntime.Constrain` garde le minimum).
- Agrandir ignore le maximum. `Resizable = false` : pas de poignées, pas de bouton Agrandir, et
  les commandes clavier redimensionner, agrandir et ancrer ne font rien.
- Taille retenue (ADR 0116 et 0122, [`window-geometry.md`](../dev/window-geometry.md)) : la taille et
  l'état agrandi de chaque fenêtre d'app, pas sa position, sont écrits dans
  `~/.local/state/kazoos/windows/<appId>.config` et relus par
  `WindowLaunchCoordinator.PresentAsync`, qui corrige la taille pour l'écran et les tailles
  déclarées du moment ; la fenêtre s'ouvre toujours à sa place de cascade (`InitialBounds`).
  `WindowOptions.RememberGeometry = false` refuse.
- Le shell mobile ignore ces tailles.

## 3. Commandes clavier (ADR 0095 §3)

### Fichier `keybindings.config`

Lu par `WindowKeyboardCommands` avec les couches habituelles
([`settings-files.md`](settings-files.md)) : `/etc/kazoos/keybindings.config` (écrit au
premier démarrage, tout en commentaires), puis `~/.config/kazoos/keybindings.config`. Une
modification du fichier s'applique tout de suite. Plusieurs combinaisons séparées par des virgules ;
une valeur vide retire la touche.

| Action (clé) | `[linux]` (sous verrouillage du clavier) | `[fallback]` (toujours) |
| --- | --- | --- |
| `switch-next` | `Alt+Tab` | `` Alt+` `` (`Alt+Backquote`) |
| `switch-previous` | `Alt+Shift+Tab` | `Alt+Shift+Backquote` |
| `close` | `Alt+F4` | `Alt+Q` |
| `window-menu` | `Alt+Space` | `Alt+F3` |
| `move` | `Alt+F7` | `Alt+F7` |
| `resize` | `Alt+F8` | `Alt+F8` |
| `minimize` | | `Alt+F9` |
| `maximize` | `Super+Up` | |
| `restore-or-minimize` | `Super+Down` | |
| `toggle-maximize` | | `Alt+F10` |
| `snap-left` / `snap-right` | `Super+Left` / `Super+Right` | `Alt+Shift+Left` / `Alt+Shift+Right` |
| `toggle-fullscreen` | | `Alt+F11` |
| `move-to-previous-screen` / `move-to-next-screen` | `Super+Shift+Left` / `Super+Shift+Right` | `Ctrl+Alt+Shift+Left` / `Ctrl+Alt+Shift+Right` |

Les touches Linux ne sont actives que si le clavier est verrouillé (`navigator.keyboard.lock()`,
appelé en plein écran) : sans cela, le système hôte les intercepte. Les touches de remplacement
marchent dans un onglet ordinaire. Une combinaison du gestionnaire de fenêtres passe avant l'app
qui a le focus. Une lettre est reconnue par sa touche physique quand Alt change le caractère
(Option+Q sur macOS).

### Comportement

- **Changer de fenêtre** : tant que Alt est tenu, un sélecteur liste les fenêtres (non réduites
  d'abord, de la plus récente à la plus ancienne) et la sélection avance à chaque appui ; relâcher
  Alt passe à la fenêtre choisie (restaurée si elle était réduite). Échap annule.
- **Déplacer / redimensionner** : un bandeau indique le mode ; les flèches déplacent ou
  redimensionnent de 10 px (1 px avec Ctrl), Entrée garde, Échap remet la fenêtre où elle était.
- **Ancrer** : moitié gauche ou droite de la zone de travail. La fenêtre garde sa géométrie
  d'avant (ADR 0177) : `restore-or-minimize`, ou glisser sa barre de titre, la lui rend ; Agrandir
  l'agrandit et Restaurer la remet ensuite à sa taille d'avant l'ancrage.
- **Barre de titre** (ADR 0177) : un double-clic hors des boutons bascule agrandi ↔ normal ;
  glisser la barre de titre d'une fenêtre agrandie ou ancrée la remet à sa taille d'avant, sous le
  pointeur. Une fenêtre agrandie n'a pas de poignées de redimensionnement, et les boutons passent
  toujours au-dessus des poignées.
- **Menu de fenêtre** (ADR 0164) : `Alt+Space`/`Alt+F3` ou clic droit sur l'icône
  de l'app dans la barre de titre. Un clic gauche sur l'icône active seulement la
  fenêtre ; le reste de la barre de titre n'ouvre pas le menu. Il contient toutes les actions avec leur raccourci actif :
  Agrandir/Restaurer, Réduire, Déplacer, Redimensionner, Ancrer à gauche/droite, Plein écran,
  Fermer, puis **Autre écran ▸** (Écran 1, Écran 2…, Nouvel écran). Il s'ouvre dans le menu
  contextuel de l'OS (ADR 0179 §3) : flèches, Entrée, Échap et sous-menu au clavier y fonctionnent.
- **Menu du bouton Agrandir/Restaurer** (ADR 0179 §4) : clic droit sur le bouton. Ancrer à
  gauche, Ancrer à droite, Plein écran, puis Autre écran ▸. Le clic gauche ne change pas.
- **Changer d'écran** (ADR 0178 §6) : `move-to-previous-screen` / `move-to-next-screen` envoient la
  fenêtre active sur l'écran d'avant ou d'après, par numéro, en faisant le tour. Détails dans
  [`../dev/other-screen-menus.md`](../dev/other-screen-menus.md).
- **Fermer** suit le chemin du bouton Fermer (`IWindowCloseGuard` compris).
- **Focus clavier** : la fenêtre qui devient la fenêtre active (sélecteur, fermeture de celle du
  dessus, barre des tâches) reçoit aussi le focus clavier : le dernier contrôle qui l'avait dans
  cette fenêtre, sinon son premier élément focalisable, sinon la fenêtre elle-même. Rien ne bouge
  si le focus y est déjà (clic, app qui se focalise seule). `WindowHost` le fait au rendu
  (`WindowHost.razor.js`, chantier `v3/13`).

### Architecture

- `WindowKeyBindingsSettingsDocuments`, `KeyChord`, `WindowKeyboardAction` (Platform.Core) :
  schéma du document, analyse des combinaisons, contenu par défaut.
- `WindowKeyboardCommands` (Platform.Blazor, un par session) : toutes les décisions (modes,
  sélecteur, menu, déplacements) en C#, testées sans navigateur.
- `WindowManagerOverlays` (`.razor`, `.razor.cs`, `.razor.css`, `.razor.js`) : dessine le
  sélecteur, le bandeau et le menu. Son JS écoute `keydown` en phase de capture, reconnaît les
  combinaisons envoyées par le C#, appelle le C# et bloque la touche ; il gère le plein écran et
  le verrouillage du clavier.

## 4. Titre de fenêtre (ADR 0117, ADR 0123)

### Icône de l'application (ADR 0164)

La première image déclarée dans `AppDefinition.Icons` devient l'icône de la
fenêtre et de sa taskbar. Elle doit aussi figurer dans `Assets` et exister sous
`wwwroot/` dans le projet de l'app. `WindowLaunchCoordinator` transforme son
chemin relatif au paquet en URL `_content/<assembly>/<chemin>` (segments échappés),
partagée par le chrome, la taskbar et le sélecteur. Les quatorze apps système
fenêtrées fournissent chacune leur `icons/app.svg` ; le build calcule l'empreinte
de l'asset. Sans déclaration, un symbole de fenêtre sert de repli.

Tous les thèmes de bureau affichent l'icône de titre sauf macOS, qui masque
l'icône et son bouton contextuel avec `--kos-window-icon-display: none`.
Le dock macOS garde les icônes d'apps. Le menu demeure accessible au clavier.
Le choix Lucide du lanceur et des raccourcis reste indépendant des images du chrome.
Voir l'[ADR 0164](../dev/adr/0164-app-icons-and-window-icon-context-menu.md).

Chantier `v3.1/3-window-title` de la [série v3.1](../dev/plans/v3-1-series-plan.md).

### Pour l'app

L'app écrit **tout** son titre, comme un client X11 écrit `_NET_WM_NAME` ; le gestionnaire de
fenêtres l'affiche tel quel dans la barre de titre, la barre des tâches, le sélecteur `` Alt+` ``,
les récents du bureau mobile et le nom accessible du cadre (`aria-labelledby`).

| Membre de `WindowAppBase` | Rôle |
| --- | --- |
| `WindowTitle` | Titre affiché ; le nom de l'app dans la langue du bureau tant que l'app n'a rien posé. |
| `SetWindowTitleAsync(string? title, CancellationToken)` | Remplace le titre de **cette** fenêtre. `null`, vide ou blanc rend le nom de l'app. Aucune capacité. |
| `FormatWindowTitle(string? document, bool modified = false)` | Compose la convention des apps de base : `document — App`, `*` en tête si `modified`, le nom seul sans document. |

```razor
@inherits WindowAppBase

@code {
    protected override Task OnAppInitializedAsync() => SetWindowTitleAsync(FormatWindowTitle(_name, _dirty)).AsTask();

    // Ouvrir, modifier, enregistrer finissent par un rendu : le titre suit ici. Redemander le même
    // titre ne fait rien.
    protected override Task OnAppAfterRenderAsync(bool firstRender) =>
        SetWindowTitleAsync(FormatWindowTitle(_name, _dirty)).AsTask();
}
```

- Le titre est nettoyé : caractères de contrôle retirés, espaces aux extrémités coupés, au plus
  256 caractères (coupé avec `…`, jamais au milieu d'une paire de substitution).
- Posé dans `OnAppInitialized[Async]`, il est affiché avec la fenêtre, sans clignotement.
- Les fenêtres détachées suivies par `TrackDisplaySurface` prennent le même titre une fois que l'app
  en a posé un.
- Les fenêtres sensibles de l'OS (agent d'authentification, approbation d'une capacité dangereuse)
  sont dessinées par l'OS, hors des fenêtres d'app, et ne prennent jamais un titre d'app ; la barre
  des tâches montre l'icône de l'app propriétaire à côté du titre.
- Une app sans interface (`Terminal`, `Service`) n'a pas cette API.

### Architecture

- `WindowRuntime.SetTitle(windowId, title)` (Windowing.Core) applique `SetWindowTitleCommand`,
  nettoie le titre avec `WindowTitles.Sanitize` (Windowing.Abstractions) et publie
  `WindowTitleChangedEvent` ; un titre inchangé ne produit rien. Les surfaces relisent
  `WindowRuntimeState.Title` à chaque `StateChanged`.
- `AppWindowGatewayFactory` (Platform.Blazor, enregistrée par `AddKazoOsEcosystem`) crée une
  `IAppWindowGateway` par fenêtre, liée à son `WindowId`. `WindowAppRenderer` la reçoit
  (`WindowGateways`, passé par `DesktopShell` et `MobileShell`) et la donne à l'app par un
  `CascadingValue` fixe : l'app ne peut atteindre que sa propre fenêtre.
- Le titre par défaut vient de `WindowLaunchCoordinator.DefaultTitle(manifest)` (nom traduit).

### Apps de base

| App | Titre |
| --- | --- |
| Kazo Paint | `photo.png — Kazo Paint`, `*` si l'image est modifiée ; le nom quitte la barre d'outils. |
| Éditeur de texte | `*notes.txt — Éditeur de texte` ; le bandeau de l'app garde son affichage. |
| Éditeur de code | Onglet actif, puis dossier ouvert : `*Program.cs — src — Éditeur de code`. |
| Éditeur de thèmes | `*Nom — Éditeur de thèmes` (aussi dans sa barre d'outils). |
| Explorateur de fichiers | `Documents — Explorateur de fichiers`, `/` à la racine, la corbeille quand elle est affichée. |

## 5. Fenêtre qui ne répond pas (ADR 0127 §2-3)

- `WindowRuntimeState.IsResponding` (vrai par défaut) est changé par
  `SetWindowRespondingCommand` ; `WindowRuntime` publie
  `WindowRespondingChangedEvent` seulement quand la valeur change.
- `WindowChrome` ajoute « (Not responding) » / « (Ne répond pas) » au titre
  affiché (le titre de l'app ne change pas) ; `WindowHost` grise le contenu
  (`is-not-responding`, `aria-busy`) et dessine son paramètre `Overlay` par
  dessus le contenu. `DesktopArea` et `SingleSurfaceArea` le remplissent avec
  `WindowOverlay` (un `RenderFragment<WindowRuntimeState>`).
- Le moniteur `WindowResponsivenessMonitor`, la boîte et le réglage
  `window-manager.config` sont décrits dans [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md).

## 6. Styles de fenêtre et options façon WinForms (ADR 0234)

### Pour l'app

```csharp
public override WindowOptions? Window => new()
{
    DefaultWidth = 400, DefaultHeight = 300,
    Resizable = false,
    Style = WindowStyle.Tool,     // Normal (défaut), Tool, None
    ShowIcon = true,              // icône dans la barre de titre
    MaximizeBox = true,           // bouton Agrandir
    MinimizeBox = true,           // bouton Réduire
    ShowInTaskbar = true,         // bouton dans la barre des tâches
    Modal = false,                // fenêtres enfants (chantier 5)
};
```

| Style | Barre de titre | Bordure et poignées | Boutons |
| --- | --- | --- | --- |
| `Normal` | oui | oui | selon les booléens |
| `Tool` | oui, plus basse (`--kos-tool-titlebar-height`) | non | Fermer seulement |
| `None` | non | non | aucun : l'app dessine les siens |

- Manifeste : `style` (`"normal"`, `"tool"`, `"none"`) et les booléens ne sont écrits que s'ils
  diffèrent de leur défaut ; un manifeste existant reste identique octet pour octet.
- `ShowInTaskbar = false` retire le bouton de la barre des tâches **et** le bouton Réduire :
  l'utilisateur ne peut plus réduire la fenêtre (réponse B de Benoit à la question 6). Elle reste
  dans Alt+` et le sélecteur.
- `MaximizeBox = false` retire aussi le double-clic sur la barre ; `MinimizeBox = false` retire
  aussi « Réduire » du menu de fenêtre et de la commande clavier. `Tool` et `None` retirent les
  poignées, Agrandir et Réduire ; le menu de fenêtre reste au clic droit sur la barre et à Alt+Espace.
- Sans icône (`ShowIcon = false` ou `Tool`), le clic droit sur le titre ouvre le menu de fenêtre.
- Une fenêtre `None` se déplace par tout élément marqué `data-kazo-window-drag` (les champs, liens
  et éléments éditables à l'intérieur restent cliquables).

Ce que l'app demande à **sa** fenêtre, sans capacité (#384) :

| Méthode de `WindowAppBase` | Effet |
| --- | --- |
| `WindowOptions` | Options actuelles (le manifeste tant que l'app ne les change pas). |
| `MinimizeWindowAsync`, `MaximizeWindowAsync`, `RestoreWindowAsync` | État de la fenêtre ; une fenêtre de taille fixe n'est jamais agrandie. |
| `MoveWindowAsync(x, y)` | Coin haut gauche, relatif à la zone de travail de son écran ; la fenêtre reste sur l'écran. |
| `ResizeWindowAsync(w, h)` | Taille extérieure, bornée aux tailles déclarées et à l'écran ; il faut une fenêtre redimensionnable à l'état normal. |
| `SetWindowStyleAsync(style)`, `SetWindowOptionsAsync(options)` | Style, booléens et tailles à l'exécution (partir de `WindowOptions with { … }`). |

Chaque méthode renvoie `true` si la demande est appliquée, `false` sinon (fenêtre fermée,
fenêtre détachée, demande refusée par l'état de la fenêtre, plateforme d'avant le SDK 3.1). Les
booléens règlent ce que voit l'utilisateur, pas ce que l'app peut faire : une app avec
`MinimizeBox = false` peut quand même réduire sa fenêtre.

### Architecture

- `WindowFrame` (Windowing.Abstractions) : style (`WindowFrameStyle`) et booléens dans
  `WindowRuntimeState.Frame`, avec `UserCanMinimize`, `UserCanMaximize(constraints)`,
  `UserCanResize(constraints)`. Le cadre, les commandes clavier, l'arrangement des fenêtres et la
  barre des tâches lisent ces règles ; `WindowRuntime` ne limite que ce que les contraintes
  interdisent, pour que l'app garde ses demandes.
- `SetWindowFrameCommand` et `WindowFrameChangedEvent` : change le cadre et les contraintes ; une
  fenêtre agrandie qui devient de taille fixe est restaurée.
- `WindowLaunchCoordinator.ToFrame(options)` traduit le manifeste ; `AppWindowGatewayFactory`
  applique les demandes de l'app par les commandes du runtime.
- `WindowRuntime.ActivationTarget(id)` : la fenêtre modale la plus haute d'une propriétaire. Un
  dialogue sans bouton dans la barre des tâches revient au premier plan par le bouton de sa
  propriétaire.
- Dialogues de l'OS (MessageBox, ouvrir, enregistrer, dossier, saisie, choix d'app, « ne répond
  pas ») : `WindowFrame.Dialog`, style `Tool` sans bouton dans la barre des tâches (§6 de l'ADR).
  Sur mobile, ils restent en plein écran.
- `WindowHost` ajoute `is-tool` ou `is-frameless`.

### Transparence de `None` (ADR 0234 §5, #378)

- La surface d'une fenêtre `None` n'a ni fond, ni bordure, ni ombre. La surface et la zone cliente
  reçoivent `pointer-events: none` : un clic là où l'app ne dessine rien atteint le bureau ou la
  fenêtre dessous, comme une fenêtre en couches de Windows.
- Ce que l'app dessine reprend la souris : les enfants directs de la zone cliente et tout élément
  marqué `data-kazo-window-hit` (utile dans un conteneur que l'app rend lui-même transparent avec
  `pointer-events: none`). Une app qui remplit sa zone cliente d'un seul bloc opaque ne laisse donc
  rien passer : elle dessine sa forme avec des éléments séparés.
- Sur un bureau interactif (icônes), `DesktopArea` ne rend plus la souris à une fenêtre `None`
  (`.window-host:not(.is-frameless)`).
- Test : `WindowFramelessTransparencyTests` (UI.E2E) charge les vrais styles et le vrai module de
  pointeur dans Chromium : clic à travers vers une icône, zones dessinées, élément marqué, surface
  sans fond, zone `data-kazo-window-drag` qui déplace la fenêtre sauf sur un champ.

## 7. Fenêtres enfants et dialogues d'une app (ADR 0234 §3)

```csharp
// Dans un WindowAppBase : équivalent de Form.ShowDialog(owner).
KazoDialogResult result = await ShowDialogAsync<RenameDialog>(new KazoWindowRequest
{
    Title = "Renommer",
    Options = new WindowOptions { DefaultWidth = 360, DefaultHeight = 180, MinWidth = 300, MinHeight = 160, Style = WindowStyle.Tool },
    Parameters = new Dictionary<string, object?> { [nameof(RenameDialog.Name)] = fileName },
});
if (result.IsAccepted) { fileName = result.ValueAs<string>()!; }

// Fenêtre non modale : équivalent de Form.Show(owner).
IKazoWindow palette = await ShowWindowAsync<PaletteWindow>(new KazoWindowRequest { Title = "Palette" });
```

```razor
@inherits KazoWindowBase
<input @bind="Name" />
<button @onclick="() => CloseAsync(KazoDialogResult.Ok(Name))">OK</button>
<button @onclick="() => CloseAsync(KazoDialogResult.Cancel)">Annuler</button>
@code { [Parameter] public string? Name { get; set; } }
```

- La fenêtre enfant appartient à l'instance de l'app : même processus, mêmes droits, même
  `AppContext`, mêmes menus contextuels. Sa propriétaire est la fenêtre qui l'ouvre. Elle s'ouvre
  centrée sur sa propriétaire, sur le même écran, et se ferme avec son app ou sa propriétaire
  (résultat `None`).
- `ShowDialogAsync` impose `Modal = true` : la propriétaire est bloquée, le bouton de la
  propriétaire dans la barre des tâches ramène le dialogue. Annuler le jeton ferme le dialogue
  (`Cancel`).
- Le bouton Fermer, Alt+F4 et la barre des tâches donnent `KazoDialogResult.Cancel`.
- `KazoWindowBase` (hérite de `KazoComponentBase`) : `CloseAsync(result)`, `WindowTitle`,
  `SetWindowTitleAsync`, `WindowOptions` et les méthodes de fenêtre du §6, et ses propres
  `ShowWindowAsync` et `ShowDialogAsync` pour une fenêtre enfant d'une fenêtre enfant.
- `KazoDialogResult` : `Kind` (`None`, `Ok`, `Cancel`, `Abort`, `Retry`, `Ignore`, `Yes`, `No`,
  comme `DialogResult`) et `Value` facultatif.
- Sans gestionnaire de fenêtres (tests, fenêtre détachée, plateforme d'avant le SDK 3.1),
  `ShowWindowAsync` et `ShowDialogAsync` lèvent `InvalidOperationException`.

Architecture : `IAppChildWindowGateway.OpenWindowAsync`, implémentée par la passerelle de
`AppWindowGatewayFactory.Create(windowId, context)`. La fenêtre est un `WindowRuntimeState` avec
`Content` (comme les dialogues de l'OS) qui met en cascade le contexte de l'app, ses menus, sa
propre passerelle et son `IKazoWindow`. Elle n'a pas de taille retenue (ADR 0116).

## Décisions de mise en œuvre

- **Plein écran ajouté** (`toggle-fullscreen`, `Alt+F11`, entrée du menu) : le verrouillage du
  clavier n'existe qu'en plein écran, il fallait un moyen d'y entrer.
- **`KazoWindowSize.DisplaySurfaceId`** : distingue le redimensionnement d'une fenêtre détachée de
  celui de la fenêtre du bureau.
- **Taille mesurée** dans le navigateur, estimée seulement en repli (tests, rendu serveur).
- **`/etc/kazoos/keybindings.config` livré en commentaires** : les valeurs actives viennent du
  code tant que personne n'écrit le fichier, comme les autres documents de l'OS.
- **Ancrer garde la géométrie d'avant** (ADR 0177, remplace « Ancrer ne garde pas de géométrie de
  restauration ») : la fenêtre ancrée reste `Normal`, avec `IsSnapped` et `RestoreBounds`. La taille
  enregistrée (ADR 0116, 0122) ignore cette moitié d'écran et garde celle d'avant l'ancrage.
- **Touches dans une iframe** (navigateur, sites) : elles restent dans l'iframe, la page ne les voit
  pas ; il faut d'abord rendre le focus au bureau.
- **Correction** : réduire une fenêtre en arrière-plan levait une exception dans `WindowRuntime`
  (il tentait de lui donner le focus). Elle est maintenant réduite sans toucher au focus.

## Tests

- `WindowAppIconTests` (UI.E2E) : images des apps décodées et identiques dans
  chrome/taskbar, sept thèmes et exception macOS, menu au clic droit sur l'icône
  seulement, clic gauche et clavier. `WindowLaunchCoordinatorTests` couvre la
  résolution des chemins relatifs et l'échappement des segments.

- `WindowPointerGestureTests` (UI.E2E) : module de pointeur et projection du cadre
  réels dans Chromium ; échantillonnage des positions sur plusieurs images avec
  projection avant/après le retour interop, avec ou sans correction des contraintes,
  déplacements successifs, capture perdue, retrait du composant, erreur interop et
  redimensionnement ralenti. `WindowGeometryRestoreTests` vérifie aussi chaque
  image au relâchement pendant deux déplacements dans l'OS complet.

- `WindowAppBaseWindowEventsTests` (SDK Blazor) : ordre des crochets, erreur journalisée,
  surfaces détachées.
- `WindowAppEventDispatcherTests`, `WindowKeyboardCommandsTests`, `WindowLaunchCoordinatorTests`
  (Platform.Blazor) : traduction des faits, une seule fin de redimensionnement, sélecteur, menu,
  modes clavier, tailles initiales.
- `WindowKeyBindingsTests` (Platform.Core) : analyse des combinaisons et du fichier en couches.
- `WindowRuntimeTests` (Windowing.Core) : fin de redimensionnement, minimum qui déborde, réduction
  en arrière-plan.
- Validateur, sérialiseur, mappeur et schéma (App.Abstractions).
- `WindowAppBaseWindowTitleTests` (SDK Blazor), `WindowTitleTests` (Windowing.Core),
  `AppWindowGatewayFactoryTests` (Platform.Blazor) : titre posé avant le premier rendu, même titre
  ignoré, nettoyage, retour au nom de l'app, une seule fenêtre visée, fenêtre fermée, surfaces.
- E2E `KazoPaintWindowTitleTests` (UI.E2E) : `Untitled — Kazo Paint`, `*` après un trait, nom du
  fichier après « Enregistrer sous » dans la barre de titre et la barre des tâches.
- E2E `WindowKeyboardCommandsTests` (UI.E2E) : `` Alt+` `` change de fenêtre et `Alt+Q` ferme la
  fenêtre active dans un onglet Chromium.

- `WindowFrameTests` (Windowing.Core), `WindowStyleOptionsTests` (Platform.Blazor),
  `WindowAppBaseWindowFrameTests` (SDK Blazor), sérialiseur (App.Abstractions) : styles et
  booléens (ADR 0234), cadre dessiné par style, barre des tâches, demandes de l'app (#384),
  dialogue au premier plan par sa propriétaire, champs écrits seulement hors défaut.
- `ChildWindowTests` (Platform.Blazor), `WindowAppBaseChildWindowTests` (SDK Blazor) : fenêtre
  enfant centrée, modale, résultat, bouton Fermer, fermeture avec la propriétaire, paramètres et
  cascade du composant, `Modal` imposé par `ShowDialogAsync`, jeton annulé.

## Liste des tâches

- [x] Corriger le double déplacement au relâchement et la transition avant/après projection.
- [x] Couvrir les images intermédiaires dans Chromium et dans le scénario OS de restauration.

- [x] Crochets de fenêtre dans `WindowAppBase` et contrats du SDK (`KazoWindowState`, `KazoWindowSize`).
- [x] Faits du runtime : lot publié, fin de redimensionnement, correction de la réduction en arrière-plan.
- [x] Répartiteur `WindowAppEventDispatcher` et mesure de la zone client.
- [x] Fenêtres détachées suivies (`TrackDisplaySurface`, navigateur).
- [x] `WindowOptions` dans `AppDefinition`, manifeste, schéma, validateur, sérialiseur strict.
- [x] Tailles appliquées à l'ouverture ; bouton Agrandir masqué si non redimensionnable.
- [x] `keybindings.config` (schéma, couches, défaut commenté, rechargement).
- [x] `WindowKeyboardCommands` : sélecteur, fermer, menu, déplacer, redimensionner, réduire, agrandir, ancrer, plein écran.
- [x] Menu de fenêtre et surcouches : clavier et clic droit sur l'icône (ADR 0164).
- [x] Icônes déclarées des apps système, URLs RCL et exception macOS (ADR 0164).
- [x] App d'exemple WindowApp : tailles déclarées et journal des événements.
- [x] Tests unitaires et E2E.
- [x] Documentation et index.

### Chantier `v3.1/3-window-title`

- [x] Commande `SetWindowTitleCommand`, événement `WindowTitleChangedEvent`, `WindowTitles.Sanitize`.
- [x] `IAppWindowGateway`, `AppWindowGatewayFactory`, cascade dans `WindowAppRenderer` (bureau et mobile).
- [x] `WindowTitle`, `SetWindowTitleAsync`, `FormatWindowTitle` dans `WindowAppBase` ; surfaces détachées suivies.
- [x] Kazo Paint, éditeur de texte, éditeur de code, éditeur de thèmes, explorateur.
- [x] Tests unitaires et E2E.
- [x] ADR 0123, documentation, référence du SDK et index.

### Chantier `os-3-1/4-window-style-options` (ADR 0234 §1, §2, §4, §6)

- [x] `WindowStyle` et booléens dans `WindowOptions`, manifeste, schéma, sérialiseur.
- [x] `WindowFrame`, `SetWindowFrameCommand`, `ActivationTarget` dans le runtime.
- [x] Cadre, poignées, clavier, arrangement et barre des tâches suivent le style.
- [x] Dialogues de l'OS en `Tool` sans bouton dans la barre des tâches.
- [x] `IAppWindowGateway` et méthodes de `WindowAppBase` (#384).
- [x] Tests et documentation.

### Chantier `os-3-1/5-child-windows` (ADR 0234 §3)

- [x] `KazoWindowRequest`, `KazoDialogResult`, `IKazoWindow`, `IAppChildWindowGateway`, `KazoWindowBase`.
- [x] `ShowWindowAsync` et `ShowDialogAsync` dans `WindowAppBase` et `KazoWindowBase`.
- [x] Fenêtres enfants dans `AppWindowGatewayFactory` : cascade, modale, fermeture avec la propriétaire.
- [x] Tests et documentation.

### Chantier `os-3-1/6-transparent-none` (ADR 0234 §5, #378)

- [x] Surface `None` sans fond ni ombre, clics à travers, `data-kazo-window-hit`.
- [x] Bureau interactif : la fenêtre `None` laisse passer la souris.
- [x] Test E2E et documentation.
