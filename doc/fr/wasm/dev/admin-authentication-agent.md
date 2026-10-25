> [English version](../../../en/wasm/dev/admin-authentication-agent.md)

# Agent d'authentification de l'OS

## Objet

Certaines opérations demandent un administrateur **en plus** de la capacité de
l'app : désinstaller une app pour tous les utilisateurs aujourd'hui, et plus
tard changer la politique de sécurité ou réinitialiser la machine (ADR 0056).
Comme `polkit` sous Linux, l'app ne voit jamais le mot de passe (ADR 0055,
section 5) :

1. l'app appelle une API du SDK, sans identifiants (par exemple
   `HackAppManagement.UninstallAsync(id, HackInstallScope.Machine)`) ;
2. la passerelle vérifie la capacité (`apps.manage`), puis demande à l'agent de
   confirmer un administrateur ;
3. l'agent affiche sa propre fenêtre, dessinée par la plateforme au-dessus de
   toutes les fenêtres : l'opération en clair, l'app, l'utilisateur qui la
   demande, un nom d'administrateur et un mot de passe ;
4. chaque essai passe par `IAdminElevationVerifier`, le vérificateur de `sudo`
   (ADR 0042), et l'issue est écrite dans `/var/log/auth.log` ;
5. si un administrateur s'est authentifié, l'OS fait l'opération lui-même, dans
   un contexte système audité ; sinon la passerelle lève
   `HackUnauthorizedAccessException`.

Un processus lancé par `sudo` (`IsRootElevated`) agit déjà en root : l'agent ne
demande rien et le journal porte `via=sudo`.

## Architecture

| Élément | Projet | Rôle |
|---|---|---|
| `IAdministratorAuthenticationAgent`, `AdministratorAuthenticationRequest`, `AdministratorAuthenticationResult` | `Platform/HackerOs.Platform.Core/Elevation` | contrat appelé par les passerelles |
| `AdministratorAuthenticationAgent` | idem | implémentation : root direct, trois essais, journal |
| `IAdministratorAuthenticationPrompt`, `AdministratorAuthenticationPromptRequest`, `AdministratorAuthenticationAttempt` | idem | surface qui montre la demande ; elle ne décide rien |
| `AdministratorAuthenticationBroker` | `Platform/HackerOs.Platform.Blazor/Security` | file d'attente des demandes, implémente `IAdministratorAuthenticationPrompt` |
| `AdministratorAuthenticationOverlay` | idem | fenêtre « bureau sécurisé » rendue par `DesktopShell` et `MobileShell` |

