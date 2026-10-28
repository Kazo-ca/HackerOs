> [English version](../../../en/wasm/dev/secondary-screen-desktop.md)

# Bureau et overlays des écrans secondaires

Chantier `multi-monitor/4-secondary-desktop` du [plan multi-moniteur](plans/multi-monitor-plan.md),
mise en œuvre de l'[ADR 0178 §1](adr/0178-extended-screens-in-display-surfaces.md) (bureau de
chaque écran) et de l'[ADR 0179 §2](adr/0179-per-screen-taskbars-and-other-screen-menus.md)
(overlays sur l'écran où l'utilisateur agit, dialogues sécurisés sur l'écran 1). Il s'appuie sur
le portail des fenêtres ([`window-portal.md`](window-portal.md)) et sur le service d'écrans
([`screen-service.md`](screen-service.md)). Sans écran secondaire, rien ne change.

## 1. Le bureau d'un écran secondaire

`DesktopShell` donne à `ScreenLayer.ScreenContent` un `DesktopArea` par écran :

| Paramètre | Valeur |
| --- | --- |
| `Windows` | Toutes les fenêtres de `WindowRuntime`. |
| `Layers` | Le même `WindowLayers` que la zone de l'écran 1 : les fenêtres de l'écran y arrivent par le portail. |
| `DisplayId` | Le numéro de l'écran. |
| `OnGesture` | Le gestionnaire de `DesktopShell` : la zone envoie la taille de l'écran (`ChangeViewportCommand(zone, écran)`). |

- **Fond d'écran seulement** (réponse de Benoit du 2026-10-28) : pas de `BackgroundContent`, donc
  pas d'icônes. Le fond vient des variables du thème (`--hos-desktop-background`, grille), que la
  surface hérite de l'onglet principal.
- Les fenêtres restent rendues par la zone de l'écran 1, avec ses rappels (focus, fermeture,
  menu de fenêtre) : la zone d'un écran secondaire n'en rend aucune.
- Le numéro de l'écran sur la couleur du bureau (chantier 3) n'apparaît plus que si
  `ScreenContent` est absent.

## 2. Zone de travail

La zone de travail d'un écran secondaire est la taille de **son bureau**, plus celle de sa
fenêtre : la barre des tâches de l'écran (chantier 5, [`per-screen-taskbars.md`](per-screen-taskbars.md)) reste en dehors.

- `ScreenService` envoie seulement `AddDisplayCommand` avec la taille de la fenêtre, à
  l'ouverture. Il ne change plus la zone quand la fenêtre change de taille.
- `DesktopArea.razor.js` mesure la zone avec un `ResizeObserver` **de la fenêtre qui la
  contient** : un observateur ne suit que la mise en page de sa propre fenêtre, et la zone passe
  dans la fenêtre de l'écran juste après son premier rendu. Le module refait son observateur quand
  la zone change de document (vérifié toutes les 200 ms pendant 10 s après le premier rendu).
- Une taille envoyée par la zone d'un écran déjà retiré est ignorée par `DesktopShell`.

## 3. Overlays sur l'écran où l'utilisateur agit

Le menu de fenêtre, le sélecteur de fenêtres et le bandeau de déplacement au clavier
(`WindowManagerOverlays`) et le menu contextuel de l'OS (`ContextMenuOverlay`) sont rendus une
seule fois, dans `ScreenOverlayHost` :

```
DesktopShell
 └─ ScreenOverlayHost (.screen-overlays, display: contents)
     ├─ WindowManagerOverlays
     └─ ContextMenuOverlay
ScreenLayer ─ par écran ─► .secondary-screen
                            ├─ DesktopArea (DisplayId = écran)
                            └─ [data-screen-overlay-slot] ◄── screenOverlays.js y déplace .screen-overlays
```

| Fichier | Rôle |
| --- | --- |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenOverlayHost.razor` | Conteneur des overlays ; branche `screenOverlays.js` au premier rendu. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/screenOverlays.js` | Écoute `pointerdown`, `contextmenu` et `keydown` en capture sur la fenêtre de chaque écran, et déplace le conteneur dans l'emplacement de cet écran **avant** que les overlays réagissent. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/HackerOs.Platform.Blazor.lib.module.js` | Le registre des surfaces a `watch(…)` : un module apprend l'ouverture (`attach`) et la fermeture (`detach`) de chaque surface. |

Règles :

- **Écran 1** : l'emplacement est la place d'origine du conteneur, dans `DesktopShell`.
- **Autres surfaces** (page détachée par le navigateur de l'OS, ADR 0078) : pas d'emplacement, les
  overlays ne bougent pas, et la page garde le menu du navigateur.
- **Écran fermé** pendant qu'il a les overlays (`pagehide`, ou surface retirée) : le conteneur
  revient sur l'écran 1, même s'il est déjà revenu dans l'onglet avec le contenu de l'écran.
- **Raccourcis clavier** : `WindowManagerOverlays.razor.js` écoute `keydown` et `keyup` sur
  `document` (plus `window`). Le registre des surfaces reproduit ces écouteurs sur chaque écran :
  `Alt+``, `Alt+Space`, etc. marchent dans la fenêtre d'un écran secondaire.
- **Placement** : le menu de fenêtre et le menu contextuel se placent dans la fenêtre de l'écran
  qui les montre (`ownerDocument.defaultView`), et le menu contextuel lit le focus de ce document.
- Le menu contextuel de l'OS s'ouvre aussi dans un écran secondaire (document qui a un
  `[data-screen-overlay-slot]`).

## 4. Dialogues sécurisés

Les dialogues sécurisés (approbation des capacités dangereuses, ADR 0049 ; authentification
d'administrateur) restent sur l'écran 1. Tant que l'un d'eux attend
(`DangerousCapabilityApprovalBroker.Current` ou `AdministratorAuthenticationBroker.Current`),
chaque écran secondaire affiche un voile : « Une demande de sécurité attend sur l'écran 1… » et
un bouton **Aller à l'écran 1**, qui demande au navigateur de passer à l'onglet principal (le
navigateur peut refuser ; le message dit alors où aller). Le voile couvre tout l'écran et prend
les clics.

## 5. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenLayerTests.cs` : bureau et emplacement de
  l'écran 2, voile pour une approbation en attente et pour une authentification en attente.
- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenServiceTests.cs` : la fenêtre redimensionnée
  ne change plus la zone de travail de l'écran.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs`, Chromium réel : l'écran 2 restauré montre son
  bureau (sans icônes, à la taille de la fenêtre) ; `Alt+`` pressé dans l'écran 2 ouvre le
  sélecteur dans l'écran 2 et pas dans l'onglet principal ; de retour sur l'écran 1, il s'ouvre
  dans l'onglet ; l'écran fermé rend les overlays à l'écran 1.

## 6. Limites

- Le plein écran par le raccourci (`ToggleFullScreen`) porte toujours sur l'onglet principal ;
  un écran secondaire a son bouton `⤢`.
- Les jetons de menu des éléments retirés dans un écran secondaire ne sont libérés qu'à la
  fermeture de la session (l'observateur des retraits suit le document principal).
- Une fenêtre ne change pas encore d'écran depuis l'interface : menus et raccourcis au chantier 6,
  glisser au chantier 8.
