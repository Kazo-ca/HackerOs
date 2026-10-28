> [English version](../../../en/wasm/dev/per-screen-taskbars.md)

# Une barre des tâches par écran

Chantier `multi-monitor/5-taskbars` du [plan multi-moniteur](plans/multi-monitor-plan.md), mise en
œuvre de l'[ADR 0179 §1](adr/0179-per-screen-taskbars-and-other-screen-menus.md). Il s'appuie sur
le bureau des écrans secondaires ([`secondary-screen-desktop.md`](secondary-screen-desktop.md)) et
sur le service d'écrans ([`screen-service.md`](screen-service.md)). Sans écran secondaire, rien ne
change : la barre de l'écran 1 montre toutes les fenêtres, comme avant.

## 1. Ce que montre chaque barre

| Barre | Fenêtres | Bouton Démarrer | Horloge | Zone de notification, panneau de l'horloge, bouton de session |
| --- | --- | --- | --- | --- |
| Écran 1 | Selon `primary-taskbar` : toutes (`all`, par défaut) ou seulement celles de l'écran 1 (`own`) | Oui | Oui | Oui |
| Écran 2 et suivants | Celles de son écran | Oui | Oui (sans panneau) | Non : ils restent sur l'écran 1 |

- Cliquer sur le bouton d'une fenêtre d'un autre écran (barre de l'écran 1 en mode `all`) l'active
  sur son écran, sans la déplacer. Le clic garde ses règles (réduire la fenêtre active, restaurer une
  fenêtre réduite).
- Une fenêtre qui change d'écran (commande `MoveWindowToDisplayCommand`, chantiers 6 et 8) passe
  d'une barre à l'autre à la mise à jour suivante.

## 2. Le réglage `primary-taskbar`

Il vit dans `screens.config` (ADR 0179 §7), section `[taskbar]` :

```ini
[taskbar]
primary-taskbar = own   ; all | own
```

| Membre | Rôle |
| --- | --- |
| `PrimaryTaskbarMode` (`HackerOs.Platform.Core.Shell`) | `All` ou `Own`. |
| `ScreensSettingsDocuments.ReadPrimaryTaskbar(snapshot)` | `own` (sans tenir compte de la casse ni des espaces) donne `Own` ; toute autre valeur, ou pas de ligne, donne `All`. |
| `ScreensSettingsDocuments.FormatPrimaryTaskbar(mode)` | `all` ou `own`. |
| `ScreenService.PrimaryTaskbar` | Valeur lue au démarrage, puis suivie : le service surveille le fichier (`WatchAsync`) et relit **seulement** `primary-taskbar` à chaque changement. Les écrans à restaurer ne sont lus qu'au démarrage. |
| `ScreenService.SetPrimaryTaskbarAsync(mode)` | Change la valeur et l'écrit dans le fichier de l'utilisateur ; Paramètres ▸ Écrans (chantier 7) s'en servira. |

Un changement lève `ScreenService.Changed`, et `DesktopShell` redemande la liste à la barre de
l'écran 1 : modifier le fichier au terminal suffit, sans recharger.

## 3. Menu Démarrer

- `TaskbarLauncherAdapter` reste le seul état du menu Démarrer, et retient l'écran où il est ouvert
  (`Display`, interne).
- Chaque barre reçoit une vue de cet état (`For(écran)`) : son bouton est « actif » seulement si le
  menu est ouvert sur son écran. Le bouton d'un écran ferme le menu ouvert sur cet écran, sinon
  l'ouvre sur cet écran (il quitte alors l'autre écran).
- `DesktopShell` rend `AppLauncher` dans l'onglet principal pour l'écran 1, et dans le contenu de
  l'écran secondaire (fragment `ScreenTaskbar` de `ScreenLayer`) pour les autres. Le menu est en
  `position: fixed` : il se place dans la fenêtre de son écran.
- Un écran fermé pendant que le menu y est ouvert ferme le menu.

## 4. Sur quel écran s'ouvre une nouvelle fenêtre

`WindowLaunchCoordinator` choisit l'écran de la fenêtre avant sa première attente, puis place la
fenêtre dans la zone de travail de cet écran (cascade comptée parmi les fenêtres de cet écran) :

