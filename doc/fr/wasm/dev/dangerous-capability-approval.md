> [English version](../../../en/wasm/dev/dangerous-capability-approval.md)

# Approbation des capacités dangereuses

## Objectif

Une app non système ne reçoit jamais en silence une capacité dangereuse
(`process.manage`, `network.real.access`, `filesystem.system.write`,
`keyring.read-all` de l'ADR 0100, `users.manage` de l'ADR 0120…, voir
[`capabilities.md`](../framework/capabilities.md)). Au lancement, l'OS demande à un
administrateur de l'approuver, comme le « bureau sécurisé » de l'UAC de Windows,
et mémorise la décision dans de vrais fichiers. Ce document décrit
l'implémentation du chantier `rebuild/2-dangerous-approval` de
l'[ADR 0049](adr/0049-dangerous-capabilities-and-admin-approval.md)
(sections 3 à 8).

Les apps système (ADR 0048) ne voient jamais la fenêtre, et aucune app non
système de l'hôte Ecosystem ne déclare aujourd'hui de capacité dangereuse :
l'expérience de base ne change pas.

## Fichiers

| Fichier | Propriétaire | Contenu |
|---|---|---|
| `/etc/hackeros/security.config` | `system:system 0644`, document `SystemOnly` | `dangerousCapabilities=prompt` (défaut) ou `block` |
| `/etc/hackeros/app-permissions.config` | `system:system 0644` | décisions machine `allow` / `deny` ; **seule source d'un `allow`** |
| `~/.config/hackeros/app-permissions.config` | l'utilisateur | refus (`deny`) valables pour lui seul |

Les deux fichiers de `/etc` sont livrés commentés au premier démarrage
(`SettingsStoreToFilesMigration.MigrateSystemAsync`).

```text
# /etc/hackeros/app-permissions.config
[permissions]
org.example.scanner:network.real.access=allow
org.example.scanner:filesystem.system.read=deny
```

- Clé `<appId>:<capacité>`, valeur `allow` ou `deny`, section `[permissions]`.
- Une ligne invalide (forme, capacité inconnue, valeur) est ignorée avec un
  avertissement dans `/var/log/syslog`. Une ligne qui vise une capacité normale
  ou une app système est ignorée sans effet. Un `allow` dans le fichier
  utilisateur est ignoré avec un avertissement.
- Une valeur invalide de `dangerousCapabilities` est ignorée (ADR 0044) :
  `prompt` s'applique.
- `cat /etc/hackeros/app-permissions.config` montre ce qu'un administrateur a
  approuvé ; `sudo nano` permet de le modifier à la main.

## Table de décision

`DangerousCapabilityEvaluator.Evaluate(manifest, isSystemApp, policy, decisions)`
donne l'état de chaque capacité dangereuse déclarée. Pour une app non système,
la première règle qui s'applique gagne :

