# ADR 0049 : Capacités dangereuses et approbation par un administrateur

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 2 — Modèle d'app ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 6.4, décisions D23 à D25).
- **Complète :** ADR 0002 (le statut système, venu de la politique de confiance,
  décide désormais aussi des capacités dangereuses) et ADR 0003 (chaque capacité
  exacte du catalogue reçoit un classement).
- **Complète :** ADR 0042. L'approbation ponctuelle d'une opération refusée et
  `sudo` restent en place ; cet ADR ajoute une approbation **durable** des
  capacités dangereuses, demandée au lancement.
- **Tranche** la question laissée ouverte par les ADR 0044 et 0045 : les
  capacités `settings.*` et `diagnostics.*` sont retirées du catalogue.

## Contexte

`CleanProfileCapabilityGrantSeeder` accorde à la connexion toutes les capacités
déclarées par chaque manifeste (`LocalSessionService`, source `BuildProfile`), sans
rien demander. Une app peut donc déclarer `process.manage` ou
`network.real.access` et les obtenir en silence. Le dépôt de grants
(`CapabilityGrantRepository`) est en mémoire et reconstruit à chaque connexion.
`CapabilityGrantSource.AdministratorApproval` existe mais n'est produit par aucun
code. `/etc/hackeros/policy.config` déclare `capabilityGrantsLocked`, qui n'est lu
nulle part.

## Décision

### 1. Classement des capacités (D23)

`AppCapabilities` gagne un classement exact par identifiant, dans le même fichier
(`Shared/HackerOs.App.Abstractions/AppCapabilities.cs`) :

```csharp
public enum CapabilityRisk { Normal, Dangerous }

public static CapabilityRisk GetRisk(string capability);      // ArgumentException si inconnue
public static bool IsDangerous(string capability);
public static CapabilityDescription Describe(string capability); // titre et risque, pour l'interface
```

| Capacité | Classement | Risque affiché à l'administrateur |
|---|---|---|
| `filesystem.private.read`, `filesystem.private.write` | normale | — |
| `filesystem.user-selected.read`, `filesystem.user-selected.write` | normale | — |
| `filesystem.user-home.read`, `filesystem.user-home.write` | normale | — |
| `filesystem.system.read` | **dangereuse** | Lire des fichiers hors du dossier personnel, dont la configuration du système. |
| `filesystem.system.write` | **dangereuse** | Modifier des fichiers du système, dont `/etc` et les paramètres de toutes les apps. |
| `apps.launch` | normale | — |
| `apps.manage` (**nouvelle**) | **dangereuse** | Installer, désinstaller ou réinstaller des applications. |
| `dialogs.file-open`, `dialogs.file-save`, `dialogs.folder-select` | normale | — |
| `file-associations.read` | normale | — |
| `file-associations.write` | **dangereuse** | Choisir quelle app ouvre chaque type de fichier pour toute la machine. |
| `process.list` | normale | — |
| `process.manage` | **dangereuse** | Arrêter les processus d'autres applications. |
| `services.manage` | **dangereuse** | Démarrer, arrêter et configurer les services du système. |
| `windows.manage` | **dangereuse** | Lire, déplacer ou fermer les fenêtres d'autres applications. |
| `notifications.post` | normale | — |
| `clipboard.read`, `clipboard.write` | normale | — |
| `network.simulated.read`, `network.simulated.write` | normale | — |
| `network.real.access` | **dangereuse** | Joindre de vrais serveurs sur Internet par le serveur relais. |
| `gameplay.domain.access` | normale | — |

- Toute nouvelle capacité doit recevoir un classement : un test parcourt le
  catalogue et échoue si une capacité n'en a pas.
