> [English version](../../../../en/wasm/dev/adr/0085-default-simulated-network-in-the-os.md)

# ADR 0085 : le réseau simulé par défaut est branché dans l'OS

## Statut

Accepté le 2026-10-16, avec la correction de la page d'accueil du navigateur
(branche `web/browser-home-page-g9yyu5`).

- **Remplace :** la phrase de l'[ADR 0034](0034-wire-terminal-command-catalog.md)
  « `EcosystemServiceCollectionExtensions.cs` registers
  `ISimulatedWebsiteRegistry`/`ISimulatedNetworkService` from this seed ». Le
  reste de l'ADR 0034 (catalogue des commandes, hôtes `.hackeros` de test) est
  conservé.
- **Ne touche pas :** l'[ADR 0023](0023-optional-game-domain-and-proxy-fallback.md).
  Les sites du catalogue par défaut font partie du réseau simulé de l'OS, pas
  du domaine de jeu (missions, contrats), qui reste optionnel.
- **Documentation :** [`listening-ports-and-dns.md`](../../framework/listening-ports-and-dns.md),
  [`browser-html-rendering.md`](../browser-html-rendering.md).

## Contexte

Le navigateur s'ouvre sur `https://hackersearch.net` et envoie les recherches
tapées dans la barre d'adresse à `https://hackersearch.net/search?q=`. Ses
favoris pointent aussi vers HackMail, CryptoBank, le marché darknet et le
forum. Ces hôtes existent dans `DefaultSimulatedHostCatalog`, et leurs sites
dans `Network/Websites/` (chantier `web/2` pour `/api/search` et le
formulaire). Mais l'hôte Ecosystem ne composait le réseau simulé qu'avec
`SmokeTestNetworkSeed` (`example.hackeros`, `empty.hackeros`). Seuls les tests
unitaires (`Wave4NetworkTests`) branchaient le catalogue par défaut.

Conséquence : dans les trois hôtes, la page d'accueil du navigateur affichait
« This site can't be reached » (`ERR_NAME_NOT_RESOLVED`), comme `curl
hackersearch.net` ou `ping hackmail.com`. `browser-html-rendering.md` le
notait comme problème connu.

## Décision

1. `DefaultSimulatedHostCatalog.BuildWebsites()` rend les contrôleurs des sites
   du catalogue (HackerSearch, HackMail, CryptoBank, marché darknet, forum),
   une nouvelle instance par appel, donc des sessions propres à chaque réseau
   composé.
2. `AddHackerOsEcosystem` compose le réseau simulé avec
   `DefaultSimulatedHostCatalog.Build()` **et** `SmokeTestNetworkSeed.Hosts`,
   et le registre des sites avec leurs contrôleurs. Les hôtes `.hackeros`
   restent : les tests E2E et la documentation s'en servent.
3. Le catalogue par défaut garde son hôte `localhost` (`127.0.0.1`, ssh 22 et
   mysql 3306 sans PID, ADR 0074). Le résolveur `hackdns` répond toujours
   `localhost` par la boucle locale avant de consulter le réseau simulé : les
   apps à l'écoute gardent la priorité.

## Conséquences

- Le navigateur affiche HackerSearch à l'ouverture et la recherche de la barre
  d'adresse donne des résultats. `curl`, `ping`, `nmap` et `nslookup` voient
  les hôtes du catalogue.
- Les hôtes et adresses sont ceux de `src/core/network.ts` (`192.168.1.x`), sans
  collision avec les `10.0.0.x` du jeu de test.
- Le test `Tests/HackerOs.UI.E2E.Tests/BrowserHomePageTests.cs` ouvre le
  navigateur hors mode démo et vérifie la page d'accueil puis une recherche.
- Un futur paquet de contenu (ADR 0023) pourra ajouter des hôtes au même
  registre ; ce n'est pas tranché ici.
