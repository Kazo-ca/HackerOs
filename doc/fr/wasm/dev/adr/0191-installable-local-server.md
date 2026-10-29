> [English version](../../../../en/wasm/dev/adr/0191-installable-local-server.md)

# ADR 0191 : serveur HackerOS installable sur l'ordinateur ou le réseau local

## Statut

Proposé le 2026-10-29 (fil « Évaluer un serveur local installable »). Benoit a
demandé l'ADR et l'issue de suivi [#267](https://github.com/Kazo-ca/HackerOs/issues/267).
Les choix de §2 à §5 sont les options recommandées des questions Q1 à Q4 posées
à Benoit dans le fil ; s'il en choisit une autre, cet ADR est mis à jour avant
le premier chantier de code.

- **Complète :** [ADR 0102](0102-server-network-relay-tcp-udp-and-policy.md) et
  [ADR 0108](0108-server-relay-implementation-adjustments.md) (relais),
  [ADR 0130](0130-server-serves-webassembly-os-and-api.md) (le serveur sert l'OS),
  [ADR 0134](0134-automatic-server-connection-and-status-icon.md) (connexion au
  serveur), [ADR 0173](0173-automatic-releases-from-the-version-file.md) (releases).
- **Lié :** issue [#266](https://github.com/Kazo-ca/HackerOs/issues/266) (manques du
  navigateur simulé avec le relais).

## Contexte

- Benoit veut qu'un utilisateur de l'OS WebAssembly (GitHub Pages compris)
  télécharge depuis Paramètres une application, l'installe sur son ordinateur ou
  sur une autre machine de son réseau local, sans l'exposer à internet, et ait
  ainsi le relais réseau (HTTP, TCP, UDP).
- `HackerOs.Server` a déjà le relais, les comptes, les appareils, la synchro, un
  CORS réglable (`Cors:AllowedOrigins`, aujourd'hui `http://localhost:5000` et
  `https://localhost:7000`), et il sert l'OS lui-même. L'OS sait se connecter à un
  serveur dont l'adresse est saisie dans Paramètres (ADR 0134).
- Il manque : l'intégration comme service (Windows, systemd, launchd), une image
  Docker, un installeur, et la publication du serveur par `release.yml`.
- Contraintes du navigateur pour une page HTTPS (GitHub Pages) :

| Cible | Contenu mixte | Remarque |
|---|---|---|
| `http://localhost:port` | permis (Chrome, Edge, Firefox ; Safari à vérifier) | Chrome « Local Network Access » : invite d'autorisation une fois par site |
| `http://192.168.x.x:port` | bloqué, sauf exemption « Local Network Access » de Chrome et Edge récents (IP privée littérale ou nom `.local`) | les WebSocket n'étaient pas couverts au départ |
| `https://…` avec certificat généré | permis | le certificat doit être approuvé sur chaque appareil client |

- L'isolation de l'OS (COEP, ADR 0131) ne bloque ni un `fetch` CORS ni un
  WebSocket vers le serveur : il suffit que le serveur autorise l'origine.
- Un serveur qui écoute en local peut être appelé par n'importe quel site ouvert
  dans le navigateur. CORS l'empêche de lire les réponses et le relais exige un
  jeton, mais une attaque par *DNS rebinding* contourne CORS si l'en-tête `Host`
  n'est pas vérifié (`AllowedHosts` vaut `*` aujourd'hui).

## Décision

### 1. Un seul serveur, publié comme application installable

On distribue `HackerOs.Server` tel quel (OS, comptes, synchro, relais), pas un
programme « relais seulement » (Q3, option a). La version est celle de
`build/Versions.props` : l'OS vérifie la compatibilité par `GET /api/version`.

### 2. Cible : l'ordinateur d'abord, le réseau local ensuite par HTTPS

- **Étape 1, ordinateur local** (Q1, option a) : le serveur écoute sur
  `http://localhost:<port>` (boucle locale seulement). L'OS de GitHub Pages
  l'appelle sans contenu mixte.
- **Étape 2, réseau local** (Q1, option b) : l'installeur peut écouter sur le
  réseau en HTTPS avec un certificat qu'il génère (autorité locale propre à
  l'installation). Le serveur sert le certificat de l'autorité et un guide pour
  l'approuver sur chaque appareil (Windows, macOS, Linux, Android, iOS).
- L'OS servi en `http://IP` (non sécurisé, sans service worker ni isolation) et
  l'exemption propre à Chrome ne sont **pas** des modes pris en charge ; ils
  peuvent marcher, sans garantie.

### 3. Sécurité par défaut

- Écoute sur la boucle locale seulement ; l'écoute réseau est un choix explicite
  à l'installation (ou `HACKEROS_URLS`).
- Liste `AllowedHosts` remplie à l'installation (`localhost`, `127.0.0.1`, `[::1]`,
  plus le nom et l'IP choisis pour le réseau) : un `Host` inconnu reçoit `400`.
- `Cors:AllowedOrigins` contient par défaut l'origine GitHub Pages de HackerOS et
  les origines locales ; l'administrateur peut en ajouter.
- Compte administrateur créé à l'installation ; le relais garde ses contrôles
  (jeton, `GrantedApps`, plages privées bloquées sauf `AllowPrivateNetworks`).
- L'OS ne sonde **jamais** `localhost` de lui-même depuis GitHub Pages : il ne le
  fait qu'après que l'utilisateur a activé le serveur local dans Paramètres, pour
  ne pas montrer l'invite « Local Network Access » à chaque visiteur.

### 4. Empaquetage

- `dotnet publish` autonome en un fichier pour `win-x64`, `win-arm64`,
  `linux-x64`, `linux-arm64`, `osx-x64` et `osx-arm64` : .NET n'a pas à être
  installé.
- Service : `UseWindowsService()` et `UseSystemd()` dans `Program.cs`, plist
  launchd pour macOS.
- Format (Q2, option a) : archive (`.zip` sous Windows, `.tar.gz` ailleurs) avec un
  script d'installation et de désinstallation du service (`install.ps1`,
  `install.sh`), plus une image Docker `ghcr.io/kazo-ca/hackeros-server`. Les
  installeurs natifs (MSI, winget, `.deb`, Homebrew) viendront plus tard si le
  besoin se confirme.
