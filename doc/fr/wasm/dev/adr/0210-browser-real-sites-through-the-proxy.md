> [English version](../../../../en/wasm/dev/adr/0210-browser-real-sites-through-the-proxy.md)

# ADR 0210 : vrais sites dans le navigateur par le proxy du serveur

## Statut

Accepté le 2026-10-30. Issue [#266](https://github.com/Kazo-ca/HackerOs/issues/266)
(série `todo/base`). Ordre des sources décidé par Benoit le 2026-10-29 : réseau
simulé et sites `.hack`, puis **proxy du serveur HackerOS** (cette ADR), puis
proxy public désactivable et Wayback Machine (plus tard, hors portée).

- **Complète :** [ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md) et
  [ADR 0108](0108-server-relay-implementation-adjustments.md) (relais du serveur),
  [ADR 0075](0075-browser-html-rendering.md) et
  [ADR 0079](0079-browser-html-rendering-adjustments.md) (rendu isolé du
  navigateur), [ADR 0089](0089-explicit-cookie-header.md) (en-tête `Cookie`
  explicite).
- **Modifie :** la liste `ProxyRelayPolicy.DefaultGrantedApps` du serveur
  (ADR 0102 §4) et le classement du Navigateur
  ([ADR 0048](0048-system-apps-and-uninstall.md) §1).
- **Ne change pas :** le navigateur n'ouvre jamais d'iframe vers le vrai site ;
  la page passe par le rendu isolé (`srcdoc` + `HtmlPageComposer`).

## Contexte

Le navigateur ne montrait que le réseau simulé. Le relais du serveur existait
déjà pour `curl`, `wget`, `ping`, `cat` et `nmap`, mais il manquait, pour un vrai
site dans le navigateur (constats de l'issue #266) :

- le droit : le Navigateur n'avait pas `network.real.access`, et le serveur ne
  l'accordait qu'aux commandes ;
- les cookies : l'OS n'envoyait pas les cookies et perdait les `Set-Cookie` ; le
  serveur bloquait l'en-tête `Cookie` et suivait les redirections lui-même, ce
  qui perdait les cookies des sauts intermédiaires (connexion à un compte
  impossible) ;
- la charge : le serveur refuse au-delà de 8 requêtes simultanées par appareil,
  alors qu'une page charge toutes ses ressources en même temps ;
- l'affichage : rien ne disait d'où venait une page, et un refus du relais
  donnait une erreur générique.

## Décision

### 1. Le Navigateur a `network.real.access` et devient une app système

- `BrowserApp.Capabilities` déclare `network.real.access`.
- Cette capacité est dangereuse. Pour éviter la boîte d'approbation au premier
  lancement, même sans serveur, l'hôte déclare le Navigateur app système
  (`IsSystemApp="true"` dans `HackerOs.Ecosystem.csproj`), comme `curl` et
  `wget`. Il ne demande toujours pas `filesystem.system.read`.
- Le serveur ajoute `org.hackeros.browser` à `DefaultGrantedApps` et à
  l'`appsettings.json` livré. Un opérateur qui a écrit sa propre liste
  `HackerOs:Proxy:GrantedApps` doit l'y ajouter.

### 2. Cookies relayés

- Le serveur relaie l'en-tête `Cookie`. Il bloque toujours `Host`,
  `Authorization`, `X-Forwarded-For` et `X-Real-IP`.
- L'OS envoie à chaque saut réel les cookies que son conteneur
  (`HackCookieContainer`) garde pour l'hôte, plus un en-tête `Cookie` écrit par
  l'app pour l'hôte demandé (règle de l'ADR 0089).
- Les `Set-Cookie` d'un vrai site sont appliqués comme un navigateur
  (RFC 6265 §5.2 et §5.3), en version simplifiée :
  - un attribut `Domain` qui correspond à l'hôte donne un cookie de domaine,
    gardé sous la clé `.exemple.com` et envoyé à ce domaine et à ses
    sous-domaines ; un `Domain` d'un autre site ou d'un seul mot (`com`) fait
    rejeter le cookie ;
  - `Max-Age` nul ou négatif, ou `Expires` passé, efface le cookie ;
  - `Path`, `Secure`, `HttpOnly` et `SameSite` ne sont pas gardés.
- `HackCookieContainer.GetCookies(uri)` rend aussi les cookies de domaine qui
  correspondent à l'hôte (`DomainMatches`), donc `document.cookie` les voit.

### 3. Les redirections sont suivies par l'OS

- Nouveau champ du contrat : `ProxyHttpRequest.FollowRedirects` (propriété
  `init`, vrai par défaut pour les anciens clients).
- Faux : le serveur rend la première réponse telle quelle, redirection comprise,
  avec `Location` et `Set-Cookie`. Il vérifie toujours chaque cible (SSRF, port,
  adresse), puisque chaque saut est un nouvel appel.
- L'OS envoie toujours `FollowRedirects = false` et suit les sauts dans la même
  boucle que pour le réseau simulé (10 au plus, règles de méthode de
  RFC 9110 §15.4). Chaque saut est routé de nouveau : un vrai site peut renvoyer
  vers un site simulé.

### 4. La source de la réponse

- SDK : `HackHttpResponse.Source` (propriété `init`) de type
  `HackHttpResponseSource` : `Simulated`, `LocalSite` (site de cette machine),
  `RealNetwork` (relais). Elle ne dit ni l'adresse du serveur ni son jeton
  (ADR 0056 §6). Elle traverse aussi le protocole des Web Workers.
- Navigateur : `BrowserResponse.Source` ajoute `File`. La barre d'adresse
  montre une icône de source avant celle du schéma :
  - `⇄` « Vrai site, par le proxy du serveur HackerOS » ;
  - `⌂` « Site de cette machine » ;
  - `◎` « Site du réseau simulé » ;
  - rien pour un fichier (son icône de schéma suffit) ou une page d'erreur.
- Le cadenas reste pour `https:`. Pour `http:`, le texte « Non sécurisé »
  s'affiche à côté de l'icône, comme dans Chromium.

### 5. Charge d'une page

- `HttpProxyClient` (singleton de l'OS) garde au plus **6** appels du relais en
  cours (HTTP et sondages), comme un navigateur limite ses connexions par hôte.
  Les autres attendent leur tour.
- Un refus `QUOTA_EXCEEDED` (autre onglet, ou opérateur qui a baissé la limite)
  est retenté 3 fois, après 250, 500 puis 750 ms.
- Le délai d'un appel HTTP du relais passe de 10 à 30 s, le maximum du serveur.
  Le `HEAD` de `ping` garde 10 s.
- Limites gardées et documentées : 10 Mio par réponse, corps en base64 dans le
  JSON (environ +33 %).

### 6. Erreurs claires

- `ServerConnectionException.ProxyErrorCode` donne le code du relais.
- Deux valeurs s'ajoutent à `HackNetworkError` :
  - `TimedOut` (`ERR_TIMED_OUT`) pour `TIMEOUT` ;
  - `BlockedByAdministrator` (`ERR_BLOCKED_BY_ADMINISTRATOR`) pour
    `BLOCKED_ADDRESS`, `BLOCKED_PORT`, `DNS_REBINDING_BLOCKED`,
    `GRANT_MISSING`, `RELAY_DISABLED`, `CAPABILITY_DENIED` et
    `SIMULATED_DOMAIN_BLOCKED`.
- `CONNECTION_REFUSED`, `HOST_UNREACHABLE` et `TOO_MANY_REDIRECTS` gardent leur
  erreur ; le reste (quota encore plein, réponse trop grosse…) donne
  `ConnectionFailed`.
- Le navigateur a une page « Votre administrateur a bloqué … ». La page
  « adresse introuvable » propose de connecter un serveur HackerOS pour les vrais
  sites. Sans serveur, rien ne change : « hôte introuvable » (ADR 0056 §6).

### 7. Scripts de la page

La CSP du cadre reste `connect-src 'none'` : un `fetch` ou un `XMLHttpRequest`
de la page passe par `PageBridge`, donc par le même transport, le même relais et
les mêmes cookies que la page.

## Conséquences

- Avec un serveur connecté, le navigateur ouvre les vrais sites, y reste
  connecté et montre d'où vient la page.
- Ajouts au SDK (`PublicAPI.Unshipped.txt`, prochaine mineure) :
  `HackHttpResponse.Source`, `HackHttpResponseSource`,
  `HackNetworkError.TimedOut`, `HackNetworkError.BlockedByAdministrator`,
  `HackCookieContainer.DomainMatches`.
- Limites connues : pas de `Path` ni de `Secure` pour les cookies relayés ; un
  cookie effacé par le site n'est pas retiré du conteneur de l'app (seuls les
  cookies ajoutés remontent) ; pas de WebSocket pour les pages ; une navigation
  par script (`location.href`) garde la limite de l'ADR 0079.
- Hors portée : proxy public tiers désactivable, repli Wayback Machine.

## Liste des tâches

- [x] Capacité `network.real.access` du Navigateur, app système, accord par
      défaut du serveur.
- [x] En-tête `Cookie` relayé, `FollowRedirects`, cookies de chaque saut.
- [x] Source de la réponse (SDK, Web Workers, navigateur).
- [x] Icône de source, « Non sécurisé », pages d'erreur.
- [x] File d'attente et nouvel essai du relais, délai de 30 s.
- [x] Tests : relais avec cookies, redirections avec `Set-Cookie`, cookies de
      domaine, page de 20 ressources, refus sans `network.real.access`, codes
      d'erreur, icône de source, `fetch` par le pont.
- [x] Docs fr et en.
