> [English version](../../../en/wasm/framework/sdk-network.md)

# Réseau dans le SDK

## Objet

Une app accède au réseau uniquement par le SDK (ADR 0055, ADR 0056 section 6) :
résolution de noms, ping, HTTP et sondage de ports TCP. L'OS décide seul quel
réseau sert une requête : le réseau simulé d'abord, puis, si l'hôte n'y existe
pas, le réseau réel par le proxy du serveur optionnel, quand c'est permis et
possible. L'app ne voit ni le jeton d'accès, ni l'adresse du serveur, ni même
s'il existe un serveur.

Ce document décrit les façades `KazoDns`, `KazoPing`, `KazoHttpClient`,
`KazoTcpClient` et `KazoClientWebSocket`, la passerelle `IAppNetworkGateway` qui les porte et son
implémentation `AppNetworkGateway` dans Platform Core. Il a été écrit par le
chantier `rebuild/4-sdk-network` (ADR 0058).

## Architecture

```text
App (Browser, ping, curl, cat, nmap)
  └─ KazoDns / KazoPing / KazoHttpClient / KazoTcpClient     (Kazo.Os.AppSdk.Net)
       └─ KazoExecutionContext.Current.Network : IAppNetworkGateway
            └─ AppNetworkGateway                             (Platform.Core/Network)
                 ├─ capacité (ICapabilityChecker de l'instance)
                 ├─ ISimulatedNetworkService                 (toujours essayé en premier)
                 └─ repli réel : IServerConnectionService + IProxyClient
                      (seulement si network.real.access, serveur connecté, jeton valide)
```

| Type | Projet | Rôle |
|---|---|---|
| `IAppNetworkGateway` | `Kazo.Os.AppSdk` (`Net/`) | Passerelle de l'instance : `ResolveAsync`, `ReverseLookupAsync`, `PingAsync`, `SendAsync`, `ProbeTcpAsync`, `DetectOperatingSystemAsync`, puis `ConnectTcpAsync`, `ConnectUdpAsync` (ADR 0102) et `ConnectWebSocketAsync` (ADR 0235 §1). Les échecs réseau sont un résultat (`KazoNetworkResult<T>`, `KazoNetworkError`), jamais une exception. |
| `IAppExecutionContext.Network` | `Kazo.Os.AppSdk` | Membre avec une implémentation par défaut « non prise en charge », pour que les contextes existants compilent. |
| `KazoDns`, `KazoPing`, `KazoHttpClient`, `KazoCookieContainer`, `KazoTcpClient` | `Kazo.Os.AppSdk` (`Net/`) | Façades calquées sur `System.Net` : elles lisent le contexte ambiant (ADR 0043) et lèvent les exceptions de .NET. |
| `AppNetworkGateway` | `Kazo.Os.Platform.Core` (`Network/`) | Implémentation de confiance : capacités, réseau simulé, décision du repli réel, jeton du serveur. Construite par `AppExecutionContextFactory` pour chaque instance. |

`AppExecutionContextFactory` reçoit `ISimulatedNetworkService`,
`IServerConnectionService` et `IProxyClient` (paramètres optionnels). Sans réseau
simulé, `Network` lève `NotSupportedException` ; sans serveur ni proxy, le repli
réel est simplement absent.

Depuis `web/3-listener-dns`, `AppNetworkGateway` passe d'abord par le
résolveur local `kazodns` (`/etc/hosts`, loopback, apps web, sites hébergés) :
une cible locale est servie par la table des ports de l'OS, sur `lo`, et un nom
`.hack` n'est jamais relayé au vrai réseau. Voir
[`listening-ports-and-dns.md`](listening-ports-and-dns.md).

## Capacités

| Opération | Capacité | Sans la capacité |
|---|---|---|
| DNS, ping, `GET`, `HEAD`, sondage de ports, détection d'OS | `network.simulated.read` (normale) | `UnauthorizedAccessException` (interne : `AppGatewayAccessDeniedException`) |
| `POST` et toute autre méthode HTTP, socket TCP ou UDP (`ConnectAsync`) | `network.simulated.write` (normale) | idem |
| Repli sur le réseau réel | `network.real.access` (dangereuse), vérifiée aussi par le serveur (ADR 0102 §4) | aucun repli : « hôte introuvable » |
| WebSocket vers un vrai serveur (`KazoClientWebSocket`) | `network.real.access` (dangereuse), la même que pour le relais (ADR 0235 §1) | `UnauthorizedAccessException` |
| Écouter un port (`KazoHttpListener`) hors de ses `web.urls` | `network.listen` (normale), plus `network.listen.privileged` (dangereuse) sous 1024 | `UnauthorizedAccessException` |