- **Retirées du catalogue :** `settings.read`, `settings.write`,
  `settings.system.read`, `settings.system.write` (non évaluées depuis
  l'ADR 0044 ; les paramètres sont des fichiers, couverts par `filesystem.*`) et
  `diagnostics.read`, `diagnostics.clear` (non évaluées depuis l'ADR 0045). Une
  définition d'app qui les cite ne compile plus ; un ancien grant en mémoire qui
  les nomme est simplement ignoré.
- `apps.manage` est ajoutée maintenant pour que le classement soit complet. Elle
  protégera l'API publique de gestion des apps en phase 4 (ADR 0048, section 4).

### 2. Qui reçoit quoi

| App | Capacité normale déclarée | Capacité dangereuse déclarée |
|---|---|---|
| Système (ADR 0048) | accordée d'office, source `BuildProfile` | accordée d'office, source `SystemPolicy` |
| Non système | accordée d'office, source `BuildProfile` | accordée **seulement** si une décision « allow » existe et que la politique le permet (sections 3 et 4), source `AdministratorApproval` |

Une capacité non déclarée n'est jamais accordée, quel que soit le cas (ADR 0002).

`CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities` reçoit le statut
système de l'app et les décisions lues (section 4), et applique ce tableau.

### 3. Politique de sécurité (D25)

Fichier `/etc/hackeros/security.config`, document système seulement
(`OsSettingsDocument` avec `SystemOnly: true`, comme `policy.config`),
`system:system 0644` :

```text
# /etc/hackeros/security.config
# prompt : une app non système qui demande une capacité dangereuse ouvre la
#          fenêtre d'approbation administrateur (défaut).
# block  : les capacités dangereuses ne sont jamais accordées aux apps non système.
dangerousCapabilities=prompt
```

- Clé `dangerousCapabilities`, type `Enum` (`prompt`, `block`), défaut `prompt`.
  Une valeur invalide est ignorée avec un avertissement (ADR 0044), donc `prompt`
  s'applique.
- Avec `block`, les décisions « allow » existantes sont ignorées tant que la
  politique reste à `block`, et l'approbation ponctuelle de l'ADR 0042 n'est plus
  proposée pour une capacité dangereuse d'une app non système. `sudo` n'est pas
  concerné : c'est un administrateur qui agit lui-même.
- La clé `capabilityGrantsLocked` de `policy.config`, jamais évaluée, est retirée
  du schéma.

### 4. Décisions mémorisées

Fichier machine `/etc/hackeros/app-permissions.config` (`system:system 0644`) et
fichier utilisateur `~/.config/hackeros/app-permissions.config` :

```text
# /etc/hackeros/app-permissions.config
[permissions]
org.example.scanner:network.real.access=allow
org.example.scanner:filesystem.system.read=deny
```

- Clé `<appId>:<capacité>`, valeur `allow` ou `deny`. Lu par
  `ConfigDocumentFormat.Parse` sans schéma ; une ligne invalide est ignorée avec
  un avertissement dans `/var/log/syslog`. Une ligne qui vise une app système ou
  une capacité normale est ignorée sans effet.
- **Le fichier machine est la seule source d'un « allow ».** Il est écrit par l'OS
  après une approbation administrateur (section 5), ou par un administrateur avec
  `sudo nano`.
- Le fichier utilisateur ne contient que des `deny` : le refus d'un utilisateur
  vaut pour lui seul et évite de lui redemander à chaque lancement. Un `allow`
  dans ce fichier est ignoré avec un avertissement.

**Capacité dangereuse accordée** à une app non système =
politique `prompt` **et** `allow` dans le fichier machine **et** pas de `deny`
dans le fichier utilisateur.

Une capacité dangereuse est **en attente** si le fichier machine n'a pas de
décision et que l'utilisateur ne l'a pas refusée. Une mise à jour d'app qui
ajoute une capacité dangereuse crée donc une nouvelle demande, pour cette seule
capacité.

### 5. Fenêtre d'approbation au lancement (D23, D24)

Au lancement d'une app non système qui a au moins une capacité dangereuse **en
attente**, `AppLifecycleOrchestrator.LaunchAsync` appelle, avant de créer le
processus, `IDangerousCapabilityGate.EnsureDecidedAsync(manifest, principal)`
(`Platform/HackerOs.Platform.Core/Policy/`) :

1. **Politique `block` :** pas de fenêtre. L'app démarre sans ses capacités
   dangereuses. Entrée `capability.blocked` dans `/var/log/auth.log`.
2. **Politique `prompt` :** la passerelle appelle
   `IDangerousCapabilityApprovalPrompt`, qui a deux implémentations :
   - **fenêtre spéciale** pour les apps fenêtre et service lancées depuis le
     bureau (section 6) ;
   - **mode texte** dans `TerminalPanel` pour une commande lancée depuis le
     terminal : bandeau d'avertissement, liste des capacités, puis demande du
     nom et du mot de passe administrateur, comme `sudo`.
3. **Résultat :**
   - *Autoriser* : un administrateur activé saisit son nom et son mot de passe,
     vérifiés par `IAdminElevationVerifier` (ADR 0042). Il peut s'agir d'un autre
     compte que l'utilisateur courant, comme pour `sudo`. Même un utilisateur
     administrateur doit saisir son mot de passe (D24). Trois échecs annulent la
     demande. En cas de succès, l'OS écrit les lignes `allow` dans le fichier
     machine **avec un contexte système audité** (ADR 0002), puis accorde les
     grants (source `AdministratorApproval`) avant le démarrage du processus.
   - *Refuser* : lignes `deny` dans le fichier de l'utilisateur. L'app démarre sans
     ces capacités.
   - *Fermer* sans choisir : rien n'est écrit ; l'app démarre sans ces capacités et
     la question reviendra au prochain lancement.
4. **Sans session interactive** (service démarré au boot avant toute connexion) :
   pas de fenêtre, capacités non accordées, entrée `capability.deferred` dans
   `auth.log`. L'approbation se fait ensuite depuis les Paramètres (section 7).

Une app démarrée sans une capacité dangereuse reçoit
`HackUnauthorizedAccessException` (ADR 0043) quand elle l'utilise, comme pour
toute capacité absente.

### 6. La fenêtre spéciale

Composant natif Blazor `Platform/HackerOs.Platform.Blazor/Security/DangerousCapabilityApprovalOverlay.razor`,
avec `.razor.css` collocalisé ; pas de MudBlazor, puisqu'il fait partie du
chrome de l'OS. Il imite le « bureau sécurisé » de l'UAC de Windows :

- voile sombre sur **tout** le bureau, au-dessus de toutes les fenêtres et de la
  barre des tâches ; il capte le clavier et la souris tant qu'il est ouvert ;
- fenêtre centrée, non déplaçable, sans bouton de réduction, visuellement
  distincte des boîtes de dialogue ordinaires : bandeau rouge « Autorisation
  administrateur requise », bordure d'alerte, icône de bouclier ;
- nom, icône, identifiant, éditeur et version de l'app, avec la mention « App non
  système » ;
- la liste des capacités demandées, chacune avec son titre et la phrase de risque
  de `AppCapabilities.Describe` ;
- champ nom d'administrateur (prérempli avec l'utilisateur courant s'il est
  administrateur) et champ mot de passe ;
- boutons **Autoriser** (style danger) et **Refuser** ; Échap équivaut à fermer ;
- en pied : « Cette décision sera inscrite dans /var/log/auth.log ».

Il est rendu par la coque de la plateforme, hors de toute fenêtre d'app : une app
ne peut pas dessiner par-dessus ni l'imiter dans sa propre fenêtre.

### 7. Paramètres > Applications > Permissions

Dans l'app Paramètres, une vue par app (composant à part, pour ne pas entrer en
conflit avec l'onglet de l'ADR 0048) affiche chaque capacité dangereuse déclarée
avec son état : *système*, *autorisée*, *refusée par vous*, *refusée par
l'administrateur*, *bloquée par la politique*, *en attente*.

