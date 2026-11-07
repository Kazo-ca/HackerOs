> [English version](../../../../en/wasm/dev/adr/0235-real-network-for-chat-apps.md)

# ADR 0235 : le vrai réseau pour les apps de discussion (WebSocket dans le SDK, TLS par le relais, ports demandés par l'app)

## Statut

Acceptée le 2026-11-07 ; Benoit a choisi les recommandations (questions 3, 4,
5, 9, 10 et 11 du fil « Planifier l'app IRC », voir « Questions posées à
Benoit »). Code : issue [#395](https://github.com/Kazo-ca/Os/issues/395),
série `real-network/base`.

- **Complète :** l'[ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md)
  et l'[ADR 0108](0108-server-relay-implementation-adjustments.md) (relais du
  serveur : TLS vers la cible et ports déclarés par l'app).
- **Ne change pas :** la capacité dangereuse `network.real.access` et son
  approbation, la vérification de l'accord par le serveur, les plages d'adresses
  bloquées, l'adresse épinglée, les quotas.
- **Pour :** l'app IRC ([Os-App-IRC](https://github.com/Kazo-ca/Os-App-IRC),
  plan `doc/fr/plan.md`).

## Contexte

- Un navigateur ne peut pas ouvrir de connexion TCP. Une app ne joint un vrai
  serveur en TCP que par le relais du **serveur optionnel**
  (`KazoTcpClient.ConnectAsync`, [relais réseau](../server-network-relay.md)).
  Sans serveur, une app de discussion ne peut rien joindre, alors qu'aucune
  fonction ne doit dépendre du serveur (AGENTS.md §I.1).
- Le SDK n'a pas d'API WebSocket. Beaucoup de serveurs de discussion modernes
  (IRC par WebSocket, IRCv3) acceptent pourtant une connexion WebSocket que le
  navigateur sait ouvrir seul.
- TLS ne marche pas par-dessus `KazoTcpClient` : `SslStream` n'existe pas dans
  .NET pour le navigateur. Le port IRC chiffré (6697) est donc hors de portée,
  et en clair (6667) le mot de passe du compte part en clair du serveur vers
  internet.
- Le relais livré n'accepte que les ports TCP 80 et 443 (`Tcp:AllowedPorts`).
  Une app qui a besoin d'un autre port dépend d'un administrateur qui modifie
  `appsettings.json`.

## Décision

### 1. `KazoClientWebSocket` dans le SDK

- Une classe du SDK `KazoClientWebSocket` (espace `Kazo.Os.AppSdk.Net`) suit
  `System.Net.WebSockets.ClientWebSocket`, comme le reste du SDK suit .NET avec
  le préfixe `Kazo` : `Options.AddSubProtocol`, `ConnectAsync(Uri)`,
  `SendAsync`, `ReceiveAsync`, `CloseAsync`, `State`, `SubProtocol`,
  `CloseStatus`.
- L'OS ouvre le vrai `WebSocket` du navigateur (un module JavaScript de
  Browser Infrastructure, jamais de l'app) et passe les trames à
  `AppNetworkGateway`, qui compte le trafic dans `/proc/net/dev` comme pour
  `RelayedSocket`.
- Capacité : **`network.real.access`**, la même que pour le relais. Un seul
  accord dit « cette app parle au vrai réseau ». Sans elle :
  `UnauthorizedAccessException`.
- **Le serveur n'est pas nécessaire.** Les limites sont celles du navigateur :
  pas d'en-têtes libres, `wss://` seulement quand l'OS est servi en `https`,
  et un refus du serveur sans détail (`WebSocketException` avec
  `WebSocketError.Faulted`).
- Une adresse d'un hôte du réseau simulé est refusée pour l'instant
  (`WebSocketError.Faulted`, message « hôte simulé ») : le TCP vers un hôte
  simulé est l'issue [#393](https://github.com/Kazo-ca/Os/issues/393).

### 2. TLS ouvert par le relais

- `KazoTcpClient.ConnectAsync` reçoit une surcharge avec des options
  (`KazoTcpConnectOptions` : `UseTls`, `TargetHost` pour le SNI et la
  vérification du certificat).
- Avec `UseTls`, la route de flux `GET /api/proxy/tcp` reçoit `tls=1` : le
  serveur connecte le socket, fait la négociation TLS avec la cible
  (`SslStream`, certificats vérifiés par le magasin du système, nom vérifié
  contre `TargetHost`), puis seulement accepte le WebSocket. L'app lit et écrit
  du texte en clair dans le flux ; le chiffrement est entre le serveur et la
  cible.
- Un certificat invalide refuse la connexion avec le nouveau code
  `TLS_FAILED` (fermeture `4001`), traduit en `AuthenticationException` côté
  SDK, comme `SslStream`. Aucune option pour accepter un certificat invalide.
