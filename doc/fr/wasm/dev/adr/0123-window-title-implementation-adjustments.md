> [English version](../../../../en/wasm/dev/adr/0123-window-title-implementation-adjustments.md)

# ADR 0123 : ajustements de la mise en œuvre du titre de fenêtre

## Statut

Accepté le 2026-10-17 (chantier `v3.1/3-window-title`).

- **Ajuste :** [ADR 0117](0117-sdk-window-title-and-sdk-1-1.md) §2 à §4, sans
  changer sa décision (l'app pose tout le titre, l'OS le nettoie et l'affiche).
- **Applique :** [ADR 0114](0114-sdk-1-0-freeze.md) §1 : tous les ajouts vont dans
  `PublicAPI.Unshipped.txt` et sortiront dans la 1.1 (chantier `v3.1/4-sdk-1-1`).
- **Document :** [`window-model.md`](../../framework/window-model.md) §4.

## Contexte

L'ADR 0117 décrit l'API (`WindowTitle`, `SetWindowTitleAsync`), une passerelle
« interne » `IAppWindowGateway`, la commande du runtime et les apps qui
l'adoptent. La mise en œuvre a dû préciser comment la passerelle arrive à l'app,
comment une app compose la convention `document — App` avec le nom traduit, et ce
que deviennent les titres que certaines apps affichaient déjà chez elles.

## Décision

### 1. `IAppWindowGateway` est publique, transmise en valeur en cascade fixe

- L'interface est dans `HackerOs.AppSdk.Blazor` et publique : `WindowAppBase` (SDK)
  l'appelle et la plateforme (`HackerOs.Platform.Blazor`) l'implémente ; aucune
  `InternalsVisibleTo` ne relie ces deux paquets. Comme `IWindowAppEventTarget`,
  une app ne l'implémente ni ne l'appelle : elle passe par `WindowAppBase`.
- Elle n'est **pas** dans l'injection de dépendances. `WindowAppRenderer` crée une
  instance par fenêtre (`AppWindowGatewayFactory.Create(windowId, manifest)`) et la
  donne à l'app par un `CascadingValue<IAppWindowGateway>` fixe, à côté du contexte
  d'exécution. Une app ne peut donc obtenir que la passerelle de sa propre
  fenêtre : rien ne lui permet d'en nommer une autre (§2 de l'ADR 0117, « une seule
  fenêtre visée »).
- La valeur en cascade est posée avant que le composant de l'app soit créé : un
  titre posé dans `OnAppInitialized[Async]` est appliqué avant le premier rendu.

### 2. Convention `document — App` fournie par le SDK

- `IAppWindowGateway.DefaultTitle` rend le titre par défaut de la fenêtre : le nom
  de l'app dans la langue du bureau (ADR 0104 §4), celui que
  `WindowLaunchCoordinator.DefaultTitle(manifest)` donne à une nouvelle fenêtre.
- `WindowAppBase.FormatWindowTitle(document, modified)` compose
  `document — App` avec ce nom, précédé de `*` si `modified`, ou le nom seul sans
  document. Les apps de base l'utilisent ; une app reste libre d'écrire un autre
  titre.

### 3. Détails du comportement

- `IAppWindowGateway.SetTitleAsync` rend le titre affiché une fois nettoyé ;
  `WindowAppBase` ignore un appel qui redemande le titre déjà demandé, pour qu'une
  app puisse l'appeler après chaque rendu sans aller-retour.
- Le nettoyage (`WindowTitles.Sanitize`, Windowing.Abstractions) ne coupe jamais
  une paire de substitution UTF-16 en deux ; la limite de 256 caractères compte
  l'ellipse.
- Une fenêtre déjà fermée n'est pas renommée : l'appel ne fait rien, comme une
  propriété écrite sur une fenêtre X11 détruite.
- Sans gestionnaire de fenêtres (tests, prérendu), `WindowTitle` rend le dernier
  titre demandé, coupé aux extrémités, ou le nom du manifeste.

### 4. Fenêtres détachées

Les surfaces suivies par `TrackDisplaySurface` prennent le titre de l'app
seulement après un appel à `SetWindowTitleAsync` avec un titre non vide ; une
surface suivie ensuite reçoit le titre courant. Le navigateur, qui donne à sa
surface le titre de la page et n'appelle pas `SetWindowTitleAsync`, garde son
comportement.

### 5. Titres déjà affichés par les apps

- **Hack Paint :** le titre quitte l'app (ADR 0117 §4) ; l'indicateur
  « ouverture… / enregistrement… » passe dans la barre d'état.
- **Éditeur de texte :** son bandeau de menu garde le nom du document (avec `•`
  quand il est modifié, et l'alerte d'erreur de chargement), que ses tests E2E
  lisent ; la fenêtre porte `*notes.txt — Éditeur de texte`.
- **Éditeur de thèmes :** la barre d'outils garde le titre, identique à celui de
  la fenêtre.
- **Éditeur de code :** son bandeau affiche le titre de la fenêtre ; le séparateur
  devient le tiret cadratin et `*` marque l'onglet actif modifié
  (`*Program.cs — src — Éditeur de code`). Les clés `Title.Folder`,
  `Title.Document` et `Title.DocumentFolder` de ses ressources sont retirées.
- **Explorateur de fichiers :** `/` pour la racine, le nom de la corbeille quand
  elle est affichée.

### 6. Bureau mobile

`MobileShell` passe aussi la fabrique à `WindowAppRenderer` : le titre posé par
l'app apparaît dans la liste des récents du bureau mobile comme dans la barre des
tâches du bureau.

## Conséquences

- Surface ajoutée (dans `PublicAPI.Unshipped.txt`) : SDK Blazor
  (`IAppWindowGateway`, `WindowAppBase.WindowTitle`, `SetWindowTitleAsync`,
  `FormatWindowTitle`), Windowing (`SetWindowTitleCommand`,
  `WindowTitleChangedEvent`, `WindowTitles`, `WindowRuntime.SetTitle`), plateforme
  (`AppWindowGatewayFactory`, `WindowAppRenderer.WindowGateways`,
  `WindowLaunchCoordinator.DefaultTitle`) et les surcharges ajoutées par trois apps
  de base.
- Un hôte qui compose lui-même `DesktopShell` ou `MobileShell` doit enregistrer
  `AppWindowGatewayFactory` (fait par `AddHackerOsEcosystem`).

## Options écartées

- **Passerelle injectée par DI avec l'identifiant d'instance en paramètre :** une
  app aurait pu injecter le service et nommer l'instance d'une autre app ; il
  aurait fallu un registre des contextes pour le refuser.
- **`InternalsVisibleTo` du SDK vers la plateforme :** lie deux paquets publiés
  séparément et cache une interface que la plateforme doit pouvoir implémenter.
