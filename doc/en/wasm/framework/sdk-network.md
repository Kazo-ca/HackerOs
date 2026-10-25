# Réseau dans le SDK

## Objet

Une app accède au réseau uniquement par le SDK (ADR 0055, ADR 0056 section 6) :
résolution de noms, ping, HTTP et sondage de ports TCP. L'OS décide seul quel
réseau sert une requête : le réseau simulé d'abord, puis, si l'hôte n'y existe
pas, le réseau réel par le proxy du serveur optionnel, quand c'est permis et
possible. L'app ne voit ni le jeton d'accès, ni l'adresse du serveur, ni même
s'il existe un serveur.

Ce document décrit les façades `HackDns`, `HackPing`, `HackHttpClient` et
`HackTcpClient`, la passerelle `IAppNetworkGateway` qui les porte et son
implémentation `AppNetworkGateway` dans Platform Core. Il a été écrit par le
chantier `rebuild/4-sdk-network` (ADR 0058).

## Architecture

```text
App (Browser, ping, curl, cat, nmap)
  └─ HackDns / HackPing / HackHttpClient / HackTcpClient     (HackerOs.AppSdk.Net)
       └─ HackExecutionContext.Current.Network : IAppNetworkGateway
            └─ AppNetworkGateway                             (Platform.Core/Network)
                 ├─ capacité (ICapabilityChecker de l'instance)
                 ├─ ISimulatedNetworkService                 (toujours essayé en premier)
                 └─ repli réel : IServerConnectionService + IProxyClient
                      (seulement si network.real.access, serveur connecté, jeton valide)
```

| Type | Projet | Rôle |
|---|---|---|
| `IAppNetworkGateway` | `HackerOs.AppSdk` (`Net/`) | Passerelle de l'instance : `ResolveAsync`, `ReverseLookupAsync`, `PingAsync`, `SendAsync`, `ProbeTcpAsync`, `DetectOperatingSystemAsync`. Les échecs réseau sont un résultat (`HackNetworkResult<T>`, `HackNetworkError`), jamais une exception. |
| `IAppExecutionContext.Network` | `HackerOs.AppSdk` | Membre avec une implémentation par défaut « non prise en charge », pour que les contextes existants compilent. |
| `HackDns`, `HackPing`, `HackHttpClient`, `HackCookieContainer`, `HackTcpClient` | `HackerOs.AppSdk` (`Net/`) | Façades calquées sur `System.Net` : elles lisent le contexte ambiant (ADR 0043) et lèvent les exceptions de .NET. |
| `AppNetworkGateway` | `HackerOs.Platform.Core` (`Network/`) | Implémentation de confiance : capacités, réseau simulé, décision du repli réel, jeton du serveur. Construite par `AppExecutionContextFactory` pour chaque instance. |

`AppExecutionContextFactory` reçoit `ISimulatedNetworkService`,
`IServerConnectionService` et `IProxyClient` (paramètres optionnels). Sans réseau
simulé, `Network` lève `NotSupportedException` ; sans serveur ni proxy, le repli
réel est simplement absent.

Depuis `web/3-listener-dns`, `AppNetworkGateway` passe d'abord par le
résolveur local `hackdns` (`/etc/hosts`, loopback, apps web, sites hébergés) :
une cible locale est servie par la table des ports de l'OS, sur `lo`, et un nom
`.hack` n'est jamais relayé au vrai réseau. Voir
[`listening-ports-and-dns.md`](listening-ports-and-dns.md).

## Capacités

| Opération | Capacité | Sans la capacité |
|---|---|---|
| DNS, ping, `GET`, `HEAD`, sondage de ports, détection d'OS | `network.simulated.read` (normale) | `UnauthorizedAccessException` (interne : `AppGatewayAccessDeniedException`) |
| `POST` et toute autre méthode HTTP, socket TCP ou UDP (`ConnectAsync`) | `network.simulated.write` (normale) | idem |
| Repli sur le réseau réel | `network.real.access` (dangereuse), vérifiée aussi par le serveur (ADR 0102 §4) | aucun repli : « hôte introuvable » |
| Écouter un port (`HackHttpListener`) hors de ses `web.urls` | `network.listen` (normale), plus `network.listen.privileged` (dangereuse) sous 1024 | `UnauthorizedAccessException` |

