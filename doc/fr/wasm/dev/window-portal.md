> [English version](../../../en/wasm/dev/window-portal.md)

# Portail des fenêtres entre les écrans

Chantier `multi-monitor/2-window-portal` du
[plan multi-moniteur](plans/multi-monitor-plan.md), mise en œuvre de
l'[ADR 0178 §3](adr/0178-extended-screens-in-display-surfaces.md). Il s'appuie sur les
écrans du gestionnaire de fenêtres ([`window-runtime-displays.md`](window-runtime-displays.md)).
Sans écran secondaire, rien ne change à l'écran : il n'y a qu'un `DesktopArea`, celui de
l'écran 1.

## 1. Principe

Chaque fenêtre est rendue **une seule fois**, dans le `DesktopArea` de l'écran 1. Quand
elle est sur un autre écran, son élément est déplacé dans la couche de fenêtres
(`.window-layer`) du `DesktopArea` de cet écran. Changer d'écran déplace l'élément ; le
composant `WindowHost`, l'app et leur état ne sont jamais recréés. Le moteur de rendu
continue de mettre la fenêtre à jour là où elle est, comme pour le contenu d'une surface
d'affichage ([`detached-display-windows.md`](detached-display-windows.md)).

## 2. Éléments

| Élément | Rôle |
| --- | --- |
| `WindowLayers` (`HackerOs.Windowing.Blazor`, public) | Couches de fenêtres de chaque écran. Chaque `DesktopArea` qui reçoit la même instance y inscrit sa couche après son premier rendu et la retire quand il disparaît. `TryGetLayer(écran)`, `Displays`, événement `Changed`. |
| `DesktopArea.DisplayId` | Écran que montre la zone (écran 1 par défaut). Ses changements de taille vont à la zone de travail de cet écran (`ChangeViewportCommand(zone, écran)`). L'écran doit être ajouté au gestionnaire (`AddDisplayCommand`) avant le rendu de sa zone. |
| `DesktopArea.Layers` | Les couches partagées. La zone de l'écran 1 rend alors toutes les fenêtres ; les autres zones n'en rendent aucune et reçoivent les leurs par le portail. Sans `Layers`, une zone rend seulement les fenêtres de son écran (sans portail : changer d'écran recrée la fenêtre). |
| `WindowPortal` (public, utilisé par `DesktopArea`) | Entoure chaque `WindowHost`. Il laisse une ancre vide dans la liste des fenêtres et déplace l'élément de la fenêtre dans la couche cible, ou le remet sous l'ancre. |
| `WindowPortal.js` | Garde chaque élément par une clé, pas par `ElementReference` : Blazor retrouve un élément référencé par une recherche dans le document, qui ne trouve plus un élément resté dans une couche qui vient d'être retirée. |

`DesktopShell` crée un `WindowLayers` et le passe à son `DesktopArea` et à la zone de chaque
écran secondaire, rendue dans sa surface (chantier 4, [`secondary-screen-desktop.md`](secondary-screen-desktop.md)).

## 3. Règles

- **L'ancre ne bouge jamais.** La liste des fenêtres est une liste à clés (`@key`) ;
  Blazor insère un nouvel élément à côté de ses frères réels. Les frères sont les ancres,
  qui restent dans la couche de l'écran 1 ; seul l'élément intérieur part.
- **Boîtes neutres.** L'ancre et l'enveloppe sont en `display: contents` : la fenêtre se
  place dans la couche comme avant (position, ordre Z, `pass-through` de l'ADR 0160).
- **Écran sans couche.** Une fenêtre dont l'écran n'a pas de couche (écran pas encore
  affiché, ou zone retirée) reste dans la couche de l'écran 1. Quand la zone d'un écran
  disparaît avec des fenêtres dedans, elles reviennent sous leur ancre, sans être
  recréées ; elles y retournent si la zone revient. Le gestionnaire, lui, les ramène sur
  l'écran 1 par `RemoveDisplayCommand` (chantier 3).
- **Focus.** Si le focus clavier était dans la fenêtre, il est rendu au même contrôle
  après le déplacement. `WindowHost.razor.js` lit le focus dans le document de la
  fenêtre (`ownerDocument`), pour les écrans dans une surface.
- **Iframes.** Une iframe déplacée se recharge (ADR 0078 §4, ADR 0178 §3) : le navigateur
  de l'OS réécrit sa page, les autres apps à iframe perdent l'état JS de l'iframe.

## 4. Tests

- `Tests/HackerOs.E2E.Tests/IndexedDbBrowserContractTests.ScreenPortal.cs`, scénario
  `?scenario=screen-portal` de `HackerOs.BrowserHarness.Tests` (deux écrans côte à côte
  dans une page) : la fenêtre passe dans la couche de l'écran 2 sans être recréée (même
  numéro d'instance), garde son compteur, le texte saisi et le focus, reçoit encore ses
  clics, revient sur l'écran 1, puis revient sous son ancre quand la zone de l'écran 2
  disparaît et y retourne quand elle revient.
