# ADR 0080 : ajustements des sites statiques hébergés

## Statut

Accepté le 2026-10-16, avec le chantier `web/5-static-sites`.

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Complète :** [ADR 0077](0077-hosted-static-sites.md). Les sections 1 et 2
  ci-dessous remplacent deux puces de l'ADR 0077 §2 et sa conséquence sur le
  compte `www-data` ; le reste de l'ADR 0077 (fichiers `.site`, deux
  emplacements, Web Server Manager, sites externes) est appliqué tel quel.
- **Complète aussi :** [ADR 0045](0045-logs-as-files.md) (journaux des démons)
  et [ADR 0074](0074-listening-ports-and-name-resolution.md) §4 (source des
  sites hébergés du résolveur).
- **Documentation :** [`static-sites.md`](../static-sites.md).

## Contexte

La mise en œuvre de l'ADR 0077 a rencontré quatre points que l'ADR ne
tranchait pas, ou qu'il tranchait d'une façon que la plateforme actuelle ne
permet pas.

## Décision

1. **`hackwebd` tourne sur le fil principal, pas dans un worker dédié.** Un
   Web Worker n'offre pas encore la surveillance de fichiers
   (`HackFileSystemWatcher` : la passerelle distante ne la porte pas, ADR 0072
   §3), dont dépend le rechargement à chaud de l'ADR 0077 §2. Le manifeste
   déclare donc `Execution = MainThread`. Servir un fichier ne fait que lire le
   système de fichiers par le SDK : le coût sur le fil principal est celui de
   l'explorateur. Il passera dans un worker quand les workers sauront
   surveiller un dossier, sans changer son code.
2. **`www-data` est simulé, pas un compte de l'OS.** L'OS n'a pas de comptes
   de service et en ajouter un toucherait l'authentification, `/etc/passwd`
   et les sessions, hors du sujet de ce chantier. `hackwebd` tourne avec
   l'utilisateur de la session et applique lui-même les droits de `www-data`,
   qui ne possède aucun fichier et n'est dans aucun groupe :
   - un **site système** (`/etc/hackweb/sites`) n'est servi que si chaque
     dossier, de `/` jusqu'au fichier, a le bit `o+x`, et le fichier `o+r`
     (un dossier listé : `o+r` aussi) ; sinon `403`, comme Apache sur un
     dossier personnel en `0700` ;
   - un **site d'utilisateur** (`~/.config/hackweb/sites`) est servi avec les
     droits de son propriétaire, comme un `python -m http.server` qu'il aurait
     lancé.
   Le Manager prévient quand `www-data` ne pourra pas lire la racine choisie.
3. **Journaux des démons par `IAppLoggingGateway.AppendToLogFile`.** L'ADR
   0045 réserve l'écriture dans `/var/log` au rédacteur de la plateforme ; une
   app n'y écrit pas elle-même. Pour garder cette règle et donner à
   `hackwebd` ses `/var/log/hackweb/<site>.access.log` et `error.log`, la
   passerelle de journalisation gagne `AppendToLogFile(relativePath, line)` :
   - réservé aux **apps système** (ADR 0048) ; une autre app reçoit
     `UnauthorizedAccessException` ;
   - chemin relatif en deux segments exactement, `<dossier>/<fichier>.log`,
     jamais sous `apps/` (réservé à l'ADR 0045) ;
   - la ligne est écrite telle quelle (format du démon, par exemple
     « combined » d'Apache) par `LogFileWriter`, avec la même rotation à
     1 Mio × 5 et le mode `system:system 0644` ; un saut de ligne dans la ligne
     devient `\n` pour qu'aucune entrée ne puisse en forger une autre ;
   - une app en Web Worker l'appelle par l'opération `log.file` du protocole
     des workers.
4. **La source des sites hébergés du résolveur devient asynchrone.**
   `IHostedSiteNameSource.FindSite(name)` devient `FindSiteAsync(name,
   cancellationToken)`, parce que la réponse vient des fichiers `.site`. La
   plateforme l'implémente par `HostedSiteNameSource` : elle lit les deux
   emplacements avec les droits de `root` (le résolveur est l'OS), garde les
   sites valides et activés, et publie leurs noms qui ne sont ni `localhost`,
   ni une adresse IP, ni un joker. Un site système l'emporte sur un site
   d'utilisateur du même nom. La lecture est mise en cache par révision des
   fichiers.

Hors ADR 0077, deux limites sont posées :

- une liaison `https://` est refusée à la validation : il n'y a pas encore de
  certificats simulés ;
- seuls `GET`, `HEAD` et `OPTIONS` sont servis (`405` avec `Allow` sinon) ; pas
  de requêtes partielles (`Accept-Ranges: none`).

## Conséquences

- Un démon système peut avoir ses propres journaux sous `/var/log` sans qu'une
  app écrive dans ce dossier ; les apps ordinaires gardent seulement
  `/var/log/apps/<appId>.log`.
- La visionneuse de journaux (`Error Log Viewer`) ne liste pas encore
  `/var/log/hackweb` : `cat`, `tail` et l'onglet « Logs » du Manager le lisent.
- Le jour où l'OS aura de vrais comptes de service, la section 2 sera
  remplacée par un nouvel ADR ; les droits observés resteront les mêmes.