Les classements sont inchangés (ADR 0049). Une app système reçoit ses capacités
d'office.

## Usage

```csharp
IPAddress[] ips = await HackDns.GetHostAddressesAsync("hackmail.com");      // SocketException(HostNotFound) si inconnu
HackHostEntry entry = await HackDns.GetHostEntryAsync(IPAddress.Parse("192.168.1.90"));

HackPingReply reply = await HackPing.SendAsync("hackersearch.net");          // HackPingException si le nom ne résout pas
// reply.Status : Success, DestinationHostUnreachable, TimedOut ; reply.RoundtripTime : TimeSpan

using HackHttpClient http = new(cookies);                                    // HackCookieContainer gardé par l'app
HackHttpResponse page = await http.GetAsync("https://hackmail.com/inbox");
HackHttpResponse login = await http.PostAsync("https://hackmail.com/login", [new("username", "agent")]);
HackHttpResponse put = await http.PutAsync("https://api.hack/items/7", HackJsonContent.FromJson("{\"done\":true}"));
// page.StatusCode, ReasonPhrase, RequestUri (finale), Headers, ContentType, Content (texte),
// ContentBytes (octets), Page (site historique), RedirectCount — corps HTTP : http-bodies.md

IReadOnlyList<HackPortProbeResult> ports = await HackTcpClient.ProbeAsync("10.0.0.5", 1, 1024);
HackOsFingerprint? os = await HackTcpClient.DetectOperatingSystemAsync("10.0.0.5");
```

Comportement HTTP :

- les redirections sont suivies (10 au plus) ; un `POST` redirigé par 301 ou
  302 continue en `GET`, 303 change toute méthode sauf `HEAD` en `GET`, 307 et
  308 gardent la méthode et le corps, comme un navigateur et `HttpClient`
  (ADR 0074 §1, [`http-bodies.md`](http-bodies.md)) ;
- les cookies posés en route sont rangés dans le `HackCookieContainer` du client
  (le navigateur en a un par fenêtre) ; un en-tête `Cookie` mis dans
  `DefaultRequestHeaders` est envoyé avec eux (il gagne sur un cookie de même
  nom), sur l'hôte demandé seulement, et n'est jamais rangé (ADR 0089) ;
- un statut d'erreur (404, 500) est une réponse, pas une exception ;
  `EnsureSuccessStatusCode` et `GetStringAsync` lèvent `HttpRequestException` ;
- aucune réponse (hôte inconnu, hôte éteint, trop de redirections) lève
  `HackHttpRequestException`, qui hérite de `HttpRequestException` et porte
  `NetworkError` et `ErrorCode` (`ERR_NAME_NOT_RESOLVED`…, affiché par le
  navigateur) ;
- toute méthode porte un corps typé (`HackHttpContent`) et toute réponse rend
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
| `HackPing.SendAsync` | `HEAD` chronométré : un navigateur ne peut pas envoyer d'ICMP ; `Address` vaut `null` |
| `HackTcpClient.ProbeAsync` sur **un seul** port | sondage TCP du proxy (ADR 0035) ; une plage n'est jamais relayée |
| `HackTcpClient.ConnectAsync` | flux TCP en WebSocket (`HackNetworkStream`) |
| `HackUdpClient.ConnectAsync` | datagrammes UDP en WebSocket |
| DNS, détection d'OS | jamais : « hôte introuvable » |

Une erreur du proxy (`ServerConnectionException`, `HttpRequestException`)
devient `HackNetworkError.ConnectionFailed` pour HTTP et le sondage, et
`TimedOut` pour un ping. Un refus à l'ouverture d'un socket devient l'erreur
d'une vraie pile (`SocketException` : `ConnectionRefused`, `HostUnreachable`
ou `HostNotFound`, table dans `server-network-relay.md`). Sans serveur, rien
ne signale son absence : le résultat est celui d'une machine sans passerelle
par défaut.

### Sockets TCP et UDP

