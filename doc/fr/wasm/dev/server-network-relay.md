> [English version](../../../en/wasm/dev/server-network-relay.md)

# Relais réseau du serveur optionnel

Chantier `v3/10-server-proxy` du [plan de fin de migration](plans/wasm-v3-completion-plan.md),
décisions dans l'[ADR 0102](adr/0102-server-network-relay-tcp-udp-and-policy.md)
et ses ajustements dans l'[ADR 0108](adr/0108-server-relay-implementation-adjustments.md).

## But

Le serveur optionnel (`Server/HackerOs.Server`) relaie vers le vrai réseau ce
qu'un navigateur ne peut pas faire seul :

- les requêtes HTTP de **toutes** les méthodes, avec leur corps ;
- les **flux TCP** (`HackTcpClient.ConnectAsync`) ;
- les **datagrammes UDP** (`HackUdpClient`).

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
HackHttpClient ──► AppNetworkGateway.SendRealAsync ──► IProxyClient ──► POST /api/proxy/http
HackTcpClient  ──► AppNetworkGateway.ConnectTcpAsync ─┐                  GET  /api/proxy/tcp (WebSocket)
HackUdpClient  ──► AppNetworkGateway.ConnectUdpAsync ─┴► OpenStreamAsync ► GET  /api/proxy/udp (WebSocket)
Paramètres     ──► HackServerConnection.GetRelayPolicyAsync ────────────► GET  /api/proxy/policy
```

### Contrats (`HackerOs.Server.Contracts/Proxy`)

| Type | Rôle |
|---|---|
| `ProxyHttpRequest` | porte `Body` et `ContentType` ; `Create(...)` calcule `BodyBytes` et `BodyHash` (SHA-256 hexadécimal minuscule). |
| `ProxyPolicyResponse` | politique effective : relais actif, ports HTTP/TCP/UDP, plages privées, quotas, flux ouverts, apps accordées, avertissements. |
| `ProxyStreamRoutes`, `ProxyStreamCloseCodes` | routes, paramètres de requête et codes de fermeture des flux. |
| `ProxyStreamRequest`, `ProxyStreamOpened`, `ProxyStreamClosure` | ouverture d'un flux, adresse épinglée, raison de la fermeture. |
| `IProxyStreamChannel` | un flux ouvert : `SendAsync`, `ReceiveAsync` (`null` à la fin), `Closure`. |
| `ProxyStreamException` | refus d'ouverture avec son `ProxyErrorCode`. |

Nouveaux codes d'erreur : `GRANT_MISSING` (403), `RELAY_DISABLED` (503),
`BODY_MISMATCH` (400), `CONNECTION_REFUSED` et `HOST_UNREACHABLE` (502).

### Serveur

| Classe | Rôle |
|---|---|
| `ProxyOptions`, `ProxyRelayPolicy` | section `HackerOs:Proxy` ; `ProxyRelayPolicy.From` pose les valeurs par défaut et calcule les limites effectives. |
| `ProxyGrantAuthority` | `IsGrantedAsync` : l'app est dans `GrantedApps` ou un enregistrement `grants` synchronisé du compte lui donne `network.real.access`. |
| `ProxyService` | validations communes (relais actif, app, accord, port, adresse épinglée, plages bloquées, quotas) ; `ExecuteHttpRequestAsync` envoie le corps ; `OpenStreamAsync` connecte le socket. |
| `SocketProxyStreamConnector`, `SocketProxyStreamChannel` | connexion TCP/UDP (10 s), délai d'inactivité, compteurs d'octets, audit à la fermeture. |
| `ProxyStreamWebSocketPump` | accepte le WebSocket et pompe les trames dans les deux sens. |
| `ProxyPolicyStartupReporter` | avertissements au démarrage (événements 1020 à 1022). |

### OS

| Classe | Rôle |
|---|---|
| `HttpProxyClient` | HTTP et flux (WebSocket `ws`/`wss`, jeton dans `access_token`). |
| `WebSocketProxyStreamChannel` | attend la trame texte d'ouverture ou la fermeture `4001` ; découpe un envoi TCP en blocs de 64 Kio. |
| `AppNetworkGateway` | `ConnectTcpAsync`, `ConnectUdpAsync`, relais HTTP avec corps. |
| `RelayedSocket` | `IHackSocket` sur un flux : compte le trafic (`/proc/net/dev`) et la mémoire du noyau. |
| `AppServerConnectionGateway.GetRelayPolicyAsync` | politique du serveur pour Paramètres, sans jamais exposer le jeton. |

## Protocole d'un flux

1. Le client ouvre `GET /api/proxy/tcp?host=…&port=…&appId=…&access_token=…`
   (ou `/udp`) en WebSocket. Le paramètre `access_token` n'est accepté que sur
   ces deux routes ; ailleurs seul l'en-tête `Authorization` compte.
2. Le serveur valide la requête et connecte le socket **avant** d'accepter le
   WebSocket.
3. Succès : la première trame est une trame **texte** JSON
   `ProxyStreamOpened` (adresse épinglée, port). Toutes les suivantes sont
   binaires : un message TCP est une suite d'octets (64 Kio au plus), un
   message UDP est un datagramme (65 507 octets au plus, vide permis).
4. Refus : le WebSocket est accepté puis fermé avec `4001` et la raison
   `« <ProxyErrorCode> <message> »` ; le client lève `ProxyStreamException`.
5. Fin : `4000` quand la cible ferme, `4002` après l'inactivité, `1000` quand
   le client ferme.

## Configuration (`appsettings.json`)

```json
"HackerOs": {
  "Proxy": {
    "Enabled": true,
    "Http": { "AllowedPorts": [ 80, 443 ] },
    "Tcp":  { "AllowedPorts": [ 80, 443 ] },
    "Udp":  { "AllowedPorts": [ 53, 123 ] },
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
      "org.hackeros.commands.curl", "org.hackeros.commands.wget",
      "org.hackeros.commands.ping", "org.hackeros.cmd.cat",
      "org.hackeros.commands.nmap"
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
(`AllowAllPorts`, `AllowPrivateNetworks`, quotas désactivés), un rappel que le
serveur est mono-utilisateur s'il y en a au moins un, et un avertissement si le
relais est coupé. Les mêmes phrases sont rendues dans
`ProxyPolicyResponse.Warnings` et affichées par Paramètres.

## Utilisation dans une app

```csharp
// TCP : un flux comme NetworkStream.
await using HackNetworkStream stream = await HackTcpClient.ConnectAsync("example.com", 443);
await stream.WriteAsync(request);
int read = await stream.ReadAsync(buffer); // 0 quand la cible a fermé

// UDP : un socket « connecté » à une cible.
await using HackUdpClient udp = await HackUdpClient.ConnectAsync("pool.ntp.org", 123);
await udp.SendAsync(ntpRequest);
HackUdpReceiveResult answer = await udp.ReceiveAsync();

// POST relayé avec son corps si l'hôte n'est pas simulé.
using HackHttpClient http = new();
HackHttpResponse response = await http.PostAsync(url, new HackStringContent(json, "application/json"));
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
| `GRANT_MISSING`, `RELAY_DISABLED`, `SIMULATED_DOMAIN_BLOCKED` | `HostNotFound` |
| autre | `ConnectionFailed` (erreur réseau du SDK) |

Paramètres (section du serveur) affiche la politique du serveur connecté avec
`HackServerConnection.GetRelayPolicyAsync()` : ports, plages privées, quotas,
flux ouverts, apps accordées et avertissements.

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

## Liste des tâches

- [x] Corps des requêtes HTTP : contrat, vérification taille et hash, toutes les méthodes, redirections.
- [x] Options `HackerOs:Proxy` (ports, plages privées, quotas, apps accordées) et politique effective.
- [x] Contrôle de l'accord côté serveur (`GrantedApps` et accords synchronisés).
- [x] Flux TCP et UDP : connecteur, canal, pompe WebSocket, codes de fermeture, audit.
- [x] Jeton dans la requête sur les routes de flux seulement.
- [x] Avertissements au démarrage et dans la réponse de politique.
- [x] Client WebSocket de l'OS et client direct du serveur.
- [x] `HackTcpClient.ConnectAsync`, `HackNetworkStream`, `HackUdpClient`, erreurs d'une vraie pile.
- [x] `HackServerConnection.GetRelayPolicyAsync` et panneau de Paramètres.
- [x] Tests : service, endpoints WebSocket, client direct, passerelle réseau, politique.
- [x] ADR 0108 et documentation.

Voir aussi l'[ADR 0191](adr/0191-installable-local-server.md) : serveur installable sur l'ordinateur ou le réseau local.