| Règle | État (`DangerousCapabilityState`) | Accordée ? |
|---|---|---|
| politique `block` | `BlockedByPolicy` (les `allow` existants sont ignorés) | non |
| `deny` machine | `DeniedByAdministrator` | non |
| `deny` utilisateur | `DeniedByUser` (l'emporte sur un `allow` machine) | non |
| `allow` machine | `Allowed` | **oui**, source `AdministratorApproval` |
| aucune décision | `Pending` : la passerelle demande | non |

Une app système a l'état `System` pour toutes ses capacités dangereuses
(accordées à la connexion, source `SystemPolicy`).

## Architecture

| Élément | Projet | Rôle |
|---|---|---|
| `SecuritySettingsDocuments`, `DangerousCapabilityPolicy` | Platform.Core/Policy | schéma de `security.config`, lecture de la politique |
| `AppPermissionsDocument`, `AppPermissionDecisions` | Platform.Core/Policy | chemins, analyse et édition ligne à ligne des deux fichiers |
| `IAppPermissionStore` / `AppPermissionStore` | Platform.Core/Policy | lecture des deux fichiers ; écriture machine en **contexte système audité**, écriture utilisateur avec ses propres droits |
| `DangerousCapabilityEvaluator` | Platform.Core/Policy | table de décision pure |
| `IDangerousCapabilityGate` / `DangerousCapabilityGate` | Platform.Core/Policy | passerelle de lancement, vérification des mots de passe, `auth.log`, alignement des grants |
| `IDangerousCapabilityApprovalPrompt` | Platform.Core/Policy | contrat de l'invite (fenêtre ou terminal) |
| `DangerousCapabilityApprovalBroker` | Platform.Blazor/Security | invite par défaut de l'hôte : file des demandes pour la fenêtre |
| `DangerousCapabilityApprovalOverlay` | Platform.Blazor/Security | la fenêtre spéciale, rendue par `DesktopShell` et `MobileShell` |
| `ShellHostPrompts` | Platform.Core/Shell | mode texte pour une commande lancée depuis un shell hébergé (`HackShell`, [`shell-host.md`](../framework/shell-host.md)) ; écrit et lit par le `HackCredentialPrompt` du terminal |
| `AppPermissionAdministration`, `AppPermissionsPanel` | Platform.Core/Policy, Apps.Settings | onglet Permissions des Paramètres |

### Lancement

`AppLifecycleOrchestrator.LaunchAsync`, après la vérification de singleton et la
consommation d'un éventuel ticket d'élévation, appelle
`IDangerousCapabilityGate.EnsureDecidedAsync(manifest, principal, request.ApprovalPrompt, request.Interactive)`
pour toute app qui déclare une capacité dangereuse, **avant** de créer le
processus :

1. App système : rien à faire.
2. Politique `block` : pas de fenêtre, entrée `capability.blocked`.
3. Politique `prompt` et au moins une capacité `Pending` :
   - lancement non interactif (`AppLaunchRequest.Interactive = false`, services
     démarrés automatiquement avec la session) ou aucune invite disponible
     (aucune coque affichée) : entrée `capability.deferred`, l'approbation se
     fera depuis les Paramètres ou au prochain lancement interactif ;
   - sinon l'invite s'ouvre (`request.ApprovalPrompt`, sinon la fenêtre).
4. La passerelle accorde les capacités `Allowed` et **révoque** une grant qui
   ne l'est plus (passage à `block`, refus). Un changement prend donc effet au
   prochain lancement, et les capacités d'une instance restent figées à son
   lancement (ADR 0042).

Une app démarrée sans une capacité reçoit `HackUnauthorizedAccessException`
quand elle l'utilise, comme pour toute capacité absente.

### Invite

`DangerousCapabilityApprovalRequest` porte le manifeste, les capacités en
attente (`AppCapabilities.Describe`), l'utilisateur, le nom d'administrateur à
préremplir (l'utilisateur seulement s'il est administrateur) et
`ApproveAsync(login, password)`. L'invite ne décide rien : `ApproveAsync`
vérifie les identifiants avec `IAdminElevationVerifier` (ADR 0042 ; un autre
compte que l'utilisateur courant est accepté, comme pour `sudo`), puis écrit les
lignes `allow` dans le fichier machine en contexte système audité. Même un
administrateur doit saisir son mot de passe. Après trois échecs,
`ApproveAsync` refuse sans vérifier et l'invite se ferme : rien n'est écrit.
Une invite qui répondrait `Approved` sans tentative vérifiée est traitée comme
une fermeture.

| Fin de l'invite | Effet |
|---|---|
| *Autoriser* réussi | `allow` machine, grants `AdministratorApproval`, `capability.approve` (`success`) |
| mot de passe faux | une entrée `capability.approve` (`failure:<raison>`) par essai |
| *Refuser* | `deny` dans le fichier de l'utilisateur, `capability.refuse` ; la question ne revient plus pour lui |
| Échap / fermer / trois échecs | rien d'écrit, la question revient au prochain lancement |

### La fenêtre spéciale

`DangerousCapabilityApprovalOverlay` (Blazor natif, `.razor.css` collocalisé,
sans MudBlazor) est rendu par la coque, hors de toute fenêtre d'app : un voile
sombre couvre le bureau et la barre des tâches (`z-index` au-dessus de tout),
capte la souris et le clavier (les raccourcis de la coque ne reçoivent plus les
touches). Fenêtre centrée, non déplaçable, bandeau rouge « Autorisation
administrateur requise » avec bouclier, identité de l'app (nom, identifiant,
éditeur, version, mention « App non système »), capacités avec titre et phrase
de risque, champs nom et mot de passe, boutons **Refuser** et **Autoriser**
(désactivé sans nom ni mot de passe), pied « Cette décision sera inscrite dans
/var/log/auth.log ». Le champ mot de passe prend le focus à l'ouverture.
Les libellés suivent la langue de la session (ADR 0104) : ceux cités ici sont
ceux d'une session française ; en anglais, la langue par défaut, la fenêtre
dit « Administrator authorization required », **Deny** et **Allow**.

`DangerousCapabilityApprovalBroker` montre une demande à la fois. Sans fenêtre
attachée, une demande finit en `Unavailable` (donc `capability.deferred`) ; si
la coque disparaît (déconnexion), les demandes en cours sont fermées sans
décision.

### Mode texte du terminal

Un shell hébergé par `HackShell` (ADR 0056 §7) passe son `ShellHostPrompts` à
l'interpréteur, qui le transmet au lancement de chaque commande. Il
écrit le bandeau d'avertissement et la liste des capacités dans le terminal,
propose Oui (administrateur) / Non (refuser) / Annuler, puis demande le nom et le
mot de passe avec la même boîte que `sudo` (par le `HackCredentialPrompt` du
terminal : `TerminalCredentialPrompt` pour `TerminalPanel`), jusqu'à trois essais (« Désolé,
réessayez. »). Les commandes de l'hôte sont système : seules des commandes
tierces voient ce mode.

