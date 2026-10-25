# ADR 0163 : aperçu du déplacement et coordonnées invariantes

## Statut

Accepté le 2026-10-22. Amende la restriction de l'[ADR 0009](0009-window-runtime-strategy.md)
sur la projection DOM pendant un geste de déplacement ; l'état de fenêtre reste en C#.

## Contexte

Le chrome mettait un appel interop et un rendu du bureau en file pour chaque
`pointermove`. Le pointeur pouvait ainsi prendre beaucoup d'avance sur la fenêtre.
Avec une culture française, les attributs `data-window-*` rendaient les nombres
décimaux avec une virgule. Au geste suivant, `Number("48,25")` produisait `NaN`,
sérialisé en `null` ; le paramètre C# `double` ne pouvait plus être désérialisé.

## Décision

- Les quatre attributs numériques du cadre sont rendus en culture invariante.
  Le module refuse les valeurs non finies avant l'interop ; C# vérifie aussi ses entrées.
- Le chrome donne le focus au début du geste. Pendant un **déplacement**, le
  navigateur projette uniquement une translation CSS temporaire, au plus une fois
  par `requestAnimationFrame`. Il ne modifie ni les attributs de géométrie, ni le
  runtime, ni les fichiers d'état. Au relâchement, il transmet un seul delta cumulé
  à C#, qui applique `MoveWindowCommand` et les contraintes de la zone de travail.
  L'aperçu peut dépasser cette zone ; la position validée y est ramenée au relâchement.
- Cette projection est une exception ciblée à la règle de l'ADR 0009 interdisant
  toute mutation DOM par le module de pointeur. Identité, dimensions, contraintes,
  focus, ordre et position validée restent la propriété du runtime C#.
- Le **redimensionnement** conserve ses contraintes C# en direct. Le module garde
  au plus un échantillon en cours et le plus récent en attente, synchronisés avec
  les images du navigateur. La dernière position précède la notification unique
  de fin de redimensionnement (ADR 0095).
- `pointerup`, `pointercancel` et la perte de capture terminent une seule fois le
  geste. Annulation et perte de capture gardent le dernier delta valide. Le retrait
  du composant libère les écouteurs, la capture et l'aperçu sans rappel vers C#.
- Les positions ne sont toujours pas enregistrées (ADR 0122). Aucun serveur requis.

## Vérification

`WindowPointerGeometryTests` couvre les décimales sous `fr-FR`, `fr-CA`, `en-US`
et les valeurs non finies. `WindowPointerGestureTests` exerce le vrai module
dans Chromium : déplacements successifs, validation au relâchement, capture
perdue, retrait pendant un geste et interop lente pendant un redimensionnement.
Le scénario OS `WindowGeometryRestoreTests` refait deux déplacements successifs
avant de vérifier la taille restaurée.

## Tâches

- [x] Corriger la frontière numérique et retirer le style dynamique du markup Razor.
- [x] Projeter le déplacement localement et valider sa position au relâchement.
- [x] Borner les mises à jour de redimensionnement et libérer les gestes terminés.
- [x] Ajouter les tests de régression et actualiser les documents du fenêtrage.
