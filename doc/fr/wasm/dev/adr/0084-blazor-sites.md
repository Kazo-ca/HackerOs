> [English version](../../../../en/wasm/dev/adr/0084-blazor-sites.md)

# ADR 0084 : sites Blazor interactifs dessinés par le navigateur

## Statut

Accepté le 2026-10-16, avec le chantier `web/9-blazor-sites`.

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Complète :** ADR 0073 §1 et §4 (site Blazor d'une app `Web`), ADR 0075 §5
  (choix de la vue selon la réponse), ADR 0081 §2 (contenu Blazor dans une
  fenêtre détachée). Ne remplace aucune décision.
- **Documentation :** [`blazor-sites.md`](../../framework/blazor-sites.md).

## Contexte

L'ADR 0073 §4 dit qu'un site Blazor ressemble à une app à fenêtre, mais que son
composant racine est dessiné dans la zone de page du navigateur, qu'il navigue
par l'URL de l'onglet et qu'il n'a aucun accès aux fichiers. Il ne dit pas
comment le navigateur sait qu'une adresse est un site Blazor, sous quelle
identité tourne ce code, ni comment la navigation, les liens et les erreurs se
comportent, alors que le `NavigationManager`, le `Router` et `PageTitle` de
Blazor appartiennent à l'OS lui-même (un seul moteur de rendu pour tout le
bureau).

## Décision

### 1. Déclaration

`AppDefinition.BlazorRoot` (`Type?`, `null` par défaut) désigne le composant
racine d'une app `Web`. Le générateur de manifestes l'écrit dans
`web.blazor` (`assembly`, `type`), omis sans racine pour garder les octets des
autres manifestes. Le validateur exige que le type soit dans l'assembly du point
d'entrée (`manifest.web.blazor.assembly`), comme les composants intégrables
(ADR 0063) : le chargement paresseux n'apporte que cette DLL. Un site peut être
MVC seul, Blazor seul, ou les deux (ADR 0073 §1).

### 2. Le site répond comme un site Blazor WebAssembly

