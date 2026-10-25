# ADR 0137 : ajustements de la mise en œuvre de l'OS servi par `HackerOs.Server`

## Statut

Accepté le 2026-10-18, chantier `exec/8-server-wasm`.

- **Ajuste :** l'[ADR 0130](0130-server-serves-webassembly-os-and-api.md)
  (serveur qui sert l'OS en WebAssembly) et l'[ADR 0134](0134-automatic-server-connection-and-status-icon.md)
  (connexion automatique et icône d'état) sur des points de mise en œuvre ;
  leurs décisions ne changent pas.
- **Corrige :** deux défauts anciens de l'ADR 0028 (comptes du serveur) qui
  empêchaient un deuxième appareil de se connecter à un compte existant.

## Contexte

En écrivant le chantier, quatre points des ADR 0130 et 0134 ne tenaient pas
tels quels, et la connexion à un compte existant depuis un autre navigateur
ne pouvait pas réussir. Cet ADR consigne ce qui a été fait à la place.

## Décision

### 1. Détection par `hackeros-host.json`, pas par une requête qui échoue

L'ADR 0134 §1 fait interroger `GET /api/hackeros/host-info` sur l'origine de
l'OS. Sur la PWA servie par un hôte statique, cette adresse répond 404 et
Chromium écrit « Failed to load resource » dans la console à chaque démarrage
(les tests de démarrage des hôtes refusent toute erreur de console).

- L'Ecosystem livre un fichier statique `wwwroot/hackeros-host.json`
  (`{"product":"HackerOS","isServer":false}`), lu par l'OS à côté de son
  `index.html` (`HttpHostInfoClient.FindServingServerAsync`, délai de 5 s,
  toute erreur vaut « pas de serveur »).
- `HackerOs.Server` répond lui-même à cette adresse (`isServer: true`, version
  du serveur et de l'API, `Cache-Control: no-store`) par un middleware placé
  avant le routage : `MapStaticAssets` publie aussi le fichier de l'Ecosystem
  à la même adresse.
- `GET /api/hackeros/host-info` existe aussi, comme l'ADR 0134 le prévoit :
  l'OS l'appelle sur un serveur connu pour vérifier qu'il répond et parle
  l'API 1.x (sinon état **Erreur**).
- Le service worker publié ne met jamais `hackeros-host.json` en cache (son
  contenu diffère du hash du manifeste sur un serveur) ; hors ligne, il répond
  `isServer: false` au lieu d'échouer.

### 2. Les pages de l'OS sont réécrites vers `index.html` avant le routage

`MapFallbackToFile("index.html")` ne trouve le fichier que dans un hôte publié
ou en Development. Le serveur réécrit donc toute adresse de page (`/`, un lien
profond, un rechargement : chemin sans extension hors `/api`, `/health`,
`/openapi`, `/scalar`, `/_framework`, `/_content`) en `/index.html` avant
`UseRouting`, et `MapStaticAssets` sert la page avec ses références
empreintées dans tous les environnements. Une route inconnue de l'API reste un
404.

Le SDK Razor ne transmet jamais le paquet CSS isolé d'un projet
(`HackerOs.Ecosystem.styles.css`, actif du seul projet courant) à un projet qui
le référence : la même réécriture sert à sa place `HackerOs.Server.styles.css`,
qui importe le CSS isolé des mêmes projets (le serveur ne référence que
l'Ecosystem).

### 3. Ce que « connecté » veut dire, et quand la connexion démarre

- `ServerPresenceService` (Platform Core) trouve le serveur (le compte lié à
  l'appareil, sinon celui qui a servi l'OS si `auto-connect` n'est pas coupé),
  vérifie qu'il répond (`host-info`), puis le revérifie toutes les 30 s ; un
  serveur perdu passe **Hors ligne** et se retente après 5, 10, 20, 40 puis
  60 s. La synchronisation et le relais restent ceux des ADR 0101 et 0102 :
  ils utilisent le compte lié, sans changement.
- La boucle démarre avec le bureau (l'icône `ServerStatusIndicator` est dans
  la zone de notification de la barre des tâches) et s'arrête à la
  déconnexion de la session. L'écran de connexion n'attend jamais le serveur.
- `auto-connect=false` dans `/etc/hackeros/server.config` (clé à la racine ou
  dans `[server]`) coupe seulement la détection : un serveur lié à la main
  dans Paramètres reste suivi. Seuls `false`, `no`, `off` et `0` coupent.
- Paramètres n'est pas prérempli : « S'identifier » se fait dans le panneau de
  l'icône, et « Paramètres du serveur » ouvre l'app Paramètres. Le SDK ne
  change pas.
- La zone de notification est un nouveau paramètre de la barre des tâches,
  `Taskbar.NotificationAreaContent` (ajout public de `HackerOs.Taskbar.Blazor`).

### 4. Un compte existant marche depuis n'importe quel appareil

- **Sel du client.** `ServerConnectionService` tirait un sel PBKDF2 au hasard à
  chaque appel ; le hash envoyé à la connexion ne pouvait donc jamais égaler
  celui de la création. Le sel est maintenant dérivé du nom du compte
  (`SHA-256("hackeros-server-account:" + nom)`, 16 octets) ; le serveur
  re-hache toujours ce qu'il reçoit avec son propre sel aléatoire. Un compte
  créé avant ce changement garde sa session (jeton de rafraîchissement) mais
  ne peut plus se reconnecter par mot de passe : il faut en créer un autre.
- **Appareil inconnu.** `POST /api/auth/login` refusait un appareil non
  enregistré (`DEVICE_NOT_REGISTERED`), et l'enregistrement demandait déjà un
  jeton. Le mot de passe venant d'être vérifié, la connexion enregistre
  maintenant l'appareil (audit `DEVICE_REGISTERED_AT_LOGIN`) ; un appareil
  révoqué reste refusé (`DEVICE_REVOKED`).

### 5. Ce qui disparaît du serveur

Comme l'ADR 0130 §3 : composants Razor, `AddInteractiveServerComponents`,
clients directs, transport d'assemblies en processus, environnement d'hôte
ASP.NET Core, références aux apps, et la section `HackerOs:ResourceAccounting`
de son `appsettings.json` (l'OS lit celle de l'Ecosystem). Le serveur garde
une seule référence de projet vers l'OS : `OS/HackerOs.Ecosystem`.

## Conséquences

- `build/verify-hosts.sh` : le parcours `server` attend des mesures `measured`,
  des workers et `kill -9` d'une boucle, et vérifie l'icône (« Connected, not
  identified » seul, puis « Connected » après création d'un compte depuis le
  panneau) ; les autres parcours vérifient qu'aucune icône de serveur
  n'apparaît.
- Ajouts publics non publiés (ADR 0132 §1) : `HackerOs.Server.Contracts.Hosting`
  (`HostInfoResponse`, `HostInfoRoutes`), `IHostInfoClient`,
  `HttpHostInfoClient`, `ServerPresence*` (Platform Core),
  `ServerStatusIndicator` (Platform Blazor), `Taskbar.NotificationAreaContent`.
- Documentation : [`server-hosting.md`](../server-hosting.md).

## Références

- ADR 0028, 0101, 0102, 0130, 0132, 0134.