- Autoriser ou révoquer pour la machine écrit dans
  `/etc/hackeros/app-permissions.config`, donc passe par l'invite d'approbation
  de l'ADR 0042 ; l'action est journalisée comme en section 8.
- Un utilisateur peut retirer son propre `deny`.
- Un administrateur change `dangerousCapabilities` depuis la même page.
- Un changement prend effet au prochain lancement de l'app : les capacités d'une
  instance en cours sont figées à son lancement (ADR 0042).

### 8. Journal `/var/log/auth.log`

Chaque décision passe par `IAuditLog` (`FileAuditLog`, ADR 0045) :

| Action | Quand | Propriétés |
|---|---|---|
| `capability.approve` | succès ou échec de mot de passe dans la fenêtre ou le terminal | `app`, `capabilities`, `requestedBy`, `admin`, `outcome` |
| `capability.refuse` | l'utilisateur refuse | `app`, `capabilities`, `user` |
| `capability.blocked` | politique `block` | `app`, `capabilities`, `user` |
| `capability.deferred` | pas de session interactive | `app`, `capabilities` |
| `capability.revoke` | révocation depuis les Paramètres | `app`, `capability`, `admin` |

Les mots de passe ne sont jamais journalisés ; le rédacteur existant
s'applique.

### 9. Tests de contrat

