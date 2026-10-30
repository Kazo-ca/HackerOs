> [English version](../../../../en/wasm/dev/adr/0201-switch-user-simultaneous-sessions.md)

# ADR 0201 : changer d'utilisateur, plusieurs sessions ouvertes

## Statut

Accepté le 2026-10-30. Issue [#290](https://github.com/Kazo-ca/HackerOs/issues/290)
(étiquette `TODO`), série `todo/base`, chantier `todo/16-switch-user`.

- **Complète :** [ADR 0195](0195-start-menu-power-button.md) §4 (« Changer
  d'utilisateur » grisé jusqu'ici), [ADR 0013](0013-local-user-session.md)
  (session locale), [ADR 0187](0187-guest-start-and-sign-in-credits.md)
  (compte invité), [ADR 0100](0100-sensitive-settings-and-protected-write-audit.md)
  §2 (trousseau).
- **Choix fait en attendant la réponse de Benoit** à la question « Comment
  Changer d'utilisateur doit-il fonctionner ? » (sessions simultanées, fermer
  puis choisir, ou garder grisé), posée dans le fil « Déplacer Power dans le
  menu Démarrer » puis reposée dans le fil du chantier : la recommandation,
  sessions simultanées comme Windows. Si Benoit choisit autre chose, une
  nouvelle ADR remplacera celle-ci.

## Contexte

- Le menu Power du menu Démarrer (ADR 0195) affichait « Changer d'utilisateur »
  grisé : l'OS n'avait qu'une session (`ISessionService.CurrentPrincipal`).
- Les processus savent déjà sous quel utilisateur ils tournent (`HackUser`,
  `ProcessRecord.UserId`, `IAppExecutionContext.SessionId`) : une app ne lit pas
  la session de l'interface.
- Ce qui suit la session de l'interface : les réglages vivants (thème, langue,
  fuseau, accessibilité, menu Démarrer, par `OsSettingsService`), le bureau, la
  barre des tâches, les fenêtres affichées.
- L'interface d'une fenêtre d'app vit dans le composant `DesktopShell` : si le
  bureau est retiré de la page, l'app perd son interface.

## Décision

### 1. Plusieurs sessions ouvertes, une seule au premier plan

- `LocalSessionService` garde la liste des sessions ouvertes. Une seule est au
  **premier plan** : c'est elle que donne `CurrentPrincipal`. Les autres sont en
  **arrière-plan** : leur jeton racine n'est pas annulé, leurs processus, leurs
  services et leurs fenêtres continuent.
- Nouvelle interface `IUserSwitchingService` (`HackerOs.Simulation.Abstractions.Sessions`),
  implémentée par `LocalSessionService`, pour ne pas changer `ISessionService` :
  - `OpenSessions` : toutes les sessions ouvertes (premier plan compris) ;
  - `BackgroundSessions` : celles en arrière-plan ;
  - `MaxOpenSessions` : la limite (§6) ;
  - `SwitchAwayAsync()` : la session du premier plan passe en arrière-plan et
    l'état revient à `LoggedOut` (l'écran de connexion peut s'ouvrir) ;
  - `EndBackgroundSessionAsync(SessionId)` : ferme une session en arrière-plan,
    comme `loginctl terminate-session`.
