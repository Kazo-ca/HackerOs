> [English version](../../../en/wasm/dev/server-network-relay.md)

# Relais réseau du serveur optionnel

Chantier `v3/10-server-proxy` du [plan de fin de migration](plans/wasm-v3-completion-plan.md),
décisions dans l'[ADR 0102](adr/0102-server-network-relay-tcp-udp-and-policy.md)
et ses ajustements dans l'[ADR 0108](adr/0108-server-relay-implementation-adjustments.md).

## But

Le serveur optionnel (`Server/Kazo.Os.Server`) relaie vers le vrai réseau ce
qu'un navigateur ne peut pas faire seul :

- les requêtes HTTP de **toutes** les méthodes, avec leur corps ;
- les **flux TCP** (`KazoTcpClient.ConnectAsync`) ;
- les **datagrammes UDP** (`KazoUdpClient`).

Une app qui joint un serveur **WebSocket** n'a pas besoin du relais :
`KazoClientWebSocket` ouvre le `WebSocket` du navigateur, sans serveur, avec la
même capacité `network.real.access` (ADR 0235 §1,
[`sdk-network.md`](../framework/sdk-network.md#websocket-vers-un-vrai-serveur-adr-0235-1)).

Il vérifie lui-même que l'app a le droit d'utiliser le relais, applique une
politique et des quotas configurés dans `appsettings.json`, et écrit un
avertissement au démarrage quand cette politique est affaiblie. Sans serveur,
rien ne change : l'OS se comporte comme une machine sans passerelle par
défaut.

Le relais est une API indépendante du mode de rendu de l'interface servie par
le serveur (ADR 0102) : ce chantier ne touche pas au rendu, que reprend
`exec/8`.

## Architecture

```
App (SDK)                         OS (Platform Core)                     Serveur
KazoHttpClient ──► AppNetworkGateway.SendRealAsync ──► IProxyClient ──► POST /api/proxy/http
KazoTcpClient  ──► AppNetworkGateway.ConnectTcpAsync ─┐                  GET  /api/proxy/tcp (WebSocket)
KazoUdpClient  ──► AppNetworkGateway.ConnectUdpAsync ─┴► OpenStreamAsync ► GET  /api/proxy/udp (WebSocket)
Paramètres     ──► KazoServerConnection.GetRelayPolicyAsync ────────────► GET  /api/proxy/policy
```

### Contrats (`Kazo.Os.Server.Contracts/Proxy`)

| Type | Rôle |
|---|---|
| `ProxyHttpRequest` | porte `Body` et `ContentType` ; `Create(...)` calcule `BodyBytes` et `BodyHash` (SHA-256 hexadécimal minuscule). |
| `ProxyPolicyResponse` | politique effective : relais actif, ports HTTP/TCP/UDP, ports bloqués TCP/UDP, plages privées, quotas, flux ouverts, apps accordées, ports approuvés par app (`AppPorts`), avertissements. |
| `ProxyStreamRoutes`, `ProxyStreamCloseCodes` | routes, paramètres de requête et codes de fermeture des flux. |
| `ProxyStreamRequest`, `ProxyStreamOpened`, `ProxyStreamClosure` | ouverture d'un flux (avec `UseTls` et `TargetHost`, ADR 0235 §2), adresse épinglée (avec `TlsProtocol` et `CertificateSubject`), raison de la fermeture. |
| `IProxyStreamChannel` | un flux ouvert : `SendAsync`, `ReceiveAsync` (`null` à la fin), `Closure`. |
| `ProxyStreamException` | refus d'ouverture avec son `ProxyErrorCode`. |

Nouveaux codes d'erreur : `GRANT_MISSING` (403), `RELAY_DISABLED` (503),
`BODY_MISMATCH` (400), `CONNECTION_REFUSED` et `HOST_UNREACHABLE` (502),
`TLS_FAILED` (flux refusé, fermeture `4001`, ADR 0235 §2).

### Serveur

| Classe | Rôle |
|---|---|
| `ProxyOptions`, `ProxyRelayPolicy` | section `KazoOs:Proxy` ; `ProxyRelayPolicy.From` pose les valeurs par défaut et calcule les limites effectives. |
| `ProxyGrantAuthority` | `GetGrantAsync` : l'app est dans `GrantedApps` ou un enregistrement `grants` synchronisé du compte lui donne `network.real.access` ; rend aussi les ports approuvés de cet enregistrement (`realNetworkPorts`, ADR 0235 §3). `ListGrantsWithPortsAsync` les liste pour la politique. |
| `ProxyService` | validations communes (relais actif, app, accord, port, adresse épinglée, plages bloquées, quotas) ; `ExecuteHttpRequestAsync` envoie le corps ; `OpenStreamAsync` connecte le socket. |
| `SocketProxyStreamConnector`, `SocketProxyStreamChannel` | connexion TCP/UDP (10 s), délai d'inactivité, compteurs d'octets, audit à la fermeture ; le canal lit et écrit dans la session TLS quand il y en a une. |
| `IProxyTlsNegotiator`, `SslStreamProxyTlsNegotiator` | négociation TLS avec la cible (`SslStream`, 10 s), certificat vérifié par le magasin du système et le nom `TargetHost` ; `TLS_FAILED` sinon. |
| `ProxyStreamWebSocketPump` | accepte le WebSocket et pompe les trames dans les deux sens. |
| `ProxyPolicyStartupReporter` | avertissements au démarrage (événements 1020 à 1022). |

### OS

| Classe | Rôle |
|---|---|
| `HttpProxyClient` | HTTP et flux (WebSocket `ws`/`wss`, jeton dans `access_token`). |
| `WebSocketProxyStreamChannel` | attend la trame texte d'ouverture ou la fermeture `4001` ; découpe un envoi TCP en blocs de 64 Kio. |
| `AppNetworkGateway` | `ConnectTcpAsync`, `ConnectUdpAsync`, relais HTTP avec corps. |
| `RelayedSocket` | `IKazoSocket` sur un flux : compte le trafic (`/proc/net/dev`) et la mémoire du noyau. |
| `AppServerConnectionGateway.GetRelayPolicyAsync` | politique du serveur pour Paramètres, sans jamais exposer le jeton. |

## Protocole d'un flux

1. Le client ouvre `GET /api/proxy/tcp?host=…&port=…&appId=…&access_token=…`
   (ou `/udp`) en WebSocket. Le paramètre `access_token` n'est accepté que sur
   ces deux routes ; ailleurs seul l'en-tête `Authorization` compte. En TCP,
   `tls=1` demande TLS vers la cible et `targetHost=…` donne le nom à vérifier
   quand il diffère de `host` (ADR 0235 §2).
2. Le serveur valide la requête et connecte le socket **avant** d'accepter le
   WebSocket. Avec `tls=1`, il fait aussi la négociation TLS avec la cible
   avant d'accepter.
3. Succès : la première trame est une trame **texte** JSON
   `ProxyStreamOpened` (adresse épinglée, port ; avec TLS, `tlsProtocol`, par
   exemple `TLS 1.3`, et `certificateSubject`). Toutes les suivantes sont
   binaires : un message TCP est une suite d'octets (64 Kio au plus), un
   message UDP est un datagramme (65 507 octets au plus, vide permis).
4. Refus : le WebSocket est accepté puis fermé avec `4001` et la raison
   `« <ProxyErrorCode> <message> »` ; le client lève `ProxyStreamException`.
5. Fin : `4000` quand la cible ferme, `4002` après l'inactivité, `1000` quand
   le client ferme.

## TLS ouvert par le relais (ADR 0235 §2)

Le navigateur n'a pas de `SslStream` : une app ne peut pas chiffrer elle-même
un flux TCP. Avec `UseTls`, le serveur ouvre TLS vers la cible et l'app lit et
écrit du texte en clair dans le flux ; le chiffrement est entre le serveur et
la cible, et le trajet navigateur → serveur est protégé par le `https` du
serveur.

- Ordre : socket connecté, puis négociation TLS (SNI = `TargetHost`, ou `host`
  sans lui), puis seulement le WebSocket accepté. Une cible refusée ne donne
  jamais un octet à l'app.
- Vérification : la chaîne doit finir à une racine du magasin du système du
  serveur, le nom doit correspondre, le certificat doit être valide à cette
  date. Les versions sont celles que le système accepte (TLS 1.2 et 1.3).
  La révocation suit la valeur par défaut de `SslStream` (pas de requête
  OCSP/CRL), comme `HttpClient`.
- **Aucune option pour accepter un certificat invalide**, ni dans
  `appsettings.json`, ni dans le SDK.
- Refus : certificat non approuvé, expiré ou pour un autre nom, cible en
  clair, ou négociation qui ne finit pas en 10 s → `TLS_FAILED`, fermeture
  `4001`, audit `PROXY_TLS_FAILED`. TLS sur UDP (DTLS) ou un `TargetHost` qui
  n'est pas un nom d'hôte → `MALFORMED_REQUEST` ; un `TargetHost` simulé →
  `SIMULATED_DOMAIN_BLOCKED`.
- L'audit `PROXY_TCP_OPEN` note la version TLS (`"tls":"TLS 1.3"`, ou `null`).
- Les ports restent filtrés par `Tcp:AllowedPorts` (6697 n'est pas livré ;
  les ports déclarés par l'app viennent avec l'ADR 0235 §3).

## Configuration (`appsettings.json`)

```json
"KazoOs": {
  "Proxy": {
    "Enabled": true,
    "Http": { "AllowedPorts": [ 80, 443 ] },
    "Tcp":  {
      "AllowedPorts": [ 80, 443 ],
      "BlockedPorts": [ 25, 111, 135, 137, 138, 139, 445, 465, 587, 2049 ]
    },
    "Udp":  {
      "AllowedPorts": [ 53, 123 ],
      "BlockedPorts": [ 19, 111, 137, 138, 389, 1900, 11211 ]
    },
    "AllowAllPorts": false,
    "AllowPrivateNetworks": false,
    "Quotas": {
      "Enabled": true,
      "MaxResponseBytes": 10485760,
      "MaxRequestBytes": 10485760,
      "MaxConcurrentPerDevice": 8,
      "MaxStreamsPerDevice": 4,
      "MaxRedirectHops": 5,
      "StreamIdleTimeoutSeconds": 300
    },
    "GrantedApps": [
      "org.kazoos.commands.curl", "org.kazoos.commands.wget",
      "org.kazoos.commands.ping", "org.kazoos.cmd.cat",
      "org.kazoos.commands.nmap", "org.kazoos.browser"
    ]
  }
}
```

Ce sont les valeurs livrées. Une liste absente ou vide garde la
valeur par défaut : le binder ajoute aux listes initialisées dans le code, donc
les défauts sont posés par `ProxyRelayPolicy.From`.

- **Tout autoriser** : `AllowAllPorts: true` ouvre tous les ports,
  `AllowPrivateNetworks: true` les plages privées (10/8, 172.16/12,
  192.168/16, fc00::/7…). Le bouclage, le lien local, les métadonnées de
  nuage (169.254.169.254) et les adresses non spécifiées restent toujours
  bloqués.
- **Désactiver les quotas** : `Quotas:Enabled: false` lève les limites de
  taille, de concurrence, de flux et d'inactivité. `MaxRedirectHops` reste
  appliqué (ADR 0108 §3).
- **Couper le relais** : `Enabled: false` ; toute requête reçoit
  `RELAY_DISABLED`.

Au démarrage, le serveur journalise un avertissement par affaiblissement
(`AllowAllPorts`, `AllowPrivateNetworks`, quotas désactivés, `BlockedPorts`
vide en TCP ou en UDP), un rappel que le
serveur est mono-utilisateur s'il y en a au moins un, et un avertissement si le
relais est coupé. Les mêmes phrases sont rendues dans
`ProxyPolicyResponse.Warnings` et affichées par Paramètres.

## Ports déclarés par l'app et `BlockedPorts` (ADR 0235 §3)

Une app qui a besoin d'autres ports que `AllowedPorts` (IRC : 6697 et 6667)
les déclare dans sa définition, avec `network.real.access` :

```csharp
public override IReadOnlyList<string> Capabilities => [AppCapabilities.NetworkRealAccess];
public override IReadOnlyList<string> RealNetworkPorts => ["tcp/6697", "tcp/6667"];
```

Le générateur écrit `"realNetworkPorts": ["tcp/6667", "tcp/6697"]` dans
`manifest.json` (triés, absent quand la liste est vide). Le validateur du
manifeste, donc le générateur et `kazopkg`, refuse un port mal écrit (forme
`tcp/N` ou `udp/N`, N de 1 à 65535, sans plage ni zéro en tête), un port
répété, une liste vide, ou des ports sans `network.real.access`.

La fenêtre d'approbation de `network.real.access` et le mode texte du terminal
affichent ces ports sous la capacité (« Vrais serveurs sur les ports : TCP 6667,
TCP 6697 », `RealNetworkPortText`).

Le relais décide ainsi, pour un flux TCP ou UDP :

1. port dans le `BlockedPorts` du protocole : **refusé** (`BLOCKED_PORT`), même
   s'il est dans `AllowedPorts`, déclaré et approuvé, ou avec `AllowAllPorts` ;
2. sinon `AllowAllPorts`, ou port dans `AllowedPorts` : accepté ;
3. sinon port déclaré par l'app **et** présent dans les `realNetworkPorts` de
   son accord synchronisé (`GrantsSyncPayload.RealNetworkPorts`) : accepté ;
4. sinon refusé (`BLOCKED_PORT`).

Un port TCP déclaré n'ouvre pas le même port UDP. Une entrée invalide d'un
accord est ignorée, jamais élargie. Une requête HTTP suit le `BlockedPorts`
TCP (HTTP passe par TCP) ; la sonde d'un seul port de `nmap` (ADR 0035) n'est
pas concernée.

Listes livrées : TCP 25, 465, 587 (courriel, contre le pourriel envoyé depuis
l'adresse du serveur), 111, 135, 137, 138, 139, 445, 2049 (RPC, partage de
fichiers Windows, NFS) ; UDP 19, 111, 137, 138, 389, 1900, 11211 (services
connus pour l'amplification d'attaques). Une liste absente garde ces valeurs ;
`"BlockedPorts": []` la vide (`ProxyOptions.KeepExplicitEmptyLists`, le binder
ne sait pas lier un tableau vide) et le serveur en avertit au démarrage.

Paramètres (section du serveur) affiche les ports bloqués TCP et UDP et, pour
chaque app dont l'accord porte des ports, ces ports.

**Limite actuelle** : aucun code n'émet encore d'accord synchronisé avec ses
ports. L'OS ne pousse jamais d'accord (ADR 0031) et le serveur n'a pas de route
pour en émettre ; la façon dont l'approbation de l'OS arrive au serveur est une
question posée à Benoit dans le fil « Accepter les ports déclarés par les
apps ». Jusque-là, seules les apps de `GrantedApps` utilisent le relais, avec
`AllowedPorts`.

## Utilisation dans une app

```csharp
// TCP : un flux comme NetworkStream.
await using KazoNetworkStream stream = await KazoTcpClient.ConnectAsync("example.com", 443);
await stream.WriteAsync(request);
int read = await stream.ReadAsync(buffer); // 0 quand la cible a fermé

// TLS ouvert par le relais (ADR 0235 §2) : l'app lit et écrit du texte en clair.
await using KazoNetworkStream irc = await KazoTcpClient.ConnectAsync(
    "irc.libera.chat", 6697, new KazoTcpConnectOptions { UseTls = true }, cancellationToken);
string? version = irc.TlsProtocol;               // « TLS 1.3 »
string? subject = irc.RemoteCertificateSubject;  // « CN=irc.libera.chat »

// UDP : un socket « connecté » à une cible.
await using KazoUdpClient udp = await KazoUdpClient.ConnectAsync("pool.ntp.org", 123);
await udp.SendAsync(ntpRequest);
KazoUdpReceiveResult answer = await udp.ReceiveAsync();

// POST relayé avec son corps si l'hôte n'est pas simulé.
using KazoHttpClient http = new();
KazoHttpResponse response = await http.PostAsync(url, new KazoStringContent(json, "application/json"));
```

- Un socket demande `network.simulated.write` ; le relais demande en plus
  `network.real.access` (dangereuse).
- Les lectures et écritures synchrones lèvent `NotSupportedException` : le
  navigateur n'a qu'un fil.
- Un hôte du réseau simulé, ou cette machine, n'a pas de flux brut : la
  connexion est refusée (`SocketError.ConnectionRefused`).
- Sans serveur ou sans `network.real.access`, un hôte inconnu donne
  `SocketError.HostNotFound`.

| Refus du relais | `SocketError` |
|---|---|
| `BLOCKED_PORT`, `BLOCKED_ADDRESS`, `CONNECTION_REFUSED` | `ConnectionRefused` |
| `HOST_UNREACHABLE` | `HostUnreachable` |
| `TLS_FAILED` | `AuthenticationException` (comme `SslStream`), `KazoNetworkError.TlsFailed` dans la passerelle |
| `GRANT_MISSING`, `RELAY_DISABLED`, `SIMULATED_DOMAIN_BLOCKED` | `HostNotFound` |
| autre | `ConnectionFailed` (erreur réseau du SDK) |

Paramètres (section du serveur) affiche la politique du serveur connecté avec
`KazoServerConnection.GetRelayPolicyAsync()` : ports, ports bloqués
(`TcpBlockedPorts`, `UdpBlockedPorts`), plages privées, quotas, flux ouverts,
apps accordées, ports déclarés par les apps approuvées (`AppPorts`) et
avertissements.

## Décisions clés

- Accord vérifié côté serveur par `GrantedApps` **ou** par les accords
  synchronisés (ADR 0108 §1) : sans la liste, aucun accord n'existerait sur
  le serveur et le relais refuserait tout. Question posée à Benoit dans le
  fil « V3 10 : relais du serveur ».
- Socket connecté avant d'accepter le WebSocket, refus en code de fermeture
  `4001` : un `WebSocket` de navigateur ne lit pas le statut d'une mise à
  niveau refusée (ADR 0108 §2).
- Jeton dans la requête seulement sur les routes de flux : un `WebSocket` de
  navigateur ne peut pas envoyer `Authorization`.
- Adresse résolue une fois et épinglée pour la connexion, contre le
  rebinding DNS.
- `HEAD` ne demande pas de corps ; toute autre méthode le relaie, vérifié par
  sa taille et son hash. Une redirection 303, ou 301/302 après un `POST`,
  devient un `GET` sans corps.

- Vrais sites du navigateur (ADR 0210) : l'en-tête `Cookie` est relayé (seuls
  `Host`, `Authorization`, `X-Forwarded-For` et `X-Real-IP` restent bloqués) ;
  avec `FollowRedirects = false`, le serveur rend la redirection telle quelle
  (`Location`, `Set-Cookie`) et l'OS suit les sauts ; le Navigateur
  (`org.kazoos.browser`) est dans les `GrantedApps` par défaut. Le quota de
  8 requêtes par appareil reste un refus : l'OS en garde 6 au plus en cours et
  retente un `QUOTA_EXCEEDED`.

## Liste des tâches

- [x] Corps des requêtes HTTP : contrat, vérification taille et hash, toutes les méthodes, redirections.
- [x] Options `KazoOs:Proxy` (ports, plages privées, quotas, apps accordées) et politique effective.
- [x] Contrôle de l'accord côté serveur (`GrantedApps` et accords synchronisés).
- [x] Flux TCP et UDP : connecteur, canal, pompe WebSocket, codes de fermeture, audit.
- [x] Jeton dans la requête sur les routes de flux seulement.
- [x] Avertissements au démarrage et dans la réponse de politique.
- [x] Client WebSocket de l'OS et client direct du serveur.
- [x] `KazoTcpClient.ConnectAsync`, `KazoNetworkStream`, `KazoUdpClient`, erreurs d'une vraie pile.
- [x] `KazoServerConnection.GetRelayPolicyAsync` et panneau de Paramètres.
- [x] Tests : service, endpoints WebSocket, client direct, passerelle réseau, politique.
- [x] ADR 0108 et documentation.
- [x] Ports déclarés par l'app (ADR 0235 §3) : `RealNetworkPorts` du manifeste, validation, fenêtre d'approbation et terminal, relais (`BlockedPorts`, ports approuvés de l'accord), politique et Paramètres, avertissement de liste vide, tests.
- [ ] Émission par le serveur de l'accord avec ses ports (question 1 du fil « Accepter les ports déclarés par les apps »).
- [x] TLS ouvert par le relais (ADR 0235 §2, chantier `real-network/2-relay-tls`) : `tls=1`, `targetHost`, `SslStream` avant d'accepter le WebSocket, `TLS_FAILED`, `ProxyStreamOpened` avec la session, `KazoTcpConnectOptions`, tests avec un serveur TLS de test (certificat approuvé, non approuvé, mauvais nom, cible en clair, flux en clair inchangé).

Voir aussi l'[ADR 0191](adr/0191-installable-local-server.md) : serveur installable sur l'ordinateur ou le réseau local.
