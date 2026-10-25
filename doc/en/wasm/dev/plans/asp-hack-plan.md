# Phase web — ASP.Hack : sites web internes de HackerOS

**Statut :** plan validé par Benoit le 2026-10-16 (réponses au questionnaire de
22 questions, section 4.1). Les décisions sont dans les ADR 0073 à 0078,
acceptés le même jour.

Branche d'intégration : `web/base`, créée depuis `main` après la fusion de
`rebuild/base` (PR #131, phases 1 à 5). Chaque chantier part de `web/base`, sa
branche s'appelle `web/<n>-<sujet>` et sa PR vise `web/base`. Rien n'est fusionné
dans `main` sans Benoit.

## 1. Objet

Donner à HackerOS des **sites web internes**, servis par l'OS et consultés avec
son navigateur :

- des **projets ASP.Hack** écrits en C#, sur le modèle d'ASP.NET MVC
  (contrôleurs, routes, `GET`/`POST`/`PUT`/`PATCH`/`DELETE`, liaison de modèle,
  vues), par-dessus le SDK public ;
- des **sites Blazor interactifs**, proches d'une app à fenêtre, mais dessinés
  dans le navigateur au lieu du gestionnaire de fenêtres et sans accès au
  système de fichiers ;
- des **sites statiques** (HTML, CSS, JavaScript) déclarés à partir du système
  de fichiers et gérés par une app façon IIS ;
- l'ouverture d'un `.html` local (`file://`) dans le navigateur ;
- l'ouverture d'une page dans une **fenêtre détachée**, hors de l'OS, qui
  prépare un futur deuxième écran.

## 2. Constat de départ (vérifié sur `web/base`)

- Les sites simulés (HackMail, HackerSearch, forum, darknet, banque) sont des
  `ISimulatedWebsiteController` enregistrés au démarrage de l'hôte
  (`Platform.Core/Network/Websites/`). Leur routage est minimal (`Get`/`Post`,
  chemin exact ou `{param}` sur un segment).
- `SimulatedHttpResponse` ne porte pas de HTML : il porte un `SimulatedPage`
  structuré (sections), dessiné par `SimulatedPageRenderer.razor`. Aucun moyen
  de renvoyer du HTML, du CSS, du JS, du JSON ou des octets.
- Le SDK réseau (`HackHttpClient`, `IAppNetworkGateway`, ADR 0055, 0056) sait
  envoyer toutes les méthodes HTTP, avec un repli sur le réseau réel par le
  relais du serveur optionnel (`network.real.access`). Aucune app ne peut
  écouter un port ; `netstat` n'a rien à montrer ; il n'y a pas de
  `/etc/hosts`.
- Les services tournent dans un worker dédié depuis la phase 5 (ADR 0070) ; les
  apps à fenêtre restent sur le thread principal.

## 3. Principes

1. **Mimic the Metal.** Un site est un processus qui écoute un port, visible
   dans le gestionnaire de tâches, `ps` et `netstat -tlnp` ; un nom passe par un
   résolveur DNS ; un port fermé répond `ERR_CONNECTION_REFUSED` ; le navigateur
   charge lui-même les sous-ressources d'une page.
2. **SDK public seulement.** ASP.Hack est une bibliothèque du SDK
   (`HackerOsSdkLibrary`) ; un site ne référence ni Platform Core ni les
   services internes (ADR 0055, codes `HOSAPP0030/0031`).
3. **Ressembler à ASP.NET sans le recopier.** Mêmes idées et mêmes noms
   d'attributs (`[HttpGet]`, `[Route]`, `[FromBody]`), classes de base préfixées
   `Hack` (`HackController`, `HackHttpContext`) ; seulement le sous-ensemble
   utile (section 6 de l'ADR 0076).
4. **Le JS d'un site est isolé.** Il ne touche jamais HackerOS ni Internet :
   tout passe par le réseau simulé.
5. **Aucun serveur requis.** Tout tourne dans l'hôte Ecosystem. Le serveur
   optionnel ne sert que de relais vers le réseau réel, quand il est là.
6. **Porte ouverte à la compilation dans l'OS.** Rien ne suppose qu'un site est
   forcément compilé avec la solution : le modèle de déploiement et le moteur de
   vues `.cshtml` sont choisis pour qu'une version future compile un projet
   depuis l'éditeur de code.
7. **Les trois hôtes restent opérationnels** après chaque chantier
   (`build/verify-hosts.sh`), et chaque chantier met à jour la documentation et
   l'index `docs/README.md`.

## 4. Registre des décisions

| # | Décision (réponses de Benoit du 2026-10-16) | ADR |
|---|---|---|
| 1 | Nouveau genre d'app `Web` : un site MVC, un site Blazor ou les deux ; chaque app déclare ses URL dans son manifeste. Les sites enregistrés statiquement (actuels) restent pris en charge à côté des sites-processus. | 0073 |
| 2 | Un site d'app (projet) est **externe** : servi en lecture seule depuis les ressources du projet, sous le nom qu'il déclare. Un site créé par l'utilisateur est **hébergé dans l'OS**, depuis ses fichiers, sur `localhost`. | 0073, 0077 |
| 3 | Un site n'a aucune capacité `filesystem.*` ; un site Blazor interactif n'a pas accès aux fichiers non plus. | 0073 |
| 4 | Résolveur DNS de l'OS (service `hackdns`) : `/etc/hosts`, URL des manifestes, sites statiques déclarés, réseau simulé, puis repli sur le relais du serveur s'il est disponible. | 0074 |
| 5 | `HackHttpListener` (calqué sur `System.Net.HttpListener`), capacité `network.listen`, ports < 1024 réservés à l'administrateur, table des ports à l'écoute, activation à la demande (comme les sockets de systemd), corps HTTP réels (`Content-Type` + octets). | 0074 |
| 6 | Le navigateur rend du vrai HTML dans une iframe isolée (`sandbox="allow-scripts"`, sans même origine), charge lui-même les sous-ressources par le réseau simulé et relaie `fetch`, `XMLHttpRequest`, liens et formulaires ; `file://` lit les fichiers de l'OS. | 0075 |
| 7 | ASP.Hack MVC : pipeline de middlewares, routage par attributs et conventionnel généré au build, liaison de modèle, résultats, DI par requête, session par cookie. | 0076 |
| 8 | Deux moteurs de vues : composants `.razor` rendus en HTML (`HtmlRenderer`), et vues `.cshtml` compilées au build par le compilateur Razor avec une classe de base ASP.Hack (après une preuve de faisabilité) ; compilation `.cshtml` dans l'OS plus tard. | 0076 |
| 9 | Sites statiques déclarés en fichiers (`/etc/hackweb/sites/*.site` par l'administrateur, `~/.config/hackweb/sites/*.site` par tout utilisateur sur un port ≥ 1024), servis par le service `hackwebd`, gérés par l'app « Web Server Manager » façon IIS, qui ne fait que piloter l'OS par le SDK. | 0077 |
| 10 | Fenêtre détachée : une page s'ouvre dans une fenêtre du navigateur hôte, hors de l'OS ; cette fenêtre est pilotée par l'instance principale (lien d'affichage), sans second OS indépendant. Le même lien servira au deuxième écran. | 0078 |
| 11 | Les données d'un site ne sont pas dans les fichiers de l'OS : une base au besoin dans la base locale du navigateur hôte (IndexedDB), nommée d'office d'après l'app par un helper du SDK web ; seuls les cookies, la saisie semi-automatique et les journaux de débogage activés par le site vont dans les fichiers de l'OS (Benoit, 2026-10-16 16:44Z, remplace le dossier privé validé à 16:34Z). | 0073 |
| 12 | Les sites internes utilisent le domaine `.hack` (`intranet.hack`) ; comme sur un vrai poste, `/etc/hosts` l'emporte toujours. | 0074 |

### 4.1 Questionnaire du 2026-10-16

Benoit a retenu toutes les recommandations, avec une précision sur la question 3.

| # | Question | Réponse | ADR |
|---|---|---|---|
| 1 | Genre d'app | nouveau genre `Web` | 0073 |
| 2 | MVC et Blazor dans la même app | permis (routes MVC d'abord) | 0073 |
| 3 | Où tourne le code serveur | worker, **un seul worker partagé par tous les sites** | 0073 |
| 4 | Démarrage d'un site | à la demande | 0074 |
| 5 | Ports < 1024 | approbation administrateur | 0074 |
| 6 | HTTPS | accepté et traité comme HTTP | 0074 |
| 7 | Analyse du HTML | C#, AngleSharp | 0075 |
| 8 | `localStorage` d'une page | fichiers de l'OS, avec les cookies | 0075 |
| 9 | Base d'un site | API calquée sur IndexedDB + helper typé | 0073 |
| 10 | Base à la désinstallation | effacée après confirmation | 0073 |
| 11 | Bases visibles dans l'OS | dans Diagnostic | 0073 |
| 12 | WebSocket, EventSource, `window.open` | refusés en v1 | 0075 |
| 13 | Outils de développement | Réseau et Console en v1 | 0075 |
| 14 | Table des routes | générée au build | 0076 |
| 15 | Session | en mémoire du processus | 0076 |
| 16 | Noms des types | classes `Hack*`, attributs d'ASP.NET | 0076 |
| 17 | `.cshtml` si la preuve échoue | moteur interprété maison | 0076 |
| 18 | Format des sites statiques | JSON (`.site`) | 0077 |
| 19 | Qui publie un site statique | tout utilisateur (port ≥ 1024, `~/.config`), administrateur pour `/etc` | 0077 |
| 20 | Identité de `hackwebd` | compte `www-data` | 0077 |
| 21 | Fenêtre détachée | pilotée par l'OS principal | 0078 |
| 22 | Premier chantier | ordre du plan | — |

## 5. Chantiers

| Branche | Contenu | Dépend de |
|---|---|---|
| `web/1-adr` | Ce plan, ADR 0073 à 0078, index. | — |
| `web/2-http-bodies` | Corps HTTP réels dans les contrats simulés et le SDK (`Content-Type`, octets, en-têtes) ; `SimulatedPage` reste pour les sites existants ; `curl` affiche le corps brut. | `1` |
| `web/3-listener-dns` | `HackHttpListener`, `network.listen`, table des ports, `hackdns`, `/etc/hosts`, `Urls` du manifeste, activation à la demande, `netstat -tlnp`, `/proc/net/tcp`, worker web partagé et passerelle distante pour un listener dans ce worker. | `2` |
| `web/4-browser-html` | Iframe isolée, chargeur de sous-ressources, pont `fetch`/XHR/formulaires, `file://`, onglet « source », erreurs réseau. | `2` |
| `web/5-static-sites` | Fichiers `.site`, service `hackwebd` (fichiers statiques, index, types MIME, 404), app Web Server Manager, ouverture d'un dossier comme site. | `3`, `4` |
| `web/6-mvc-core` | Bibliothèque `HackerOs.AppSdk.Web` : `WebAppDefinition`, `HackWebApplication`, middlewares, générateur de routes, contrôleurs, liaison, résultats JSON/Content/File/Redirect, DI par requête, session. | `3` |
| `web/7-razor-views` | Vues `.razor` en HTML, layout, partials, `Model`, helpers d'URL, anti-forgery, `wwwroot` du projet servi en lecture seule. | `6` |
| `web/8-cshtml-views` | Preuve de faisabilité puis vues `.cshtml` compilées au build (`@model`, `@inject`, `@section`, `_Layout`, `_ViewStart`). Si la preuve échoue, un ADR de remplacement est écrit avant le code. | `7` |
| `web/9-blazor-sites` | Sites Blazor interactifs dessinés dans la zone de page du navigateur, navigation par URL, sans capacité `filesystem.*`. | `4`, `6` |
| `web/10-detached-window` | Fenêtre détachée pilotée par l'instance principale (lien d'affichage), pour les pages HTML d'abord. | `4` |
| `web/11-sample-final` | App exemple « Intranet » (pages, API REST, JS, Blazor), guide `asp-hack-developer-guide.md`, `sdk-reference.md`, vérification finale. | tous |
| `web/12-site-data` | Ajouté après la clôture (ADR 0087 §4) : base `HackWebDb` d'un site, journaux de débogage `/var/log/hackweb/<id>.log`, saisie semi-automatique du navigateur (ADR 0073 §3, ADR 0088). | `11` |

Ordre : `2` ; puis `3` et `4` en parallèle ; puis `5`, `6` et `10` ; puis `7`
et `9` ; puis `8` ; puis `11`.

## 6. Hors périmètre (pour l'instant)

- Compiler un projet ASP.Hack ou une vue `.cshtml` **dans l'OS** (Roslyn dans le
  navigateur) : le moteur et le déploiement le permettent, le chantier viendra
  plus tard.
- Migrer HackMail, HackerSearch et les autres sites enregistrés vers ASP.Hack.
- Deuxième écran du bureau (le lien d'affichage de l'ADR 0078 le prépare).
- HTTPS réel, certificats, authentification complète (rôles, OAuth), OpenAPI,
  WebSockets.

## 7. Questions ouvertes

Aucune. Les deux dernières ont été tranchées par Benoit le 2026-10-16 (décisions
11 et 12 de la section 4).

## 8. Liste des tâches

### Préparation
- [x] Questions à Benoit et réponses (2026-10-16).
- [x] Fusion de `rebuild/base` dans `main` (PR #131) et création de `web/base`.
- [x] Ce plan et les ADR 0073 à 0078.
- [x] Validation du plan et des ADR par Benoit (questionnaire, 2026-10-16).

### Chantiers
- [x] `web/2-http-bodies` ([`http-bodies.md`](../../framework/http-bodies.md))
- [x] `web/3-listener-dns` ([`listening-ports-and-dns.md`](../../framework/listening-ports-and-dns.md))
- [x] `web/4-browser-html` ([`browser-html-rendering.md`](../browser-html-rendering.md), ADR 0079)
- [x] `web/5-static-sites` ([`static-sites.md`](../static-sites.md), ADR 0080)
- [x] `web/6-mvc-core` ([`asp-hack-mvc.md`](../../framework/asp-hack-mvc.md), ADR 0082)
- [x] `web/7-razor-views` ([`asp-hack-razor-views.md`](../../framework/asp-hack-razor-views.md), ADR 0083)
- [x] `web/8-cshtml-views` ([`asp-hack-cshtml-views.md`](../../framework/asp-hack-cshtml-views.md), ADR 0086 : preuve réussie, pas de moteur interprété)
- [x] `web/9-blazor-sites` ([`blazor-sites.md`](../../framework/blazor-sites.md), ADR 0084)
- [x] `web/10-detached-window` ([`detached-display-windows.md`](../detached-display-windows.md), ADR 0081)
- [x] `web/11-sample-final` ([`asp-hack-developer-guide.md`](../../framework/asp-hack-developer-guide.md), ADR 0087 : exemple Intranet, liens `target` des sites Blazor, vues compilables dans une app livrée, `TryValidateModel` ; la base `HackWebDb` de l'ADR 0073 §3, les journaux de débogage d'un site et la saisie semi-automatique sont faits par `web/12`)
- [x] `web/12-site-data` ([`asp-hack-site-data.md`](../../framework/asp-hack-site-data.md), ADR 0088 : base `hackweb.<id>` par une passerelle de l'OS, Diagnostic et désinstallation, `ILogger<T>` vers `/var/log/hackweb/<id>.log`, historique de formulaires du navigateur ; l'Intranet garde ses notes dans sa base)
