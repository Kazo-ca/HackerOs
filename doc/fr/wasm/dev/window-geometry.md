> [English version](../../../en/wasm/dev/window-geometry.md)

# Taille des fenêtres enregistrée et restaurée

## Objet

Une fenêtre d'app rouvre avec la taille que l'utilisateur lui a donnée, et
agrandie si elle l'était, mais toujours à sa place de cascade : sa position
n'est pas retenue. Mis en œuvre par le chantier `v3.1/2-window-geometry` selon
l'[ADR 0116](adr/0116-window-geometry-restore.md), corrigé par
l'[ADR 0122](adr/0122-window-size-only-not-position.md) (taille seulement,
réponse de Benoit), dans la série [`v3-1-series-plan.md`](plans/v3-1-series-plan.md).
C'est le §10 de l'[ADR 0044](adr/0044-settings-as-files.md).

C'est le modèle de GNOME : une app GTK retient sa taille et son état agrandi,
le gestionnaire de fenêtres place la fenêtre. HackerOS tient la géométrie en C#
(ADR 0009) ; il écrit donc lui-même cette taille, pour toutes les apps.

## Fichier d'état

Un fichier par app et par utilisateur, propre à l'appareil, jamais synchronisé :
`~/.local/state/hackeros/windows/<appId>.config`.

```ini
# Window size of org.hackeros.text-editor (ADR 0116, ADR 0122)
width=900
height=640
maximized=false
```

- `width`, `height` : taille **normale** en pixels CSS, culture invariante.
  Une fenêtre agrandie y garde sa taille de restauration.
- `maximized` : facultatif, `false` s'il manque. Seul `true` agrandit : une
  faute de frappe laisse la fenêtre normale.
- Un fichier sans `width` et `height` valides est ignoré : la fenêtre s'ouvre à
  sa taille déclarée.
- Un fichier écrit avant l'ADR 0122 (avec `x=` et `y=`) garde sa taille ; sa
  position est ignorée, et la prochaine écriture le réécrit sans ces clés.
- Oublier la taille d'une app : `rm ~/.local/state/hackeros/windows/<appId>.config`.

## Architecture

| Classe (Platform.Blazor, `Windows/`) | Rôle |
|---|---|
| `WindowGeometrySettings` | Chemin du fichier, `Parse` et `Serialize`. |
| `SavedWindowGeometry` | Taille normale (`Width`, `Height`) et état agrandi lus ou à écrire. |
| `WindowGeometryPersistence` | Lit et écrit le fichier par `IOsSettingsService`, sous l'identité de l'utilisateur ; n'écrit que si le contenu change ; un échec (pas de session, dossier en lecture seule) est ignoré. |
| `WindowGeometryRecorder` | Écoute `WindowRuntime.EventsApplied`, garde en mémoire la dernière taille normale et l'état agrandi de chaque fenêtre suivie et l'écrit après 500 ms sans changement, ou tout de suite à la fermeture. |
| `WindowLaunchCoordinator` | `PresentAsync` lit le fichier avant d'ouvrir la fenêtre, place la fenêtre à sa place de cascade avec la taille lue et corrigée (`RestoredBounds`), agrandit si besoin, puis confie la fenêtre au recorder. |

Les trois services sont `Scoped`, comme `WindowRuntime`
(`EcosystemServiceCollectionExtensions`).

### Écriture

- Déclencheurs : tout lot de faits de `WindowRuntime` qui change la taille
  normale ou l'état agrandi d'une fenêtre suivie (redimensionnement, agrandir,
  restaurer, changement de zone de travail). Un déplacement seul n'écrit rien. Le
  recorder relit l'état du runtime après chaque lot plutôt que chaque fait, pour
  enregistrer les bornes réellement appliquées.
- Regroupement : le redimensionnement transmet des échantillons regroupés par
  image, avec un seul appel interop en cours (ADR 0163) ; les changements d'une
  même app sont écrits une fois, 500 ms après le dernier. Un déplacement ne
  transmet sa position finale qu'au relâchement et n'écrit toujours rien sur disque.
- Fermeture : écriture immédiate de la dernière taille connue.
- Les écritures d'une même app passent l'une après l'autre ; ouvrir une fenêtre
  lance tout de suite l'écriture qui attendait encore et attend la fin des
  écritures de l'app avant de lire le fichier. Une fenêtre rouverte juste après
  sa fermeture, ou une deuxième fenêtre ouverte pendant le regroupement, voit
  donc la dernière taille.
