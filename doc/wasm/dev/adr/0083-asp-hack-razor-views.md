# ADR 0083 : vues `.razor` d'ASP.Hack, anti-falsification et `wwwroot` embarqué

## Statut

Accepté le 2026-10-16, avec le chantier `web/7-razor-views`.

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Complète :** [ADR 0076](0076-asp-hack-mvc.md) §5 (`View`, `PartialView`), §6
  (jeton anti-falsification) et §7 (vues `.razor`) ; [ADR 0073](0073-web-apps.md)
  §2 (`wwwroot` du projet, lecture seule) ; [ADR 0082](0082-asp-hack-route-source-generator.md)
  (le générateur produit aussi la table des vues).
- **Amende :** [ADR 0007](0007-enforce-collocated-razor-assets.md) : la vérification au build
  admet une référence `<script src="...">` sans corps (§6).
- **Documentation :** [`asp-hack-razor-views.md`](../../framework/asp-hack-razor-views.md).

## Contexte

L'ADR 0076 §7 fixe le principe : une vue est un composant Razor compilé au build,
rendu en HTML statique par `HtmlRenderer`, trouvé par convention
`Views/<Contrôleur>/<Action>.razor`. Il reste à décider comment `View()` trouve le
composant sans réflexion (le générateur de routes a exclu la réflexion au
démarrage, ADR 0082), comment un layout, des sections et des partials
fonctionnent avec un rendu en une passe de Blazor, comment le jeton
anti-falsification se transporte dans le réseau simulé, et comment le `wwwroot`
d'un projet est servi alors qu'un site n'a aucune capacité `filesystem.*`
(ADR 0073 §3).

## Décision

### 1. Rendu

`HackViewEngine` rend une vue avec `Microsoft.AspNetCore.Components.Web.HtmlRenderer`
(statique, sans circuit ni interactivité), dont les services sont la **portée DI
de la requête** : `@inject` d'une vue reçoit les mêmes instances que le
contrôleur. Le contexte de la page (`HackViewContext` : requête, action, modèle,
`ModelState`, `ViewData`, URL, layout, sections) est fourni à tout composant de
la page par une valeur en cascade fixe.

### 2. Table des vues générée au build

