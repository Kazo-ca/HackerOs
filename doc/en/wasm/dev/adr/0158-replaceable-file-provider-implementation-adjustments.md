# ADR 0158 : ajustements du fournisseur de fichiers remplaçable

## Statut

Accepté le 2026-10-20.

- **Chantier :** `desk/3c-replaceable-file-provider` (chantier 3c du plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)).
- **Ajuste :** l'ADR 0156 sur des points de mise en œuvre ; ses décisions
  restent vraies.

## Contexte

L'ADR 0156 ouvre `hackeros.file-view` et `hackeros.file-dialogs` à toute app
fenêtrée et fait choisir le fournisseur par un administrateur, dans
`/etc/hackeros/components.config`, avec repli journalisé sur l'explorateur.
Le chantier 3c le met en œuvre. Plusieurs détails n'étaient pas fixés.

## Décision

### 1. Les contrats ouverts gardent une portée à part

`AppComponentProviderScope.SystemApps` devient
`AppComponentProviderScope.AdministratorSelected` (API pas encore publiée,
renommage sans coût). Ces contrats acceptent toute app fenêtrée, mais restent
les contrôles de l'OS : le choix de l'administrateur ne s'applique qu'à eux, et
l'absence de tout fournisseur qui se charge reste l'erreur
`component.unavailable`. `AppManifestValidator.ValidateComponentProviders`,
`BuiltInApplicationsAggregator.FindReservedComponentContracts` et l'avertissement
`catalog.component.reserved` disparaissent. Le code **`HOSAPP0025` est retiré
et ne sera pas réutilisé**.

### 2. Le fichier est lu ligne par ligne, à chaque résolution

Les noms de contrats contiennent des points : `components.config` n'a pas de
schéma déclaré. `ComponentProvidersDocument` le lit avec le lecteur tolérant
des `.config` (ADR 0044 §5) et le réécrit sans toucher aux commentaires
(ADR 0044 §7). Une clé qui n'est pas un contrat choisi par un administrateur
est ignorée ; une app inconnue du catalogue aussi, au moment de résoudre.

Le fichier est relu à chaque résolution d'un contrat (`IComponentProviderSelection`),
pas mis en cache : un contrôle n'est résolu qu'à son affichage, et une
modification faite avec `sudo nano` s'applique ainsi au contrôle suivant, sans
surveillance de fichier. Une installation neuve n'a pas de fichier ; le premier
choix l'écrit avec un en-tête commenté. Revenir à l'explorateur **retire la
ligne** : choisir l'explorateur ne l'écrit jamais.

### 3. Une passerelle du SDK, `HackComponentProviders`

Paramètres utilise le SDK public seulement (ADR 0055). Le SDK gagne
`HackComponentProviders` (`GetOverviewAsync`, `SetProviderAsync`) et ses
contrats (`IAppComponentProviderAdministrationGateway`,
`HackComponentContractInfo`, `HackComponentProviderInfo`), sur le modèle de
`HackPermissionAdministration`. La capacité exigée est **`apps.manage`**, que
Paramètres déclare déjà : choisir le fournisseur est de l'administration des
apps, pas besoin d'une capacité de plus.

`SetProviderAsync` prend **une liste de contrats et une app** : quand l'app
fournit les deux contrats, Paramètres propose de régler les deux ensemble
(case cochée par défaut), et une seule approbation couvre les deux.

### 4. L'approbation réutilise la fenêtre des capacités dangereuses

Pour une app non système, l'OS montre la fenêtre d'approbation de l'ADR 0049
(`IDangerousCapabilityApprovalPrompt`) : l'app candidate, et une ligne par
contrat avec son titre et sa phrase de risque (ressources
`Capability.hackeros.file-view.*` et `Capability.hackeros.file-dialogs.*` du
cœur). Seule une tentative d'un administrateur vérifiée écrit le fichier ;
« Refuser » ou la fermeture n'écrivent rien et ne mémorisent rien (pas de
refus retenu comme pour une capacité). Pour une app système ou le retour à
l'explorateur, l'agent d'authentification (ADR 0055 §5) demande seulement un
administrateur.

Chaque décision est écrite dans `/var/log/auth.log` avec l'action
**`component.provider`** (sujet `/etc/hackeros/components.config`, propriétés
`contracts`, `provider` — `default` pour le retour à l'explorateur — et
`user`). Les échecs de mot de passe et le refus y sont aussi, en `Denied`.

### 5. Le repli est journalisé une fois

`HackComponentHost` met le fournisseur choisi en tête. S'il est désactivé pour
l'utilisateur, ne se charge pas, ou n'est pas un composant intégrable, le
suivant (l'explorateur) prend le relais et `component.fallback` est écrit en
avertissement dans `/var/log/syslog` (source `components`), **une fois par
contrat, app choisie et fournisseur utilisé** pour la durée de la session :
chaque contrôle affiché répéterait sinon la même ligne.

### 6. Seule une désinstallation pour la machine retire la ligne

L'ADR 0156 §4 dit qu'une désinstallation retire la ligne. Le fichier vaut pour
toute la machine : seule une désinstallation **pour la machine**
(`UninstalledForMachine`) la retire, écrite par l'OS comme opération système
auditée, avec `component.provider.removed` dans `/var/log/syslog`. Une app
désinstallée pour un seul utilisateur garde sa ligne : cet utilisateur a le
repli de §5, les autres gardent le choix. Le retrait suit tout changement de
l'état installé (Paramètres, `apps.config` modifié à la main).

### 7. Paramètres : onglet « Composants de l'OS »

Paramètres n'a pas de groupe « Applications » : la page est un onglet à part,
« Composants de l'OS », placé après « Applications installées ». Il montre,
pour chaque contrat, les apps qui le fournissent (nom, identifiant, app
système ou non, par défaut, non installée), celle en service, et le bouton
« Revenir à l'explorateur de fichiers » quand un choix existe. L'index de
l'onglet « Application » (ADR 0096 §4) est corrigé au passage : il n'avait pas
suivi l'ajout de l'onglet Mémoire.

## Conséquences

- Nouvelles API non livrées (`PublicAPI.Unshipped.txt`) dans `AppSdk`
  (`HackComponentProviders`, passerelle et enregistrements,
  `IAppExecutionContext.ComponentProviderAdministration`), `Platform.Core`
  (`ComponentProviderService`, `ComponentProvidersDocument`,
  `IComponentProviderSelection`) et `Platform.Blazor`
  (`HackComponentHost.Selection`, `ReportWarning`) ; elles seront publiées avec
  le SDK 2.0 (chantier 6).
- Tests : service (fichier, validation, approbation, refus, retour à
  l'explorateur, désinstallation), passerelle (capacité, fenêtre d'approbation
  plutôt que mot de passe, refus), hôte (choix en tête, repli journalisé une
  fois, app désactivée, contrat ouvert non concerné, `component.unavailable`),
  catalogue, validateur et agrégation.

## Références

- ADR 0044, 0048, 0049, 0055, 0063, 0096, 0148, 0153, 0156.
- [`embedded-components.md`](../../framework/embedded-components.md),
  [`settings-files.md`](../../framework/settings-files.md).
