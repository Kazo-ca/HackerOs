> [English version](../../../en/wasm/framework/asp-hack-developer-guide.md)

# Guide du développeur ASP.Hack

Chantier `web/11-sample-final`, dernier de la phase web ([`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md)).
Ce guide montre comment écrire un **site interne** de HackerOS avec ASP.Hack, en
suivant l'exemple **Intranet** (`Apps/Samples/HackerOs.Samples.Intranet`,
`http://intranet.hack/`), qui réunit toutes les briques de la phase. Décisions
propres à ce chantier : [ADR 0087](../dev/adr/0087-asp-hack-intranet-sample-adjustments.md).
Référence de chaque type : [`sdk-reference.md`](sdk-reference.md#sites-web-asphack).
Guide des apps fenêtrées, commandes et services : [`sdk-developer-guide.md`](sdk-developer-guide.md).

## Objet

ASP.Hack reprend la façon d'écrire un site ASP.NET Core MVC, sans ASP.NET (qui
ne tourne pas dans le navigateur) : une classe de démarrage, des middlewares,
des contrôleurs à attributs `[HttpGet]`/`[HttpPost]`..., des vues `.cshtml` ou
`.razor`, un `wwwroot`, une session par cookie, et, si on le veut, des pages
Blazor interactives. Le site est une **app de genre `Web`** : un processus de
l'OS qui écoute un port, démarré à la première requête, visible dans `ps`, le
gestionnaire de tâches et `netstat -tlnp`.

| Brique | Chantier, document | Dans l'exemple |
|---|---|---|
| Corps HTTP réels | `web/2`, [`http-bodies.md`](http-bodies.md) | JSON, formulaires, CSS et JS servis avec leur `Content-Type` |
| Écoute, DNS, activation à la demande | `web/3`, [`listening-ports-and-dns.md`](listening-ports-and-dns.md) | `intranet.hack` résolu par le manifeste, site démarré par `curl` ou le navigateur |
| Page HTML isolée, pont `fetch` | `web/4`, [`browser-html-rendering.md`](../dev/browser-html-rendering.md) | `health.js`, `people.js`, `notes.js` |
| Pipeline, routage, contrôleurs, session | `web/6`, [`asp-hack-mvc.md`](asp-hack-mvc.md) | `IntranetSite.Configure`, `Controllers/` |
| Vues `.razor`, anti-falsification, `wwwroot` | `web/7`, [`asp-hack-razor-views.md`](asp-hack-razor-views.md) | `Views/Notes/`, `Views/Shared/_Layout.razor`, `wwwroot/` |
| Vues `.cshtml` | `web/8`, [`asp-hack-cshtml-views.md`](asp-hack-cshtml-views.md) | `Views/Home/`, `Views/People/`, `_ViewStart.cshtml` |
| Site Blazor interactif | `web/9`, [`blazor-sites.md`](blazor-sites.md) | `Board/` sous `/board` |
| Fenêtre détachée | `web/10`, [`detached-display-windows.md`](../dev/detached-display-windows.md) | bouton `⧉` du navigateur, sur n'importe quelle page |
| Sites statiques sans code | `web/5`, [`static-sites.md`](../dev/static-sites.md) | (autre sorte de site : fichiers de l'utilisateur servis par `hackwebd`) |
| Base du site, journal de débogage, saisie semi-automatique | `web/12`, [`asp-hack-site-data.md`](asp-hack-site-data.md) | notes dans la base IndexedDB `hackweb.org.hackeros.samples.intranet`, `ILogger<NotesController>`, titres proposés par le navigateur |

## Visiter l'exemple

Lancer un hôte (`dotnet run --project OS/HackerOs.Ecosystem`, voir
[`building-and-testing.md`](../dev/building-and-testing.md)), ouvrir le navigateur de
l'OS et taper `intranet.hack`.

| Adresse | Ce qu'on y voit | Ce que ça montre |
|---|---|---|
| `/` | Annonces, compteurs, état du site | vue `.cshtml` dans un layout `.razor` ; `health.js` appelle `/health` par `fetch` |
| `/Home/Profile` | Choix d'un nom | `Html.BeginForm`, `TextBoxFor`, validation, jeton anti-falsification, nom gardé en session |
| `/People` | Annuaire | recherche faite par le serveur (`?q=`), puis filtrée par `people.js` sur `/api/people` à chaque touche |
| `/Notes` | Wiki d'équipe | vues `.razor`, partial `_NoteCard`, création/édition/suppression par formulaires (post/redirect/get), étoile qui épingle par `PATCH /api/notes/{id}` ; notes gardées dans la base du site (`HackWebDb`), qui survivent à l'arrêt du site et au redémarrage |
| `/board` | Tableau de tâches | site Blazor dessiné par le navigateur : ajouter, déplacer, supprimer une carte sans recharger, lien vers `/board/tasks/{id}` |
| `/api/notes`, `/api/people`, `/api/tasks`, `/api/me`, `/health` | JSON | API REST complète (`GET`, `POST`, `PUT`, `PATCH`, `DELETE`, problèmes RFC 9457) |

Dans un terminal de l'OS :

```text
$ curl http://intranet.hack/health
{"status":"up","notes":3,"tasks":4,"startedAt":"2026-10-17T00:42:10+00:00"}
$ curl -i -X POST -H 'Content-Type: application/json' -d '{"title":"From curl"}' http://intranet.hack/api/notes
HTTP/1.1 201 Created
Location: http://intranet.hack/api/notes/4
...
$ netstat -tlnp        # le site écoute :80, sous l'identifiant de l'app
$ ps aux | grep intranet
```

`F12` dans le navigateur ouvre l'onglet **Network** (chaque `fetch` des scripts
y passe) et **Console** (les `console.log` des scripts).

## 1. Créer le projet

Un site est un projet sous `Apps/` (comme toute app livrée), au SDK Razor :

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.Web" Version="10.0.13" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\Shared\HackerOs.App.Abstractions\HackerOs.App.Abstractions.csproj" />
    <ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk\HackerOs.AppSdk.csproj" />
    <ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk.Web\HackerOs.AppSdk.Web.csproj" />
    <!-- Seulement pour des pages Blazor interactives : -->
    <ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk.Blazor\HackerOs.AppSdk.Blazor.csproj" />
    <ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk.Web.Blazor\HackerOs.AppSdk.Web.Blazor.csproj" />
  </ItemGroup>
</Project>
```

Le build (`build/HackerOs.Apps.targets`) ajoute tout seul, parce que le projet
référence `HackerOs.AppSdk.Web` :

- le générateur de routes (ADR 0082) : table des contrôleurs, table des vues et
  classe d'entrée `<Définition>WebHost` ;
- le générateur de manifeste : `manifest.json` (genre `web`, `web.urls`,
  `web.blazor`) ;
- l'embarquement de `wwwroot/**` en ressources de l'assembly ;
- pour les `.cshtml` : le `_ViewImports.cshtml` racine et les types de
  compatibilité du compilateur Razor (ADR 0086). Ne mettez **pas** de
  `_ViewImports.cshtml` à la racine du projet (erreur de build) : utilisez
  `Views/_ViewImports.cshtml`.

Arborescence de l'exemple :

```text
HackerOs.Samples.Intranet/
  IntranetSite.cs                 [AppManifest] partial WebAppDefinition, Configure
  Models/                         Note, Person, BoardTask, ProfileForm, IntranetJsonContext
  Services/                       NoteStore, TaskBoard, StaffDirectory, AnnouncementFeed, IntranetSession
  Controllers/                    HomeController, NotesController, PeopleController (pages)
  Controllers/Api/                NotesApiController, PeopleApiController, TasksApiController
  Views/
    _Imports.razor                @using des vues .razor
    _ViewImports.cshtml           @using des vues .cshtml
    _ViewStart.cshtml             @{ Layout = "_Layout"; }
    Shared/_Layout.razor(.cs)     layout commun aux deux genres de vues
    Home/Index.cshtml, Home/Profile.cshtml, People/Index.cshtml
    Notes/Index.razor, Notes/Details.razor, Notes/Edit.razor, Notes/_NoteCard.razor
  wwwroot/css/site.css, wwwroot/js/health.js, people.js, notes.js
  Board/                          partie Blazor : BoardApp (racine), BoardLayout, Pages/BoardPage, Pages/TaskPage
```

## 2. Déclarer le site : `WebAppDefinition`

```csharp
[AppManifest]
public sealed partial class IntranetSite : WebAppDefinition   // partial : le build écrit IntranetSiteWebHost
{
    public override string Id => "org.hackeros.samples.intranet";
    public override string Name => "Intranet";
    public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];
    public override Type? BlazorRoot => typeof(BoardApp);           // facultatif : pages Blazor
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.NetworkListen, AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkSimulatedWrite];

    public override void Configure(HackWebApplicationBuilder builder) { ... }
}
```

- **`Urls`** : les adresses que le DNS de l'OS associe au site (domaine `.hack`
  pour un site interne, ADR 0074). Le port vient de l'URL (80 par défaut ; un
  port < 1024 est accordé d'office sur ses propres `Urls`).
- **Capacités** : `network.listen` pour écouter ; `network.simulated.*` si les
  pages Blazor appellent l'API du site. Jamais `filesystem.*` : le générateur la
  refuse (`HOSAPP0050`), les données d'un site ne vont pas dans les fichiers de
  l'OS (ADR 0073 §3).
- `Kind` (`Web`), la catégorie (`internet`) et la visibilité (`Hidden` : un site
  ne s'ouvre pas depuis le lanceur) sont fixés par `WebAppDefinition`.

## 3. Le démarrage : services, pipeline, endpoints

`Configure` tourne dans le processus du site à chaque démarrage, dans le worker
`web` partagé quand l'hôte en a un. L'ordre est celui d'ASP.NET Core :

```csharp
public override void Configure(HackWebApplicationBuilder builder)
{
    builder.Services.AddSingleton<NoteStore>();                 // un par processus du site
    builder.Services.AddSingleton<TaskBoard>();
    builder.Json.SerializerOptions.TypeInfoResolverChain.Insert(0, IntranetJsonContext.Default);

    HackWebApplication app = builder.Build();
    app.UseExceptionPage();          // une exception devient une page d'erreur au style console
    app.UseStaticFiles();            // wwwroot/ embarqué, avant le routage des contrôleurs
    app.UseCookieSession();          // HttpContext.Session, cookie .HackSession
    app.Use(async (context, next) => { context.Response.Headers["X-Powered-By"] = "ASP.Hack"; await next(); });

    app.MapGet("/health", context => HackResults.Json(..., IntranetJsonContext.Default.SiteStatus));
    app.MapGet("/api/me", context => HackResults.Json(new Visitor(IntranetSession.DisplayName(context)), ...));
    app.MapControllers();            // routes d'attribut + {controller=Home}/{action=Index}/{id?}
    // Avec BlazorRoot, la page hôte Blazor est ajoutée après Configure, en dernier recours.
}
```

- Enregistrez un `JsonSerializerContext` (génération de sources) pour tous les
  types échangés : le site est compilé pour le trimming comme toute app.
- Un singleton vit autant que le processus du site ; un service `Scoped` vit le
  temps d'une requête (les contrôleurs et les `@inject` des vues de la même
  requête reçoivent la même instance).

## 4. Contrôleurs de pages

`HackController` (le `Controller` d'ASP.NET) : `View()`, `PartialView()`,
`ViewData`, `RedirectToAction`, liaison des types complexes depuis le
formulaire.

```csharp
public sealed class NotesController(NoteStore store, ILogger<NotesController> logger) : HackController
{
    public async Task<IHackActionResult> Index(CancellationToken cancellationToken) =>
        View(await store.AllAsync(cancellationToken));                       // Views/Notes/Index.razor

    [HttpGet]
    public IHackActionResult Create() => View("Edit", new Note());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IHackActionResult> Create(Note note, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) { return View("Edit", note); }               // ré-affiché avec ses erreurs
        Note created = await store.AddAsync(note, cancellationToken);         // base IndexedDB du site
        logger.LogInformation("Note {NoteId} created", created.Id);           // /var/log/hackweb/<id>.log
        return RedirectToAction(nameof(Details), new { id = created.Id });   // post/redirect/get
    }
}
```

- Une action peut être asynchrone (`Task<IHackActionResult>`, `Task<T>` pour
  une API) ; un paramètre `CancellationToken` reçoit `RequestAborted`. Le
  suffixe `Async` d'un nom de méthode est retiré du nom de l'action, comme dans
  ASP.NET Core.

- Une action sans route d'attribut suit la route conventionnelle :
  `/Notes/Edit/4` → `NotesController.Edit(4)`. Deux actions du même nom se
  distinguent par `[HttpGet]`/`[HttpPost]`.
- `[ValidateAntiForgeryToken]` refuse un `POST` sans jeton valide (`400`). Le
  navigateur de l'OS garde le cookie anti-falsification entre la page et
  l'envoi ; `curl -d` sans jeton reçoit `400`, comme sur un site ASP.NET.
- La validation vient des `DataAnnotations` du modèle (`[Required]`,
  `[StringLength]`, `[Display(Name)]`...).
- Session : `HttpContext.Session.SetString("displayName", ...)`, lue partout
  par `HttpContext.Session.GetString(...)` (`IntranetSession` dans l'exemple).

## 5. API REST

`HackApiController` (`[ApiController]` d'ASP.NET) : corps JSON par défaut,
`400` automatique avec un problème RFC 9457 si `ModelState` est invalide, et
statuts d'erreur sans corps changés en problèmes. Une action d'API doit avoir
une route d'attribut (`HOSWEB0007`).

```csharp
[Route("api/notes")]
public sealed class NotesApiController(NoteStore store) : HackApiController
{
    [HttpGet] public IReadOnlyList<Note> List([FromQuery] string? q = null) => ...;
    [HttpGet("{id:int}", Name = "GetNote")]
    public IHackActionResult Get(int id) => store.Find(id) is Note note ? Ok(note) : NotFound();
    [HttpPost]
    public IHackActionResult Create(Note note) =>
        CreatedAtAction(nameof(Get), new { id = store.Add(note).Id }, note);      // 201 + Location absolue
    [HttpPut("{id:int}")] public IHackActionResult Replace(int id, Note note) => ...;
    [HttpPatch("{id:int}")]
    public IHackActionResult Patch(int id, NotePatch patch)
    {
        Note note = ...;                                   // fusion du corps partiel dans l'entité
        if (!TryValidateModel(note)) { return ValidationProblem(); }              // ADR 0087 §3
        return Ok(store.Update(note));
    }
    [HttpDelete("{id:int}")] public IHackActionResult Delete(int id) => store.Remove(id) ? NoContent() : NotFound();
}
```

Le chemin connu avec une mauvaise méthode répond `405` avec `Allow`
(`DELETE /api/notes`). `OPTIONS` répond `204`, `HEAD` utilise le `GET`.

## 6. Vues

Les deux genres de vues se mêlent ; choisissez par goût, le moteur est le même.

| | `.cshtml` (ADR 0086) | `.razor` (ADR 0083) |
|---|---|---|
| Modèle | `@model HomeViewModel` | `@inherits HackView<Note>` |
| Layout | `_ViewStart.cshtml` (`Layout = "_Layout"`) ou `Layout = "..."` | `@layout _Layout` |
| Lien | `@Html.ActionLink("Notes", "Index", "Notes")` | `<HackLink Action="Index" Controller="Notes">` |
| Formulaire | `@using (Html.BeginForm("Profile", "Home")) { ... }` | `<HackForm Action="Edit" RouteValues="...">` |
| Champ lié | `@Html.TextBoxFor(m => m.Name)`, `ValidationMessageFor` | `<input name="Title" value="@Model.Title" />`, `<HackValidationMessage For="Title" />` |
| Section | `@section Scripts { ... }` | `<HackSection Name="Scripts">` |
| Partial | `@await Html.PartialAsync("_Card", x)` | `<HackPartial Name="_NoteCard" Model="note" />` |

Dans l'exemple, le layout est un composant `.razor` (`Views/Shared/_Layout.razor`,
classe `HackLayout`, code dans `_Layout.razor.cs`) ; les vues `.cshtml` le
trouvent par `_ViewStart.cshtml`, les vues `.razor` par `@layout _Layout`. Il
rend le titre (`ViewData.Title`), la navigation (lien actif d'après
`Request.RouteValues["controller"]`), le nom de session et la section
`Scripts` de la vue.

Règles à connaître :

- Pas de style ni de script en ligne (ADR 0007, vérifié au build pour les
  `.razor` et les `.cshtml`) : le CSS va dans `wwwroot/css`, le JavaScript dans
  `wwwroot/js`, référencés par `Url.Content("~/...")`. `<script src>` sans corps
  est permis. Le CSS scopé (`.razor.css`) ne s'applique pas à une vue : la page
  est rendue dans l'iframe du navigateur, pas dans l'OS.
- `View("texte")` est un **nom de vue** ; pour un modèle `string`, passez par
  une classe de modèle.
- Une valeur écrite par du C# est encodée en HTML (`·` devient `&#xB7;`, que le
  navigateur affiche normalement).

## 7. JavaScript des pages

Une page HTML s'affiche dans une iframe isolée (ADR 0075) : son JavaScript
s'exécute, mais tout ce qu'il demande passe par le navigateur de l'OS et le
réseau simulé. `fetch`, `XMLHttpRequest`, `document.cookie`, `localStorage`,
`history.replaceState` marchent ; `WebSocket`, `window.open` non.

```js
// wwwroot/js/notes.js : épingle une note sans recharger la page
const response = await fetch(api + "/" + card.dataset.noteId, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ pinned: pinned })
});
```

- Les URL des API passent du serveur au script par des attributs `data-*`
  (`data-api="@Url.Content("~/api/notes")"`), pas par du script en ligne.
- Prévoyez le cas sans script (formulaire `GET` de `/People`) : le serveur rend
  déjà le résultat.
- Une navigation par script (`location.href = ...`) ne peut pas être suivie
  dans l'iframe (ADR 0079) : mettez la page à jour sur place, ou utilisez un
  lien ou un formulaire.

## 8. Pages Blazor interactives

`BlazorRoot` donne au site un composant racine. Les chemins qu'aucune route du
site ne prend (`/board`, `/board/tasks/5`) répondent la page hôte Blazor ; le
navigateur la reconnaît, démarre un **processus de rendu** du site et dessine
le composant dans la zone de page, sans iframe (ADR 0084).

```razor
@* Board/BoardApp.razor *@
@inherits HackComponentBase
<HackSiteRouter AppAssembly="typeof(BoardApp).Assembly" DefaultLayout="typeof(BoardLayout)">
    <NotFound>...</NotFound>
</HackSiteRouter>

@* Board/Pages/BoardPage.razor *@
@page "/board"
@inherits HackComponentBase
<HackPageTitle Title="Board - Intranet" />
...
```

```csharp
// BoardPage.razor.cs
[CascadingParameter] private HackWebSite? Site { get; set; }

IReadOnlyList<BoardTask>? tasks = await Site.Http.GetFromJsonAsync("api/tasks", IntranetJsonContext.Default.IReadOnlyListBoardTask);
HackHttpResponse created = await Site.Http.PostAsync("api/tasks", HackJsonContent.Create(draft, IntranetJsonContext.Default.BoardTask));
```

- Chaque composant porte `@inherits HackComponentBase` (ou `HackSiteLayoutBase`
  pour un layout) : ses événements s'exécutent sous l'identité du processus de
  rendu. La règle est vérifiée par fichier (`AmbientFrameSourceTests`).
- Les pages n'ont pas accès aux services du site : elles appellent son API par
  `Site.Http`, avec les **cookies de l'onglet**. Le tableau lit ainsi `/api/me`
  et voit le nom choisi sur `/Home/Profile` : même origine, même session.
- Une page est recréée à chaque navigation : l'état durable vit dans un objet
  du layout cascadé aux pages (`BoardSession`).
- **Lier MVC et Blazor** (ADR 0087 §1) : un lien d'une page MVC vers `/board`
  est un lien ordinaire ; un lien d'une page Blazor vers une page MVC porte
  `target="_top"` (`<a href="/Notes" target="_top">`), sinon le routeur Blazor
  le prend pour une page à lui. C'est la règle de Blazor : un lien vers un autre
  cadre n'est pas intercepté.
- Ne déclarez pas de route `@page` qui recouvre une route MVC : les routes du
  site passent avant la page hôte.

## 9. Tester un site

Trois niveaux, tous dans l'exemple :

1. **En mémoire** (`Tests/HackerOs.Samples.Intranet.Tests`, comme `TestServer`
   d'ASP.NET Core) : le site est construit sans OS ni listener et reçoit des
   requêtes.

   ```csharp
   HackWebApplication app = HackWebHost.CreateApplication(
       new IntranetSite(), HackWebGeneratedRoutes.Table, views: HackWebGeneratedRoutes.Views);
   HackWebResponseData home = await app.ProcessAsync(HackWebRequestData.Create("GET", "http://intranet.hack/"));
   ```

   `HackWebGeneratedRoutes` est interne à l'assembly du site :
   `<InternalsVisibleTo Include="HackerOs.Samples.Intranet.Tests" />`.
   `IntranetClient` y garde les cookies comme un onglet et retrouve le champ
   anti-falsification d'un formulaire.
2. **Dans un OS complet** (`Tests/HackerOs.Platform.WorkerHost.Tests/MvcSiteTests`) :
   `curl` démarre le site à la demande, sur le fil principal et dans le worker
   `web`.
3. **Dans Chromium** (`Tests/HackerOs.UI.E2E.Tests/BrowserIntranetTests`) : le
   navigateur de l'OS parcourt l'exemple (scripts, formulaire, session, tableau
   Blazor, retour par `target="_top"`). `BrowserSiteDataTests` y vérifie la
   base IndexedDB du site, la saisie semi-automatique, le journal de débogage
   et le Diagnostic.

En mémoire, `HackWebDb` est une base en mémoire propre à l'application : les
tests n'ont rien à configurer ([`asp-hack-site-data.md`](asp-hack-site-data.md#hors-de-hackeros-et-dans-les-tests)).

## 10. Installer le site dans l'OS

Un site intégré est une `ProjectReference` de l'hôte Ecosystem (le catalogue
`BuiltInApplications.json` en est tiré) et de `test/test` avec
`<BlazorWebAssemblyLazyLoad Include="Mon.Site.dll" />` : l'assembly n'est
chargée qu'à la première requête. Sans `IsSystemApp="true"`, le site est
désinstallable comme une app ordinaire. Les tests de l'Ecosystem comptent les
manifestes et les assemblies paresseuses (`BuildKnownLazyAppsTests`,
`EcosystemBootCoordinatorTests`) : mettez-les à jour.

## 11. Déboguer

| Besoin | Outil |
|---|---|
| Le site tourne-t-il ? | `ps aux`, gestionnaire de tâches, `netstat -tlnp`, `cat /proc/net/tcp` |
| Le nom se résout-il ? | `nslookup intranet.hack`, `/etc/hosts` l'emporte |
| Ce que répond le serveur | `curl -i`, vue source du navigateur (`Ctrl+U`) |
| Requêtes et erreurs des scripts | `F12` : onglets Network et Console |
| Exception du serveur | `UseExceptionPage()` l'affiche ; sinon `500` nu et une ligne dans le journal de l'OS |
| Traces du site | `builder.Logging.EnableDebugLog()`, puis `tail /var/log/hackweb/<id>.log` : requêtes, `ILogger<T>`, exceptions |
| Contenu de la base du site | app « IndexedDB Diagnostic », rubrique *Website databases* (magasins, nombre d'enregistrements, taille ; effacement) |
| Page Blazor qui plante | « Aw, Snap! » du navigateur ; le processus de rendu finit en défaut, l'OS continue |
| Redémarrer le site | `kill` du processus : la requête suivante le redémarre (sessions et données en mémoire perdues, base `HackWebDb` conservée) |

## Limites connues

- **Données.** Les notes de l'exemple sont dans sa base `HackWebDb` (ADR 0088) ;
  les cartes du tableau, les annonces et l'annuaire restent en mémoire du
  processus du site et reviennent à leur état initial quand il s'arrête.
- Les formulaires d'un site Blazor n'ont pas la saisie semi-automatique du
  navigateur (ADR 0088 §6).
- Pas de tag helpers, de Razor Pages (`@page` dans un `.cshtml`), de `TempData`
  ni de `ViewBag` ; pas d'environnements `Development`/`Production`.
- Les requêtes `Site.Http` des pages Blazor n'apparaissent pas dans l'onglet
  Network.
- Chaque `curl` a son pot à cookies. Pour rejouer une session, passez le
  cookie à la main : `curl -H 'Cookie: .HackSession=…'` est envoyé au site,
  fusionné avec le pot (ce qui est tapé gagne), sur l'hôte demandé seulement,
  sans jamais être stocké (ADR 0089).
- Compiler un site **dans l'OS** reste hors périmètre (ADR 0076 §8).

## Tâches

- [x] Lire les documents et ADR 0073 à 0086 de la phase web.
- [x] Exemple Intranet : définition, services, contrôleurs de pages et d'API, vues `.cshtml` et `.razor` dans un layout commun, `wwwroot` (CSS, trois scripts), session, anti-falsification, tableau Blazor.
- [x] Liens d'un site Blazor vers ses pages MVC par `target` (ADR 0087 §1).
- [x] Vues compilables dans une app livrée : classes de base sans annotation de trimming (ADR 0087 §2).
- [x] `TryValidateModel` sur les contrôleurs (ADR 0087 §3).
- [x] Exemple installé dans l'Ecosystem et `test/test`, tests de l'Ecosystem mis à jour.
- [x] Tests en mémoire (`HackerOs.Samples.Intranet.Tests`) et E2E Chromium (`BrowserIntranetTests`).
- [x] `web/12` : notes de l'exemple dans `HackWebDb`, journal de débogage, filtre `GET /api/notes?author=` par l'index `byAuthor` ([`asp-hack-site-data.md`](asp-hack-site-data.md)).
- [x] Ce guide, `sdk-reference.md`, ADR 0087, plan, index et documents de la phase mis à jour.