| Lancement | Écran |
| --- | --- |
| Depuis le menu Démarrer d'un écran | Cet écran : `DesktopShell` ouvre une portée `OpenOnDisplay(appId, écran)` (interne) autour du lancement et de la présentation. |
| Depuis la zone de notification (icône du serveur) | L'écran 1. |
| Autrement (commande, autre app, fichier ouvert) | L'écran de la fenêtre active, l'écran 1 s'il n'y en a pas. |
| Écran demandé fermé entre-temps | L'écran 1. |

La portée couvre aussi la présentation déclenchée par l'observateur des processus
(`WindowProcessObserver`), qui peut arriver avant celle du shell. Une fenêtre modale suit toujours
son propriétaire (ADR 0178 §2).

## 5. Mise en page dans un écran secondaire

```
.secondary-screen (flex, colonne)
 ├─ .secondary-screen-desktop (flex: 1)  ← DesktopArea : mesure la zone de travail
 ├─ AppLauncher (quand il est ouvert sur cet écran)
 ├─ footer.taskbar-container              ← fragment ScreenTaskbar de ScreenLayer
 └─ overlays, voile, bandeau, bouton plein écran
```

La barre est hors du `DesktopArea` : la zone de travail mesurée par le bureau (chantier 4) l'exclut
d'elle-même, et une fenêtre agrandie s'arrête au-dessus de la barre.

| Fichier | Rôle |
| --- | --- |
| `Platform/HackerOs.Platform.Blazor/Shell/TaskbarAdapters.cs` | `TaskbarScreenFilter.Lists`, liste filtrée de `TaskbarWindowSourceAdapter` (`Refresh`), vues par écran de `TaskbarLauncherAdapter`. |
| `Platform/HackerOs.Platform.Blazor/Shell/DesktopShell.razor` | Barre de l'écran 1 filtrée, barre et menu Démarrer de chaque écran secondaire, écran du lancement. |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenLayer.razor` | Paramètre `ScreenTaskbar`, rendu sous le bureau de l'écran. |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenService.cs` | `PrimaryTaskbar`, `SetPrimaryTaskbarAsync`, surveillance de `screens.config`. |
| `Platform/HackerOs.Platform.Blazor/Windows/WindowLaunchCoordinator.cs` | `OpenOnDisplay`, écran et zone de travail d'une nouvelle fenêtre. |
| `Platform/HackerOs.Platform.Core/Shell/ScreensSettings.cs` | `PrimaryTaskbarMode`, section `[taskbar]`. |

Le paquet `HackerOs.Taskbar.Blazor` ne change pas : le filtre vit dans l'adaptateur de l'OS.

## 6. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/Shell/ScreenTaskbarTests.cs` : filtre (`all` / `own`, écrans
  secondaires), listes qui suivent une fenêtre qui change d'écran, menu Démarrer par écran.
- `Tests/HackerOs.Platform.Blazor.Tests/WindowLaunchCoordinatorTests.cs` : app du menu Démarrer d'un
  écran ouverte sur cet écran ; autre lancement sur l'écran de la fenêtre active ; écran disparu.
- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenServiceTests.cs` : `primary-taskbar` lu au
  démarrage, suivi quand le fichier change, écrit dans le fichier de l'utilisateur.
- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenLayerTests.cs` : barre rendue sous le bureau.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ScreensSettingsTests.cs` : lecture et écriture de
  `[taskbar]` sur le disque virtuel.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs`, Chromium réel : l'écran 2 a sa barre, sous son
  bureau, sans zone de notification ; son menu Démarrer s'ouvre chez lui et y ouvre son app ; le menu
  de l'écran 1 reste sur l'écran 1 ; la barre de l'écran 1 liste les deux fenêtres, puis seulement la
  sienne quand `primary-taskbar = own` est écrit au terminal.

## 7. Limites

- La barre d'un écran secondaire n'a pas de panneau d'horloge (notifications, calendrier) : il reste
  sur l'écran 1.
- La barre de l'écran 1 en mode `all` ne dit pas sur quel écran est chaque fenêtre.
- Le choix de `primary-taskbar` dans Paramètres ▸ Écrans arrive au chantier 7 ; en attendant, le
  réglage s'écrit dans le fichier.
