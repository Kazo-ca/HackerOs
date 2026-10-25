# ADR 0097 : état et santé des services, démarrage au boot ou à la connexion, désinstallation d'une app active

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour le chantier `v3/5-services`).
Le §4 suit la réponse de Benoit du 2026-10-17 : seules les apps système
démarrent au boot.

- **Complète :** ADR 0056 (`HackServiceController`), ADR 0064 (`systemctl`),
  ADR 0069 (arrêt demandé), ADR 0048 (désinstallation), ADR 0049 §5.4
  (démarrage automatique et capacités dangereuses).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  ligne `v3/5-services` ; manques R9 et R23.

## Contexte

- `ServiceHealth` (`Unknown`, `Healthy`, `Degraded`, `Unhealthy`,
  `Shared/HackerOs.Simulation.Abstractions/Processes/ProcessContracts.cs:115`)
  vaut toujours `Unknown` : `InMemoryProcessManager` le pose à la création et
  rien ne le change. Rien ne l'affiche.
- `HackServiceControllerStatus` (`Stopped`, `StartPending`, `StopPending`,
  `Running`) n'a pas d'état d'échec : un service qui plante redevient
  `Stopped`, et `systemctl status` affiche `inactive (dead)`.
- Le seul réglage de démarrage est `AutoStart` (préréglage du paquet) et
  `ServiceStartModeStore` (`~/.config/<appId>/service.conf`, mode effectif).
  `AppLifecycleOrchestrator.StartAllServicesAsync` lance les services
  `Automatic`, mais **aucun code de production ne l'appelle** (seulement des
  tests) : aujourd'hui, aucun service ne démarre tout seul, sauf les apps web
  lancées par `WebAppActivator`.
- `AppInstallationService.UninstallAsync` arrête en silence les processus des
  apps désinstallées (`AppLifecycleOrchestrator.DisableAsync`) et le résultat ne
  les liste pas.

## Décision

### 1. États d'un service, façon systemd

- Un service a un **état actif** : `inactive`, `activating`, `active`,
  `deactivating`, `failed`, comme `ActiveState` de systemd. Il est porté par
  `HackServiceController.ActiveState` (nouveau type `HackServiceActiveState`).
  `Status` (calqué sur `System.ServiceProcess`) reste et se déduit de l'état
  actif ; `failed` donne `Stopped`.
- `activating` dure du lancement du processus jusqu'au retour de
  `OnStartAsync` du service ; `active` ensuite.
- Un service passe à `failed` quand son processus se termine sans qu'on le lui
  ait demandé : exception non gérée, code de sortie non nul, arrêt forcé après
  l'escalade (ADR 0069). `HackServiceController.Result` donne la raison, avec les
  mots de systemd : `exit-code`, `exception`, `killed`, `timeout` (démarrage plus
  long que 30 s), `success` sinon. L'état `failed` reste jusqu'au prochain
  démarrage ou jusqu'à `systemctl reset-failed`.
- Pas de redémarrage automatique (`Restart=`) dans ce chantier.

### 2. Santé rapportée par le service

- `ServiceApp` reçoit `ReportHealth(HackServiceHealth health, string? status)`,
  l'équivalent de `sd_notify("STATUS=…")` : un service actif mais dégradé (par
  exemple un site qui ne joint plus sa base) le dit sans s'arrêter.
- Santé par défaut : `Healthy` quand `OnStartAsync` revient, `Unknown` avant.
  `ProcessRecord.Health` porte la valeur, et le protocole des workers la
  transporte déjà.
- `HackServiceController.Health` et `.StatusText` l'exposent.

### 3. Affichage

- `systemctl status` affiche `Active: failed (Result: exit-code) since …`,
  `activating (start)`, `active (running)` et la ligne `Status: "…"` quand le
  service a donné un texte. `systemctl is-failed` et `systemctl reset-failed`
  sont ajoutés ; `is-active` répond `failed` avec le code 3, comme systemd.
- L'onglet Services du gestionnaire de tâches affiche l'état actif, la santé et
  le texte.
- Un passage à `failed` est écrit dans `/var/log/syslog` (« `<unit>`: Failed
  with result 'exit-code'. »).

### 4. Démarrage au boot ou à la connexion

- Nouveau champ du manifeste `ServiceStartup` (`AppDefinition.ServiceStartup`) :
  `Login` (par défaut) ou `Boot`. Il ne vaut que pour une app de genre
  `Service` ; le validateur le refuse ailleurs.
- **Services de session (`Login`)** : démarrés après la connexion, sous
  l'utilisateur connecté, arrêtés à la déconnexion. C'est le rôle de
  `StartAllServicesAsync`, que le chantier branche enfin dans la séquence de
  connexion de l'OS.
- **Services système (`Boot`)** : démarrés quand l'OS a monté son système de
  fichiers, avant l'écran de connexion, et arrêtés seulement à l'extinction.
  Seule une **app système** (ADR 0048, `IsSystemApp` sur la référence de
  l'hôte) peut demander `Boot` : le catalogue ignore `Boot` pour une autre app
  et la démarre à la connexion, avec un avertissement dans `/var/log/syslog`.
  Un service système tourne sous un compte de service sans connexion
  (`hackeros-svc` par défaut, créé par l'OS ; `hackwebd` garde `www-data`,
  ADR 0080), jamais sous un utilisateur humain. `hackwebd` passe en `Boot`,
  comme un vrai serveur web.
- Le mode effectif d'un service système se règle dans
  `/etc/<appId>/service.conf` (administrateur), celui d'un service de session
  dans `~/.config/<appId>/service.conf` (inchangé).

### 5. Désinstaller une app dont un processus tourne

- `AppInstallationService` gagne `PlanUninstallAsync(appId)`, qui renvoie, sans
  rien changer, l'ensemble des apps qui seront désinstallées (dépendantes
  comprises) et **les processus actifs** de cet ensemble (PID, nom, utilisateur).
- Paramètres affiche cette liste dans la confirmation (« Ces processus vont
  être arrêtés : … ») avant de désinstaller. (Il n'existe pas de commande de
  désinstallation ; si une est ajoutée plus tard, elle fera de même.)
- Les processus sont arrêtés par l'arrêt demandé de l'ADR 0069, puis forcés
  après le délai ; `AppInstallationResult` liste ceux qui ont été arrêtés et
  ceux qui ont dû être forcés.

## Conséquences

- `v3/5` modifie `HackServiceController`, `ServiceApp`, `systemctl`, l'onglet
  Services, `AppInstallationService`, le validateur du manifeste et la séquence
  de démarrage de l'OS, avec des tests pour chaque transition d'état.
- `sdk-reference.md`, `app-model.md`, `linux-commands.md`, `task-manager.md`
  et `app-installation.md` sont mis à jour.
- Le compte `hackeros-svc` est ajouté à `/etc/passwd` à la création de l'OS et
  par une migration pour les OS existants.