### Approbation ponctuelle (ADR 0042) et `block`

Avec `dangerousCapabilities=block`, pour une app non système :

- `LaunchAsync` refuse un ticket `CapabilityApproval` qui vise une capacité
  dangereuse (`ElevationDenied`, `lifecycle.elevation.blocked-by-policy`) ;
- l'instance reçoit un `OneShotElevationGrantSet` où chaque capacité
  dangereuse est bloquée (`Block` / `IsBlocked`) : `Grant` est ignoré et
  `TryConsume` échoue ;
- `WindowAppBase` ne propose plus l'invite d'approbation ponctuelle pour une
  capacité bloquée.

`sudo` n'est pas concerné : c'est un administrateur qui agit lui-même.

### Paramètres > Permissions

Nouvel onglet **Permissions** de l'app Paramètres (`AppPermissionsPanel`,
composant séparé de l'onglet « Installed Apps » de l'ADR 0048). Il liste chaque
app qui déclare une capacité dangereuse et l'état de chaque capacité
(*Système*, *Autorisée*, *Refusée par vous*, *Refusée par l'administrateur*,
*Bloquée par la politique*, *En attente*), et permet :

- de changer `dangerousCapabilities` (prompt / block) ;
- d'autoriser, révoquer (`deny` machine) ou remettre en attente (retrait de la
  ligne) une capacité pour la machine ;
- de retirer son propre refus (sans administrateur).

Chaque action machine ouvre l'invite d'identifiants de l'ADR 0042 ;
`AppPermissionAdministration` vérifie l'administrateur, écrit en contexte
système audité et journalise. Le panneau résout ce service de façon optionnelle :
sur un hôte sans ADR 0049 il affiche un message au lieu d'échouer.

## Journal `/var/log/auth.log`

Toutes les entrées passent par `IAuditLog` (`FileAuditLog`, ADR 0045) ; les mots
de passe ne sont jamais journalisés.

