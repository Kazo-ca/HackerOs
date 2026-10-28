> [English version](../../../../en/wasm/dev/adr/0184-screens-administration-sdk.md)

# ADR 0184 : API système des écrans pour Paramètres (`HackScreens`)

## Statut

Accepté le 2026-10-28, suite du chantier `multi-monitor/7-screen-settings` du
[plan multi-moniteur](../plans/multi-monitor-plan.md). Choix de Benoit (2026-10-28) : une
petite API du SDK réservée aux apps système, pour que Paramètres ▸ Écrans pilote les écrans.
Revient sur la réponse « plus tard » à la question Q1 du plan pour cette seule API
d'administration ; l'API des écrans pour toutes les apps (écran de ma fenêtre, placer une
fenêtre) reste à plus tard. Mise en œuvre : [`screens-settings.md`](../screens-settings.md).

## Contexte

- L'[ADR 0179 §7](0179-per-screen-taskbars-and-other-screen-menus.md) prévoit dans Paramètres ▸
  Écrans la liste des écrans ouverts avec le nom du moniteur, les boutons Identifier, Nouvel
  écran, Restaurer les écrans et Plein écran, et l'état de la permission `window-management`.
- Paramètres est une app de base : elle passe seulement par le SDK public. `ScreenService`
  (ADR 0180) est un service de l'OS dans `HackerOs.Platform.Blazor`, hors de sa portée. La
  première version de la page (PR #233) lit et écrit seulement `screens.config`.
- Les autres pages d'administration suivent un même modèle : une façade statique `Hack*` dans
  `HackerOs.AppSdk.Administration`, une passerelle `IApp*Gateway` sur `IAppExecutionContext`
  (non prise en charge par défaut), une passerelle de confiance dans Platform Core qui vérifie
  une capacité, et un service de l'OS derrière (ADR 0056 §8 à 11, ADR 0159 §8).
- Le navigateur impose ses gestes : `window.open`, la demande de permission
  (`getScreenDetails()`) et `requestFullscreen` doivent partir pendant un clic, et le plein écran
  doit être demandé par le document qui reçoit le clic.

## Décision

1. **Façade.** `HackScreens` dans `HackerOs.AppSdk.Administration`, chaque membre avec une
   surcharge à contexte explicite :

   | Membre | Rôle |
   | --- | --- |
   | `IsSupported`, `IsSupportedFor(context)` | L'hôte ouvre des écrans (navigateur de bureau). |
   | `GetState(context)` | `HackScreensState` : écrans, permission, `CanOpenScreen`, écrans à restaurer, écran de l'appelant. |
   | `WatchAsync(context, onChanged)` | Rappel à chaque changement (écran ouvert, fermé, renommé, plein écran, permission). |
   | `OpenScreenAsync` | Ouvre un écran ; `HackScreenOpenStatus` : `Opened`, `Blocked`, `Unavailable`, `LimitReached`. |
   | `RestoreScreensAsync` | Rouvre les écrans de la dernière session ; rend le nombre ouvert. |
   | `CloseScreenAsync(number)` | Ferme un écran secondaire ; ses fenêtres reviennent sur l'écran 1. |
   | `IdentifyAsync` | Affiche le voile et le numéro de chaque écran (ADR 0179 §6) pendant 3 secondes. |
   | `ToggleFullScreenAsync` | Plein écran de l'écran où est la fenêtre de l'appelant (l'onglet principal pour l'écran 1). |
   | `RequestPermissionAsync` | Demande la permission `window-management` ; rend le nouvel état. |

   `HackScreenInfo(Number, MonitorName, Width, Height, IsFullScreen)` : l'écran 1 est toujours
   le premier. `HackScreenPermission` : `Unknown`, `Unsupported`, `Prompt`, `Granted`, `Denied`.
2. **Capacité.** Une nouvelle capacité **normale** `screens.manage` (« Gérer les écrans ») est
   exigée par chaque membre sauf `IsSupported`. Normale, car tout passe par un clic de
   l'utilisateur et rien ne sort de la machine : une fenêtre du navigateur s'ouvre ou se ferme,
   et les fenêtres d'un écran fermé reviennent sur l'écran 1. Paramètres la déclare.
3. **Passerelle.** `IAppScreensGateway` (SDK) ; `AppScreensGateway` (Platform Core) vérifie la
   capacité et appelle `IScreenControl` (Platform Core), que `ScreenControl`
   (Platform Blazor) met en œuvre sur `ScreenService` et `WindowRuntime`. L'écran de l'appelant
   est celui de sa fenêtre active, sinon de sa première fenêtre, sinon l'écran 1.
4. **Gestes.** Les appels qui ouvrent une fenêtre ou passent en plein écran doivent venir d'un
   clic ; la passerelle ne les diffère pas. Sans geste, `OpenScreenAsync` rend `Blocked`,
   `ToggleFullScreenAsync` rend `false`.
5. **Identifier.** `ScreenService.IdentifyAsync` allume le voile de chaque écran, l'écran de la
   fenêtre de l'appelant compris, sans case en surbrillance ; il s'éteint après 3 secondes, au
   passage du pointeur (comme le voile du menu) ou à un nouvel appel.
6. **Version.** L'API entre dans `PublicAPI.Unshipped.txt` du SDK ; la version du SDK passe de
   2.1 à 2.2 à la prochaine publication (ADR 0173), décidée par Benoit avec la fusion vers `main`.

## Conséquences

- Paramètres ▸ Écrans affiche les écrans ouverts (nom du moniteur quand Chrome ou Edge le donne,
  taille, plein écran), l'état de la permission avec un bouton pour la demander, et les boutons
  Identifier, Nouvel écran, Restaurer les écrans, Fermer (par écran) et Plein écran. La partie
  fichier de la PR #233 reste (barre de l'écran 1, places retenues, Oublier).
- Un hôte sans écrans (mobile, tests, hôte sans JavaScript) garde la passerelle non prise en
  charge : la page cache les boutons et garde la partie fichier.
- Une app tierce qui déclare `screens.manage` peut ouvrir et fermer des écrans pendant les clics
  de l'utilisateur ; elle ne voit pas les fenêtres des autres apps.

## Vérification

- Tests unitaires : façade (capacité manquante, hôte sans écrans), passerelle de confiance,
  `ScreenControl` (état, écran de l'appelant, identifier puis extinction), panneau de Paramètres
  (boutons, états, français).
- UI.E2E : Paramètres ▸ Écrans ouvre l'écran 2 par Nouvel écran, Identifier montre le voile sur
  l'écran 2, Fermer le referme.
- Compatibilité du SDK : `PublicAPI.Unshipped.txt`, fixture de compatibilité inchangée.

## Tâches

- [ ] `HackScreens`, `IAppScreensGateway`, capacité `screens.manage`.
- [ ] `AppScreensGateway`, `IScreenControl`, `ScreenControl`, identification par le service.
- [ ] Boutons de Paramètres ▸ Écrans, tests, docs et index.