Les classements sont inchangés (ADR 0049). Une app système reçoit ses capacités
d'office.

## Usage

```csharp
IPAddress[] ips = await KazoDns.GetHostAddressesAsync("hackmail.com");      // SocketException(HostNotFound) si inconnu
KazoHostEntry entry = await KazoDns.GetHostEntryAsync(IPAddress.Parse("192.168.1.90"));

KazoPingReply reply = await KazoPing.SendAsync("hackersearch.net");          // KazoPingException si le nom ne résout pas
// reply.Status : Success, DestinationHostUnreachable, TimedOut ; reply.RoundtripTime : TimeSpan

using KazoHttpClient http = new(cookies);                                    // KazoCookieContainer gardé par l'app
KazoHttpResponse page = await http.GetAsync("https://hackmail.com/inbox");
KazoHttpResponse login = await http.PostAsync("https://hackmail.com/login", [new("username", "agent")]);
KazoHttpResponse put = await http.PutAsync("https://api.hack/items/7", KazoJsonContent.FromJson("{\"done\":true}"));
// page.StatusCode, ReasonPhrase, RequestUri (finale), Headers, ContentType, Content (texte),
// ContentBytes (octets), Page (site historique), RedirectCount — corps HTTP : http-bodies.md

IReadOnlyList<KazoPortProbeResult> ports = await KazoTcpClient.ProbeAsync("10.0.0.5", 1, 1024);
KazoOsFingerprint? os = await KazoTcpClient.DetectOperatingSystemAsync("10.0.0.5");
```

Comportement HTTP :

- les redirections sont suivies (10 au plus) ; un `POST` redirigé par 301 ou
  302 continue en `GET`, 303 change toute méthode sauf `HEAD` en `GET`, 307 et
  308 gardent la méthode et le corps, comme un navigateur et `HttpClient`
  (ADR 0074 §1, [`http-bodies.md`](http-bodies.md)) ;
- les cookies posés en route sont rangés dans le `KazoCookieContainer` du client
  (le navigateur en a un par fenêtre) ; un en-tête `Cookie` mis dans
  `DefaultRequestHeaders` est envoyé avec eux (il gagne sur un cookie de même
  nom), sur l'hôte demandé seulement, et n'est jamais rangé (ADR 0089) ;
- un statut d'erreur (404, 500) est une réponse, pas une exception ;
  `EnsureSuccessStatusCode` et `GetStringAsync` lèvent `HttpRequestException` ;
- aucune réponse (hôte inconnu, hôte éteint, trop de redirections) lève
  `KazoHttpRequestException`, qui hérite de `HttpRequestException` et porte
  `NetworkError` et `ErrorCode` (`ERR_NAME_NOT_RESOLVED`…, affiché par le
  navigateur) ;
- toute méthode porte un corps typé (`KazoHttpContent`) et toute réponse rend
  ses octets avec leur `Content-Type` ([`http-bodies.md`](http-bodies.md)) ;
- le corps d'une page d'un site historique est son rendu texte
  (`SimulatedPageTextFormatter`, `text/plain`), celui qu'affichent `curl` et
  `cat` ; la page structurée reste dans `Page` pour le navigateur.

## Repli sur le réseau réel

L'OS ne tente le réseau réel que si l'hôte n'existe pas dans le réseau simulé,
que l'app a `network.real.access`, que le serveur optionnel est connecté et que
`EnsureAccessTokenAsync` rend un jeton. Depuis `v3/10-server-proxy`
(ADR 0102, 0108, [`server-network-relay.md`](../dev/server-network-relay.md)), le
serveur vérifie aussi l'accord de son côté et relaie :