Pour un site qui a une racine Blazor, `HackWebHost` ajoute après
`Configure` trois réponses, avec la priorité la plus basse (les routes MVC
passent d'abord) :

- la **page hôte** (`index.html` d'un vrai site Blazor) pour toute requête
  `GET`/`HEAD` sans endpoint dont le dernier segment n'a pas d'extension
  (`MapFallbackToFile` d'ASP.NET Core) : `<div id="app">`, puis
  `<script src="_framework/blazor.hack.js">` ;
- `/_framework/blazor.hack.js`, le chargeur, qui dans un autre navigateur
  (ou une page sans l'OS) affiche seulement que le site demande le navigateur
  de HackerOS ;
- `/_framework/blazor.boot.json` : identifiant de l'app, nom, composant racine,
  assembly d'entrée.

`curl` voit donc la même chose qu'avec un vrai site Blazor : une page presque
vide qui charge un script.

### 3. Le navigateur démarre le site comme un moteur de rendu

Quand une réponse `text/html` charge `_framework/blazor.hack.js`, le navigateur
lit `blazor.boot.json` (visible dans l'onglet Network), puis demande à l'OS de
démarrer le site par `IHackWebSiteHost` (SDK Blazor, implémenté par la
plateforme, comme `IHackComponentHost`). L'OS :

1. retrouve l'app `Web` qui déclare l'origine et le chemin de la page dans
   `web.urls`, et refuse si `blazor.boot.json` nomme une autre app (une page ne
   peut pas démarrer le code d'un autre site) ;
2. charge son assembly (chargement paresseux) et vérifie que la racine est un
   composant ;
3. démarre un **processus de rendu**, enfant du processus du navigateur, comme
   `chrome --type=renderer` : même identifiant d'app que le site, utilisateur de
   l'onglet, `argv` `[<hôte>, --type=renderer, <url>]`, capacités accordées au
   site **moins** `network.listen*` (le code client n'écoute pas). Ce processus
   est **auxiliaire** : il n'est jamais pris pour l'instance du site, si bien que
   l'activation à la demande du serveur (ADR 0074 §3) ne change pas. Il tourne
   sur le fil principal, où le navigateur le dessine (ADR 0073 §1).

Le processus de rendu se termine quand l'onglet change de document, se ferme ou
recharge la page (un rechargement démarre un nouveau processus, comme un
navigateur recharge le runtime).

### 4. Rendu dans la zone de page

`HackWebSiteView` (SDK Blazor) dessine le composant racine directement dans la
zone de page, sans iframe : c'est du code C# installé, au même niveau de
confiance qu'une app à fenêtre, limité par ses capacités, et son style est isolé
par le CSS scopé. Le JavaScript des pages HTML reste dans l'iframe isolée de
l'ADR 0075. Le composant reçoit en cascade le contexte d'exécution du processus
de rendu (les composants dérivés de `HackComponentBase` appellent le SDK sous
l'identité du site) et un `HackWebSite` : adresse, navigation, client HTTP,
titre.

Une exception non gérée d'un composant du site est arrêtée par une
`ErrorBoundary` : le processus de rendu passe en défaut, l'onglet montre la page
« Aw, Snap! » et la console des outils de développement l'exception. Tuer le
processus de rendu (gestionnaire de tâches, `kill`) donne la même page. Le reste
de l'OS n'est jamais touché.

### 5. Navigation par l'URL de l'onglet

Le `NavigationManager`, le `Router`, `NavLink` et `PageTitle` de Blazor sont
ceux de l'OS (DI du moteur de rendu partagé) : un site ne doit pas les utiliser.
Le SDK fournit leurs équivalents propres à l'onglet :

- `HackSiteNavigationManager` dérive de `NavigationManager` : `NavigateTo`,
  `LocationChanged`, `BaseUri`, `ToBaseRelativePath` fonctionnent comme dans
  Blazor, sur l'adresse de l'onglet ;
- `HackSiteRouter` trouve les pages par leurs directives `@page` dans
  l'assembly du site (gabarits de Blazor : `{id:int}`, `{x?}`, `{*rest}`), avec
  `Found`/`NotFound` et un layout par défaut ;
- `HackNavLink` et `HackPageTitle` remplacent `NavLink` et `PageTitle`.

Les liens `<a href>` dessinés par le site sont interceptés dans la zone de page
(module JS du composant) : un lien vers une adresse du site devient une
navigation interne, ajoutée à l'historique de l'onglet sans requête, comme
`history.pushState` ; un lien vers ailleurs, ou `forceLoad`, est une navigation
du navigateur. Précédent et suivant entre deux entrées du même site restent dans
le site ; la barre d'adresse et Recharger font une vraie requête.

### 6. Réseau et cookies

`HackWebSite.Http` est un `HackHttpClient` dont `BaseAddress` est l'adresse du
site (nouvelle propriété du SDK, comme `HttpClient.BaseAddress`), qui partage le
pot à cookies de l'onglet et s'exécute sous l'identité du processus de rendu :
un appel `GET api/notes` passe par le réseau simulé et arrive au même site, avec
la session ouverte par ses pages.

### 7. Fenêtre détachée

La zone de page d'un site Blazor se déplace dans la fenêtre détachée par le
portail de l'ADR 0081 §2, sans recharger : contrairement à une page HTML, le
site garde son état (compteurs, formulaires en cours).

## Conséquences

- Les apps `Web` gagnent un manifeste `web.blazor` facultatif ; le schéma, le
  sérialiseur et le validateur le connaissent.
- `ProcessStartRequest` gagne `Auxiliary` : `TryGetSingleton` ignore les
  processus auxiliaires.
- `DisplaySurfaceContent.Surface` devient facultatif (section 7) : le
  navigateur rend sa zone de page une seule fois à travers le portail, et un
  site Blazor garde son état au détachement.
- Le site d'exemple `HackerOs.Samples.BlazorSite` est installé dans les deux
  hôtes (app cachée, `http://blazor-sample.hack/`) : c'est le seul site Blazor
  que le navigateur peut servir tant qu'aucune app n'en déclare.
- Un site Blazor utilise les composants de navigation du SDK, pas ceux de
  Blazor ; un `[Inject] NavigationManager` dans un site obtient celui de l'OS.
  Le guide le dit, et la page « Aw, Snap! » remplace le bandeau d'erreur global
  de Blazor.
- Un formulaire HTML sans gestionnaire `@onsubmit` n'est pas envoyé (aucune
  navigation de document dans l'OS) : un site poste par `EditForm` ou `Http`.
- Les requêtes du site par `Http` ne sont pas dans l'onglet Network (seuls le
  document et `blazor.boot.json` y sont) ; à reprendre si le besoin vient.
