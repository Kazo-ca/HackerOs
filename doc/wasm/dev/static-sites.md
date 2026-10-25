# Sites statiques : fichiers `.site`, `hackwebd` et Web Server Manager

Chantier `web/5-static-sites` de la phase web (ASP.Hack). Décisions :
[ADR 0077](adr/0077-hosted-static-sites.md) et ses ajustements,
[ADR 0080](adr/0080-hosted-static-sites-adjustments.md). Plan :
[`asp-hack-plan.md`](plans/asp-hack-plan.md) §5. S'appuie sur `web/3`
([`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md)) pour les ports et
les noms, et sur `web/4` ([`browser-html-rendering.md`](browser-html-rendering.md))
pour afficher les pages.

## Objet

Publier un dossier comme site web, comme avec IIS, Apache ou nginx :

- un site est un **fichier** `.site` (JSON), lisible par `cat` et modifiable à
  la main ;
- le service **`hackwebd`** lit ces fichiers, ouvre un port par liaison et sert
  les fichiers : types MIME, documents par défaut, liste du dossier si permise,
  `301`, `304`, `403`, `404`, `405`, journaux à la manière de nginx ;
- l'app **Web Server Manager** (façon gestionnaire IIS) ne fait qu'écrire ces
  fichiers et piloter le service par le SDK public ;
- l'explorateur gagne **« Publish as a web site… »** sur un dossier.

## Architecture

```text
 Web Server Manager ─┬─ écrit ───────────► /etc/hackweb/sites/*.site      (admin, tous ports, servi en www-data)
 (fenêtre)           │                     ~/.config/hackweb/sites/*.site (utilisateur, ports ≥ 1024)
                     ├─ HackServiceController (start/stop/restart hackwebd)
                     └─ HackIPGlobalProperties (sockets de hackwebd → état des sites)

 hackwebd (service, worker dédié, AutoStart)
   ├─ HackFileSystemWatcher "*.site" sur les deux dossiers ─► rechargement à chaud
   ├─ HackHttpListener par préfixe normalisé ─► StaticFileHandler
   │      └─ WwwDataAccess (droits « other » pour un site système)
   └─ context.Logging.AppendToLogFile ─► /var/log/hackweb/<site>.access.log, error.log

 hackdns (OS) ── HostedSiteNameSource (lit les .site en root) ─► « docs.hack » → 127.0.0.1
```

| Élément | Projet | Rôle |
|---|---|---|
| `HackWebSite`, `HackWebSiteDeclaration`, `HackWebSiteScope` | `Shared/HackerOs.AppSdk/Net/Hosting` | Contenu d'un `.site` et fichier lu (chemin, portée, propriétaire, erreurs). |
| `HackWebSiteFile` | idem | Emplacements, `Parse`, `Validate`, `Serialize`, `TryParseBinding`, `ReadAllAsync`. Partagé par le démon, le Manager et le résolveur. |
| `HackWebdApp`, `HackWebdService` | `Apps/System/HackerOs.Apps.WebServer` | Le service : réconciliation des écoutes, boucle d'acceptation, journaux. |
| `StaticFileHandler`, `WwwDataAccess`, `HackWebdLog` | idem, `Daemon/` | Traitement d'une requête, droits de `www-data`, formats des journaux. |
| `WebServerManagerApp`, `WebServerManagerWindow`, `WebSiteStore` | idem, `Manager/` | L'app à fenêtre et sa logique sans interface. |
| `WebServerManagerApp.RegisterFileContextMenu`, `PublishSiteIntent` | `Apps/System/HackerOs.Apps.WebServer` | Entrée « Publish as a web site… » des menus de dossiers, inscrite par Web Server Manager (ADR 0151 §8, ADR 0159) : portée `Directory`, un seul dossier, intent `org.hackeros.web-server-manager.publish-site.v1` reçu au lancement ou par `IntentReceived`. Était `PublishSiteContextMenuProvider`, puis `FileExplorerMenuEntries.PublishSite`. |
| `HostedSiteNameSource` | `Platform/HackerOs.Platform.Core/Network/Local` | Source 4 du résolveur (`IHostedSiteNameSource`, asynchrone). |
| `IAppLoggingGateway.AppendToLogFile`, `LogFileTarget.ForDaemon` | `Simulation.Abstractions`, `Platform.Core` | Journaux des démons système (ADR 0080 §3). |

Le service et le Manager sont dans **le même assembly** : le Manager pilote
son service compagnon sans `services.manage`, comme le prévoit la règle des
services d'une même app. Les deux sont des apps système (`IsSystemApp` sur la
`ProjectReference` des hôtes).

## Fonctionnement

### Le fichier `.site`

```json
{
  "name": "docs",
  "root": "/home/benoit/www/docs",
  "bindings": ["http://docs.hack:8080/", "http://localhost:8081/"],
  "defaultDocuments": ["index.html", "index.htm", "default.htm"],
  "directoryBrowsing": false,
  "enabled": true
}
```

- Le fichier s'appelle `<name>.site` ; sinon le site est invalide.
- Seuls `name`, `root` et `bindings` sont obligatoires ; les commentaires `//`
  et les virgules finales sont tolérés (fichier édité à la main).
- `root` : chemin absolu, ni `/`, ni `..`.
- `bindings` : préfixes `http://hôte[:port]/chemin/` normalisés comme ceux de
  `HackHttpListener` (`+`, `*` acceptés). `https://` refusé (pas de
  certificats, ADR 0080). Un site d'utilisateur ne peut pas prendre un port
  < 1024 : l'erreur va dans `error.log`.
- Un site invalide ou désactivé n'est pas servi ; il reste listé (état
  « invalid » ou « stopped ») dans le Manager.

### `hackwebd`

- Démarré au boot (`AutoStart`), visible par `systemctl status hackwebd`,
  arrêtable par `systemctl stop hackwebd` (les ports se libèrent :
  `curl` répond alors `Failed to connect`, code 7).
- À chaque changement d'un `.site` (ou au démarrage), il **réconcilie** : un
  `HackHttpListener` par préfixe normalisé ; un préfixe déjà pris (par un autre
  site ou un autre programme) est journalisé en `[emerg]` et le site reste
  « partial » ou « down ».
- Requête : `GET`, `HEAD`, `OPTIONS` (sinon `405` + `Allow`) ; chemin décodé,
  segments `.` et `..` résolus par l'analyseur d'URL puis refusés s'il en reste,
  `\` et NUL refusés (`400`) : jamais de fichier hors de `root`.
- Dossier sans `/` final : `301`. Dossier : premier document par défaut
  trouvé, sinon liste « Index of » si `directoryBrowsing`, sinon `403`.
- Fichier : `Content-Type` par extension (`SimulatedFileTypes`), `ETag`
  (`"<révision>-<taille>"`), `Last-Modified`, `304` sur `If-None-Match` puis
  `If-Modified-Since` ; `HEAD` envoie `Content-Length` sans corps.
- Droits : un site d'utilisateur est lu avec les droits de son propriétaire ;
  un site système applique ceux de `www-data` (bits « other » sur chaque
  dossier et le fichier) — un site système dans un dossier personnel `0700`
  répond `403`, comme Apache.
- Pages d'erreur et en-tête `Server: hackwebd/1.0` à la manière de nginx.

### Journaux

| Fichier | Contenu |
|---|---|
| `/var/log/hackweb/<site>.access.log` | Format « combined » : `127.0.0.1 - - [16/Oct/2026:12:00:00 +0000] "GET / HTTP/1.1" 200 512 "-" "curl/8.5.0"` |
| `/var/log/hackweb/error.log` | Format nginx : `2026/10/16 12:00:00 [error] 42#0: docs: open() "/…" failed (13: Permission denied for www-data)` ; démarrage, écoutes (`[notice]`), conflits (`[emerg]`), refus, `exiting`. |

Écrits par `LogFileWriter` (rotation 1 Mio × 5, `system:system 0644`).

### Résolution des noms

`HostedSiteNameSource` lit les `.site` des deux emplacements avec les droits
de `root`, garde les sites valides et activés et publie leurs hôtes (ni
`localhost`, ni IP, ni joker). `nslookup docs.hack` répond
`;; source: hosted site docs (this machine)`. Le nom reste publié quand
`hackwebd` est arrêté (le fichier existe toujours) : la connexion est alors
refusée, comme un serveur arrêté derrière un DNS valide.

### Web Server Manager

- Trois groupes : **System** (`/etc/hackweb/sites`), **My sites**
  (`~/.config/hackweb/sites`), **External** (apps `Web` de l'ADR 0073, en
  lecture seule avec leurs URL).
- État par site (pastille) : running, partial, down, stopped, invalid,
  external, calculé d'après les sockets que possède `hackwebd`.
- Barre du haut : Refresh, Start/Restart/Stop `hackwebd`, « + New site ».
- Formulaire : nom, portée (mes sites / système), racine (bouton
  « Browse… », sélecteur de dossier), liaisons (une par ligne), documents par
  défaut, liste du dossier, activé ; erreurs de `Validate` en direct ;
  avertissement si `www-data` ne pourra pas lire la racine d'un site système.
- Save, Start/Stop site (bascule `enabled`), Open in browser
  (`org.hackeros.browser`), Delete (confirmation), onglet **Logs** (journal
  d'accès du site, lignes d'`error.log` qui le concernent).
- Écrire dans `/etc/hackweb` est une écriture système : l'approbation
  administrateur de la plateforme s'affiche, puis le Manager demande
  d'appuyer de nouveau sur Save.
- Lancé avec un dossier en argument (menu de l'explorateur), il ouvre
  directement un nouveau site prérempli : nom tiré du dossier, premier port
  libre à partir de 8080 sur `localhost`.

## Utilisation

```sh
mkdir -p ~/www && echo '<h1>Hello</h1>' > ~/www/index.html
mkdir -p ~/.config/hackweb/sites
cat > ~/.config/hackweb/sites/docs.site <<'EOF'
{ "name": "docs", "root": "/home/benoit/www", "bindings": ["http://docs.hack:8080/"] }
EOF
curl http://docs.hack:8080/          # <h1>Hello</h1>
netstat -tlnp                         # 0.0.0.0:8080 … LISTEN  <pid>/hackwebd
tail /var/log/hackweb/docs.access.log
systemctl restart hackwebd
```

Ou, dans l'explorateur, clic droit sur un dossier → **Publish as a web site…**,
puis Save dans le Manager et **Open in browser**.

Pour un développeur d'app, le format est dans le SDK :

```csharp
IReadOnlyList<HackWebSiteDeclaration> sites = await HackWebSiteFile.ReadAllAsync();
HackWebSiteDeclaration parsed = HackWebSiteFile.Parse(path, json, HackWebSiteScope.User, "alice");
IReadOnlyList<string> errors = HackWebSiteFile.Validate(site, HackWebSiteScope.User);
```

## Décisions clés

- Fichiers plutôt que base de configuration : ce que montre le Manager est
  exactement ce que montre `cat` (ADR 0077 §1).
- Un seul démon pour tous les sites statiques, un listener par préfixe : le
  routage par hôte et par chemin reste celui de `http.sys` (ADR 0074).
- `www-data` simulé, journaux par `AppendToLogFile`, résolveur asynchrone :
  [ADR 0080](adr/0080-hosted-static-sites-adjustments.md). `hackwebd` tourne
  en worker dédié depuis que la surveillance de fichiers traverse la frontière
  du worker ([ADR 0124](adr/0124-remote-event-and-watch-gateways.md) §6,
  qui rend sans objet le fil principal de l'ADR 0080 §1).
- `enabled` est une propriété à `set` (pas `init`) : le générateur JSON donne
  `default` à une propriété `init` absente, ce qui aurait arrêté tout site
  dont le fichier omet `enabled`.

## Limites connues

- Pas de `https://`, de requêtes partielles (`Range`), de compression, ni de
  réécriture d'URL.
- L'Error Log Viewer ne liste pas `/var/log/hackweb` (lire par `cat`/`tail`
  ou l'onglet Logs du Manager).

## Tests

- `Tests/HackerOs.Platform.WorkerHost.Tests/StaticSitesTests.cs` : OS complet
  (processus sur le fil principal et en workers) — site d'utilisateur servi
  (type, document par défaut, `404`, `nslookup`), `304` par `ETag`, `403` puis
  liste du dossier après rechargement à chaud, refus d'un port privilégié et
  formats des journaux, site système sur le port 80 servi en `www-data`
  (`403` sur un fichier `0600` et sur un dossier personnel), `netstat`,
  `systemctl stop`.
- `Tests/HackerOs.Apps.WebServer.Tests` : correspondance URL → fichier
  (traversées refusées), états des sites, suggestion d'un site depuis un
  dossier, formats des journaux.
- `Tests/HackerOs.AppSdk.Tests/Net/HackWebSiteFileTests.cs` : lecture,
  validation et écriture des `.site`.
- `Tests/HackerOs.Platform.Core.Tests/Diagnostics/DaemonLogFileTests.cs` :
  `LogFileTarget.ForDaemon` et la règle « apps système seulement ».

## Liste des tâches

- [x] Format `.site` dans le SDK (`HackWebSite`, `HackWebSiteFile`), validation façon `nginx -t`.
- [x] `/etc/hackweb/sites` créé par `FileSystemSeeder` ; `~/.config/hackweb/sites` créé par `hackwebd`.
- [x] Journaux des démons système (`AppendToLogFile`, `LogFileTarget.ForDaemon`, opération `log.file` des workers).
- [x] `HostedSiteNameSource` et `IHostedSiteNameSource` asynchrone ; `HackAppInfo.Urls` (sites externes).
- [x] `hackwebd` : réconciliation à chaud, fichiers, documents par défaut, liste, MIME, `301`/`304`/`403`/`404`/`405`, `www-data`.
- [x] Web Server Manager : groupes, états, formulaire, portée, Browse, Save/Start/Stop/Delete, navigateur, journaux, contrôle du service.
- [x] Explorateur : « Publish as a web site… » sur un dossier.
- [x] Hôtes (Ecosystem, test, Server) et solution.
- [x] Tests de bout en bout et unitaires.
- [x] Documentation, ADR 0080, index.