| Appel | Relais réel |
|---|---|
| `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `OPTIONS`… | requête du proxy avec le corps de la requête (taille et hash vérifiés) et celui de la réponse |
| `HEAD` | requête du proxy sans corps de réponse |
| `KazoPing.SendAsync` | `HEAD` chronométré : un navigateur ne peut pas envoyer d'ICMP ; `Address` vaut `null` |
| `KazoTcpClient.ProbeAsync` sur **un seul** port | sondage TCP du proxy (ADR 0035) ; une plage n'est jamais relayée |
| `KazoTcpClient.ConnectAsync` | flux TCP en WebSocket (`KazoNetworkStream`) |
| `KazoUdpClient.ConnectAsync` | datagrammes UDP en WebSocket |
| DNS, détection d'OS | jamais : « hôte introuvable » |

Une erreur du proxy devient, pour HTTP, l'erreur que montrerait un navigateur
(ADR 0210 §6) : `BlockedByAdministrator` (`ERR_BLOCKED_BY_ADMINISTRATOR`) pour
un port, une adresse ou une app que l'opérateur n'autorise pas, `TimedOut`
(`ERR_TIMED_OUT`) pour un délai dépassé, `ConnectionRefused`,
`HostUnreachable` ou `TooManyRedirects` quand le relais le dit, sinon
`ConnectionFailed`. Le sondage TCP donne `ConnectionFailed` et un ping
`TimedOut`. Un refus à l'ouverture d'un socket devient l'erreur
d'une vraie pile (`SocketException` : `ConnectionRefused`, `HostUnreachable`
ou `HostNotFound`, table dans `server-network-relay.md`). Sans serveur, rien
ne signale son absence : le résultat est celui d'une machine sans passerelle
par défaut.

### Vrais sites en HTTP : redirections, cookies, source (ADR 0210)

- L'OS demande au serveur de **ne pas suivre** les redirections
  (`ProxyHttpRequest.FollowRedirects = false`) et suit les sauts lui-même,
  comme pour le réseau simulé : chaque saut envoie les cookies du
  `KazoCookieContainer` pour l'hôte et range les `Set-Cookie` de la réponse.
- Un `Set-Cookie` avec `Domain=exemple.com` est gardé sous la clé
  `.exemple.com` et envoyé à ce domaine et à ses sous-domaines
  (`KazoCookieContainer.DomainMatches`). `Max-Age=0` ou un `Expires` passé
  l'efface ; `Path`, `Secure`, `HttpOnly` et `SameSite` ne sont pas gardés.
- `KazoHttpResponse.Source` dit d'où vient la réponse finale : `Simulated`,
  `LocalSite` (site de cette machine) ou `RealNetwork` (relais). Elle ne dit
  rien du serveur ni de son jeton.
- `HttpProxyClient` garde au plus 6 appels du relais en cours pour tout l'OS et
  retente 3 fois un refus `QUOTA_EXCEEDED` ; un appel HTTP attend jusqu'à 30 s.

### Sockets TCP et UDP

`KazoTcpClient.ConnectAsync(host, port)` rend un `KazoNetworkStream`
(`ReadAsync`, `WriteAsync`, `RemoteEndPoint`, `0` lu quand la cible a fermé) ;
`KazoUdpClient.ConnectAsync(host, port)` rend un socket UDP « connecté »
(`SendAsync`, `ReceiveAsync` → `KazoUdpReceiveResult`, 65 507 octets au
plus). Les deux demandent `network.simulated.write`. Le réseau simulé n'a pas
de flux bruts : un hôte qu'il connaît, ou cette machine, refuse la connexion.
Les appels synchrones lèvent `NotSupportedException`.

### WebSocket vers un vrai serveur (ADR 0235 §1)

`KazoClientWebSocket` suit `System.Net.WebSockets.ClientWebSocket` et hérite
de `WebSocket` : une app peut le passer partout où un `WebSocket` est attendu.
Chantier `real-network/1-websocket`, issue
[#395](https://github.com/Kazo-ca/Os/issues/395).

```csharp
using KazoClientWebSocket socket = new();
socket.Options.AddSubProtocol("text.ircv3.net");
await socket.ConnectAsync(new Uri("wss://irc.example.org/webirc"));   // UnauthorizedAccessException sans network.real.access
await socket.SendAsync("NICK kazo"u8.ToArray(), WebSocketMessageType.Text, endOfMessage: true);
byte[] buffer = new byte[4096];
ValueWebSocketReceiveResult received = await socket.ReceiveAsync(buffer.AsMemory());
// socket.State, socket.SubProtocol, socket.CloseStatus, socket.CloseStatusDescription
await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
```

- **Sans serveur.** L'OS ouvre le `WebSocket` du navigateur pour l'app
  (`AppNetworkGateway.ConnectWebSocketAsync`, un `ClientWebSocket` de .NET, qui
  est le `WebSocket` du navigateur sous WebAssembly). Il marche dans l'hôte
  Ecosystem seul ; le relais du serveur n'est pas utilisé.
- **Limites du navigateur.** Seuls les sous-protocoles se règlent
  (`Options.AddSubProtocol`) : pas d'en-têtes, de cookies ni de certificat
  client. `wss://` est obligatoire quand l'OS est servi en `https` (contenu
  mixte). Un refus du serveur, un nom inconnu ou une page qui n'accepte pas la
  mise à niveau donnent tous `WebSocketException` avec
  `WebSocketError.Faulted` et le message « Unable to connect to the remote
  server … », sans le code HTTP, comme un navigateur.
