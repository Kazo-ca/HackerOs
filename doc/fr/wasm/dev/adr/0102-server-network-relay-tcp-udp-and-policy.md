> [English version](../../../../en/wasm/dev/adr/0102-server-network-relay-tcp-udp-and-policy.md)

# ADR 0102 : relais réseau du serveur optionnel (corps HTTP, flux TCP, UDP, accord vérifié, politique dans `appsettings`)

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour le chantier `v3/10-server-proxy`).

- **Complète :** ADR 0023 (partie relais, le reste est remplacé par l'ADR 0092),
  [ADR 0035](0035-nmap-tcp-probe-fallback.md) (sonde TCP de `nmap`), ADR 0056 §6
  (repli sur le réseau réel décidé par l'OS, capacité dangereuse
  `network.real.access`), ADR 0031 (accords synchronisés, le serveur fait
  autorité).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  ligne `v3/10-server-proxy` ; manque R19.

## Contexte

- `ProxyEndpoints` expose `POST /api/proxy/http`, `POST /api/proxy/tcp-probe` et
  `GET /api/proxy/policy`. `Program.cs:21` annonce « HTTP/TCP/UDP », mais
  `ProxyProtocol.Tcp` et `.Udp` sont refusés par `ValidateRequest`
  (`ProxyService.cs:386`) et n'ont pas de contrat de flux.
- `ProxyHttpRequest` porte le hash et la taille du corps, jamais ses octets :
  un `POST` arrive vide.
- Les protections SSRF (`ProxyService.cs`) sont des constantes : suffixes
  simulés refusés, ports 80/443, plages privées bloquées, IP épinglée contre le
  rebinding DNS. Les quotas aussi (`MaxResponseBytes` 10 Mio,
  `MaxConcurrentPerDevice` 8, 5 redirections, sonde de 5 s).
- Le serveur vérifie l'appareil et la présence d'un `AppId`, mais pas que
  l'app a l'accord `network.real.access`.

## Décision

### 1. Corps des requêtes HTTP

`ProxyHttpRequest` reçoit le corps (`Body`, octets, 10 Mio au plus par défaut)
avec son type de contenu ; le serveur vérifie qu'il correspond au hash et à la
taille annoncés, puis l'envoie. `HackHttpClient` (ADR 0056) le transmet quand il
retombe sur le réseau réel.

### 2. Flux TCP

- `GET /api/proxy/tcp?host=…&port=…&appId=…` ouvert en **WebSocket** : chaque
  message binaire est un morceau du flux dans un sens, la fermeture du
  WebSocket ferme la connexion TCP (et inversement, avec le code de fermeture
  qui dit qui a fermé).
- Mêmes validations que HTTP (suffixes simulés, plages privées, IP épinglée),
  mais les ports permis viennent de `Proxy:Tcp:AllowedPorts`.
- Côté OS, `HackTcpClient` (ADR 0056) l'utilise quand la cible n'est pas sur le
  réseau simulé et que l'app a `network.real.access`.

### 3. UDP

- `GET /api/proxy/udp?host=…&port=…&appId=…` en WebSocket : un message binaire
  est **un** datagramme, dans chaque sens, vers une seule destination (socket
  UDP « connecté »). Pas de diffusion ni de multidiffusion.
- Ports permis : `Proxy:Udp:AllowedPorts`, par défaut `53` (DNS) et `123`
  (NTP). Nouveau `HackUdpClient` dans le SDK réseau.

### 4. Accord vérifié par le serveur

Le serveur refuse (`403`, `ProxyErrorCode.GrantMissing`) une requête dont
l'app n'a pas l'accord `network.real.access` pour cet utilisateur dans **ses**
accords synchronisés (ADR 0031, le serveur fait autorité). Le contrôle de l'OS
reste ; celui du serveur empêche un client modifié de s'en passer.

### 5. Politique dans `appsettings`

Section `HackerOs:Proxy` de `appsettings.json`, lue au démarrage par des
`IOptions` :

| Clé | Défaut | Effet |
|---|---|---|
| `Enabled` | `true` | coupe tout le relais |
| `Http:AllowedPorts` | `[80, 443]` | ports HTTP |
| `Tcp:AllowedPorts` | `[80, 443]` | ports des flux TCP (`nmap` garde sa sonde sur tout port, ADR 0035) |
| `Udp:AllowedPorts` | `[53, 123]` | ports UDP |
| `AllowAllPorts` | `false` | « tout autoriser » : aucun filtre de port |
| `AllowPrivateNetworks` | `false` | autorise les plages privées (le réseau local de la machine du serveur) ; la boucle locale et les métadonnées du cloud (`169.254.0.0/16`) restent bloquées |
| `Quotas:Enabled` | `true` | « désactiver les quotas » |
| `Quotas:MaxResponseBytes`, `MaxRequestBytes`, `MaxConcurrentPerDevice`, `MaxStreamsPerDevice`, `MaxRedirectHops`, `StreamIdleTimeoutSeconds` | 10 Mio, 10 Mio, 8, 4, 5, 300 | limites |

- Au démarrage, le serveur écrit un **avertissement** pour chaque réglage
  risqué (`AllowAllPorts`, `AllowPrivateNetworks`, quotas désactivés) et rappelle
  qu'il est mono-utilisateur.
- Le relais est une API du serveur, indépendante de la façon dont le serveur
  sert l'interface : il marche de la même façon aujourd'hui (rendu côté serveur,
  ADR 0027) et quand le serveur enverra l'OS en WebAssembly avec seulement son
  API (choix de Benoit du 2026-10-17, chantier `exec/8`).
- `GET /api/proxy/policy` renvoie la politique effective, que l'OS affiche
  dans Paramètres (connexion au serveur).
- Le commentaire de `Program.cs` dit ce qui existe vraiment.

## Conséquences

- `v3/10` ajoute les contrats (`HackerOs.Server.Contracts/Proxy`), les deux
  points WebSocket, les options, le contrôle des accords, `HackUdpClient`, et des
  tests serveur (corps transmis, flux TCP aller-retour contre un écho local avec
  `AllowPrivateNetworks`, datagramme UDP, accord manquant, avertissements).
- L'hôte Ecosystem sans serveur ne change pas : sans serveur, le réseau réel
  n'est simplement pas disponible.
- `sdk-network.md`, `sdk-reference.md`, `capabilities.md` et la doc du serveur
  (`docs/old/hosting-model.md` par renvoi, `project-overview.md`) sont mis à jour.
