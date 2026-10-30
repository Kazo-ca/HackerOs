> [English version](../../../en/wasm/dev/user-switching.md)

# Changer d'utilisateur

« Changer d'utilisateur » du bouton Marche/arrêt du menu Démarrer garde la
session ouverte en arrière-plan, avec ses apps, et ouvre l'écran de connexion
pour un autre compte. Décision : [ADR 0201](adr/0201-switch-user-simultaneous-sessions.md),
issue [#290](https://github.com/Kazo-ca/HackerOs/issues/290).

## Ce que voit l'utilisateur

1. Menu Démarrer ▸ Marche/arrêt ▸ **Changer d'utilisateur** (grisé pour
   l'invité). Pas de confirmation : la session passe en arrière-plan.
2. L'écran de connexion s'ouvre, dans la langue du système. La liste des
   comptes ajoute « — session ouverte » aux comptes qui en ont une.
3. Un autre compte ouvre sa propre session : son bureau, sa barre des tâches,
   son thème. Les fenêtres de l'autre session ne sont pas visibles, ni dans la
   barre ni dans Alt+Tab.
4. Revenir à son compte (mot de passe ou clé d'accès) **reprend** sa session :
   les fenêtres et ce qui était tapé dedans sont toujours là.
5. **Se déconnecter** nomme les autres sessions ouvertes ; la case « Fermer
   aussi les autres sessions » les ferme aussi. **Redémarrer** les ferme
   toujours (la page est rechargée).
6. Au plus 3 sessions ouvertes. Une quatrième est refusée (« Trop de sessions
   ouvertes ») ; reprendre une session ouverte reste possible.

## Architecture

| Pièce | Rôle |
| --- | --- |
| `IUserSwitchingService` (`HackerOs.Simulation.Abstractions.Sessions`) | `OpenSessions`, `BackgroundSessions`, `MaxOpenSessions`, `SwitchAwayAsync`, `EndBackgroundSessionAsync`. |
| `LocalSessionService` | Implémente `ISessionService` et `IUserSwitchingService`. Liste des sessions ouvertes (principal + jeton racine) ; `CurrentPrincipal` = la session du premier plan. `LoginAsync` d'un compte en arrière-plan le reprend (`SessionResumedEvent`, audit `session.resume`) au lieu d'en ouvrir une autre. `HasOpenSession(userId)`, `DefaultMaxOpenSessions` = 3, `MaxOpenSessions` réglable à la construction (`init`). |
| `SessionLimitReachedException` | Une session de plus que la limite. |
| `SessionSwitchedOutEvent`, `SessionResumedEvent` | Le premier plan change. `OsSettingsService`, `AppInstallationService` se recalent ; `SyncScheduler` s'arrête ou repart ; `NetworkDriveService` démonte ou remonte les lecteurs réseau. Codés pour les Web Workers (`WirePayloadCodec`). |
| `SessionWindowFilter` (`HackerOs.Platform.Blazor.Shell`) | Une fenêtre appartient à la session de son instance d'app (`IAppExecutionContext.SessionId`) ; seules celles du premier plan sont montrées. |
| `DesktopArea.IsWindowShown`, `WindowHost.IsSuppressed` (`HackerOs.Windowing.Blazor`) | Une fenêtre cachée reste rendue (`hidden`), comme une fenêtre réduite : l'app garde son interface. |
| `WindowKeyboardCommands.ListsWindow` | Alt+Tab et la fenêtre active ne voient que le premier plan. |
| `DesktopShell.ForegroundSession`, `DesktopShell.OnSwitchUser`, `AppLauncher.OnSwitchUser` | L'hôte dit au bureau quelle session est au premier plan : les icônes du bureau et les barres des tâches sont recréées (`@key`), les menus de l'autre session se ferment. |
| `App.razor` (hôte Ecosystem) | Garde le bureau dans la page, caché (`visibility: hidden`, `inert`) sous l'écran de connexion, tant qu'une session attend en arrière-plan. Le changement d'utilisateur ne relance pas le démarrage : seuls les comptes et la langue du système sont rechargés. |
| `LogoutDialog` | Nomme les autres sessions, case « Fermer aussi les autres sessions », toujours fermées par Redémarrer (apps arrêtées par `StopSessionAppsAsync`, puis `EndBackgroundSessionAsync`). |
| `LocalAccountService.HasOpenSession` | `userdel` refuse un compte qui a une session ouverte, même en arrière-plan. |

Le bureau caché garde sa taille : `display: none` ferait mesurer une zone de
0 × 0 à la zone de bureau et déplacerait les fenêtres de la session en
arrière-plan.

## Limites

- Pendant l'écran de connexion, `CurrentPrincipal` est `null` : un service de
  l'interface ne voit personne. Les apps gardent leur utilisateur (`HackUser`).
- Le trousseau suit le premier plan : une app d'une session en arrière-plan
  qui lit le trousseau le trouve verrouillé.
- L'interface mobile n'a pas de menu Power.
- Une seule session par compte.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Sessions/LocalSessionServiceUserSwitchingTests.cs` :
  arrière-plan, reprise, mauvais mot de passe, jetons, fin d'une session en
  arrière-plan, limite, arrêt, `userdel`.
- `Tests/HackerOs.UI.E2E.Tests/StartMenuSwitchUserTests.cs` : reprise avec le
  texte d'une fenêtre ; deuxième compte avec son propre bureau, retour au
  premier, boîte de déconnexion qui nomme l'autre session.
- `Tests/HackerOs.UI.E2E.Tests/StartMenuPowerTests.cs` : « Changer
  d'utilisateur » actif.

## Tâches

- [x] Plusieurs sessions ouvertes dans `LocalSessionService`, une au premier plan.
- [x] Écran « Changer d'utilisateur » : la session reste ouverte, ses processus continuent.
- [x] Fenêtres, barre des tâches et Alt+Tab limités à la session du premier plan.
- [x] Réglages vivants appliqués à chaque changement de session.
- [x] Déconnexion et Redémarrer : les autres sessions ouvertes sont nommées et fermées sur demande.
- [x] Limite de 3 sessions ouvertes.
- [x] Compte invité : toujours grisé.
- [x] Docs fr/en et index.