- Jamais enregistré : l'état réduit (la fenêtre garde ce qu'elle avait avant) et
  la moitié d'écran d'une fenêtre ancrée (elle garde sa taille d'avant
  l'ancrage et rouvre comme une fenêtre normale).

### Lecture (ADR 0116 §4, ADR 0122)

1. Pas de fichier, fichier invalide ou `RememberGeometry = false` : place de
   cascade, taille déclarée (`InitialBounds`, comportement d'avant).
2. Sinon, place de cascade (`InitialBounds`), avec la taille lue ramenée entre
   le minimum et le maximum que l'app déclare **aujourd'hui** (une fenêtre de
   taille fixe prend sa taille déclarée), puis à la zone de travail sans
   descendre sous le minimum.
3. Une deuxième fenêtre de la même app prend la place de cascade suivante,
   comme toute nouvelle fenêtre, avec la même taille.
4. `maximized=true` : la fenêtre s'ouvre agrandie, la taille lue devient sa
   taille de restauration. Une fenêtre de taille fixe ne rouvre jamais agrandie.

## Usage pour une app

Rien à faire : toute app de genre `Window` retient sa taille. Pour refuser
(une calculatrice, un assistant qui doit toujours s'ouvrir à la même taille) :

```csharp
public override WindowOptions? Window => new()
{
    DefaultWidth = 320, DefaultHeight = 480,
    RememberGeometry = false,
};
```

`WindowOptions.RememberGeometry` est un ajout du SDK 1.1
(`PublicAPI.Shipped.txt` de `HackerOs.App.Abstractions` depuis `v3.1/4-sdk-1-1`). Le générateur écrit
`window.rememberGeometry` dans `manifest.json` seulement quand il vaut `false` :
les manifestes des apps qui gardent le défaut restent identiques octet pour
octet. Le schéma v1 l'accepte comme champ facultatif, le désérialiseur strict
aussi.

## Ce qui n'est pas couvert

- Interface mobile : plein écran, elle ignore ce fichier.
- Fenêtres détachées (ADR 0078, 0081) : placées par le système hôte.
- Fenêtres de l'OS qui ne sont pas des apps (boîtes de dialogue de fichiers,
  agent d'authentification, « Ouvrir avec ») : jamais suivies.
- La position : jamais retenue (ADR 0122).
- Pas de bouton « oublier la taille » dans Paramètres.

## Décisions de mise en œuvre

- **`PresentAsync` remplace `Present`** : lire le fichier est asynchrone. Le
  shell présente parfois deux fois le même processus (son propre lancement et le
  passage du processus à `Running`) ; pendant la lecture, le second appel rend la
  main sans ouvrir de deuxième fenêtre, et l'existence de la fenêtre est
  revérifiée après la lecture.
- **Ancrage reconnu à sa forme** : `WindowRuntime` ne garde pas d'état « ancré »
  (window-model.md). Une fenêtre normale qui couvre exactement la moitié gauche ou
  droite de la zone de travail est traitée comme ancrée et n'est pas enregistrée.
- **`SaveAsync` sans `isEligible`** : c'est le coordinateur qui décide quelles
  fenêtres suivre (`Track`) ; la ligne retirée a été marquée `*REMOVED*` dans
  `PublicAPI.Unshipped.txt` de Platform.Blazor (hors promesse du SDK, ADR 0114 §2),
  puis appliquée à la publication de la 1.1 (« Ruptures hors SDK » de [`sdk-packages.md`](../framework/sdk-packages.md#ruptures-hors-sdk)).
- **Échecs journalisés** : `WindowGeometryPersistence` écrit un avertissement
  quand le fichier ne peut être lu ou écrit, puis continue.
- **Contenu comparé avant écriture** : fermer une fenêtre dont la taille n'a pas
  changé ne réécrit pas le fichier et ne réveille pas les observateurs.
- **Taille seulement (ADR 0122)** : livré d'abord avec la position (ADR 0116
  §1), puis corrigé sur la réponse de Benoit ; `RestoredBounds` prend la taille
  et l'indice de cascade, `MinimumVisibleTitleBarWidth` a disparu.
- **En-tête commenté** dans le fichier, comme les autres documents de l'OS.

## Tests

- `WindowGeometryPersistenceTests` (Platform.Blazor) : chemin, aller-retour,
  état agrandi, `maximized` facultatif, fichier invalide, pas de réécriture,
  pas de session.
- `WindowGeometryRestoreTests` (Platform.Blazor) : réouverture avec la même
  taille à la place de cascade, nouvelle session, déplacement seul sans
  écriture, agrandie, réduite ou ancrée, regroupement d'un glisser, écriture en
  attente vidée à l'ouverture, `RememberGeometry = false`, fichier invalide,
  fichier d'avant l'ADR 0122, double présentation, écran plus petit, tailles
  déclarées changées, fenêtre fixe, fenêtre de l'OS non suivie.
- `AppManifestJsonSerializerTests`, `ManifestSchemaConformanceTests`
  (App.Abstractions) : `rememberGeometry` écrit seulement à `false`, relu, accepté
  par le schéma.
- E2E `WindowGeometryRestoreTests` (UI.E2E) : glisser la barre de titre et le
  coin d'une fenêtre, la fermer, la rouvrir avec la même taille à sa place de
  cascade, recharger la page, se reconnecter et la retrouver ainsi.

## Liste des tâches

- [x] `WindowOptions.RememberGeometry`, projection JSON, schéma, désérialiseur strict, `PublicAPI.Unshipped.txt`.
- [x] Clé `maximized` et `SavedWindowGeometry` ; `SaveAsync` sans `isEligible`, écrit seulement si le contenu change.
- [x] `WindowGeometryRecorder` : suivi, regroupement de 500 ms, écriture à la fermeture, réduction et ancrage ignorés.
- [x] `WindowLaunchCoordinator.PresentAsync` : lecture, correction, deuxième instance, agrandie, garde contre la double présentation.
- [x] `DesktopShell` et injection de dépendances.
- [x] Tests unitaires et E2E.
- [x] `window-model.md`, `settings-files.md`, `sdk-developer-guide.md`, `sdk-reference.md`, `app-sdk.md`, bilan v3, plan v3.1, index.
- [x] ADR 0122 : taille seulement ; position retirée du fichier, de la lecture et des tests.