| Action | Quand | Propriétés |
|---|---|---|
| `capability.approve` | succès ou échec d'identifiants (fenêtre, terminal, Paramètres) | `app`, `capabilities`, `requestedBy`, `admin`, `outcome` |
| `capability.refuse` | l'utilisateur refuse | `app`, `capabilities`, `user` |
| `capability.blocked` | politique `block` au lancement | `app`, `capabilities`, `user` |
| `capability.deferred` | pas de session interactive / pas d'invite | `app`, `capabilities` |
| `capability.revoke` | révocation ou remise en attente depuis les Paramètres | `app`, `capability`, `admin`, `decision` |
| `capability.policy` | changement de `dangerousCapabilities` depuis les Paramètres (ajout de ce chantier) | `dangerousCapabilities`, `admin` |

## Composition (hôtes)

`AddHackerOsEcosystem` (Ecosystem, `test/test` et le serveur) enregistre, en
`Scoped` : `IAppPermissionStore`, `DangerousCapabilityApprovalBroker` (aussi
comme `IDangerousCapabilityApprovalPrompt`), `IDangerousCapabilityGate`,
`AppPermissionAdministration`, et passe la passerelle à
`AppLifecycleOrchestrator`. Le paramètre est optionnel : sans passerelle, une
app non système ne détient simplement jamais de capacité dangereuse.

## Décisions

- La passerelle est le **seul** code qui accorde une capacité dangereuse à une
  app non système ; elle aligne aussi les grants à chaque lancement (accord et
  révocation), ce qui rend les fichiers source de vérité sans relire les
  fichiers à la connexion.
- La vérification des identifiants est dans la passerelle, pas dans l'invite :
  une interface (ou une app qui imiterait l'invite) ne peut pas fabriquer une
  approbation.
- Le mode texte du terminal demande les identifiants avec la boîte de `sudo`
  existante, puisque le terminal n'a pas de lecture de ligne pendant
  l'exécution d'une commande.
- L'action `capability.policy` complète la table de la section 8 pour tracer le
  changement de politique fait depuis les Paramètres.

## Tests

- `DangerousCapabilityApprovalTests` (`HackerOs.Platform.Core.Tests`) :
  analyse des fichiers, table de décision, app système sans invite, app non
  système sans décision non accordée et invite appelée au lancement, `allow` +
  `prompt` accordé / + `block` refusé, `deny` utilisateur prioritaire, `allow`
  utilisateur ignoré, trois mots de passe faux (rien d'écrit, trois
  `capability.approve` en échec), écriture machine réussie pour un utilisateur
  non administrateur, refus, `deferred`, approbation non vérifiée rejetée,
  révocation au passage à `block`, `block` qui désactive l'approbation
  ponctuelle mais pas `sudo`, fichiers par défaut, administration des
  Paramètres.
- `DangerousCapabilityApprovalOverlayTests` (`HackerOs.Platform.Blazor.Tests`,
  rendu `HtmlRenderer`, la solution n'utilisant pas bUnit) : capacités et
  risques affichés, nom prérempli, bouton Autoriser désactivé sans mot de
  passe, invite indisponible sans fenêtre, demande fermée quand la coque
  disparaît.

## Tâches

- [x] `security.config` (schéma, défaut livré, lecture de la politique).
- [x] `app-permissions.config` machine et utilisateur (analyse, édition, avertissements).
- [x] Table de décision `DangerousCapabilityEvaluator`.
- [x] `IDangerousCapabilityGate` appelée par `AppLifecycleOrchestrator.LaunchAsync`, grants alignés.
- [x] Fenêtre spéciale `DangerousCapabilityApprovalOverlay` dans les deux coques.
- [x] Mode texte du terminal.
- [x] Journal `auth.log` (`capability.*`).
- [x] Interaction avec l'approbation ponctuelle de l'ADR 0042 sous `block`.
- [x] Vue Permissions des Paramètres.
- [x] Tests de contrat et rendu de la fenêtre.