Enregistrement dans l'hôte Ecosystem (`EcosystemServiceCollectionExtensions`) :
le courtier et l'agent sont `Scoped`, comme la fenêtre d'approbation des
capacités dangereuses. L'agent est passé à `AppExecutionContextFactory` par
`AppCatalogGatewayServices`, résolu à la demande (dépendance circulaire avec
l'orchestrateur sinon).

## Comportement

- **Pas de surface** (aucun shell rendu, hôte sans interface) : refus
  immédiat, raison `unavailable`.
- **Trois essais** au plus, comme la fenêtre d'approbation de l'ADR 0049 ; au
  troisième échec la fenêtre se ferme, raison `too-many-attempts`, et les
  essais suivants sont refusés sans vérification.
- **Annuler** (**Cancel** dans une session anglaise : la fenêtre suit la
  langue de la session, ADR 0104), Échap, ou la fermeture du shell : raison
  `cancelled`, rien n'est écrit.
- **Préremplissage** : le nom de l'utilisateur de la session s'il est
  administrateur. Il retape quand même son mot de passe (ADR 0049, D24). Un
  autre compte administrateur peut répondre, comme avec `sudo`.
- **La fenêtre ne peut pas forger une approbation** : seul un essai vérifié par
  l'agent compte, quoi que la surface renvoie.
- **Clé d'accès** (ADR 0168 §4) : quand l'hôte branche
  `AdministratorAuthenticationAgent.PasskeyVerifier`, la fenêtre montre
  « Utiliser une clé d'accès » ; `AdminElevationVerifier.VerifyWithPasskeyAsync`
  vérifie une clé d'accès du compte saisi (raison `no-passkey` s'il n'en a pas).
  Une demande du navigateur fermée (`passkey-cancelled`) ou un navigateur sans
  WebAuthn (`passkey-unsupported`) ne compte pas comme un essai raté.

### Journal `/var/log/auth.log`

Action `admin.authenticate`, une entrée par essai et une pour l'échec final :

| Propriété | Contenu |
|---|---|
| `app` | id de l'app qui demande |
| `operation` | id stable de l'opération : `apps.uninstall.machine`, `apps.reinstall.machine`, `permissions.policy`, `permissions.machine-decision`, `system.reboot`, `system.factory-reset`, `users.password-reset`, `users.password-delete`, `users.add`, `users.delete` |
| `requestedBy` | login de l'utilisateur de la session |
| `admin` | login saisi (ou `root`, ou `-`) |
| `outcome` | `success` ou `failure:<raison>` |
| `via` | `sudo` quand le processus était déjà root |

Le mot de passe n'est jamais journalisé.

## Utilisation par une passerelle

```csharp
AdministratorAuthenticationResult auth = await agent.AuthenticateAsync(
    new AdministratorAuthenticationRequest(
        callerManifest,
        "Uninstall Calculator for all users",   // affiché
        "apps.uninstall.machine",                // journalisé
        principal,
        isRootElevated),
    cancellationToken);
if (!auth.Succeeded)
{
    throw new HackUnauthorizedAccessException($"Administrator authentication failed ({auth.FailureReason}).");
}
// L'OS agit ensuite lui-même, en contexte système.
```

`AppManagementGateway` (`Platform/HackerOs.Platform.Core/Execution`) est le
premier utilisateur. Le chantier `4-sdk-system-admin` le réutilise tel quel
par `AdministratorGate` (`Platform/HackerOs.Platform.Core/Administration`) :
`AppPermissionAdministrationGateway` (politique et décisions machine) et
`AppSystemPowerGateway` (redémarrage, réinitialisation). Voir
[`system-administration.md`](../framework/system-administration.md).

## Décisions

- **Une fenêtre distincte** de celle des capacités dangereuses, de la même
  famille visuelle (bandeau rouge, voile, pied « auth.log ») : l'une approuve
  des capacités pour une app, l'autre autorise une seule opération.
- **L'OS écrit lui-même après l'authentification** au lieu d'élargir les droits
  de l'app : l'app ne reçoit aucun ticket réutilisable, comme `pkexec` qui
  lance l'assistant en root.
- **Mode texte du terminal non couvert ici.** L'ADR 0055 demande une saisie en
  mode texte quand l'opération part d'un terminal. Le terminal ne dispose pas
  encore d'une invite d'identifiants fournie par l'appelant : elle arrive avec
  `HackCredentialPrompt` (chantier `4-sdk-shell-host`). D'ici là, une commande
  qui demande un administrateur voit la fenêtre du bureau, et `sudo` reste le
  moyen texte d'agir en root.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Elevation/AdministratorAuthenticationAgentTests.cs` :
  root sans demande, absence de surface, essai faux puis juste, trois échecs,
  surface qui tente de forger une approbation, demande de clé d'accès fermée
  qui ne coûte pas d'essai puis clé vérifiée, pas de bouton sans vérificateur.
- `Tests/HackerOs.Platform.Core.Tests/Execution/AppCatalogGatewayTests.cs` :
  portée machine refusée ou acceptée, app système refusée avant toute demande,
  `sudo` sans demande.
- `Tests/HackerOs.Platform.Blazor.Tests/Security/AdministratorAuthenticationOverlayTests.cs` :
  rendu de la fenêtre et comportement du courtier.

## Liste des tâches (`rebuild/4-sdk-user-and-apps`)

- [x] Contrats et implémentation de l'agent dans Platform Core.
- [x] Courtier et fenêtre dans Platform Blazor, rendus par les deux shells.
- [x] Enregistrement dans l'hôte Ecosystem et passage à la fabrique de contextes.
- [x] Utilisation par `HackAppManagement` pour la portée machine.
- [x] Tests et documentation.
- [ ] Mode texte dans le terminal (avec `HackCredentialPrompt`, `4-sdk-shell-host`).
