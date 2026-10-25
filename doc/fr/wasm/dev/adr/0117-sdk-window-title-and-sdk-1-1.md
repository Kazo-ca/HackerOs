> [English version](../../../../en/wasm/dev/adr/0117-sdk-window-title-and-sdk-1-1.md)

# ADR 0117 : titre de fenêtre posé par l'app et publication du SDK 1.1

## Statut

Accepté le 2026-10-17 (chantier `v3.1/1-adr`, pour les chantiers
`v3.1/3-window-title` et `v3.1/4-sdk-1-1`). Deux questions sont posées à
Benoit dans le fil du chantier (§1 et §5) ; ce qui suit applique la réponse
recommandée en attendant.

- **Corrige l'écart avec :** [ADR 0103](0103-hack-paint-image-files-and-theme-editor.md)
  §1 (« titre de fenêtre `nom.png — Hack Paint`, avec `*` quand l'image est
  modifiée »), jamais réalisé faute d'API.
- **Applique :** [ADR 0114](0114-sdk-1-0-freeze.md) §1 (un ajout va dans
  `PublicAPI.Unshipped.txt` ; une publication mineure le verse dans `Shipped`)
  et [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md) §3 (version
  minimale du SDK écrite dans le manifeste).
- **Complète :** [ADR 0095](0095-window-events-sizes-and-keyboard.md) §1
  (crochets de fenêtre dans `WindowAppBase`) et
  [ADR 0081](0081-detached-display-windows-adjustments.md) (fenêtres
  détachées).
- **Plan :** [`v3-1-series-plan.md`](../plans/v3-1-series-plan.md) ; lacune « pas
  d'API pour le titre » de [`wasm-v3-final-status.md`](../plans/wasm-v3-final-status.md).

## Contexte

- `WindowRuntimeState.Title` (`Platform/HackerOs.Windowing.Abstractions/WindowRuntimeState.cs:98`)
  est fixé à l'ouverture par `WindowLaunchCoordinator` avec le nom de l'app et
  ne change plus. La barre de titre, la barre des tâches et le sélecteur Alt+`
  (`WindowManagerOverlays.razor`) le lisent.
- Ni `WindowAppBase` ni `IAppExecutionContext` n'offrent de moyen de le
  changer. Hack Paint affiche donc le nom du fichier dans sa propre barre
  d'outils ; l'éditeur de texte, Code Editor et l'éditeur de thèmes n'affichent
  pas le document ouvert dans le titre.
- Les fenêtres détachées ont déjà un `SetTitleAsync` interne
  (`DisplaySurfaceService.cs:234`) qui change le titre de la fenêtre du
  navigateur ; les sites (`HackWebSite.SetTitle`) et les onglets du terminal
  (`TerminalPanel.OnTitleChanged`) ont leurs propres titres, sans lien avec la
  fenêtre.
- Sous X11 ou Wayland, l'app pose elle-même tout le titre (`_NET_WM_NAME`,
  `xdg_toplevel.set_title`) ; le gestionnaire de fenêtres l'affiche tel quel.
- Le SDK est gelé en 1.0.0 (ADR 0114) : toute API ajoutée doit sortir dans une
  mineure, 1.1.0.

## Décision

### 1. L'app pose tout le titre (question 1, défaut appliqué)

- L'app fournit le titre complet ; l'OS ne lui ajoute rien. La convention des
  apps de base est `document — App` (`photo.png — Hack Paint`), avec `*` en
  tête quand le document a des modifications non enregistrées
  (`*photo.png — Hack Paint`), comme GNOME et KDE.
- Un titre vide ou `null` rend le titre par défaut : le nom localisé de l'app
  (`AppManifest.Name`, ADR 0111).
- Le titre est nettoyé avant affichage : caractères de contrôle retirés, espaces
  aux extrémités coupés, 256 caractères au plus (au-delà, coupé avec `…`).
- **Usurpation :** comme sous X11, une app peut choisir un titre trompeur. Les
  fenêtres sensibles de l'OS (agent d'authentification, approbation d'une
  capacité dangereuse, ADR 0049) restent dessinées par l'OS, avec son propre
  cadre, et ne prennent jamais le titre d'une app ; la barre des tâches montre
  toujours l'icône de l'app propriétaire à côté du titre.

### 2. API du SDK

Dans `HackerOs.AppSdk.Blazor`, sur `WindowAppBase` :

```csharp
/// Titre affiché par le gestionnaire de fenêtres (barre de titre, barre des tâches, Alt+`).
protected string WindowTitle { get; }