Le générateur de sources de l'ADR 0082 lit les fichiers `.razor` que le SDK
Razor lui passe comme `AdditionalFiles` (avec leur chemin relatif `TargetPath`)
et écrit `HackWebGeneratedRoutes.Views : HackViewTable`, un
`HackViewDescriptor("Home/Index", typeof(Views.Home.Index))` par fichier sous
`Views/` (sauf `_Imports.razor`). Il nomme la classe comme le compilateur Razor
le fait (`<RootNamespace>.<dossiers>.<fichier>`, identifiants assainis, ou le
`@namespace` du fichier ou d'un `_Imports.razor` au-dessus). L'hôte généré
`<Nom>WebHost` passe la table au `HackWebHost`. Seule la table est lue à
l'exécution ; un composant hors de `Views/` reste utilisable par type
(`View<MonComposant>(model)`).

### 3. Vue, modèle, layout, sections, partials

- Classes de base : `HackView` (modèle non typé), `HackView<TModel>` (`Model`
  typé ; un modèle d'un autre type est une erreur explicite, comme dans ASP.NET
  MVC), `HackLayout` (`@Body`). Tout composant peut servir de vue ; ces classes
  ajoutent les raccourcis MVC (`ViewData`, `Url`, `ModelState`, `Request`).
- **Le layout se rend après la vue**, dans une passe séparée qui reçoit le HTML
  de la vue comme `Body`, puis le layout du layout, et ainsi de suite (comme un
  `.cshtml` s'exécute avant son `_Layout`). Ainsi la vue peut fixer
  `ViewData["Title"]`, changer `Layout` ou définir une section que le layout
  lira. Le layout vient de `@layout` (attribut `[Layout]`), modifiable par la vue.
- Sections : `<HackSection Name>` dans la vue, `<HackRenderSection Name Required>`
  dans le layout ; une section définie et jamais rendue, ou requise et absente,
  est une erreur, comme dans ASP.NET MVC. Un layout sans `@Body` est une erreur.
- Partials : `<HackPartial Name Model>` cherche la vue dans le dossier du
  contrôleur puis `Shared/`, et la rend **dans la même passe** avec un contexte
  dérivé (même requête et `ViewData`, autre modèle). `PartialView()` rend une vue
  sans layout ni vérification des sections.
- Recherche : `View()` → `Views/<Contrôleur>/<Action>`, puis `Views/Shared/<Action>` ;
  un chemin (`~/Views/X/Y.razor`, `/Views/X/Y`) est pris tel quel. L'erreur
  liste les emplacements cherchés.

### 4. `HackController`

`View()`, `View(model)`, `View(name)`, `View(name, model)`, `View<TView>(model)`,
les `PartialView` équivalents, `ViewData`. `HackApiController` n'a pas de vues.
Comme dans ASP.NET MVC, `View("texte")` désigne un **nom de vue** : un modèle
`string` se passe par `View(viewName: null, model: texte)` ou dans une classe.

### 5. Anti-falsification

`HackAntiforgery` (service du site) suit le schéma d'ASP.NET Core : un secret
aléatoire de 32 octets dans un cookie `.HackAntiforgery` (`HttpOnly`,
`SameSite=Strict`, `Path=/`) et, dans chaque formulaire, un jeton de requête
`nonce ‖ HMAC-SHA256(clé, secret ‖ nonce)` (champ `__RequestVerificationToken`,
ou en-tête `RequestVerificationToken` pour un script). La clé est tirée au
démarrage du processus du site et vit en mémoire : après un redémarrage, un
formulaire déjà affiché est refusé (`400`) et doit être rechargé, comme sans
anneau de clés persistant. `[ValidateAntiForgeryToken]` est un filtre d'action
(`400 Bad Request` texte, raison incluse ; `GET`/`HEAD`/`OPTIONS`/`TRACE` jamais
vérifiés) ; `[IgnoreAntiforgeryToken]` l'annule pour une action. `<HackForm>`
écrit le champ caché sauf `Antiforgery="false"` ou `Method="get"` ;
`<HackAntiforgeryToken>` le fournit à un `<form>` écrit à la main.

### 6. `wwwroot` embarqué, servi en lecture seule

- `build/HackerOs.Apps.targets` embarque `wwwroot/**` d'un projet qui référence
  `HackerOs.AppSdk.Web` comme ressources `wwwroot/<chemin>` (propriété
  `HackerOsEmbedWebRoot` pour forcer ou interdire), et les retire des contenus :
  le `wwwroot` d'un site n'est jamais une ressource web statique de l'hôte
  (`_content/`). Le Razor class library d'une app à fenêtre garde son `wwwroot`
  habituel.
- `app.UseStaticFiles()` (`HackStaticFileMiddleware`) sert ces ressources :
  `Content-Type` par extension (`SimulatedFileTypes`), `ETag`, `304`,
  `Cache-Control: public, max-age=3600`, `index.html` d'un dossier, `405` hors
  `GET`/`HEAD` ; une requête sans fichier continue vers les contrôleurs. Les
  noms sont lus une fois au démarrage et comparés exactement (`..` ne peut rien
  atteindre).
- La vérification de l'ADR 0007 (pas de CSS/JS inline dans un `.razor`) admet
  désormais `<script src="...">` **sans corps** : un layout lie ainsi les scripts
  du `wwwroot` du site, comme toute page web ; `<style>`, `style=`, `<script>`
  avec corps et `onxxx=` restent refusés.

## Conséquences

- `HackerOs.AppSdk.Web` référence `Microsoft.AspNetCore.Components.Web` (pour
  `HtmlRenderer` et les composants) ; les projets de test qui ont des vues
  passent au SDK `Microsoft.NET.Sdk.Razor`.
- `HackWebApplicationBuilder` et `HackWebHost` gagnent une surcharge avec
  `HackViewTable` et le type de la définition (assembly du `wwwroot`) ; les
  anciennes signatures restent (table vide, pas de `wwwroot`).
- Le chantier `web/8` (`.cshtml`) peut réutiliser `HackViewEngine.FindView`,
  `HackViewContext` et l'anti-falsification : seule la classe de base de la vue
  change.
- Limites acceptées : pas de `TempData` ni de `ViewBag` dynamique ; pas de
  `@section` imbriquées dans un partial ; un `@namespace` déclaré ailleurs que
  dans le fichier ou un `_Imports.razor` (par exemple dans `.csproj`) n'est pas
  lu ; `HEAD` d'un fichier statique n'envoie pas `Content-Length`.