- `ProxyStreamOpened` ajoute la version TLS négociée et le sujet du
  certificat, pour que l'app puisse les afficher.
- Le trajet navigateur → serveur reste protégé par le `https` du serveur
  lui-même (ce que le relais exige déjà pour le jeton).

### 3. Ports demandés par l'app

- L'app déclare dans son manifeste les ports réels dont elle a besoin (TCP et
  UDP ; la forme exacte de la propriété de `[AppManifest]` est fixée au
  chantier, par exemple `RealNetworkPorts = ["tcp/6697", "tcp/6667"]`).
- La fenêtre d'approbation de `network.real.access` les affiche (« cette app
  veut joindre de vrais serveurs sur les ports 6697 et 6667 »). L'accord
  synchronisé garde la liste des ports approuvés.
- Le relais accepte un port s'il est dans `AllowedPorts` **ou** déclaré et
  approuvé pour cette app, **sauf** s'il est dans la nouvelle liste
  `BlockedPorts` de la politique. `BlockedPorts` gagne toujours. Valeurs
  livrées : au moins 25 (SMTP, contre le pourriel) ; le chantier complète la
  liste et la documente.
- `AllowPrivateNetworks`, les adresses toujours bloquées (bouclage, lien local,
  métadonnées de nuage) et les quotas s'appliquent comme avant. `AllowAllPorts`
  garde son sens.
- Paramètres affiche `BlockedPorts` et, pour chaque app accordée, ses ports
  déclarés.

### 4. Ordre

Une série `real-network/base` dans ce dépôt, un chantier par point
(`real-network/1-websocket`, `real-network/2-relay-tls`,
`real-network/3-declared-ports`), puis une version de l'OS qui les publie. Les
chantiers de l'app IRC qui se connectent attendent cette version ; ceux qui
n'ont pas besoin du réseau (solution, protocole, interface) commencent avant.

## Questions posées à Benoit

Réponses de Benoit (2026-11-07) : 3 C, 4 A, 5 A avec la précision « les
applications peuvent maintenant demander de se connecter à un port s'il n'y a
pas de policy qui le refuse », puis 9, 10 et 11 : les recommandations.

3. Par où passe la connexion de l'app IRC ?
   - A) Par le relais du serveur seulement.
   - B) WebSocket direct seulement (nouvelle API du SDK).
   - C) Les deux : WebSocket quand le réseau l'accepte, sinon le relais (option
     disponible seulement quand le serveur est là). **(Choisi)**
4. Connexion chiffrée (TLS, 6697) ?
   - A) Le relais ouvre TLS vers la cible (changement de l'OS + ADR).
     **(Recommandé, choisi)**
   - B) 6667 en clair seulement d'abord, TLS plus tard.
5. Ports IRC sur le relais ?
   - A) Ajouter les ports IRC aux ports livrés. **(Recommandé, choisi, avec la
     précision ci-dessus, détaillée par la question 9)**
   - B) Ne rien changer, l'administrateur ajoute les ports.
9. « Demander un port si aucune policy ne le refuse » ?
   - A) L'app déclare ses ports dans son manifeste, l'approbation les affiche,
     le serveur les accepte sauf `BlockedPorts`. **(Recommandé, choisi)**
   - B) Tous les ports pour toutes les apps sauf `BlockedPorts`, sans
     déclaration.
10. Organisation des changements de l'OS ?
    - A) Une ADR en trois sections, une série `real-network/base`.
      **(Recommandé, choisi)**
    - B) Trois ADR et trois séries.
11. Capacité du WebSocket direct ?
    - A) La même `network.real.access`. **(Recommandé, choisi)**
    - B) Une nouvelle capacité `network.real.websocket`.

## Conséquences

- Une app de discussion marche **sans serveur** avec les réseaux qui acceptent
  WebSocket, et joint les autres (TCP, TLS compris) quand le serveur est
  connecté.
- Une app tierce n'a plus besoin qu'un administrateur ouvre ses ports : elle
  les déclare, l'utilisateur les voit à l'approbation, le serveur garde le
  dernier mot avec `BlockedPorts`.
- Le SDK grandit (`KazoClientWebSocket`, `KazoTcpConnectOptions`, propriété du
  manifeste) : `PublicAPI.Shipped.txt`, fixture `sdk-compat` et version
  mineure du SDK.
- Tests prévus : WebSocket refusé sans capacité, trafic compté ; TLS accepté,
  certificat invalide refusé (`TLS_FAILED`), nom de cible faux refusé ; port
  déclaré et approuvé accepté, port déclaré mais dans `BlockedPorts` refusé,
  port non déclaré refusé.
- Docs à mettre à jour au code : [`server-network-relay.md`](../server-network-relay.md),
  [`capabilities.md`](../../framework/capabilities.md), `features.md`, entrée
  `changes/unreleased`.