- Binaires **non signés** pour la v1 (Q4) : la documentation explique
  l'avertissement SmartScreen et « Ouvrir quand même » de macOS.

### 5. Releases

`release.yml` (ADR 0173) ajoute un job en matrice qui publie
`hackeros-server-<version>-<rid>.zip|tar.gz` et les joint à la **même** GitHub
Release que l'OS, et un job qui pousse l'image Docker (`:<version>` et `:latest`).

### 6. Paramètres de l'OS

Section « Serveur local » :

- lien de téléchargement vers la dernière GitHub Release, avec le fichier qui
  correspond au système détecté mis en avant, et le lien vers le guide ;
- adresse du serveur, bouton « Tester », état du relais (`GET /api/proxy/policy`) ;
- **appairage** : le serveur affiche un code à usage unique (ou une adresse
  `…#pair=<code>`) ; l'OS l'échange contre un jeton d'appareil, sans retaper le mot
  de passe. La connexion par compte existante reste possible.

## Conséquences

- Chantiers proposés, suivis dans #267 : (1) sécurité du serveur local (§3) ;
  (2) service et publication autonome ; (3) Dockerfile ; (4) `release.yml` ;
  (5) Paramètres et appairage ; (6) HTTPS du réseau local ; (7) documentation.
- Les manques de #266 (capacité du navigateur simulé, cookies, quota de requêtes
  simultanées) valent aussi pour ce serveur.
- Sans serveur installé, rien ne change : l'OS reste hors ligne pour le réseau
  réel (AGENTS.md I.1).
