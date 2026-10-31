> [English version](../../../../en/wasm/dev/adr/0199-taskbar-window-previews.md)

# ADR 0199 : aperçus des fenêtres au survol de la barre des tâches, et « peek »

## Statut

Accepté le 2026-10-30, chantier 5 de la série barre des tâches (issue
[#242](https://github.com/Kazo-ca/HackerOs/issues/242), points 10 et 11 des décisions prises
avec Benoit le 2026-10-29 : choix B, « aperçu + peek comme sous Windows »). Fait dans la série
`todo/` (branche `todo/13-taskbar-previews`). S'appuie sur les barres par écran
([ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md)) et les menus de la barre
([ADR 0194](0194-taskbar-context-menus.md)). Mise en œuvre :
[`taskbar-window-previews.md`](../taskbar-window-previews.md).

## Contexte

- La barre des tâches (`HackerOs.Taskbar.Blazor`) est un paquet neutre : elle reçoit ses
  sources et ses commandes par des interfaces. Survoler le bouton d'une fenêtre ne montre rien.
- Chaque fenêtre est un élément `article.window-host` avec `data-window-id` (paquet
  `HackerOs.Windowing.Blazor`). Une fenêtre minimisée garde son élément, avec l'attribut
  `hidden`. Les fenêtres d'un écran secondaire sont déplacées par portail dans le document de
  cet écran, où se trouve aussi sa barre (ADR 0178, 0179).
- Décisions de Benoit : l'aperçu est une copie réduite **en direct** de la fenêtre (HTML cloné,
  CSS `scale`), avec une image de repli (icône + titre) quand on ne peut pas copier ; il
  apparaît après ~400 ms ; un clic active la fenêtre, un « × » la ferme ; pas d'aperçu sur
  mobile ; survoler l'aperçu rend les autres fenêtres transparentes (« peek »).

## Décision

1. **Le paquet de la barre reste neutre.** Il reçoit un nouveau paramètre `Previews`
   (`ITaskbarWindowPreviews`) avec deux membres : `LiveCopySelector(id)`, le sélecteur CSS de
   l'élément à copier dans le document de la barre (ou `null` pour l'icône et le titre), et
   `Peek(id?)`, qui commence ou termine le peek. Sans ce paramètre, rien ne change. La barre
   possède le minutage, la carte d'aperçu et la copie en direct. Ajout à l'API publique du
   paquet (`PublicAPI.Unshipped.txt`), sans rupture.
2. **Minutage** (C#, `TaskbarPreviewController`, testé sans navigateur) :
   - l'aperçu s'ouvre après **400 ms** de survol d'un bouton par une souris ou un stylet ; un
     pointeur tactile n'ouvre jamais d'aperçu (pas de survol), donc rien sur mobile ;
   - quand un aperçu est ouvert, survoler un autre bouton y passe **tout de suite**, comme sous
     Windows ;
   - quitter le bouton ou l'aperçu le ferme après **300 ms**, le temps de passer de l'un à
     l'autre ;
   - un clic sur un bouton ferme l'aperçu (puis fait son action habituelle) ;
   - l'aperçu se ferme quand sa fenêtre quitte la barre (fermée ou envoyée sur un autre écran).
3. **La carte d'aperçu**, au-dessus du bouton, dans la barre : l'icône, le titre, un bouton
   « × » (`Fermer {titre}`) qui ferme la fenêtre comme sa barre de titre, et la vignette. Un
   clic sur la vignette montre la fenêtre : la restaure si elle est minimisée, puis l'active.
   Contrairement au bouton, il ne la minimise jamais. Un clic droit sur la carte ouvre le menu
   du bouton de la fenêtre (ADR 0194 : mêmes attributs).
4. **La copie en direct.** Un module JavaScript du paquet (`Taskbar.razor.js`), limité à ce que
   C# ne peut pas faire : placer la carte au-dessus du bouton et copier la fenêtre.
   - Il clone l'élément de la fenêtre, le réduit avec `transform: scale` dans la vignette, et
     recommence toutes les **500 ms** tant que l'aperçu est ouvert.
   - La copie ne duplique aucun identifiant et n'est pas interactive : `id`, `name`,
     `tabindex`, `for`, `aria-labelledby` et les autres références par identifiant sont
     retirés ; la copie est dans une couche `inert` et `aria-hidden`. Sans `name`, un bouton
     radio copié ne décoche pas l'original.
   - Les canevas sont copiés pixel par pixel (`drawImage`) : un clone de canevas est vide.
     Une fenêtre qui contient un `iframe`, une `video`, un `embed` ou un `object` garde
     l'icône et le titre : leur contenu ne se clone pas. *(Précision de la décision 10 :
     Benoit avait prévu le repli aussi pour les canevas ; la copie des pixels les montre.)*
   - Une fenêtre minimisée est copiée à sa taille normale.
