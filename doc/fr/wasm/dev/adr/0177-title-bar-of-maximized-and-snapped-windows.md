> [English version](../../../../en/wasm/dev/adr/0177-title-bar-of-maximized-and-snapped-windows.md)

# ADR 0177 : barre de titre des fenêtres agrandies et ancrées

## Statut

Accepté le 2026-10-28. Benoit a signalé le bug et demandé les deux comportements
(« quand maximized ou snap, si on la déplace, elle redevient en état normal » ;
« double-click sur la barre de titre, on toggle le mode normal/maximize »). Il a
choisi « Agrandir, comme Windows » pour le bouton Agrandir d'une fenêtre ancrée.
Remplace la décision « Ancrer ne garde pas de géométrie de restauration » de
[`window-model.md`](../../framework/window-model.md) (ADR 0095) et complète
l'[ADR 0163](0163-window-pointer-preview-and-invariant-coordinates.md) (geste de
déplacement).

## Contexte

- Sur une fenêtre agrandie ou ancrée, Fermer et Agrandir semblaient ne rien faire ;
  seul Réduire répondait, et le clic droit sur l'icône ▸ Fermer marchait.
- Cause : les poignées de redimensionnement (`resize-top`, `resize-top-right`,
  `resize-right`, `z-index: 2`) passent par-dessus les boutons de la barre de titre.
  Une fenêtre agrandie ou ancrée touche le haut de l'écran, donc le pointeur arrive
  souvent sur cette bande. Le clic tombe sur une poignée, qui ne fait rien quand la
  fenêtre est agrandie. Mesuré dans Chromium : de 23 % (thème HackerOS) à 48 %
  (Windows 98) de la surface du bouton Fermer est couverte, 10 à 20 % des autres.
- L'ancrage (`snap-left`, `snap-right`) redimensionnait une fenêtre normale avec
  `ResizeWindowCommand` et ne gardait aucune géométrie : rien ne permettait de
  revenir à la taille d'avant.
- Glisser la barre de titre d'une fenêtre agrandie ne faisait rien, et un double-clic
  sur la barre de titre non plus.

## Décision

1. **Boutons au-dessus des poignées.** `.window-actions` a `position: relative;
   z-index: 3`. Un clic sur un bouton ne commence jamais un redimensionnement.
2. **Pas de poignées sur une fenêtre agrandie.** `WindowChrome` ne les dessine que
   pour une fenêtre `Normal` redimensionnable (une fenêtre ancrée en garde).
3. **L'ancrage garde la géométrie d'avant.** Nouvelle commande
   `SnapWindowCommand(WindowId, WindowBounds)` et propriété
   `WindowRuntimeState.IsSnapped`. La fenêtre reste `Normal` ; `RestoreBounds` garde
   la géométrie d'avant le premier ancrage (ou d'avant l'agrandissement si elle était
   agrandie). Règles de `WindowRuntime` :
   - `RestoreWindowCommand` sur une fenêtre ancrée lui rend cette géométrie ;
   - agrandir une fenêtre ancrée garde cette géométrie : restaurer ensuite défait
     l'ancrage (choix « Agrandir, comme Windows » de Benoit) ;
   - réduire puis restaurer une fenêtre ancrée la remet sur sa moitié d'écran ;
   - `MoveWindowCommand` la sort de l'ancrage en gardant sa taille actuelle
     (déplacement au clavier) ;
   - événements : `WindowResizedEvent` et `WindowResizeCompletedEvent` (plus
     `WindowVisualStateChangedEvent` si elle était agrandie).
   `Super+Down` (`restore-or-minimize`) restaure une fenêtre ancrée au lieu de la
   réduire. `WindowGeometryRecorder` lit `IsSnapped` au lieu de deviner une moitié
   d'écran à ses coordonnées.
4. **Glisser la barre de titre d'une fenêtre agrandie ou ancrée la restaure.** Après
   4 px de trajet (un clic ou un double-clic ne la restaure pas), le script appelle
   `WindowChrome.ReportDragRestoreAsync` : la fenêtre reprend sa géométrie d'avant et
   se place pour que le pointeur garde sa position relative sur la barre de titre,
   puis le geste continue comme un déplacement normal (ADR 0163). `WindowHost`
   expose `data-window-snapped="true"`.
5. **Double-clic sur la barre de titre** (hors boutons) : bascule agrandi ↔ normal,
   par le même chemin que le bouton (`WindowChrome.ReportTitleDoubleClickAsync`).
   Une fenêtre ancrée est agrandie. Rien pour une fenêtre non redimensionnable
   (sauf la restaurer si elle est agrandie).

## Conséquences

- API publique ajoutée (dans `PublicAPI.Unshipped.txt`) :
  `HackerOs.Windowing.Abstractions.SnapWindowCommand`,
  `WindowRuntimeState.IsSnapped`, `WindowChrome.ReportDragRestoreAsync` et
  `WindowChrome.ReportTitleDoubleClickAsync`. Ajouts seulement, rien ne change pour
  les apps.
- Le menu de fenêtre garde « Agrandir » pour une fenêtre ancrée.
- Test E2E `WindowTitleBarTests` (boutons cliqués à leur bord du haut, double-clic,
  glisser depuis agrandi et depuis ancré) et tests de `WindowRuntime`.
