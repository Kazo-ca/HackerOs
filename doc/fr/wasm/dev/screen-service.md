> [English version](../../../en/wasm/dev/screen-service.md)

# Service d'écrans

Chantier `multi-monitor/3-screen-service` du [plan multi-moniteur](plans/multi-monitor-plan.md),
mise en œuvre de l'[ADR 0178 §4](adr/0178-extended-screens-in-display-surfaces.md),
précisée par l'[ADR 0180](adr/0180-screen-service-placement-and-memory.md). Le
service ouvre chaque écran secondaire dans une fenêtre du navigateur hôte, l'enregistre
dans le gestionnaire de fenêtres ([`window-runtime-displays.md`](window-runtime-displays.md))
et s'en souvient d'une session à l'autre. Les menus « Nouvel écran » (chantier 6) et
la page Paramètres ▸ Écrans (chantier 7) s'en serviront ; ce chantier montre seulement
l'offre « Restaurer les écrans ».

## 1. Architecture

```
Onglet principal (écran 1)                          Fenêtre d'un écran secondaire (window.open)
──────────────────────────                          ───────────────────────────────────────────
DesktopShell                                         <body class="hackeros-surface">
 └─ ScreenLayer                                       └─ .secondary-screen[data-display=2]
     ├─ <DisplaySurfaceContent> par écran ─ déplace ►     ├─ bureau de l'écran (chantier 4 ; numéro en attendant)
     └─ carte « Restaurer les écrans »                    ├─ bandeau « onglet principal caché »
                                                          └─ bouton plein écran ⤢
ScreenService ── AddDisplay / ChangeViewport / RemoveDisplay ──► WindowRuntime
 ├─ DisplaySurfaceService.OpenScreenSurfaceAsync ──► displaySurfaces.js open(…, placement)
 │                                                     └─ screens.js prepare / settle
 └─ screens.js : permission, getScreenDetails, screenschange, place lue chaque seconde,
                 plein écran, visibilitychange de l'onglet principal
~/.config/hackeros/screens.config [screens] : open = 2,3 ; 2 = left,top,width,height
```

| Fichier | Rôle |
| --- | --- |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenService.cs` | Le service : numéros, ouverture, restauration, fermeture, plein écran, mémoire, commandes envoyées à `WindowRuntime`. |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenLayer.razor(.css)` | Rendu des écrans par le portail des surfaces, bandeau, bouton plein écran, carte de restauration. |
| `Platform/HackerOs.Platform.Blazor/Display/DisplaySurfaceService.cs` | `OpenScreenSurfaceAsync` (interne) : ouverture avec placement, notification de blocage facultative. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/screens.js` | API Window Management, choix du moniteur libre, déplacement après la permission, place, plein écran, onglet caché. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/displaySurfaces.js` | `open` reçoit un placement facultatif et appelle `screens.js` avant et après `window.open`. |
| `Platform/HackerOs.Platform.Core/Shell/ScreensSettings.cs` | `ScreenGeometry` et `ScreensSettingsDocuments` : la section `[screens]` de `screens.config`. |
| `OS/HackerOs.Ecosystem.Host/EcosystemServiceCollectionExtensions.cs` | `DisplaySurfaceService` (aussi sous `IDisplaySurfaceService`) et `ScreenService`, par session. |

## 2. API du service

| Membre | Rôle |
| --- | --- |
| `StartAsync()` | Lit `screens.config` (écrans à restaurer, places) et suit la permission et l'onglet principal. Appelé par `ScreenLayer` au premier rendu. |
| `OpenScreenAsync()` | Ouvre un écran avec le plus petit numéro libre, pendant un geste. Retire l'offre de restauration. |
| `RestoreScreensAsync()` | Rouvre les écrans de la session précédente, dans l'ordre, pendant un geste ; ceux que le navigateur bloque restent proposés. |
| `DismissRestoreAsync()` | Retire l'offre et l'écrit. |
| `CloseScreenAsync(écran)` | Ferme un écran ; il n'est plus restauré. |
| `ToggleFullScreenAsync(écran)` | Plein écran de l'écran, depuis un clic fait dans cet écran. |
| `Screens`, `RestorableScreens`, `Permission`, `IsPrimaryHidden`, `CanOpenScreen`, `Changed` | État lu par l'interface. |
| `PrimaryTaskbar`, `SetPrimaryTaskbarAsync(mode)` | Réglage `primary-taskbar` de `[taskbar]`, suivi pendant la session (chantier 5, [`per-screen-taskbars.md`](per-screen-taskbars.md)). |