/// Change le titre ; null ou vide rend le nom de l'app.
protected ValueTask SetWindowTitleAsync(string? title, CancellationToken cancellationToken = default);
```

- L'appel passe par une passerelle de la plateforme (`IAppWindowGateway`,
  interne), jamais par un type de `HackerOs.Windowing.*` dans le SDK
  (ADR 0055). Aucune capacité n'est demandée : une app ne change que le titre
  de ses propres fenêtres.
- Appelé avant le premier rendu (dans `OnAppInitializedAsync`), le titre est
  posé avant l'affichage de la fenêtre, sans clignotement.
- L'appel ne vise que la fenêtre de l'instance qui l'appelle, jamais une
  autre fenêtre de la même app.
- Une app sans interface (genre `Terminal` ou `Service`) n'a pas cette API ;
  le titre d'un onglet de terminal reste celui de `TerminalPanel`.

### 3. Côté plateforme

- `WindowRuntimeState.Title` devient modifiable par
  `WindowRuntime.SetTitle(windowId, title)`, qui publie un nouvel événement
  `WindowTitleChanged`. Toutes les surfaces qui affichent le titre (barre de
  titre, barre des tâches, sélecteur, menu de la fenêtre) s'y abonnent. Ces types
  sont hors de la promesse du SDK (ADR 0114 §2) ; leurs ajouts vont tout de même
  dans `PublicAPI.Unshipped.txt`.
- **Accessibilité :** le nom accessible de la fenêtre (`aria-label` du cadre)
  suit le titre. Le changement n'est pas annoncé par un lecteur d'écran (pas de
  région `aria-live`), comme un changement de titre sous Orca.
- **Fenêtres détachées :** le titre est aussi posé sur la fenêtre du navigateur
  qui porte la surface, par le `SetTitleAsync` existant.
- **Onglet du navigateur hôte :** l'élément `<title>` de la page reste celui de
  l'OS (`HackerOS`) ; seul le titre des fenêtres détachées suit l'app.

### 4. Apps de base qui l'adoptent

- **Hack Paint :** `nom.png — Hack Paint`, `*` quand l'image est modifiée
  (ADR 0103 §1) ; le nom du fichier quitte la barre d'outils.
- **Éditeur de texte, Code Editor, éditeur de thèmes :** même convention avec
  le fichier ouvert (Code Editor : le fichier de l'onglet actif, puis le nom du
  dossier ouvert, comme VS Code).
- **Explorateur de fichiers :** le nom du dossier courant (`Documents —
  Explorateur de fichiers`).
- Les autres apps gardent leur nom.

### 5. Publication du SDK 1.1 : un chantier à part en fin de série (question 2, défaut appliqué)

La série v3.1 se termine par un petit chantier `v3.1/4-sdk-1-1`, après tous
les autres et après la fusion de `v3/base` dans `v3.1/base` (voir
[`v3-1-series-plan.md`](../plans/v3-1-series-plan.md)) :

- chaque `PublicAPI.Unshipped.txt` est versé dans son `PublicAPI.Shipped.txt`
  (lignes triées, sans doublon), comme pour la 1.0 (ADR 0114 §1). Cela couvre
  les ajouts de la série v3.1 (`WindowOptions.RememberGeometry`, ADR 0116 ;
  `SetWindowTitleAsync`, ce document) et ceux arrivés de `v3/base`
  (associations par utilisateur, `passwd`) ;
- `build/Versions.props` : `HackerOsVersion` = `1.1.0`. Les manifestes générés
  portent alors `sdkCompatibility.minimumVersion: 1.1.0` ;
- l'app de compatibilité compilée en **1.0.0** (`Tests/fixtures/sdk-compat`)
  reste telle quelle et doit toujours se charger et tourner sur l'OS 1.1 : c'est
  la preuve de la promesse « 1.x tourne sur 1.y, y ≥ x » (ADR 0114 §2, §4). Le
  paquet n'est pas reconstruit ;
- la section « Ruptures hors SDK » de [`sdk-packages.md`](../../framework/sdk-packages.md)
  liste les lignes `*REMOVED*` de la série, et
  [`building-on-hackeros.md`](../../framework/building-on-hackeros.md) dit ce que la 1.1
  ajoute ;
- le chantier refait la vérification complète de `v3/16-final` (build Debug et
  Release, toutes les suites, `build/publish-trimmed.sh`, `build/verify-hosts.sh`)
  et met à jour le bilan [`wasm-v3-final-status.md`](../plans/wasm-v3-final-status.md).

## Conséquences

- `v3.1/3-window-title` ajoute l'API, la passerelle, l'événement de
  `WindowRuntime`, l'abonnement des surfaces (barre de titre, barre des tâches,
  sélecteur, fenêtres détachées) et l'adoption par les
  apps de base. Tests unitaires : nettoyage du titre, retour au nom de l'app,
  titre posé avant le premier rendu, une seule fenêtre visée. Test E2E : ouvrir
  une image dans Hack Paint, voir son nom dans la barre de titre et la barre des
  tâches, la modifier et voir `*`.
- `app-sdk.md`, `sdk-reference.md`, `sdk-developer-guide.md`, `window-model.md`
  et `hack-paint.md` décrivent l'API et la convention.
- Le dépôt du jeu (ADR 0092) peut utiliser l'API en compilant contre 1.1.0 ; ses
  apps compilées en 1.0.0 continuent de tourner.

## Options écartées

- **L'OS ajoute toujours « — Nom de l'app »** (question 1) : empêche une app de
  se faire passer pour une autre dans la barre de titre, mais impose un format
  que les vrais gestionnaires de fenêtres n'imposent pas, et double le nom quand
  l'app l'écrit déjà.
- **Une propriété `[Parameter]` ou un composant `<HackWindowTitle>`** (comme
  `<PageTitle>` de Blazor) : pratique dans le balisage, mais un deuxième chemin
  pour la même chose ; il pourra venir plus tard, appuyé sur
  `SetWindowTitleAsync`.
- **Passer la version à 1.1.0 dans `v3.1/3-window-title`** (question 2) : un
  chantier de moins, mais les ajouts arrivés de `v3/base` après lui sortiraient
  dans une 1.2, et la vérification complète se ferait sur une série
  incomplète.
