# ADR 0108 : ajustements de la mise en œuvre du relais du serveur

## Statut

Accepté le 2026-10-17 (chantier `v3/10-server-proxy`).

- **Complète :** [ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md)
  (relais du serveur), sans en changer les décisions.
- **Document :** [`server-network-relay.md`](../server-network-relay.md).

## Contexte

En réalisant l'ADR 0102, cinq points n'y étaient pas tranchés ou ne pouvaient
pas l'être tels quels :

1. §4 veut que le serveur vérifie l'accord `network.real.access` dans **ses**
   accords synchronisés. Mais l'OS ne pousse jamais d'accord (ADR 0031 §1) et
   aucune API du serveur n'en émet : la base du serveur n'en contient aucun.
   Appliqué seul, le contrôle refuserait tout, `curl` compris.
2. §2 et §3 ouvrent les flux en WebSocket, mais un `WebSocket` de navigateur
   ne peut pas porter d'en-tête `Authorization`, et il ne peut pas non plus
   lire le statut HTTP d'une mise à niveau refusée.
3. §5 ne dit pas ce qu'il advient de `MaxRedirectHops` quand les quotas sont
   désactivés.
4. L'ADR 0102 ne dit pas quelle capacité locale demande un flux, ni ce que
   devient une connexion vers un hôte du réseau simulé, qui n'a pas de flux
   d'octets bruts.
5. Le serveur peut aussi servir l'OS en rendu côté serveur (ADR 0027) : le
   client direct (ADR 0036) doit ouvrir un flux sans WebSocket.

## Décision

### 1. Accord du serveur : liste de l'opérateur, plus les accords synchronisés

Le serveur accorde le relais à une app si son identifiant figure dans
`HackerOs:Proxy:GrantedApps` (`appsettings.json`) **ou** si la dernière
révision d'un enregistrement `grants` du compte nomme cette app avec
`network.real.access` sans être révoquée. La liste par défaut contient les
commandes système qui déclarent `network.real.access` : `curl`, `wget`,
`ping`, `cat` et `nmap`, que l'OS accorde déjà par sa politique système.

C'est l'option recommandée de la question posée à Benoit dans le fil
« V3 10 : relais du serveur » le 2026-10-17 ; si sa réponse en choisit une
autre, un nouvel ADR remplace ce paragraphe.

L'identifiant d'app reste déclaré par le client : le contrôle empêche un
client modifié d'ouvrir le relais à une app que le serveur ne connaît pas,
pas de se faire passer pour `curl`. Le serveur est mono-utilisateur
(ADR 0027) ; la vraie frontière reste le compte et l'appareil.

### 2. Ouverture d'un flux

- Le serveur valide la requête et connecte le socket **avant** d'accepter le
  WebSocket. En cas de succès, sa première trame est une trame **texte**
  (`ProxyStreamOpened`, JSON : adresse épinglée et port) ; toutes les trames
  suivantes sont binaires.
- Un refus accepte le WebSocket puis le ferme aussitôt avec le code `4001`
  et la raison `« <ProxyErrorCode> <message> »`. Les autres codes : `4000`
  la cible a fermé, `4002` inactivité.
- Le jeton d'accès peut passer dans le paramètre `access_token` de la requête,
  sur les deux routes de flux seulement, comme SignalR. Ailleurs, seul
  l'en-tête `Authorization` est accepté.

### 3. Quotas désactivés

`Quotas:Enabled = false` lève les limites de taille, de concurrence, de flux
et d'inactivité. `MaxRedirectHops` reste appliqué : sans lui une boucle de
redirections ne finit jamais.

### 4. Côté OS

- Un socket TCP ou UDP demande `network.simulated.write` : il transporte des
  octets arbitraires dans les deux sens, comme une requête `POST`.
- Le réseau simulé n'a pas de flux bruts : une connexion vers un hôte qu'il
  connaît, ou vers cette machine, est refusée (`ECONNREFUSED`). Un hôte
  inconnu passe par le relais si l'app a `network.real.access`, sinon il est
  introuvable.
- Un refus du relais devient l'erreur d'une vraie pile : port ou plage
  bloqués et refus de la cible donnent `ConnectionRefused`, cible muette
  `HostUnreachable`, accord manquant ou relais coupé `HostNotFound` (pas de
  route).

### 5. Flux en processus

`IProxyClient.OpenStreamAsync` a deux implémentations : `HttpProxyClient`
(WebSocket, hôtes WebAssembly) et `DirectProxyClient` (socket du serveur
lui-même, OS servi en rendu côté serveur), avec les mêmes validations.

## Conséquences

- Le vrai réseau marche dès qu'un appareil est connecté, pour les commandes
  système, sans configuration.
- Une app tierce qui veut le relais doit être ajoutée par l'opérateur à
  `GrantedApps`, ou recevoir un accord émis par le serveur (aucune API ne le
  fait encore, comme le notait déjà l'ADR 0031).
- Le binder de configuration ajoute aux listes initialisées dans le code au
  lieu de les remplacer : les valeurs par défaut sont posées par
  `ProxyRelayPolicy.From`, et une liste vide ou absente garde le défaut.
