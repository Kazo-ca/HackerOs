> [English version](../../../en/wasm/dev/detached-display-windows.md)

# Fenêtres détachées

Le contrat des surfaces pour les apps (`IDisplaySurfaceService`, `DisplaySurfaceContent`, événements, `HackBrowser.OpenDetachedAsync`) est dans [`display-surface-api.md`](../framework/display-surface-api.md).

**Phase web, chantier `web/10-detached-window`** ([`asp-hack-plan.md`](plans/asp-hack-plan.md),
section 5). Décision : [ADR 0078](adr/0078-detached-display-windows.md),
précisée par l'[ADR 0081](adr/0081-detached-display-windows-adjustments.md).

## Objet

Une page web peut quitter le bureau de HackerOS et s'ouvrir dans une **fenêtre
du navigateur hôte** : l'utilisateur garde l'OS dans un onglet et la page dans
une autre fenêtre, qu'il peut placer sur un autre moniteur. Ce n'est pas un
second HackerOS : la fenêtre ne démarre ni .NET ni OS. Elle est une **surface
d'affichage** de l'instance principale, comme un deuxième écran branché sur la
même machine : l'instance principale y dessine, en reçoit les clics et les
touches, et continue de servir la page (réseau, cookies, `localStorage`).

Le mécanisme est général : n'importe quel contenu Blazor d'une fenêtre d'app
peut être affiché dans une surface. Il prépare le **deuxième écran** que Benoit
veut plus tard : un bureau dessiné dans une surface.

## Architecture

```
Fenêtre de l'OS (onglet principal)                    Fenêtre détachée (window.open, même origine)
──────────────────────────────────                    ─────────────────────────────────────────────
BrowserWindow                                          <body class="hackeros-surface">
 ├─ placeholder « page ouverte dans une fenêtre »       └─ .hackeros-surface-root
 └─ <DisplaySurfaceContent Surface=…> ── déplace ──►       └─ .surface-content (rendu par l'instance
      barre de la fenêtre + zone de page                       principale : barre, page, iframe isolée)
           │                                                       │ clics, touches (écouteurs relayés)
IDisplaySurfaceService (SDK Blazor)                                │ messages du pont de la page
 └─ DisplaySurfaceService (Platform.Blazor) ◄── displaySurfaces.js ◄┘ (remis à la fenêtre principale)
         │                                       │  styles recopiés, taille, focus, fermeture
         └─ notification si la fenêtre est bloquée └─ displaySurfaceRelay.js (chargé dans la surface)
HackerOs.Platform.Blazor.lib.module.js (initialiseur JS, avant Blazor) :
  écouteurs de `document` reposés sur chaque surface ; `[_bl_…]` cherché aussi dans les surfaces
```

| Fichier | Rôle |
|---|---|
| `Platform/HackerOs.Platform.Blazor/Display/DisplaySurfaceService.cs` | Implémentation : ouvre, suit et ferme les surfaces ; publie la notification « fenêtre bloquée ». |
| `Platform/HackerOs.Platform.Blazor/wwwroot/displaySurfaces.js` | `window.open`, document de la surface (`<base>`, styles recopiés et tenus à jour, apparence héritée de l'ancre), déplacement du contenu, événements `resize`/`focus`/`blur`/fermeture, messages remis à la fenêtre principale. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/displaySurfaceRelay.js` | Chargé dans la surface : poster un message « depuis » la surface. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/displaySurface.css` | Style de base du document d'une surface. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/HackerOs.Platform.Blazor.lib.module.js` | Initialiseur JS (`beforeStart`, `beforeWebStart`) : relais des écouteurs et des `@ref`. |
| `Apps/System/HackerOs.Apps.Browser/BrowserWindow.razor(.cs,.css)` | Bouton `⧉`, placeholder, barre de la fenêtre détachée, rattachement, argument `--detached`. |
| `Apps/System/HackerOs.Apps.Browser/BrowserPageView.razor.js` | Réponses du pont envoyées par le relais quand la page est dans une surface. |

## Fonctionnement

### Ouvrir une surface

- **Ouverte** : le module construit le document (titre, `<base>` sur l'adresse
  de l'OS, feuilles de style du bureau recopiées et mises à jour quand elles
  changent, attributs de `<html>`), commence à relayer les écouteurs et attend
  le relais de messages. Résultat `Opened` avec la surface (`surface-1`,
  `surface-2`…).

### Dessiner dans une surface

Ce qui fait marcher Blazor dans un autre document (ADR 0081 §2) :

- les éléments sont créés par le document principal puis adoptés par celui de
  la surface ;
- l'initialiseur JS pose sur chaque surface les écouteurs que Blazor (et
  MudBlazor) posent sur `document` ;
- un `@ref` dont l'élément est dans une surface est retrouvé par le sélecteur
  `[_bl_<id>]`, cherché aussi dans les surfaces.

### Le navigateur

- Le bouton `⧉` (`#browser-btn-detach`) ouvre une surface et y déplace la zone
  de page : une barre (icône du schéma, titre, adresse, `↻`, `⇲ Reattach`) et
  la page. La fenêtre du navigateur de l'OS garde sa barre d'adresse, ses
  outils de développement et sa barre d'état ; la zone de page y montre « This
  page is open in a detached window » avec **Show the window** et **Bring the
  page back here**.
- Le titre de la fenêtre détachée suit le titre de la page.
- Naviguer depuis l'OS (barre d'adresse, favoris, précédent) ou depuis la page
  (liens, formulaires) change la page dans la fenêtre détachée.
