> [English version](../../../../en/wasm/dev/adr/0233-server-issued-real-network-grants.md)

# ADR 0233 : le serveur émet l'accord du vrai réseau approuvé dans l'OS, avec ses ports

## Statut

Acceptée le 2026-11-07 : Benoit a choisi les recommandations des quatre
questions du fil « Accepter les ports déclarés par les apps » (voir
« Questions posées à Benoit »). Code : série `real-network/base`, issue
[#395](https://github.com/Kazo-ca/Os/issues/395).

- **Complète :** l'[ADR 0235](0235-real-network-for-chat-apps.md) §3 (ports
  déclarés par l'app) et l'[ADR 0031](0031-grants-sync.md) (accords
  synchronisés en lecture seule) : c'est l'« autorité qui émet les accords »
  que l'ADR 0031 annonçait.
- **Ne change pas :** l'OS ne pousse toujours aucun enregistrement `grants`
  par la synchronisation ; la fenêtre d'approbation, ses mots de passe et
  `/etc/kazoos/app-permissions.config` (ADR 0049) ; `GrantedApps` ; les
  plages bloquées, les quotas ; `BlockedPorts`.

## Contexte

- Le relais n'accorde `network.real.access` qu'aux apps de `GrantedApps`
  (`appsettings.json`) ou à celles qu'un enregistrement `grants` synchronisé
  du compte nomme (ADR 0108 §1). Le chantier `real-network/3-declared-ports`
  (#401) lit en plus les ports approuvés dans cet enregistrement
  (`realNetworkPorts`).
- Mais aucun code n'écrit un tel enregistrement : l'OS ne pousse jamais
  d'accord (ADR 0031 §1, pour qu'une poussée forgée ne puisse rien élargir) et
  le serveur n'a aucune route pour en émettre. Une app tierce approuvée par un
  administrateur dans l'OS reçoit donc `GRANT_MISSING` du relais, et ses ports
  déclarés ne servent à rien. L'ADR 0235 voulait justement éviter qu'un
  administrateur doive modifier `appsettings.json`.

## Décision

### 1. Une route du serveur émet l'accord

`POST /api/proxy/grants` (authentifié comme les autres routes du relais :
compte et appareil vérifiés) reçoit `ProxyGrantIssueRequest` :

| Champ | Rôle |
|---|---|
| `AppId` | l'app approuvée (même contrôle que les routes du relais) |
| `UserId` | l'utilisateur de l'OS pour qui l'accord vaut |
| `RealNetworkPorts` | les ports approuvés, `tcp/6697` (`RealNetworkPorts.TryParse`) ; vide permis |
| `Revoked` | `true` pour retirer l'accord |

Le serveur valide la **forme** lui-même (capacité fixée à
`network.real.access`, ports valides et sans doublon, au plus 64 ports), puis
**écrit lui-même** l'enregistrement `grants` : identifiant stable par compte,
app et utilisateur (une nouvelle approbation fait une nouvelle révision du
même enregistrement), charge utile `GrantsSyncPayload` (source
`AdministratorApproval`, `IsRevoked`, `RealNetworkPorts`), hachage calculé
par le serveur, journal d'audit `PROXY_GRANT_ISSUE` ou `PROXY_GRANT_REVOKE`.
Une demande identique à la dernière révision ne crée rien. L'OS récupère
l'enregistrement par la synchronisation habituelle (lecture seule).

Un port de `BlockedPorts` est accepté dans l'accord (il reste refusé à
l'usage, et Paramètres le montre) : la politique peut changer sans réécrire
les accords.

### 2. Confiance

Le serveur est mono-utilisateur (ADR 0027) : il fait confiance au compte et à
l'appareil authentifiés pour dire qu'un administrateur de l'OS a approuvé,
comme il leur fait déjà confiance pour l'identifiant d'app (ADR 0108 §1). La
route ne permet que `network.real.access` et des ports bien formés ;
`BlockedPorts`, les plages bloquées et les quotas gardent le dernier mot.
L'opérateur peut fermer cette porte : `KazoOs:Proxy:AcceptOsGrants`
(défaut `true`) ; à `false`, la route répond `403 GRANT_ISSUING_DISABLED`, le
relais ignore les accords émis ainsi, et seule `GrantedApps` compte.
Avertissement au démarrage quand `AcceptOsGrants` est vrai et
`AllowAllPorts` aussi.

### 3. Quand l'OS demande l'accord

- À l'approbation : quand `network.real.access` passe à `allow` (fenêtre
  spéciale, mode texte du terminal, onglet Permissions des Paramètres) et
  qu'un serveur est connecté, l'OS appelle la route avec les ports approuvés.
  Sans serveur, rien ne part ; l'approbation locale vaut quand même pour
  `KazoClientWebSocket` (ADR 0235 §1).
