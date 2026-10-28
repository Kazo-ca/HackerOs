> [English version](../../../en/wasm/dev/screens-settings.md)

# Paramètres ▸ Écrans

Chantier `multi-monitor/7-screen-settings` du [plan multi-moniteur](plans/multi-monitor-plan.md),
mise en œuvre de l'[ADR 0179 §7](adr/0179-per-screen-taskbars-and-other-screen-menus.md), puis de
l'[ADR 0184](adr/0184-screens-administration-sdk.md) (chantier `multi-monitor/7b-screens-sdk`). La
page lit et écrit `screens.config` et pilote les écrans ouverts par `HackScreens` ; elle s'appuie sur le service d'écrans
([`screen-service.md`](screen-service.md)) et sur les barres des tâches par écran
([`per-screen-taskbars.md`](per-screen-taskbars.md)), qui suivent le fichier.

## 1. Ce que montre la page

| Bloc | Contenu | Écrit |
| --- | --- | --- |
| Écrans ouverts (ADR 0184) | Chaque écran ouvert : numéro, nom du moniteur (avec la permission), taille, « Plein écran », « Cette fenêtre » pour l'écran de la fenêtre de Paramètres, bouton **Fermer** sur les écrans 2 à 9. Boutons **Identifier**, **Nouvel écran**, **Restaurer les écrans (N)** s'il en reste à rouvrir, **Plein écran** / **Quitter le plein écran**. État de la permission `window-management`, avec **Autoriser maintenant** tant qu'elle n'est pas demandée. Bloc caché sur un hôte sans écrans (mobile, tests). | — (appelle `HackScreens`) |
| Barre des tâches de l'écran 1 | Choix `all` (toutes les fenêtres de tous les écrans) ou `own` (celles de l'écran 1), avec l'origine de la valeur (défaut, système, utilisateur) et le verrou de l'administrateur. | `[taskbar] primary-taskbar` de la couche utilisateur |
| Écrans retenus | L'écran 1 (cet onglet du navigateur), puis chaque écran 2 à 9 listé dans `open` ou ayant une place valide : état « ouvert, ou proposé à la prochaine connexion » ou « fermé », place `largeur × hauteur à (gauche, haut)`. Bouton **Oublier** sur un écran fermé qui a une place. | Retire la ligne numérotée de la couche utilisateur |
| Ouvrir et déplacer les écrans | Les autres commandes : Autre écran ▸ Nouvel écran (clic droit sur l'icône ou sur Agrandir), raccourcis `Super+Shift+←/→`, carte **Restaurer les écrans**, bouton `⤢`. | — |

Le fichier est `~/.config/hackeros/screens.config` ; `/etc/hackeros/screens.config` donne les
valeurs par défaut du système (un administrateur l'écrit, par exemple avec `sudo nano`).

## 2. Les boutons d'écran : `HackScreens`

Paramètres est une app de base : elle passe seulement par le SDK public. Le fichier passe par
`HackSettings.OpenSystemAsync(AppContext, "screens")` (document listé dans
`OsSettingsDocumentRegistry`) ; les écrans ouverts passent par la façade système `HackScreens`
(ADR 0184, espace `HackerOs.AppSdk.Administration`), qui demande la capacité normale
`screens.manage` déclarée par Paramètres.

| Appel | Effet |
| --- | --- |
| `IsSupportedFor(ctx)` | Vrai seulement quand le service d'écrans tourne (bureau du navigateur). La page cache le bloc sinon. |
| `GetState(ctx)`, `WatchAsync` | Liste des écrans, permission, écrans à restaurer, écran de la fenêtre appelante ; la page se met à jour à chaque changement. |
| `OpenScreenAsync`, `RestoreScreensAsync` | Ouvrent une fenêtre du navigateur pendant le clic. `Blocked` : la page dit d'autoriser les fenêtres surgissantes. |
| `CloseScreenAsync(n)` | Ferme l'écran `n` ; ses fenêtres reviennent à l'écran 1. |
| `IdentifyAsync` | Voile `is-identify` avec le numéro sur chaque écran, celui-ci compris, pendant 3 s. |
| `ToggleFullScreenAsync` | Plein écran de l'écran de la fenêtre appelante (l'onglet principal pour l'écran 1). |
| `RequestPermissionAsync` | Demande la permission `window-management` pendant le clic. |

Le navigateur n'accepte une nouvelle fenêtre, le plein écran ou sa demande de permission que pendant
un clic : chaque bouton appelle `HackScreens` tout de suite, sans attente avant.

## 3. Qui écrit quoi

| Ligne | Écrite par | Suivie par |
| --- | --- | --- |
| `[taskbar] primary-taskbar` | Paramètres ▸ Écrans, `ScreenService.SetPrimaryTaskbarAsync` | `ScreenService` (surveillance du document) : les barres changent tout de suite |
| `[screens] open` | `ScreenService` seulement | Paramètres ▸ Écrans (affichage) |
| `[screens] 2` à `9` | `ScreenService` (place), Paramètres ▸ Écrans (Oublier) | `ScreenService` à la prochaine ouverture de l'écran |

La page ne change pas `open` : le service garde sa liste en mémoire pendant la session et la réécrit
à chaque ouverture ou fermeture d'un écran. C'est pourquoi seul un écran fermé peut être oublié. Un
écran oublié rouvre à la place par défaut (1280 × 800, ou un moniteur libre avec la permission).

La page suit le fichier (`HackSettingsDocument.Changed`) : un écran ouvert ou déplacé, ou une
modification au terminal, s'affiche sans recharger la page.

## 4. Fichiers

| Fichier | Rôle |
| --- | --- |
| `Apps/System/HackerOs.Apps.Settings/ScreensPanel.razor(.cs, .css)` | La page, `RememberedScreen`, `RememberedPlace`. |
| `Apps/System/HackerOs.Apps.Settings/SettingsWindow.Navigation.cs` | Entrée `screens` dans le groupe Personnalisation (`--page=screens`). |
| `Platform/HackerOs.Platform.Core/Settings/OsSettingsDocumentRegistry.cs` | Document `screens` ouvert aux apps. |
| `Shared/HackerOs.AppSdk/Administration/HackScreens.cs`, `ScreensAdministrationContracts.cs` | Façade et contrats du SDK (ADR 0184). |
| `Platform/HackerOs.Platform.Core/Administration/AppScreensGateway.cs`, `IScreenControl.cs` | Passerelle de confiance : `screens.manage`, instance appelante. |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenControl.cs` | `IScreenControl` sur `ScreenService` et `WindowRuntime` (écran de la fenêtre appelante). |
| `Tests/HackerOs.Apps.Settings.Tests/ScreensPanelTests.cs` | Clés identiques au schéma de l'OS, choix `own` écrit, liste et places, Oublier, changement extérieur, écrans ouverts et boutons, refus, français. |
| `Tests/HackerOs.Platform.Core.Tests/Administration/AppScreensGatewayTests.cs` | Capacité exigée, hôte sans écrans, façade. |
| `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs` | Nouvel écran, Identifier et Fermer depuis Paramètres dans un vrai navigateur. |
| `Tests/HackerOs.Tests.Support/MinimalAppExecutionContext.cs` | Paramètres `systemSettings` et `screens` pour les tests de panneaux. |

## 5. Limites

- Le nom du moniteur n'apparaît qu'avec la permission `window-management` (Chrome, Edge).
- Une place oubliée pendant qu'un écran est ouvert n'est pas possible : il faut d'abord le fermer.
- Sur mobile, le bloc des écrans ouverts est caché et aucun écran secondaire ne s'ouvre (`MobileShell`).
