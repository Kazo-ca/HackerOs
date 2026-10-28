> [English version](../../../en/wasm/dev/drag-between-screens.md)

# Glisser une fenêtre d'un écran à l'autre

Chantier `multi-monitor/8-drag-between-screens` du [plan multi-moniteur](plans/multi-monitor-plan.md),
mise en œuvre de l'[ADR 0183](adr/0183-drag-a-window-between-screens.md). Il s'appuie sur le
portail des fenêtres ([`window-portal.md`](window-portal.md)), le service d'écrans
([`screen-service.md`](screen-service.md)) et le bureau des écrans secondaires
([`secondary-screen-desktop.md`](secondary-screen-desktop.md)). Sans écran secondaire, rien ne change.

## 1. Pour l'utilisateur

Saisir la barre de titre d'une fenêtre, l'amener au-dessus d'un autre écran, relâcher : la
fenêtre passe sur cet écran, à l'endroit du pointeur, avec son contenu intact. Pendant le
geste, l'écran visé montre un contour vert de la fenêtre. Relâchée entre deux écrans ou sur
son propre écran, la fenêtre se déplace comme d'habitude.

## 2. Fichiers

| Fichier | Rôle |
| --- | --- |
| `Platform/HackerOs.Windowing.Blazor/wwwroot/windowScreenDrop.js` | Apprend où la page de chaque fenêtre d'écran commence sur le bureau physique, trouve l'écran sous le pointeur, dessine et retire le contour. |
| `Platform/HackerOs.Windowing.Blazor/WindowChrome.razor.js` | Pendant un déplacement : écran sous le pointeur à chaque mouvement, contour, et au relâchement `ReportScreenDropAsync` au lieu du déplacement normal. |
| `Platform/HackerOs.Windowing.Blazor/WindowChrome.razor` | `ReportScreenDropAsync(écran, x, y)` : `MoveWindowToDisplayCommand` puis `MoveWindowCommand`. |
| `Platform/HackerOs.Windowing.Blazor/DesktopArea.razor.css` | Style du contour `.window-drop-preview`. |
| `Platform/HackerOs.Platform.Blazor/Shell/DesktopShell.razor` | Ignore un dépôt sur un écran déjà fermé. |

## 3. Trouver l'écran sous le pointeur

```
pointermove (capturé par la barre de titre, dans la fenêtre du navigateur d'origine)
 ├─ pointeur dans cette fenêtre ? ── oui ─► couche .window-layer[data-display] sous (clientX, clientY)
 └─ non ─► pour l'onglet principal et chaque surface d'écran :
             origine de la page sur le bureau = (screenX - clientX) du dernier événement vu
                                                + déplacement de la fenêtre depuis
             point = (screenX, screenY) - origine ─► couche sous ce point
```

- Les origines sont apprises par des écouteurs `pointermove`, `pointerdown` et `pointerover`
  passifs, en capture, sur chaque fenêtre ; le registre des surfaces (`watch`) annonce les
  fenêtres d'écran. Avant tout événement dans une fenêtre, l'origine est estimée par sa
  position et ses bordures.
- Une couche cachée (taille nulle) n'est jamais une cible.
- Le point de saisie (écart entre le pointeur et le coin de la fenêtre au début du geste) est
  gardé : la fenêtre arrive au même endroit sous le pointeur. Le haut est ramené à 0 au moins
  pour que la barre de titre reste visible.

## 4. Règles

- L'élément de la fenêtre ne change de document qu'au relâchement : le déplacer pendant le geste
  ferait perdre la capture du pointeur.
- Le contour n'apparaît que dans un écran qui est une autre fenêtre du navigateur ; dans la même
  page (harnais), la fenêtre elle-même montre où elle va.
- La position est ensuite bornée par `WindowRuntime` à la zone de travail de l'écran cible, comme
  tout déplacement. Une fenêtre modale suit son propriétaire.
- Seule une fenêtre normale et déplaçable se glisse.

## 5. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/WindowPointerGeometryTests.cs` : `ReportScreenDropAsync` envoie le
  changement d'écran puis la position ; numéro d'écran invalide, valeur non finie ou fenêtre agrandie :
  aucune commande.
- `Tests/HackerOs.E2E.Tests/IndexedDbBrowserContractTests.ScreenDrag.cs`, scénario
  `?scenario=screen-portal` : la fenêtre glissée sur l'écran 2 y passe sans être recréée, au point
  de dépôt (à 2 px près), reste vivante, ne change pas d'écran relâchée entre les écrans, et revient
  sur l'écran 1 de la même façon.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs`, Chromium réel : l'écran 2 restauré, l'Éditeur
  de texte glissé hors de l'onglet principal vers un point de l'écran 2 ; l'écran 2 montre le contour
  pendant le geste, puis la fenêtre. Le test calcule le point à partir des origines des deux pages
  (en headless, les fenêtres se chevauchent : le point choisi est dans l'écran 2 et hors de l'onglet).

## 6. Limites

- Zoom du navigateur différent de 100 % : la conversion entre bureau et page est approximative.
- Fenêtres d'écran qui se chevauchent sur un même moniteur : la fenêtre d'origine gagne, puis
  l'onglet principal, puis les surfaces dans l'ordre d'ouverture.
- Pas de glisser d'une fenêtre agrandie ou ancrée, ni d'ancrage sur un bord de l'écran cible.