`HackTcpClient.ConnectAsync(host, port)` rend un `HackNetworkStream`
(`ReadAsync`, `WriteAsync`, `RemoteEndPoint`, `0` lu quand la cible a fermé) ;
`HackUdpClient.ConnectAsync(host, port)` rend un socket UDP « connecté »
(`SendAsync`, `ReceiveAsync` → `HackUdpReceiveResult`, 65 507 octets au
plus). Les deux demandent `network.simulated.write`. Le réseau simulé n'a pas
de flux bruts : un hôte qu'il connaît, ou cette machine, refuse la connexion.
Les appels synchrones lèvent `NotSupportedException`.

## Apps migrées

- **Browser** : plus d'injection ; `HackHttpClient` et un `HackCookieContainer`
  par fenêtre. Une connexion (formulaire `POST` redirigé) arrive directement
  sur la page cible. Depuis `web/4-browser-html` (ADR 0075), le navigateur
  rend le HTML dans une iframe isolée et relaie `fetch`, XHR, liens,
  formulaires et `document.cookie` par ce même client ; `document.cookie`
  supprime un cookie expiré avec `HackCookieContainer.Remove` ; détail dans
  [`browser-html-rendering.md`](../dev/browser-html-rendering.md).
- **`ping`** : quatre `HackPing.SendAsync`, statistiques calculées sur les
  réponses reçues (min/moy/max/écart).
- **`curl`** : `HackHttpClient.SendAsync` ; `-I` envoie un `HEAD` et affiche
  les en-têtes ; `-d` suit maintenant la redirection, comme `curl -L` (`-L`
  était déjà sans effet). Depuis `web/2-http-bodies` (ADR 0074 §1), `curl`
  écrit le corps brut, envoie `-d`/`--json` tels quels et, comme le vrai
  `curl`, n'échoue (code 22, sans corps) sur un statut ≥ 400 qu'avec `-f` ;
  détail dans [`http-bodies.md`](http-bodies.md).
- **`cat <url>`** : `HackHttpClient.GetAsync`, même rendu que `curl`.
- **`nmap`** : `HackDns` pour l'adresse, `HackPing` pour la découverte de
  l'hôte, `HackTcpClient` pour le sondage et `-O`. `-p 80` et `-p 80-80` sont
  équivalents pour le repli réel (un seul port).
- **`cd`** : `ShellErrorText` est passé de Platform Core au SDK
  (`HackerOs.AppSdk`, à côté de `IShellSession`).

`HackerOs.Commands` ne référence plus `Platform.Core` ni `Server.Contracts` ;
`HackerOs.Apps.Browser` et `HackerOs.Commands` sont sortis de la liste
d'exemptions de la frontière du SDK.

## Décisions

- **Échecs réseau en résultat dans la passerelle, en exceptions .NET dans les
  façades.** La passerelle reste simple à tester et à implémenter ; le code
  d'app lit comme du code .NET (`SocketException`, `HttpRequestException`).
- **`HackTcpClient.DetectOperatingSystemAsync`**, absente de l'ADR 0056 :
  `nmap -O` en a besoin et c'est une détection par la pile TCP/IP, donc à sa
  place à côté du sondage de ports. Simulée seulement.
- **`HackPingReply.RoundtripTime` est un `TimeSpan`**, pas des millisecondes
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

## Liste des tâches du chantier `rebuild/4-sdk-network`

- [x] `IAppNetworkGateway`, `HackNetworkResult<T>`, `HackNetworkError` et `IAppExecutionContext.Network`.
- [x] Façades `HackDns`, `HackPing`, `HackHttpClient` (`HackCookieContainer`, `HackHttpResponse`, `HackHttpRequestException`), `HackTcpClient`.
- [x] `AppNetworkGateway` dans Platform Core : capacités, réseau simulé, repli réel et jeton gérés par l'OS.
- [x] Câblage dans `AppExecutionContextFactory` et l'hôte Ecosystem.
- [x] `ShellErrorText` déplacé dans le SDK.
- [x] Migration de Browser, `ping`, `curl`, `cat`, `nmap` ; `HackerOs.Commands` sans `Platform.Core` ni `Server.Contracts`.
- [x] Exemptions de `HackerOs.Apps.Browser` et `HackerOs.Commands` retirées.
- [x] Tests : commandes sur la passerelle réelle, façades, capacités, repli réel refusé sans `network.real.access`.
- [x] Documentation : ce document, `app-sdk.md`, `shell-interpreter.md`, index.