- À chaque synchronisation (bouton « Synchroniser » et tâche planifiée) et à
  la connexion à un serveur, l'OS **réconcilie** : il émet l'accord de chaque
  app autorisée pour `network.real.access` dans le fichier machine, et le
  retire pour chaque app refusée (`deny` machine ou utilisateur) ou dont
  l'autorisation a disparu. Les demandes identiques ne créent rien.
- Un échec réseau ne bloque ni le lancement ni l'approbation : il est noté
  dans `/var/log/syslog` et repris à la synchronisation suivante.

### 4. Les ports approuvés sont gardés dans l'OS

L'approbation écrit les ports affichés dans une nouvelle section du fichier
machine :

```text
# /etc/kazoos/app-permissions.config
[permissions]
ca.kazo.irc:network.real.access=allow

[real-network-ports]
ca.kazo.irc=tcp/6667,tcp/6697
```

L'OS envoie au serveur ces ports-là, pas ceux du manifeste. Quand une mise à
jour de l'app déclare un port qui n'y est pas, `network.real.access` redevient
`Pending` pour cette app (même table de décision, ADR 0049) : la fenêtre
revient au prochain lancement interactif et liste les nouveaux ports, comme un
système qui redemande une permission élargie. Un port retiré par la mise à
jour reste approuvé jusqu'à la prochaine approbation, sans effet puisque l'app
ne le demande plus. Une ligne invalide de la section est ignorée avec un
avertissement, comme dans `[permissions]`.

## Questions posées à Benoit

Réponse de Benoit (2026-11-07) : « Je suis d'accord avec tes
recommandations », donc 1 A, 2 A, 3 A et 4 A.

1. Comment l'accord et ses ports arrivent sur le serveur ?
   - A) Nouvelle route du serveur qui émet l'accord après l'approbation de
     l'OS. **(Recommandé, choisi)**
   - B) L'administrateur seulement, dans `GrantedApps`.
   - C) L'OS envoie les ports à chaque flux.
2. Forme de la déclaration : `AppDefinition.RealNetworkPorts`, ports seuls
   `tcp/N` ou `udp/N`. **(Recommandé, choisi, livré par #401)**
3. `BlockedPorts` livrée : courriel, RPC et partage de fichiers en TCP,
   services d'amplification en UDP. **(Recommandé, choisi, livré par #401)**
4. `BlockedPorts` gagne toujours, même sur `AllowedPorts` et `AllowAllPorts`.
   **(Recommandé, choisi, livré par #401)**

## Conséquences

- Une app tierce approuvée dans l'OS joint ses ports déclarés par le relais
  sans qu'un administrateur touche au serveur ; l'opérateur peut l'interdire
  (`AcceptOsGrants: false`).
- Contrats : `ProxyGrantIssueRequest`, `ProxyGrantIssued`, code
  `GRANT_ISSUING_DISABLED` ; Platform Core : client de la route,
  réconciliation, section `[real-network-ports]`. Le SDK public ne change pas.
- Tests prévus : émission et révision, demande identique sans effet, port mal
  écrit refusé, `AcceptOsGrants: false`, réconciliation (émet, retire),
  nouveau port d'une mise à jour qui redemande l'approbation, relais qui
  accepte le port après l'émission.
- Docs : [`server-network-relay.md`](../server-network-relay.md),
  [`dangerous-capability-approval.md`](../dangerous-capability-approval.md),
  `features.md`, entrée `changes/unreleased`.
