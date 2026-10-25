# ADR 0122 : une fenêtre retient sa taille, pas sa position

## Statut

Accepté le 2026-10-17 (série v3.1, correction de `v3.1/2-window-geometry`),
sur la réponse de Benoit à la question 1 du fil « V3.1 1 » : « 1.c », soit
« la taille seulement, comme sous GNOME ».

- **Remplace :** [ADR 0116](0116-window-geometry-restore.md) §1 (« taille,
  position et état agrandi »), les clés `x` et `y` du §2, et la correction de
  position et le décalage de la deuxième instance du §4 (points 3 et 4).
- **Garde :** le reste de l'ADR 0116 : fichier
  `~/.local/state/hackeros/windows/<appId>.config`, état agrandi, regroupement
  des écritures (§3), correction de la taille (§4 point 2),
  `WindowOptions.RememberGeometry`, ce qui n'est pas couvert (§5).
- **Document :** [`window-geometry.md`](../window-geometry.md).

## Contexte

L'ADR 0116 recommandait de tout retenir, comme Windows et macOS, et le chantier
`v3.1/2-window-geometry` (PR #170) l'a livré ainsi. Benoit a choisi l'autre
modèle : sous GNOME, le gestionnaire de fenêtres ne replace pas une fenêtre, et
une app GTK ne retient que sa taille et son état agrandi (gedit, Nautilus). Ce
modèle est plus fidèle au bureau Linux que HackerOS imite.

## Décision

- Une fenêtre d'app retient sa **taille normale** et son **état agrandi**.
  Elle s'ouvre toujours à sa place de cascade (`InitialBounds`), avec la taille
  lue, corrigée comme avant (tailles déclarées aujourd'hui, zone de travail,
  jamais sous le minimum).
- Le fichier ne contient plus que `width`, `height` et `maximized` :

  ```ini
  # Window size of org.hackeros.text-editor (ADR 0116, ADR 0122)
  width=900
  height=640
  maximized=false
  ```

- Un fichier écrit avant cet ADR (avec `x=` et `y=`) reste lisible : sa taille
  est reprise, sa position ignorée, et la prochaine écriture le réécrit sans
  ces clés.
- Un déplacement seul ne déclenche plus d'écriture ; seuls un changement de
  taille, l'agrandissement, la restauration et la fermeture en déclenchent.
- Une deuxième fenêtre de la même app prend la place de cascade suivante, comme
  toute nouvelle fenêtre, avec la même taille lue.

## Conséquences

- `SavedWindowGeometry` porte `Width`, `Height` et `IsMaximized` (plus de
  `Bounds`) ; `WindowLaunchCoordinator.RestoredBounds` prend la taille lue et
  l'indice de cascade ; `MinimumVisibleTitleBarWidth` disparaît. Ces types sont
  dans `HackerOs.Platform.Blazor`, hors de la promesse du SDK (ADR 0114 §2), et
  n'étaient pas encore publiés (`PublicAPI.Unshipped.txt`).
- `WindowOptions.RememberGeometry` (SDK 1.1) ne change pas : à `false`, la
  fenêtre s'ouvre toujours à sa taille déclarée.
- Tests unitaires et E2E adaptés : la fenêtre rouvre avec la même taille, à sa
  place de cascade.

## Options écartées

- **Taille, position et état agrandi** (ADR 0116 §1) : ce que font Windows et
  macOS, écarté par Benoit au profit du modèle GNOME.
- **Retenir aussi la position sans la relire** : un fichier qui garde une
  donnée jamais utilisée prête à confusion.