- **Hôte simulé refusé.** Une adresse du réseau simulé, de cette machine
  (`localhost`, 127.0.0.0/8) ou du domaine interne `.hack` est refusée avant
  d'ouvrir quoi que ce soit : `WebSocketException` (`Faulted`), message
  « simulated host … (issue #393) ». Le réseau simulé ne porte pas encore de
  WebSocket ([#393](https://github.com/Kazo-ca/Os/issues/393)).
- **Trafic compté.** Chaque trame passe par `MeteredWebSocket` (Platform
  Core) : la poignée de main compte le SYN, le SYN-ACK, la requête `Upgrade` et
  la réponse `101` ; chaque trame compte un segment TCP de son en-tête
  RFC 6455 (2, 4 ou 10 octets, plus 4 de masque pour une trame envoyée) et de
  ses octets, sur `eth0` et dans `rchar`/`wchar` du processus ; les tampons du
  socket (144 Kio) sont imputés au processus jusqu'à la fermeture (ADR 0068).
- **Web Worker.** La passerelle d'une app qui tourne dans un Web Worker
  (`RemoteNetworkGateway`) n'ouvre pas encore de socket, comme pour
  `KazoTcpClient.ConnectAsync` : `ConnectAsync` lève
  `NotSupportedException`.
- **Compatibilité.** `IAppNetworkGateway.ConnectWebSocketAsync` a une
  implémentation par défaut qui lève `NotSupportedException` : une passerelle
  écrite pour le SDK 3.0 compile et se charge toujours.

## Apps migrées

- **Browser** : plus d'injection ; `KazoHttpClient` et un `KazoCookieContainer`
  par fenêtre. Une connexion (formulaire `POST` redirigé) arrive directement
  sur la page cible. Depuis `web/4-browser-html` (ADR 0075), le navigateur
  rend le HTML dans une iframe isolée et relaie `fetch`, XHR, liens,
  formulaires et `document.cookie` par ce même client ; `document.cookie`
  supprime un cookie expiré avec `KazoCookieContainer.Remove` ; détail dans
  [`browser-html-rendering.md`](../dev/browser-html-rendering.md).
- **`ping`** : quatre `KazoPing.SendAsync`, statistiques calculées sur les
  réponses reçues (min/moy/max/écart).
- **`curl`** : `KazoHttpClient.SendAsync` ; `-I` envoie un `HEAD` et affiche
  les en-têtes ; `-d` suit maintenant la redirection, comme `curl -L` (`-L`
  était déjà sans effet). Depuis `web/2-http-bodies` (ADR 0074 §1), `curl`
  écrit le corps brut, envoie `-d`/`--json` tels quels et, comme le vrai
  `curl`, n'échoue (code 22, sans corps) sur un statut ≥ 400 qu'avec `-f` ;
  détail dans [`http-bodies.md`](http-bodies.md).
- **`cat <url>`** : `KazoHttpClient.GetAsync`, même rendu que `curl`.
- **`nmap`** : `KazoDns` pour l'adresse, `KazoPing` pour la découverte de
  l'hôte, `KazoTcpClient` pour le sondage et `-O`. `-p 80` et `-p 80-80` sont
  équivalents pour le repli réel (un seul port).
- **`cd`** : `ShellErrorText` est passé de Platform Core au SDK
  (`Kazo.Os.AppSdk`, à côté de `IShellSession`).

`Kazo.Os.Commands` ne référence plus `Platform.Core` ni `Server.Contracts` ;
`Kazo.Os.Apps.Browser` et `Kazo.Os.Commands` sont sortis de la liste
d'exemptions de la frontière du SDK.

## Décisions

- **Échecs réseau en résultat dans la passerelle, en exceptions .NET dans les
  façades.** La passerelle reste simple à tester et à implémenter ; le code
  d'app lit comme du code .NET (`SocketException`, `HttpRequestException`).
- **`KazoTcpClient.DetectOperatingSystemAsync`**, absente de l'ADR 0056 :
  `nmap -O` en a besoin et c'est une détection par la pile TCP/IP, donc à sa
  place à côté du sondage de ports. Simulée seulement.
- **`KazoPingReply.RoundtripTime` est un `TimeSpan`**, pas des millisecondes
  entières comme `PingReply` : les latences simulées ont des décimales.
- **Gigue du ping dans l'OS**, pas dans la commande : ±5 % de la latence de
  l'hôte, déterministe et cyclique par instance, comme un vrai lien varie.
- **Pas d'option pour ne pas suivre les redirections** : aucune app n'en a
  besoin et le réseau simulé n'expose pas de requête à un seul saut pour `GET`.
- **Comptage des octets** pour `/proc/net/dev` : `AppNetworkGateway` est le
  point de passage unique. Depuis le chantier `4-proc-metrics` (ADR 0057),
  chaque méthode publique compte son échange sur `eth0` et dans `rchar`/`wchar`
  du processus de l'app (`NetworkTrafficMeter`) ; un appel refusé faute de
  capacité ne compte rien. Détails : [`system-metrics.md`](../dev/system-metrics.md).

- **`ClientWebSocket` de .NET plutôt qu'un module JavaScript** (chantier
  `real-network/1-websocket`) : l'ADR 0235 §1 prévoyait un module JavaScript
  de Browser Infrastructure. Sous WebAssembly, `ClientWebSocket` *est* le
  `WebSocket` du navigateur (le relais l'utilise déjà dans `HttpProxyClient`),
  ouvert par l'OS et jamais par l'app : aucun JavaScript à écrire (AGENTS.md
  §I.2), les tests xUnit parlent à un vrai serveur WebSocket (Kestrel), et la
  même classe servira dans un Web Worker. `AppNetworkGateway.WebSocketConnector`
  permet aux tests de viser leur serveur.

## Liste des tâches du chantier `real-network/1-websocket`

- [x] `KazoClientWebSocket` et `KazoClientWebSocketOptions` (`Kazo.Os.AppSdk.Net`), calqués sur `ClientWebSocket`.
- [x] `IAppNetworkGateway.ConnectWebSocketAsync` avec implémentation par défaut (compatibilité du SDK 3.0).
- [x] `AppNetworkGateway.ConnectWebSocketAsync` : `network.real.access`, hôte simulé refusé, `WebSocket` du navigateur.
- [x] `MeteredWebSocket` : trames comptées dans `/proc/net/dev` et `/proc/<pid>/io`, tampons imputés.
- [x] `PublicAPI.Unshipped.txt` du SDK et de Platform Core.
- [x] Tests (`Wave4NetworkTests.WebSocket.cs`) : refus sans capacité, hôte simulé refusé, échange de trames et sous-protocole avec un vrai serveur WebSocket, refus de la poignée de main, trafic compté, contexte sans WebSocket.
- [x] Documentation : ce document, `capabilities.md`, `server-network-relay.md`, `app-sdk.md`, `sdk-reference.md`, `features.md`, index, entrée `changes/unreleased`.

## Liste des tâches du chantier `rebuild/4-sdk-network`

- [x] `IAppNetworkGateway`, `KazoNetworkResult<T>`, `KazoNetworkError` et `IAppExecutionContext.Network`.
- [x] Façades `KazoDns`, `KazoPing`, `KazoHttpClient` (`KazoCookieContainer`, `KazoHttpResponse`, `KazoHttpRequestException`), `KazoTcpClient`.
- [x] `AppNetworkGateway` dans Platform Core : capacités, réseau simulé, repli réel et jeton gérés par l'OS.
- [x] Câblage dans `AppExecutionContextFactory` et l'hôte Ecosystem.
- [x] `ShellErrorText` déplacé dans le SDK.
- [x] Migration de Browser, `ping`, `curl`, `cat`, `nmap` ; `Kazo.Os.Commands` sans `Platform.Core` ni `Server.Contracts`.
- [x] Exemptions de `Kazo.Os.Apps.Browser` et `Kazo.Os.Commands` retirées.
- [x] Tests : commandes sur la passerelle réelle, façades, capacités, repli réel refusé sans `network.real.access`.
- [x] Documentation : ce document, `app-sdk.md`, `shell-interpreter.md`, index.