- **Reattach** (dans la fenêtre détachée), `⧉` à nouveau ou **Bring the page
  back here** rattachent l'onglet ; fermer la fenêtre détachée aussi.
- Le pont de la page isolée (ADR 0075) suit l'iframe : la surface remet ses
  messages à la fenêtre principale, et les réponses partent par le relais de
  la surface, seul expéditeur que le pont accepte.
- Une iframe qui change de document repart de zéro : au détachement comme au
  rattachement, la page est réécrite à partir du document déjà reçu, sans
  nouvelle requête, avec ses cookies et son `localStorage` actuels.

## Décisions clés

- **Une instance, plusieurs surfaces** (ADR 0078 §1) : aucune seconde instance
  .NET, aucun second circuit ; marche avec les trois hôtes.
- **Contrat dans le SDK Blazor** (ADR 0081 §1) : les apps de base n'atteignent
  l'OS que par le SDK.
- **Portail plutôt que second moteur de rendu** (ADR 0081 §2) : le contenu est
  rendu par le moteur principal et déplacé ; l'initialiseur JS comble les deux
  hypothèses « un seul document » de Blazor.
- **Page réécrite au déplacement** (ADR 0081 §4) : plutôt que de garder une
  iframe impossible à déplacer vivante.
- **Pas de capacité** : une surface montre ce que l'app montre déjà.

## Limites connues

- Un onglet ne peut avoir qu'une fenêtre détachée ; le navigateur de l'OS n'a
  qu'un onglet par fenêtre.
- Sur mobile, le navigateur hôte ouvre la fenêtre comme un onglet.

## Tests

- `Tests/HackerOs.Platform.Blazor.Tests/Display/DisplaySurfaceServiceTests.cs` :
  ouverture, fenêtre bloquée et notification, hôte sans JavaScript, événements
  et fermeture par l'utilisateur, fermeture par l'OS, titre, montage et focus.
- `Tests/HackerOs.AppSdk.Tests/Apps/HackBrowserTests.cs` : arguments du
  lancement, adresse vide, navigateur absent.
- `Tests/HackerOs.UI.E2E.Tests/BrowserDetachedWindowTests.cs` (Chromium réel,
  hôte `test/test`) : détacher une page, vérifier son script, son `fetch`, la
  recopie des styles ; naviguer depuis l'OS et poster un formulaire dans la
  fenêtre détachée ; cliquer **Reattach** dans la fenêtre détachée (preuve de
  l'ADR 0078 §4) ; fermer la fenêtre à la main et retrouver la page.

## Liste des tâches

- [x] Contrat `IDisplaySurfaceService` / `IDisplaySurface` dans le SDK Blazor.
- [x] Portail `DisplaySurfaceContent`.
- [x] `DisplaySurfaceService` et module `displaySurfaces.js` (document,
  styles, événements, fermeture, onglet de l'OS fermé).
- [x] Initialiseur JS : écouteurs de `document` et `@ref` dans les surfaces.
- [x] Relais des messages du pont des pages isolées.
- [x] Navigateur : `⧉`, placeholder, barre de la fenêtre détachée, rattacher,
  réécriture de la page, titre suivi, `--detached`.
- [x] `HackBrowser.OpenAsync` et `OpenDetachedAsync`.
- [x] Notification et bandeau quand la fenêtre est bloquée.
- [x] Tests unitaires et E2E.
- [x] ADR 0081, ce document, index, plan, références du SDK.
