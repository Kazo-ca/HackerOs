# Ports à l'écoute, `hackdns` et sites web locaux

Chantier `web/3-listener-dns` de la phase web (ASP.Hack). Décisions : ADR 0073
(app web, worker web partagé), ADR 0074 (ports à l'écoute, résolution de noms).
Plan : [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md).

## Objet

Une app peut maintenant **servir** du HTTP, et pas seulement en consommer :

- `HackHttpListener` (SDK) ouvre un port, comme `System.Net.HttpListener` ;
- l'OS tient une **table des ports à l'écoute** (`ListeningPortTable`), avec
  `EADDRINUSE` et la règle des ports privilégiés ;
- un nouveau genre d'app, **`web`**, déclare ses URL dans son manifeste
  (`web.urls`) ; l'OS la démarre **à la demande**, au premier accès ;
- un résolveur local, **`hackdns`** (`HackDnsResolver`), lit `/etc/hosts`,
  le loopback, les URL des apps web et les sites hébergés avant le réseau
  simulé ;
- le trafic local passe par `lo` (compteurs de `/proc/net/dev`) ;
- `netstat -tlnp`, `/proc/net/tcp` et `nslookup` montrent tout cela ;
- le code serveur de **tous** les sites tourne dans **un seul** Web Worker,
  le worker `web` (décision de Benoit, plan §4.1, réponse 3).

Le rendu des pages dans le navigateur (`web/4`) et le framework ASP.Hack
(`web/6` et suivants, voir [`asp-hack-mvc.md`](asp-hack-mvc.md)) ne font pas
partie de ce chantier. Un site ASP.Hack est une app web comme les autres : son
processus (`HackWebHost`) dérive de `WebServerAppBase`.

## Architecture

```text
client (curl, navigateur, HackHttpClient)
  └─ AppNetworkGateway.SendAsync              (Platform.Core/Network)
       ├─ HackDnsResolver.ResolveAsync        hosts → loopback → web.urls → sites hébergés → réseau simulé
       │     └─ réponse locale ?
       │          ├─ ListeningPortTable.Route(host, port, path)       socket qui écoute
       │          ├─ sinon WebAppActivator.ActivateAsync(app)         démarrage à la demande
       │          ├─ sinon port ouvert : 400 Bad Request - Invalid Hostname
       │          └─ sinon ConnectionRefused
       │     └─ OsHttpListenerSocket.ExchangeAsync  ─►  HackHttpListener.GetContextAsync (app)
       │                    (worker web : message hreq ; réponse hres)
       └─ sinon : réseau simulé, puis repli réel (jamais pour .hack)
```

| Type | Projet | Rôle |
|---|---|---|
| `AppKind.Web`, `AppExecutionHost.Web`, `WebAppManifest`, `WebAppUrls`, `WebBinding` | `HackerOs.App.Abstractions` | Genre `web`, champ `web.urls`, analyse et normalisation des URL et préfixes. |
| `HackHttpListener`, `HackHttpListenerContext/Request/Response` | `HackerOs.AppSdk` (`Net/`) | Écoute HTTP calquée sur `System.Net.HttpListener`. |
| `WebServerAppBase` | `HackerOs.AppSdk` | Base d'une app web : écoute ses `Urls`, appelle `ProcessRequestAsync` pour chaque requête. |
| `HackIPGlobalProperties` | `HackerOs.AppSdk` (`Net/`) | `GetActiveTcpListenersAsync`, `GetActiveTcpListenerEndPointsAsync` (ce que lit `netstat`). |
| `IAppNetworkGateway.ListenHttpAsync`, `GetTcpListenersAsync`, `IHackHttpListenerSocket`, `HackTcpListenerInfo` | `HackerOs.AppSdk` (`Net/`) | Contrat de passerelle. |
| `ListeningPortTable`, `OsHttpListenerSocket` | `Platform.Core` (`Network/Local/`) | Table des sockets : liaison, conflits, routage, inodes, nettoyage des processus morts. |
| `HackDnsResolver` | `Platform.Core` (`Network/Local/`) | Résolveur local avec cache (30 s simulées). |
| `WebAppActivator` | `Platform.Core` (`Network/Local/`) | Activation à la demande, comme l'activation par socket de systemd. |
| `LocalNetworkServices`, `LocalNetworkCaller` | `Platform.Core` (`Network/Local/`) | Ce que `AppExecutionContextFactory` passe à chaque passerelle : table, résolveur, activateur, appelant (manifeste, principal, élévation). |
| `RemoteListenerTable`, `RemoteHttpListenerSocket` | `Platform.WorkerHost` (`Worker/`) | Côté worker : un listener ouvert dans un Web Worker. |

Câblage : `EcosystemServiceCollectionExtensions` enregistre la table, le
résolveur et l'activateur (portée de l'hôte) et les passe à la fabrique de
contextes et à `/proc` (`ProcMetricsSources.Listeners`).

