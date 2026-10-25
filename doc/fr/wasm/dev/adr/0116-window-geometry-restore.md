> [English version](../../../../en/wasm/dev/adr/0116-window-geometry-restore.md)

# ADR 0116 : géométrie des fenêtres enregistrée et restaurée

## Statut

Accepté le 2026-10-17 (chantier `v3.1/1-adr`, pour le chantier
`v3.1/2-window-geometry`). Une question est posée à Benoit dans le fil du
chantier (§1) ; ce qui suit applique la réponse recommandée en attendant.

- **Met en œuvre :** [ADR 0044](0044-settings-as-files.md) §10 (géométrie des
  fenêtres dans `~/.local/state/hackeros/windows/<appId>.config`, propre à
  l'appareil, jamais synchronisée).
- **Complète :** [ADR 0095](0095-window-events-sizes-and-keyboard.md) §2
  (tailles déclarées par l'app) et [ADR 0009](0009-window-runtime-strategy.md)
  (géométrie tenue par le gestionnaire de fenêtres en C#).
- **Plan :** [`v3-1-series-plan.md`](../plans/v3-1-series-plan.md) ; lacune
  « géométrie jamais relue » de [`wasm-v3-final-status.md`](../plans/wasm-v3-final-status.md).

## Contexte

- `WindowGeometryPersistence` et `WindowGeometrySettings`
  (`Platform/HackerOs.Platform.Blazor/Windows/WindowGeometrySettings.cs`)
  savent lire et écrire `x=`, `y=`, `width=`, `height=` dans le fichier d'état
  d'une app. Le service est enregistré dans l'injection de dépendances
  (`EcosystemServiceCollectionExtensions.cs:820`), mais **aucun code ne
  l'appelle** : rien n'écrit la géométrie, rien ne la relit.
  `settings-files.md` le présente pourtant comme « restaure et enregistre ».
- `WindowLaunchCoordinator.InitialBounds` place chaque nouvelle fenêtre en
  cascade (8 positions décalées de 28 px) à la taille déclarée par l'app
  (ADR 0095 §2), ramenée à la zone de travail sans descendre sous le minimum.
- `SaveAsync` attend un indicateur `isEligible` qu'aucun champ du manifeste ne
  fournit.
- Sous un vrai bureau, deux modèles existent : Windows et macOS rouvrent une
  fenêtre là où elle était ; sous GNOME, le gestionnaire de fenêtres ne le fait
  pas et chaque app GTK enregistre au moins sa taille et son état agrandi.
  HackerOS tient la géométrie en C# (ADR 0009) ; c'est donc à lui de
  l'enregistrer, pas à chaque app.

## Décision

### 1. Toutes les fenêtres retiennent taille, position et état agrandi (question 1, défaut appliqué)

- Chaque fenêtre d'app de genre `Window` retient, par app et par utilisateur,
  sa taille, sa position et le fait qu'elle était agrandie.
- `WindowOptions` (`HackerOs.App.Abstractions`) reçoit
  `RememberGeometry` (booléen, vrai par défaut). Une app qui le met à faux
  (une calculatrice qui veut toujours s'ouvrir au même endroit, un assistant)
  s'ouvre toujours à sa place de cascade. Le générateur écrit
  `window.rememberGeometry` dans `manifest.json` seulement quand il vaut faux ;
  le schéma v1 l'ajoute comme champ facultatif (ajout compatible).
- C'est un ajout au SDK : la propriété va dans `PublicAPI.Unshipped.txt` et
  sort avec la 1.1 ([ADR 0117](0117-sdk-window-title-and-sdk-1-1.md) §4).
- Les fenêtres de l'OS qui ne sont pas des apps (boîtes de dialogue de
  fichiers, agent d'authentification, sélecteur « Ouvrir avec ») ne retiennent
  rien.

### 2. Format du fichier

Le fichier existant garde ses quatre clés et en reçoit une cinquième :

```ini
# Géométrie de la fenêtre de org.hackeros.text-editor (ADR 0116)
x=312
y=96
width=900
height=640
maximized=false
```

- `x`, `y`, `width`, `height` sont toujours la géométrie **normale** (non
  agrandie, non ancrée), en pixels CSS, culture invariante. Une fenêtre
  agrandie garde donc sa géométrie de restauration, comme sous X11.
- `maximized` est facultatif ; absent, il vaut `false`. Un fichier sans les
  quatre nombres reste ignoré : la fenêtre s'ouvre à sa place de cascade (la
  règle actuelle de `WindowGeometrySettings.Parse`).
- L'état réduit n'est jamais enregistré : une fenêtre ne rouvre jamais réduite.
- Une fenêtre ancrée à gauche ou à droite enregistre sa géométrie d'avant
  l'ancrage (l'ancrage n'en garde pas, `window-model.md`) : elle rouvre comme
  une fenêtre normale.

### 3. Quand la géométrie est écrite

- À la fin d'un déplacement ou d'un redimensionnement (pointeur relâché ou
  commande clavier Alt+F7, Alt+F8 validée), à l'agrandissement, à la
  restauration et à la fermeture de la fenêtre. Jamais à chaque pixel : les
  événements de `WindowRuntime` qui alimentent déjà `OnWindowResized`
  (ADR 0095 §1) servent de déclencheur, avec un regroupement de 500 ms.
- L'écriture passe par `IOsSettingsService` sous l'identité de l'utilisateur,
  comme aujourd'hui ; un échec (pas de session, dossier personnel en lecture
  seule) est ignoré, la géométrie étant un confort.
- Le fichier est écrit seulement s'il change, pour ne pas réveiller les
  observateurs de fichiers à chaque fermeture.

### 4. Quand la géométrie est relue

`WindowLaunchCoordinator` lit le fichier avant d'ouvrir la fenêtre, à la place
de `InitialBounds` :

1. fichier absent ou invalide, ou `RememberGeometry` faux : place de cascade
   (comportement actuel) ;
2. la taille lue est ramenée entre le minimum et le maximum **déclarés
   aujourd'hui** par l'app (le manifeste a pu changer depuis l'écriture), puis
   à la zone de travail, sans descendre sous le minimum ;
3. la position est corrigée pour que la barre de titre reste atteignable : au
   moins 48 px de la barre visibles horizontalement, et le haut de la fenêtre
   dans la zone de travail. Un écran plus petit que la dernière fois (autre
   appareil, fenêtre du navigateur réduite) ramène donc la fenêtre à l'écran,
   comme un gestionnaire de fenêtres qui perd un moniteur ;
4. si une fenêtre de la même app est déjà ouverte (deuxième instance), la
   nouvelle s'ouvre décalée de 28 px depuis la géométrie lue, pour ne pas
   recouvrir exactement la première ;
5. `maximized=true` ouvre la fenêtre agrandie, avec la géométrie lue comme
   géométrie de restauration.

### 5. Ce qui n'est pas couvert

- **Interface mobile :** plein écran, elle ignore ce fichier (comme les tailles
  de l'ADR 0095).
- **Fenêtres détachées** (ADR 0078, 0081) : leur fenêtre de navigateur est
  placée par le système hôte ; elles ne lisent ni n'écrivent ce fichier.
- **Oublier la géométrie :** supprimer le fichier
  (`rm ~/.local/state/hackeros/windows/<appId>.config`) suffit, comme on
  efface l'état d'une app sous Linux. Pas de bouton dans Paramètres pour
  l'instant.
- **Synchronisation :** aucune, le fichier est sous `~/.local/state`
  (ADR 0044 §10) ; un autre appareil garde ses propres positions.

## Conséquences

- `v3.1/2-window-geometry` branche `WindowGeometryPersistence` dans
  `WindowLaunchCoordinator` et sur les événements de `WindowRuntime`, ajoute
  `maximized`, `WindowOptions.RememberGeometry`, le champ du schéma et son
  contrôle par le validateur, et retire le paramètre `isEligible` de
  `SaveAsync` (type de la plateforme, hors promesse du SDK, ADR 0114 §2 ; la
  ligne retirée est écrite `*REMOVED*`).
- Tests unitaires : correction de taille et de position (écran plus petit,
  minimum changé, deuxième instance), état agrandi, `RememberGeometry` faux,
  fichier invalide. Test E2E : déplacer et redimensionner une fenêtre, la
  fermer, la rouvrir au même endroit ; recharger la page et retrouver la même
  place.
- `window-model.md`, `settings-files.md`, `app-sdk.md`, `sdk-reference.md` et
  la doc du schéma du manifeste décrivent le comportement et le champ.

## Options écartées

- **Seulement sur demande de l'app** (`RememberGeometry` faux par défaut) :
  aucune app existante n'en profiterait sans être modifiée, et la lacune
  resterait entière pour les apps des autres dépôts.
- **Taille seulement, sans position** (modèle GNOME) : plus fidèle à un bureau
  Linux, mais l'OS tient déjà la géométrie lui-même ; ne garder que la taille
  surprendrait la plupart des utilisateurs, habitués à Windows ou macOS.
- **Une géométrie par instance** : il n'existe pas d'identité stable d'une
  instance entre deux ouvertures ; un fichier par app suffit.
