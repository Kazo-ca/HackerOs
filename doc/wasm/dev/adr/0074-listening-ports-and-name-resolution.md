# ADR 0074 : ports à l'écoute, résolveur DNS de l'OS et corps HTTP réels

## Statut

Accepté le 2026-10-16 (proposé le même jour, puis validé par Benoit avec le
questionnaire de la section 4 du plan).

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Complète :** ADR 0055 et 0056 §6 (réseau par le SDK), ADR 0057 (`/proc/net`).

## Contexte

Une app ne peut pas écouter un port, la réponse HTTP simulée ne porte pas de
corps (seulement un `SimulatedPage`), et les noms ne sont résolus que par la
table du réseau simulé. Benoit veut que chaque app déclare son URL dans son
manifeste et qu'un service de DNS fasse le lien, avec un repli sur le relais du
serveur s'il est disponible.

## Décision

### 1. Corps HTTP réels

`SimulatedHttpRequest` et `SimulatedHttpResponse` gagnent un corps en octets et
un `Content-Type`. `SimulatedPage` reste un corps possible (sites existants) ;
le navigateur choisit le rendu selon le type. `HackHttpResponse.Content` donne
le texte, `ContentBytes` les octets. `curl` écrit le corps brut.

### 2. `HackHttpListener`

Calqué sur `System.Net.HttpListener` (`Prefixes`, `Start`, `GetContextAsync`,
`HackHttpListenerContext.Request/Response`), dans `HackerOs.AppSdk.Net`.

- Capacité `network.listen` (normale). Un port < 1024 exige
  `network.listen.privileged` (dangereuse, approbation administrateur), comme
  `CAP_NET_BIND_SERVICE` sous Linux. Une app `Web` reçoit d'office le droit
  d'écouter ses propres `Urls` (ADR 0073).
- La **table des ports à l'écoute** vit dans Platform Core, à côté
  d'`AppNetworkGateway` : `(adresse, port) → processus`. Elle est libérée quand
  le processus se termine ; une requête vers un port fermé donne
  `ERR_CONNECTION_REFUSED`.
- `netstat -tlnp`/`ss` listent les ports et le PID ; `/proc/net/tcp` les
  expose au format de Linux.
- Un listener dans un worker (ADR 0070) reçoit ses requêtes par la passerelle
  distante : nouveaux messages `HttpRequest`/`HttpResponse` du protocole des
  workers.

### 3. Activation à la demande

Une app `Web` installée n'est pas démarrée au boot. Le premier appel vers une de
ses `Urls` la démarre (comme l'activation par socket de systemd), puis la
requête lui est remise. Elle apparaît alors dans le gestionnaire de tâches et
peut être arrêtée ; la requête suivante la redémarre.

### 4. Résolveur `hackdns`

Service système qui répond à `HackDns` et aux requêtes du navigateur, dans cet
ordre :

1. `/etc/hosts` (vrai fichier, éditable par l'administrateur) ;
2. `localhost` et `127.0.0.0/8` → la machine locale ;
3. les `Urls` des apps `Web` installées (manifestes) ;
4. les sites hébergés déclarés (ADR 0077) qui ont un nom ;
5. le réseau simulé (hôtes et sites enregistrés) ;
6. le relais du serveur optionnel, si `network.real.access`, serveur connecté et
   jeton valide (comportement actuel d'`AppNetworkGateway`).

`https://` est accepté et traité comme `http://` (pas de chiffrement dans le
réseau simulé) ; le navigateur affiche le cadenas. Des certificats simulés
pourront venir plus tard dans un ADR à part.

Les sites internes utilisent le domaine `.hack` (`intranet.hack`), qui
n'existe pas sur Internet : le repli 6 ne s'applique jamais à ce domaine.

Le résultat est mis en cache avec un TTL ; `/etc/hosts` modifié vide le cache.
Une commande `nslookup` est ajoutée ; elle montre, comme `HackDns`, la source
de la réponse.

## Conséquences

- `AppNetworkGateway` reste le seul point de passage du trafic : il interroge
  `hackdns` puis la table des ports avant le réseau simulé.
- Le comptage d'octets de `/proc/net/dev` (ADR 0057) compte aussi le trafic
  local, sur `lo`.
- Les contrats de `HackerOs.Simulation.Abstractions` changent : les sites
  existants sont adaptés dans le même chantier.