## Usage

### Écouter (`HackHttpListener`)

```csharp
await using HackHttpListener listener = new();
listener.Prefixes.Add("http://localhost:8080/");
await listener.StartAsync();                          // HttpListenerException(98) si le port est pris

while (true)
{
    HackHttpListenerContext context = await listener.GetContextAsync(cancellationToken);
    context.Response.ContentType = "text/plain";
    await context.Response.OutputStream.WriteAsync("bonjour"u8.ToArray(), cancellationToken);
    context.Response.Close();
}
```

- Préfixes de `HttpListener` : `http://hote:port/chemin/`, avec `+` (tout
  hôte) et `*` (tout hôte qu'aucun autre préfixe ne prend) ; le chemin finit
  par `/`.
- Routage comme `http.sys` : hôte exact, puis `+`, puis un nom de loopback,
  puis `*` ; à hôte égal, le chemin le plus long gagne.
- La requête donne `HttpMethod`, `Url`, `Headers`, `QueryString`, `Cookies`,
  `ContentType`, `Body`/`InputStream`, `ReadForm()`, `RemoteEndPoint`
  (`127.0.0.1:<port éphémère>`), `IsLocal`. La réponse donne `StatusCode`,
  `ContentType`, `Headers`, `OutputStream`, `RedirectLocation`,
  `AppendHeader("Set-Cookie", …)`, `Close()`.

### Capacités

| Opération | Capacité |
|---|---|
| Écouter une URL de son propre manifeste (`web.urls`) | aucune |
| Écouter un autre préfixe, port 1024 ou plus | `network.listen` (normale) |
| Écouter un autre préfixe, port sous 1024 | `network.listen` et `network.listen.privileged` (**dangereuse**, comme `CAP_NET_BIND_SERVICE`) |
| Lister les ports à l'écoute (`HackIPGlobalProperties`) | aucune ; le PID et le programme ne sont visibles que pour root ou le même utilisateur, comme `netstat -p` |

Un port déjà pris donne `HackNetworkError.AddressInUse`, levée en
`HttpListenerException` code 98 (`EADDRINUSE`) avec « 0.0.0.0:8080: address
already in use ». Les démons simulés de `localhost` (ssh 22, mysql 3306)
occupent leur port.

### Déclarer une app web

```csharp
[AppManifest(Execution = AppExecutionHost.Web)]
public sealed class IntranetApp : AppDefinition
{
    public override AppKind Kind => AppKind.Web;
    public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
    …
}

public sealed class IntranetSite(AppManifest manifest) : WebServerAppBase(manifest)
{
    protected override Task ProcessRequestAsync(HackHttpListenerContext context, CancellationToken ct) { … }
}
```

Règles du validateur et du générateur :

| Code | Règle |
|---|---|
| `manifest.web.urls.required` | une app `web` déclare au moins une URL |
| `manifest.web.url.invalid` | URL absolue `http`/`https`, nom d'hôte valide (pas de joker), chemin finissant par `/`, sans requête ni fragment |
| `manifest.web.url.duplicate` | deux URL identiques après normalisation |
| `manifest.web.forbidden` | `web` sur une app qui n'est pas `web` |
| `manifest.web.filesystem`, `HOSAPP0050` | un site ne demande aucune capacité `filesystem.*` (ses données vont dans l'IndexedDB du navigateur, ADR 0073 §3) |
| `manifest.execution.web` | une app `web` tourne dans le worker `web` ou sur le thread principal |
| `manifest.execution.web-only` | seule une app `web` tourne dans le worker `web` |
| `catalog.web.url-conflict` | deux apps installées ne déclarent pas le même nom d'hôte |

Le mappeur normalise les URL (`WebAppUrls.Normalize` : schéma et hôte en
minuscules, port par défaut retiré) ; le sérialiseur garde l'ordre déclaré (la
première URL est l'adresse principale du site).

### Activation à la demande

Une app web n'est pas démarrée au boot. La première requête vers une de ses
URL la lance (`AppLifecycleOrchestrator`, non interactif) **sous l'identité de
l'appelant**, attend que son socket écoute (30 s au plus, démarrage du worker
compris), puis lui passe la requête. Des requêtes simultanées partagent le même
démarrage. Arrêtée (gestionnaire de tâches, `kill`), elle redémarre à la
requête suivante.

### Résolution de noms (`hackdns`)

Ordre, du premier au dernier :

1. `/etc/hosts` (seedé `0644` root : `localhost`, `hackeros`), relu dès qu'il
   change ;
2. le loopback (`localhost`, `*.localhost`, 127.0.0.0/8) ;
3. les URL des apps web installées et activées ;
4. les sites hébergés (`IHostedSiteNameSource.FindSiteAsync`, implémenté par
   `HostedSiteNameSource` depuis `web/5` : hôtes des fichiers `.site` valides et
   activés, [`static-sites.md`](../dev/static-sites.md), ADR 0080 §4) ;
5. le réseau simulé : le catalogue par défaut (`hackersearch.net`, `hackmail.com`...)
   et les hôtes de test `.hackeros` (ADR 0085).

Les réponses sont gardées 30 s simulées. Un nom en `.hack` n'est **jamais**
relayé au vrai réseau. Une ligne de `/etc/hosts` vers un hôte simulé envoie la
requête à cet hôte avec son vrai nom (en-tête `Host` réécrit), comme un alias.
`HackDns.GetHostEntryAsync` rend la source (`HackDnsSource` : `HostsFile`,
`Loopback`, `WebApp`, `HostedSite`, `SimulatedNetwork`).

### Commandes

```text
$ netstat -tlnp
Active Internet connections (only servers)
Proto Recv-Q Send-Q Local Address           Foreign Address         State       PID/Program name
tcp        0      0 0.0.0.0:22              0.0.0.0:*               LISTEN      -
tcp        0      0 0.0.0.0:3306            0.0.0.0:*               LISTEN      -
tcp        0      0 0.0.0.0:80              0.0.0.0:*               LISTEN      57/intranet

$ nslookup intranet.hack
Server:		127.0.0.53
Address:	127.0.0.53#53

;; source: web app org.hackeros.intranet (web.urls, this machine)
Name:	intranet.hack
Address: 127.0.0.1
```

- `netstat` lit `HackIPGlobalProperties` ; avec `-p`, un propriétaire caché
  donne la note de net-tools « (Not all processes could be identified… ».
- `/proc/net/tcp` suit `proc(5)` : adresse en hexadécimal petit-boutiste,
  état `0A` (LISTEN), `uid` 0 pour root et les démons, inode du socket.
- `nslookup <nom|adresse>` (capacité `network.simulated.read`) indique la
  source de la réponse ; un nom inconnu donne
  `** server can't find X: NXDOMAIN` et le code 1.

### Trafic local sur `lo`

`NetworkTrafficMeter` compte un échange local (DNS, ping, HTTP, sondage,
détection d'OS) sur `lo` au lieu d'`eth0`. Un ping local répond en ~0,04 ms.

## Worker web partagé (ADR 0073 §1)

- `WorkerRole.Web` : un seul worker `web` pour tous les sites
  (`WorkerPool.AcquireWebAsync`). Le premier site prend le worker prêt (qui est
  remplacé) ; les suivants le réutilisent ; il est terminé quand son dernier
  site s'arrête. Il apparaît comme `web` dans `/proc/hackeros/workers`.
- Protocole (en plus de [`worker-processes.md`](../dev/worker-processes.md)) :

| Message | Sens | Contenu |
|---|---|---|
| `call net.listen` | worker → OS | préfixes ; réponse : numéro de socket et préfixes normalisés |
| `note net.unlisten` | worker → OS | fermer le socket |
| `call net.listeners` | worker → OS | lire la table (`HackIPGlobalProperties`) |
| `hreq` | OS → worker | requête : processus, socket, échange, méthode, URL, en-têtes, corps, points de terminaison |
| `hres` | worker → OS | réponse : statut, raison, en-têtes, type, corps |

- Une requête arrivée avant la réponse à `net.listen` est gardée par
  `RemoteListenerTable` et livrée dès que le socket est connu.
- La fin du processus ferme tous ses sockets, des deux côtés.

## Décisions

- **Données en mémoire, pas de fichier de sockets** : la table vit dans
  l'hôte (portée de l'hôte), `/proc/net/tcp` la lit à la demande, comme le
  noyau.
- **Démons de `localhost`** : le catalogue simulé n'ouvre plus 80 ni 8080 sur
  `127.0.0.1` ; ces ports sont libres pour les apps. ssh 22 et mysql 3306
  restent ouverts (sans PID).
- **Port ouvert, hôte inconnu** : `400 Bad Request - Invalid Hostname`, la
  réponse de `http.sys`, plutôt qu'un refus de connexion.
- **Identité** : un site activé à la demande tourne sous l'utilisateur qui l'a
  appelé (session unique pour l'instant).
- **Pas de repli réel pour `.hack`** : un site interne qui ne répond pas donne
  « connexion refusée », jamais une requête sur Internet.

## Tests

- `Tests/HackerOs.Platform.WorkerHost.Tests/LocalNetworkTests.cs` (avec et sans
  worker) : activation à la demande, cookies, redirections, `POST`, worker web
  partagé et sa fin, arrêt puis redémarrage, `netstat`, `/proc/net/tcp`,
  permissions d'écoute, `EADDRINUSE`, connexion refusée, 400, `nslookup`,
  priorité de `/etc/hosts`, `.hack` jamais relayé, alias vers un hôte simulé.
- `Tests/HackerOs.App.Abstractions.Tests` : `WebAppUrlsTests`, règles `web` du
  validateur, aller-retour `web.urls` du sérialiseur, fixtures de schéma
  `web.valid.json`, `invalid-web-missing-web-block.json`,
  `invalid-web-on-service.json`.
- `CommandParityTests` : `nslookup` (64 commandes).

## Liste des tâches du chantier `web/3-listener-dns`

- [x] Genre `web`, hôte `web`, `web.urls` (abstractions, validateur, schéma, sérialiseur, mappeur, générateur `HOSAPP0050`, conflit du catalogue).
- [x] Capacités `network.listen` (normale) et `network.listen.privileged` (dangereuse).
- [x] `HackHttpListener`, `WebServerAppBase`, `HackIPGlobalProperties`, contrat de passerelle.
- [x] `ListeningPortTable` : liaison, `EADDRINUSE`, routage `http.sys`, démons de `localhost`, nettoyage.
- [x] `HackDnsResolver` : `/etc/hosts` (seedé), loopback, URL des apps web, sites hébergés, cache, `.hack` jamais relayé.
- [x] `WebAppActivator` : activation à la demande sous l'identité de l'appelant.
- [x] Passerelle : envoi local, cookies, redirections, 400 Invalid Hostname, trafic sur `lo`, sondage et ping locaux.
- [x] Worker `web` partagé, `net.listen`/`net.unlisten`/`net.listeners`, `hreq`/`hres`.
- [x] `/proc/net/tcp`, `netstat -tlnp`, `nslookup`.
- [x] Tests (bout en bout dans les deux modes, abstractions, parité).
- [x] Documentation et index.
