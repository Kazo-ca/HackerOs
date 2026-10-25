# ADR 0107 : ajustements de la mise en œuvre des états et du démarrage des services

## Statut

Accepté le 2026-10-17 (chantier `v3/5-services`).

- **Ajuste :** [ADR 0097](0097-service-health-and-startup.md) §1, §4 et
  « Conséquences » (l'ADR 0097 n'est pas modifié).
- **Questions posées à Benoit** dans le fil du chantier, avec choix et
  implications : le compte `hackeros-svc` (§2) et le démarrage de `hackwebd`
  (§3). Le code suit l'option recommandée en attendant sa réponse ; une autre
  réponse se fera dans un chantier de suite.

## Contexte

En écrivant `v3/5`, quatre points de l'ADR 0097 ne correspondaient pas au code :

- `ServiceAppBase` n'avait pas de `OnStartAsync` : il n'y avait donc pas de
  moment « le démarrage est fini » pour passer de `activating` à `active`.
- L'OS n'a pas de fichier `/etc/passwd` : les comptes sont dans le magasin
  d'utilisateurs IndexedDB, et l'écran de connexion liste ce magasin.
- `hackwebd` lit `~/.config/hackweb/sites` de l'utilisateur connecté
  (ADR 0077, 0080) : démarré au boot sous `hackeros-svc`, il ne trouverait plus
  les sites de l'utilisateur.
- `AppLifecycleOrchestrator.StartAllServicesAsync` existait mais rien ne
  l'appelait ; rien ne démarrait les services au boot ni ne les arrêtait à la
  déconnexion.

## Décision

### 1. `OnStartAsync` et notification « prêt »

- `ServiceAppBase` gagne `protected virtual Task OnStartAsync(context, token)`
  (vide par défaut). `RunAsync` l'attend, envoie la notification « prêt »
  (`IAppExecutionContext.ServiceNotify`, l'équivalent de `sd_notify("READY=1")`),
  puis appelle `RunCoreAsync`. Un service existant, qui n'a que
  `RunCoreAsync`, devient `active` tout de suite.
- `ReportHealth(health, status)` passe par le même canal. Dans un worker
  (ADR 0070), la notification traverse le protocole (`svc.notify`) ; l'état
  (`svc.status`) et `reset-failed` (`svc.resetfailed`) aussi.
- Le délai de démarrage (30 s, `AppLifecycleOrchestrator.ServiceStartTimeout`)
  se compte en temps simulé quand l'OS a une horloge : une simulation en pause
  ne fait pas échouer un démarrage.
- Un service dont le jeton est annulé sous lui (racine de session à la
  déconnexion, portée système à l'extinction) termine son processus au lieu de
  rester `active` sans code qui tourne.

### 2. Compte `hackeros-svc` intégré (option recommandée, question ouverte)

- `ServiceAccount` (Platform Core) définit un compte fixe : identifiant stable
  (`00000000-0000-4000-8000-0000000003e7`), groupe propre, maison
  `/var/lib/hackeros-svc`, shell `/usr/sbin/nologin`. Il n'est pas dans le
  magasin d'utilisateurs : il n'apparaît pas à l'écran de connexion, aucune
  migration IndexedDB, et la création du premier administrateur refuse ce nom.
- Il n'y a pas de `/etc/passwd`. La phrase « ajouté à `/etc/passwd` … par une
  migration » de l'ADR 0097 ne s'applique pas.
- Les processus d'un service système appartiennent à une session système fixe
  (`ServiceAccount.SystemSessionId`) et à une **portée système** du gestionnaire
  de processus (`ProcessStartRequest.SystemScope`) : la déconnexion n'annule pas
  leur jeton, l'extinction oui (`InMemoryProcessManager.CancelSystemScope`).

### 3. `hackwebd` reste un service de connexion (option recommandée, question ouverte)

- `hackwebd` ne déclare pas `ServiceStartup = Boot` : il démarre à la connexion
  sous l'utilisateur, comme avant, et sert les sites de cet utilisateur. Le
  passer en `Boot` demande de déplacer ses sites hors de `~` (par exemple
  `/srv/hackweb`) ou une instance par utilisateur ; c'est la question posée à
  Benoit.

### 4. Qui démarre quoi, et quand

- `ServiceStartupCoordinator` (Platform Core) joue le rôle de
  `multi-user.target` et de `systemd --user` :
  - fin du boot (`EcosystemBootCoordinator.BootAsync`, après l'application de
    `/etc/hackeros/apps.config`) : `StartBootServicesAsync` sous `hackeros-svc` ;
  - après chaque connexion (`SessionActivatedEvent`) : le `apps.config` de
    l'utilisateur est appliqué, puis `StartAllServicesAsync` lance ses services
    `Automatic` ;
  - déconnexion (`SessionLoggedOutEvent`) : `StopSessionServicesAsync` arrête
    les services de cette session ; les services système continuent ;
  - extinction (`SessionShutDownEvent`) : `StopAllAsync(Shutdown)`, puis
    annulation de la portée système.
  Un échec est écrit dans `/var/log/syslog` et ne bloque ni le boot ni la
  connexion.
- Démarrer, arrêter ou changer le mode d'un service système demande un
  administrateur (`service.system.administrator-required`), comme
  `sudo systemctl` pour une unité système. Démarré à la main, il tourne quand
  même sous `hackeros-svc`, dans la portée système. Lire son état reste libre.

### 5. Rapport de désinstallation

- `AppInstallationService.UninstallAsync` relève les processus actifs avant de
  modifier `apps.config` : la surveillance du fichier peut appliquer le
  changement (et arrêter les processus) avant le rechargement qui suit, et le
  rapport doit quand même les lister. Côté SDK :
  `HackAppManagement.PlanUninstallAsync` et `UninstallWithReportAsync`.

## Conséquences

- Champ de manifeste `serviceStartup` (`login` | `boot`, absent = `login`) dans
  le schéma, le sérialiseur (liste blanche) et le validateur
  (`manifest.serviceStartup.forbidden`, `manifest.serviceStartup.invalid`).
- Si Benoit choisit une autre option au §2 ou au §3, un nouvel ADR la remplace
  et un chantier de suite la réalise.