- `LoginAsync` (mot de passe ou clé d'accès) d'un compte qui a déjà une session
  en arrière-plan la **reprend** : le mot de passe est vérifié, le dossier
  personnel n'est pas préparé de nouveau, le `SessionId` reste le même. Sinon
  une nouvelle session s'ouvre, comme avant.
- `LogoutAsync` ne ferme que la session du premier plan ; `ShutdownAsync` les
  ferme toutes.
- `CreateLinkedCancellationSource()` lie au jeton de la session du premier plan,
  comme avant ; les jetons déjà liés d'une session en arrière-plan restent liés
  à la sienne.
- Nouveaux événements : `SessionSwitchedOutEvent` (une session passe en
  arrière-plan) et `SessionResumedEvent` (elle revient au premier plan).
  `SessionActivatedEvent` ne sert qu'à une nouvelle session, et
  `SessionLoggedOutEvent` qu'à une session fermée (au premier plan ou non).

### 2. Ce qui suit la session du premier plan

- `OsSettingsService` et `AppInstallationService` se recalent sur les deux
  nouveaux événements : thème, fuseau, accessibilité et menu Démarrer de la
  session reprise s'appliquent tout de suite.
- Langue : l'écran de changement d'utilisateur parle la langue du système, puis
  la session reprise retrouve la sienne ([ADR 0104](0104-localization.md)).
- Le trousseau (ADR 0100 §2) suit le premier plan : verrouillé quand la session
  passe en arrière-plan, déverrouillé de nouveau par le mot de passe (ou la clé
  d'accès) qui la reprend.
- La synchronisation périodique ([ADR 0101](0101-synchronized-deletions-and-conflicts.md))
  s'arrête quand plus aucune session n'est au premier plan et repart à la
  reprise.
- Les lecteurs réseau ([ADR 0181](0181-host-folder-mounted-as-a-network-drive.md))
  de la session qui passe en arrière-plan sont démontés, puis remontés à sa
  reprise : l'autre utilisateur ne les voit pas. Fermer une session en
  arrière-plan ne démonte pas ceux du premier plan.

### 3. Les fenêtres et le bureau

- Le bureau (`DesktopShell`) reste dans la page tant qu'une session est ouverte,
  même pendant l'écran de changement d'utilisateur (il est alors caché et
  inerte) : les apps des sessions en arrière-plan gardent leur interface.
- Une fenêtre appartient à la session de son instance d'app
  (`IAppExecutionContext.SessionId`). Seules les fenêtres de la session du
  premier plan sont visibles ; les autres restent rendues mais cachées
  (`hidden`), comme une fenêtre réduite. Nouveaux paramètres
  `DesktopArea.IsWindowShown` et `WindowHost.IsSuppressed` dans
  `HackerOs.Windowing.Blazor`.
- La barre des tâches et le sélecteur de fenêtres (Alt+Tab) ne listent que les
  fenêtres du premier plan.
- Les icônes du bureau, le menu Démarrer et la barre des tâches sont recréés à
  chaque changement de session : ils lisent le dossier et les réglages de
  l'utilisateur du premier plan.

### 4. Écran « Changer d'utilisateur »

- « Changer d'utilisateur » du menu Power est actif pour tout compte sauf
  l'invité (toujours grisé, ADR 0195 §4 et ADR 0187).
- Un clic met la session en arrière-plan sans confirmation, comme Windows, puis
  ouvre l'écran de connexion. Il montre la liste des comptes ; ceux qui ont une
  session ouverte portent la mention « session ouverte ». Choisir son propre
  compte et taper son mot de passe reprend sa session.

### 5. Se déconnecter et Redémarrer avec d'autres sessions ouvertes

- La boîte de déconnexion nomme les autres sessions ouvertes.
- **Se déconnecter** ne ferme que la session du premier plan ; la case
  « Fermer aussi les autres sessions » ferme aussi les autres (leurs apps
  s'arrêtent par le chemin normal, brouillons compris).
- **Redémarrer** recharge la page : toutes les sessions sont fermées. La boîte
  le dit et nomme les sessions qui seront fermées.
- Après une déconnexion, s'il reste des sessions en arrière-plan, l'écran de
  connexion les propose ; le bureau reste dans la page, caché.

### 6. Limite de mémoire

- Tout tourne dans un seul onglet WebAssembly : au plus **3 sessions
  ouvertes** (`LocalSessionService.DefaultMaxOpenSessions`). Ouvrir une
  quatrième session est refusé (« Trop de sessions ouvertes ») ; reprendre une
  session ouverte reste possible.

### 7. Comptes

- `userdel` (et Paramètres ▸ Utilisateurs) refuse de supprimer un compte qui a
  une session ouverte, au premier plan ou non (« is currently signed in »).

## Conséquences

- Les apps d'une session en arrière-plan continuent : leur temps CPU et leur
  mémoire comptent toujours (gestionnaire des tâches, OOM killer).
- Pendant l'écran de changement d'utilisateur, `CurrentPrincipal` est `null` :
  un service d'interface qui lit la session à ce moment ne voit personne. Les
  apps, elles, gardent leur utilisateur.
- L'interface mobile n'a pas de menu Power : rien ne change sur mobile.
- La géométrie des fenêtres n'est toujours pas enregistrée à la déconnexion
  (connu, ADR 0187).
- Le bureau caché garde sa taille (`visibility: hidden`) : avec `display: none`
  la zone de bureau mesurerait 0 × 0 et déplacerait les fenêtres.
- Tests : `LocalSessionServiceUserSwitchingTests` (cœur), tests de bout en bout
  `StartMenuSwitchUserTests` et `StartMenuPowerTests`. Doc :
  [`user-switching.md`](../user-switching.md).