5. **Le peek.** Après **300 ms** sur la carte, la barre appelle `Peek(id)` ; quitter la carte
   (ou la fermer) appelle `Peek(null)`. Le bureau passe la fenêtre à `DesktopArea`
   (`PeekWindowId`), qui donne à chaque `WindowHost` un `WindowPeekState` :
   - `Shown` pour la fenêtre et ses propres boîtes de dialogue : elle s'affiche au-dessus des
     autres, même minimisée ;
   - `SeeThrough` pour les autres fenêtres **du même écran** : seul leur contour reste, comme
     du verre, et elles ne prennent plus le pointeur ;
   - `None` pour les fenêtres des autres écrans.
   Le peek ne change ni l'ordre ni l'état des fenêtres : rien n'est enregistré.
6. **Multi-écran.** Le bureau donne la même instance (`TaskbarWindowPreviews`) à la barre de
   chaque écran. Le module cherche la fenêtre dans le document de la barre, qui est aussi
   celui des fenêtres de son écran.
7. **Thèmes et animations.** La carte utilise les jetons `--hos-*` existants et en ajoute :
   `--hos-taskbar-preview-width`, `-height`, `-background`, `-border`, `-radius`, `-shadow`,
   `-duration`, `--hos-taskbar-preview-close-hover-*`, `--hos-window-peek-outline`, `--hos-window-peek-glass` et `--hos-window-peek-z`. Son
   apparition (fondu de 140 ms) est coupée par le réglage « Réduire les animations » et par
   `prefers-reduced-motion`.
8. **Textes** : « Aperçu de {0} » et « Fermer {0} » dans les ressources du paquet de la barre
   (anglais et français).

## Conséquences

- Survoler un bouton de la barre montre la fenêtre sans l'activer ; le peek permet de la voir
  à sa place.
- Le paquet de la barre gagne `ITaskbarWindowPreviews`, `Taskbar.Previews` et
  `Taskbar.DisposeAsync` ; le paquet des fenêtres gagne `WindowPeekState`,
  `DesktopArea.PeekWindowId` et `WindowHost.PeekState`. Ils seront dans la prochaine version
  mineure de ces paquets. Aucun changement du SDK des apps.
- La copie suit les styles isolés (`b-…`) de l'app, puisqu'elle garde leurs attributs ; un
  style qui dépend d'un ancêtre hors de la fenêtre peut différer légèrement dans la vignette.
- Le clavier n'a pas d'aperçu (comme sous Windows) : le menu de fenêtre et le sélecteur de fenêtres (Alt+`, ADR 0095) restent
  les chemins au clavier.

## Alternatives écartées

- **Une capture d'image** (`html2canvas` ou équivalent) : une bibliothèque lourde, lente, et
  non « en direct ».
- **Le peek en JavaScript** (styles posés sur les fenêtres depuis la barre) : la barre aurait
  dû connaître la mise en page des fenêtres, et Blazor aurait pu écraser ces styles. Le peek
  passe par C# et le CSS isolé de `WindowHost`.
- **Des rappels (`EventCallback`) de la barre vers le bureau pour le minutage** : le minutage
  est le même pour tout hôte, il reste dans le paquet.