`ScreenOpenStatus` : `Opened`, `Blocked` (fenêtre refusée), `Unavailable` (hôte sans
JavaScript), `LimitReached` (numéros 2 à 9 pris). `ScreenPermission` : `Unknown`,
`Unsupported`, `Prompt`, `Granted`, `Denied`.

## 3. Ouvrir un écran

1. Le numéro est le plus petit libre (`WindowRuntime.NextDisplayId`), ou celui de
   l'écran restauré s'il est libre.
2. `screens.js` choisit la place avant `window.open` (ADR 0180 §2) : moniteur libre si
   la permission est accordée ; sinon la place retenue (ou 1280 × 800), et la demande
   de permission part dans le même geste. Si l'utilisateur accepte, la fenêtre est
   déplacée sur un moniteur libre et `OnScreenPlaced` donne le nom du moniteur.
3. La surface ouverte, le service envoie `AddDisplayCommand(écran, 0, 0, largeur,
   hauteur)`, puis `ChangeViewportCommand` à chaque changement de taille.
4. `open` de `screens.config` est réécrit.

## 4. Fermer un écran

| Cause | Fenêtres | `open` |
| --- | --- | --- |
| L'utilisateur ferme la fenêtre, ou elle part ailleurs (`Lost`) | Reviennent sur l'écran 1 (`RemoveDisplayCommand`) | L'écran en sort |
| Moniteur débranché (`screenschange`) | Idem | L'écran en sort |
| `CloseScreenAsync` | Idem | L'écran en sort |
| Rechargement, onglet fermé, fermeture de session | Idem (la session finit) | Inchangé : proposé à la session suivante |

La place de l'écran reste dans sa ligne numérotée.

## 5. Dans la fenêtre d'un écran

- **Bureau** : `ScreenLayer.ScreenContent` reçoit le bureau de l'écran (chantier 4,
  [`secondary-screen-desktop.md`](secondary-screen-desktop.md)) ; sans lui, le numéro de l'écran
  sur la couleur du bureau.
- **Plein écran** : bouton `⤢` en haut à droite, `⤡` pour en sortir (ou `Échap`).
- **Bandeau** : « L'onglet principal de HackerOS est caché… » tant que l'onglet
  principal est caché (`visibilitychange`).
- **Zone de travail** : la taille intérieure de la fenêtre à l'ouverture, puis celle du
  bureau de l'écran, qu'il mesure lui-même (chantier 4) ; le service ne la change plus quand
  la fenêtre change de taille.

## 6. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenServiceTests.cs` : modules JS
  scriptés, `WindowRuntime` réel, réglages en mémoire.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ScreensSettingsTests.cs` : lecture et
  écriture de `[screens]` sur le disque virtuel.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs` : Chromium réel ; le fichier est
  écrit au terminal, l'OS est rechargé, **Restore screens** ouvre l'écran 2 à
  900 × 700, puis l'écran fermé à la main n'est plus proposé.
- `Tests/HackerOs.UI.E2E.Tests/BrowserDetachedWindowTests.cs` reste vert : sans
  placement, `displaySurfaces.js` ouvre comme avant.

## 7. Limites

- Placement automatique, moniteur débranché et nom du moniteur : Chrome et Edge
  seulement. Sans l'API, l'utilisateur place la fenêtre, et l'OS retient sa place.
- La place est lue chaque seconde : un déplacement juste avant un rechargement peut
  ne pas être retenu.
- Un navigateur peut n'ouvrir qu'une fenêtre par clic : restaurer trois écrans peut
  demander trois clics.
- Pas d'écran secondaire sur mobile (`MobileShell` ne rend pas `ScreenLayer`).
