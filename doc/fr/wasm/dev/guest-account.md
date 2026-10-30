> [English version](../../../en/wasm/dev/guest-account.md)

# Compte invité

## Objet

Décrit le compte invité de HackerOS : essayer l'OS sans créer de compte, puis
créer son propre compte en gardant ou non le travail de l'invité. Décision :
[ADR 0187](adr/0187-guest-start-and-sign-in-credits.md), partie A. Suivi :
[issue #243](https://github.com/Kazo-ca/HackerOs/issues/243).

## Pour l'utilisateur

1. **Premier démarrage.** Sous le formulaire de création du premier compte,
   le bouton **Démarrer en invité** ouvre tout de suite le bureau. Il n'est
   proposé que si aucun compte n'existe encore.
2. **Visites suivantes.** Tant que l'invité est le seul compte, l'écran de
   connexion affiche « Invité » avec **Continuer en invité** (pas de mot de
   passe) et le lien **Créer mon compte**.
3. **Actions d'administrateur.** L'invité est administrateur. Quand l'OS
   demande la confirmation d'un administrateur (fenêtre de l'agent
   d'authentification, capacité sensible), l'invité clique sur
   **Confirmer** sans mot de passe.
4. **Créer mon compte.** On l'ouvre depuis trois endroits :
   - le lien sous **Continuer en invité** sur l'écran de connexion ;
   - le bandeau « Vous utilisez le compte invité » de Paramètres ▸
     Utilisateurs (la session invité se ferme d'abord : enregistrez votre
     travail) ;
   - le rappel « Invité · Créer mon compte » sous le nom, dans le menu
     Démarrer, qui ouvre Paramètres ▸ Utilisateurs.

   Le formulaire demande un nom de connexion, un nom affiché, un mot de passe
   (8 caractères au moins) et un choix :
   - **Migrer** (par défaut) : l'invité devient votre compte. Fichiers,
     réglages, applications installées et autorisations sont gardés ;
     `/home/guest` devient `/home/<login>`.
   - **Supprimer l'invité** : un nouveau compte vide est créé, puis l'invité
     et son dossier `/home/guest` sont supprimés. Les applications installées
     pour la machine restent.

   Le compte créé est administrateur et sa session s'ouvre aussitôt. Ensuite
   le bouton invité ne revient plus sur cette installation.

## Fonctionnement

| Élément | Où | Rôle |
| --- | --- | --- |
| `LocalUser.IsGuest` | `Simulation.Abstractions` | Marque l'invité (enregistrée `isGuest` dans IndexedDB). Le nom `guest` est aussi réservé (`LocalAccountService.IsReserved`). |
| `GuestAccountService` | `Platform.Core/Sessions` | `CanCreateAsync` (aucun compte), `CreateAsync` (compte `guest`, groupe `guest` + `administrators`, sans mot de passe), `FindLoneGuestAsync`, `MigrateAsync`, `ReplaceAsync`. |
| `AdminElevationVerifier` | `Platform.Core/Elevation` | Accepte un mot de passe vide seulement pour l'invité seul, sans mot de passe et actif ; `CanConfirmWithoutPasswordAsync` fait afficher **Confirmer** par les fenêtres de l'agent. |
| `GuestAccountRequests` | `Platform.Core/Sessions` | Une app demande le formulaire à l'hôte (`CreateAccountRequested`) ; l'hôte possède le formulaire, l'app ne voit jamais le mot de passe. |
| `HackUserAccounts.RequestGuestAccountCreationAsync` | SDK | Utilisée par Paramètres ▸ Utilisateurs ; acceptée seulement pour la session de l'invité. `HackUserAccount.IsGuest` marque l'invité dans la liste. |
| `App.razor` | `Ecosystem.Host` | Boutons invité, formulaire « Créer mon compte », fermeture de la session invité à la demande de Paramètres. |
| `AppLauncher` | `Platform.Blazor/Shell` | Rappel dans la tuile utilisateur (`OnCreateAccount`), qui ouvre `--page=users`. |

### Migrer

`MigrateAsync` fait l'équivalent de `usermod -l NOUVEAU -d /home/NOUVEAU -m`,
`groupmod -n NOUVEAU guest` puis `passwd NOUVEAU` :

1. vérifie le formulaire (`HackUserAccounts.ValidateNewAccount`), le nom
   réservé ou déjà pris, et refuse si la session de l'invité est ouverte ;
2. **copie** `/home/guest` vers `/home/<login>` au nom du nouveau login, en
   gardant le mode de chaque entrée. Les chemins `/home/guest` des fichiers
   texte de `~/.config` et des cibles de liens symboliques sont réécrits ;
3. convertit le compte : même `LocalUserId`, nouveau login et nom affiché,
   mot de passe, marque d'invité retirée ;
4. renomme le groupe `guest`, puis supprime `/home/guest` ;
5. écrit `users.guest-migrate` dans `auth.log`.

Si la copie ou la conversion échoue, la copie est effacée et l'invité reste
intact.

### Supprimer l'invité

`ReplaceAsync` crée le nouvel administrateur (groupe propre et
`administrators`, mot de passe), puis supprime le compte `guest`, son groupe et
`/home/guest` (comme `userdel -r`), et écrit `users.guest-delete`.

### Fermeture de la session invité

Depuis Paramètres, l'hôte arrête d'abord toutes les apps de la session
(`AppLifecycleOrchestrator.StopSessionAppsAsync`, ce qui ferme leurs
fenêtres), ferme la session, puis affiche le formulaire sur l'écran de
connexion. La fermeture de session normale (menu de session) fait la même
chose depuis ce chantier.

## Limites

- Le dossier personnel est copié puis l'ancien supprimé : les dates de
  modification des fichiers sont celles de la copie.
- Seuls les fichiers de `~/.config` sont réécrits ; un chemin `/home/guest`
  écrit ailleurs par l'utilisateur reste tel quel.
- L'invité n'avait pas de trousseau : celui du compte est créé à sa première
  connexion avec le mot de passe choisi.
- La position des fenêtres ouvertes au moment de la fermeture de la session
  invité n'est pas enregistrée.

## Tests

- `GuestAccountServiceTests`, `GuestConversionTests`, `GuestElevationTests`,
  `GuestAccountRequestsTests` (Platform.Core).
- `AdministratorAuthenticationOverlayTests` (Platform.Blazor), `UsersPanelTests`
  (Settings), `EcosystemHostStateTests` (Ecosystem).
- `AppLifecycleOrchestratorTests.Signing_out_stops_the_window_apps_of_the_session_too`.