- Chaque capacité du catalogue a un classement ; `settings.*` et `diagnostics.*`
  n'existent plus.
- Une app système reçoit ses capacités dangereuses sans fenêtre.
- Une app non système sans décision ne reçoit pas ses capacités dangereuses, et
  la passerelle est appelée au lancement.
- `allow` machine + `prompt` = accordée ; + `block` = refusée ; `deny`
  utilisateur l'emporte sur `allow` machine ; `allow` dans le fichier utilisateur
  ignoré.
- Mot de passe faux trois fois : rien d'écrit, trois entrées `capability.approve`
  en échec.
- L'écriture du fichier machine après approbation réussit même si l'utilisateur
  courant n'est pas administrateur.
- `block` désactive l'approbation ponctuelle de l'ADR 0042 pour une capacité
  dangereuse d'une app non système, mais pas `sudo`.
- Rendu bUnit de la fenêtre : capacités et risques affichés, bouton Autoriser
  désactivé sans mot de passe.

## Conséquences

- Une app tierce ne peut plus obtenir en silence le droit de tuer des processus,
  de modifier `/etc` ou de joindre Internet.
- Les apps système ne voient aucune fenêtre, et aucune app non système de l'hôte
  Ecosystem ne déclare aujourd'hui de capacité dangereuse : l'expérience de base
  ne change pas.
- `cat /etc/hackeros/app-permissions.config` montre ce qu'un administrateur a
  approuvé, comme `apps.config` montre ce qu'il a désinstallé.
- Une décision vaut pour la machine entière ; il n'y a pas d'approbation par
  utilisateur. Un utilisateur peut seulement refuser pour lui.
- Les commandes du terminal fournies par l'hôte sont système et ne déclenchent
  jamais l'invite ; seules des commandes tierces la verront.
- Les apps qui citent `settings.*` ou `diagnostics.*` doivent retirer ces
  capacités dans le même changement que leur suppression du catalogue.

## Chantiers d'implémentation

| Branche | Contenu | Dépend de |
|---|---|---|
| `rebuild/2-capability-classification` | `CapabilityRisk`, `GetRisk`, `IsDangerous`, `Describe`, `apps.manage`, retrait de `settings.*` et `diagnostics.*` (catalogue, définitions d'app, tests), seeder selon la section 2 sans fenêtre (une capacité dangereuse d'une app non système n'est pas accordée), retrait de `capabilityGrantsLocked`. | `2-app-migration` (ADR 0047), `2-system-apps` (ADR 0048) |
| `rebuild/2-dangerous-approval` | `security.config`, fichiers `app-permissions.config`, `IDangerousCapabilityGate` dans le lancement, fenêtre spéciale, mode texte du terminal, journal `auth.log`, interaction avec l'approbation ponctuelle, vue Permissions des Paramètres. | `2-capability-classification` |

## Références

- ADR 0002 et ADR 0003 (complétés), ADR 0042 (complété), ADR 0043 (exceptions),
  ADR 0044 (paramètres en fichiers), ADR 0045 (`auth.log`), ADR 0048 (apps
  système).
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 6.4.
- `Platform/HackerOs.Platform.Core/Policy/CleanProfileCapabilityGrantSeeder.cs`,
  `Shared/HackerOs.App.Abstractions/AppCapabilities.cs`.
