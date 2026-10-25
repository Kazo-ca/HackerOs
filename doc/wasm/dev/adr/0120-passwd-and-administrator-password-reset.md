# ADR 0120 : `passwd`, réinitialisation du mot de passe par un administrateur et effet sur le trousseau

## Statut

Accepté le 2026-10-17 (suite du chantier `v3/8-settings-security`). Benoit a
choisi « Dans v3/8 » sur la carte de décision du fil du chantier : la commande
`passwd` et la réinitialisation dans Paramètres sont ajoutées maintenant.

- **Complète :** [ADR 0100](0100-sensitive-settings-and-protected-write-audit.md)
  §2 (le trousseau se re-chiffre au changement de mot de passe et est mis de
  côté à une réinitialisation), [ADR 0013](0013-local-user-session.md)
  (comptes locaux, `LocalPasswordHasher`), ADR 0055 §5 (agent
  d'authentification administrateur).
- **Ajoute au SDK 1.1 :** des membres dans `PublicAPI.Unshipped.txt`, sans
  rupture ([ADR 0114](0114-sdk-1-0-freeze.md)).
- **Document :** [`sensitive-settings.md`](../sensitive-settings.md).

## Contexte

- Un compte a un `LocalPasswordCredential` (PBKDF2-SHA-256) fixé à sa création,
  mais rien ne permettait de le changer ni de le réinitialiser :
  `ILocalUserRepository` n'avait pas d'opération pour cela.
- Le trousseau de l'ADR 0100 avait déjà les deux points d'entrée
  `UserKeyringService.ChangePasswordAsync` et `SetAsideAsync`, que rien
  n'appelait.
- Le terminal n'avait pas de saisie masquée : la ligne d'un programme qui lit
  l'entrée standard est toujours affichée. `sudo` passe par une fenêtre de
  l'agent d'authentification, pas par le terminal.

## Décision

### 1. Changer son propre mot de passe, sans capacité

- Comme `passwd`, qui est `setuid` et vérifie l'ancien mot de passe par PAM,
  une app change le mot de passe de **l'utilisateur de la session** sans
  capacité : `HackUserAccounts.ChangePasswordAsync(courant, nouveau)`.
- Le mot de passe courant est vérifié (vide si le compte n'en a pas). Un échec
  est écrit dans `auth.log` (`passwd.change`, refusé) et lève
  `UnauthorizedAccessException`.
- Règles du nouveau mot de passe : 8 caractères au moins (comme l'assistant de
  création du premier compte), 256 au plus, différent de l'actuel
  (`Password unchanged`). Sinon `ArgumentException`.
- Le trousseau de l'utilisateur, déverrouillé, est re-chiffré sous le nouveau
  mot de passe (`ChangePasswordAsync`). Un compte qui n'avait pas de mot de
  passe n'avait pas de trousseau : il est créé à la connexion suivante.

### 2. Réinitialiser le mot de passe d'un autre compte : capacité dangereuse et administrateur

- `HackUserAccounts.ResetPasswordAsync(login, nouveau)` et
  `RemovePasswordAsync(login)` (`passwd -d`) exigent la nouvelle capacité
  **dangereuse** `users.manage` **et** un administrateur, par l'agent
  d'authentification (ADR 0055 §5) ; rien n'est demandé sous `sudo`.
- Le trousseau du compte visé devient illisible : l'OS le renomme
  `login.keyring.old` (`SetAsideAsync`), comme GNOME. Si le compte visé est
  celui de la session, le trousseau ouvert est re-chiffré (réinitialisation) ou
  mis de côté puis verrouillé (retrait).
- Retirer le mot de passe du **dernier administrateur actif** est refusé : sans
  mot de passe, n'importe qui ouvrirait la session qui peut tout approuver.
- `auth.log` reçoit `passwd.reset` ou `passwd.delete` avec l'administrateur
  qui a confirmé.

### 3. Lister les comptes

`HackUserAccounts.ListAsync()` donne, sans capacité, le nom de connexion, le nom
affiché, l'autorité, l'état et la présence d'un mot de passe de chaque compte,
comme `/etc/passwd` est lisible par tous. Jamais le hachage.

### 4. Saisie masquée dans le terminal

- L'entrée standard du terminal implémente `ITerminalInputMode`
  (`EchoEnabled`), l'équivalent du drapeau `ECHO` de `termios`. Quand il est
  coupé, la discipline de ligne du terminal ne montre ni les caractères ni leur
  effacement ; Entrée passe toujours à la ligne.
- `TerminalExecutionContext.ReadSecretLineAsync(invite)` écrit l'invite, coupe
  l'écho, lit une ligne et le rétablit, comme `getpass(3)`. Une entrée qui n'est
  pas un terminal (tube) est lue telle quelle.
- `passwd` reste sur le fil principal (ADR 0070) : l'entrée d'un worker n'est
  pas le terminal.

### 5. La commande `passwd`

| Forme | Effet |
|---|---|
| `passwd` | Change son mot de passe : `Current password:`, `New password:`, `Retype new password:`. |
| `passwd NOM` | Son propre compte : comme `passwd`. Un autre compte : `sudo` exigé (`You may not view or modify password information for NOM.`), puis `New password:` et `Retype new password:`. |
| `passwd -d NOM` | Retire le mot de passe (sous `sudo`). |
| `passwd -S [NOM]` | État façon `passwd -S` : `NOM P` (mot de passe), `NP` (aucun) ou `L` (compte désactivé), avec la date de dernière modification. |

Sous `sudo` sans nom, `passwd` refuse : HackerOS n'a pas de compte `root` avec
un mot de passe.

### 6. Paramètres : onglet « Utilisateurs »

L'onglet liste les comptes. Chacun peut y changer son mot de passe ; un
administrateur peut réinitialiser celui d'un autre compte (avec l'avertissement
que son trousseau sera mis de côté).

### 7. Ajouts d'API

| Bibliothèque | Ajout |
|---|---|
| `HackerOs.Simulation.Abstractions` | `ILocalUserRepository.SetCredentialAsync` (membre par défaut qui lève `NotSupportedException`) |
| `HackerOs.App.Abstractions` | `AppCapabilities.UsersManage` (`users.manage`, dangereuse) |
| `HackerOs.AppSdk` | `HackUserAccounts`, `IAppUserAccountsGateway`, `HackUserAccount`, `IAppExecutionContext.UserAccounts` (membre par défaut), `ITerminalInputMode`, `TerminalExecutionContext.ReadSecretLineAsync` |
| `HackerOs.AppSdk.Terminal` | `TerminalInputReader` implémente `ITerminalInputMode` |
| `HackerOs.Platform.Core` | `LocalPasswordService`, `SystemAdministrationGatewayServices.Passwords` (propriété `init`) |

## Conséquences

- Une app compilée contre 1.0 continue de tourner : chaque ajout à une
  interface a une implémentation par défaut.
- Le catalogue gagne une capacité (36) ; la commande `passwd` s'ajoute aux
  commandes de base.
- Pas de politique de complexité ni d'expiration (`chage`) : seule la longueur
  minimale est vérifiée.
