> [English version](../../../en/wasm/framework/asp-hack-mvc.md)

# ASP.Hack MVC : pipeline, routage, contrôleurs et session

Chantier `web/6-mvc-core` de la phase web. Décisions : [ADR 0076](../dev/adr/0076-asp-hack-mvc.md)
(ASP.Hack MVC) et [ADR 0082](../dev/adr/0082-asp-hack-route-source-generator.md) (table
de routes produite par un générateur de sources). Plan :
[`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md). Socle réseau : [`listening-ports-and-dns.md`](listening-ports-and-dns.md).

## Objet

`Shared/HackerOs.AppSdk.Web` permet d'écrire un site interne de HackerOS comme
un site ASP.NET Core MVC : une classe de démarrage, des middlewares, des
contrôleurs avec `[HttpGet]`/`[HttpPost]`/`[HttpPut]`/`[HttpPatch]`/`[HttpDelete]`,
la liaison de modèle, la validation `DataAnnotations`, des résultats JSON, texte,
fichier ou redirection, l'injection de dépendances par requête et une session
par cookie. Le site est une app de genre `web` (ADR 0073) : l'OS la démarre au
premier accès à l'une de ses URL et son code tourne dans le worker `web` partagé.

Vues `.razor`, `View()`, `PartialView()`, anti-falsification et `wwwroot` :
[`asp-hack-razor-views.md`](asp-hack-razor-views.md) (`web/7`, ADR 0083). Vues
`.cshtml` : [`asp-hack-cshtml-views.md`](asp-hack-cshtml-views.md) (`web/8`, ADR 0086).
Hors de ce chantier : sites Blazor (`web/9`).

## Exemple

```csharp
[AppManifest]
public sealed partial class IntranetApp : WebAppDefinition   // partial : le build donne l'EntryPoint
{
    public override string Id => "com.example.intranet";
    public override string Name => "Intranet";
    public override IReadOnlyList<string> Urls => ["http://intranet.hack/"];

    public override void Configure(HackWebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<NoteStore>();
        builder.Json.SerializerOptions.TypeInfoResolverChain.Insert(0, IntranetJsonContext.Default);
        HackWebApplication app = builder.Build();
        app.UseExceptionPage();
        app.UseCookieSession();
        app.Use(async (context, next) => { context.Response.Headers["X-Powered-By"] = "ASP.Hack"; await next(); });
        app.MapGet("/health", () => HackResults.Ok("up"));
        app.MapControllers();          // routes d'attribut + {controller=Home}/{action=Index}/{id?}
    }
}

[Route("api/[controller]")]
public sealed class NotesController(NoteStore store) : HackApiController
{
    [HttpGet] public IReadOnlyList<Note> List([FromQuery] bool? done = null) => ...;
    [HttpGet("{id:int}", Name = "GetNote")]
    public IHackActionResult Get(int id) => store.Find(id) is Note n ? Ok(n) : NotFound();
    [HttpPost] public IHackActionResult Create(Note note) => CreatedAtAction(nameof(Get), new { id = store.Add(note).Id }, note);
}

public sealed class HomeController : HackController
{
    public string Index() => "home";
    [HttpPost, ActionName("Contact")]
    public IHackActionResult ContactPost(ContactForm form) =>
        ModelState.IsValid ? RedirectToAction(nameof(Index)) : BadRequest(ModelState);
}
```

`curl http://intranet.hack/api/notes/1` démarre le site, puis lui passe la
requête par le socket local, comme pour tout site (`web/3`).

## Architecture

```text
HackHttpListener (worker web)                                  web/3
  └─ HackWebHost.ProcessRequestAsync            Hosting/       requête du listener → HackWebRequestData
       └─ HackWebApplication.ProcessAsync                       une portée DI par requête, HackHttpContext
            ├─ routage : HackEndpointRouter.Match               Routing/  404, 405 + Allow, OPTIONS 204, HEAD → GET
            ├─ middlewares (Use, UseMiddleware<T>, UseCookieSession, UseStatusCodePages)
            └─ endpoint
                 ├─ route minimale (MapGet/MapPost/... ou HackRequestDelegate)
                 └─ action : HackActionInvoker.InvokeAsync      Mvc/
                      ├─ CreateController (généré)              services de la portée
                      ├─ BindArguments (généré) → HackModelBinder, ModelState
                      ├─ 415, puis 400 automatique d'une API
                      ├─ filtres : contrôleur, globaux, contrôleur (attribut), action
                      ├─ Invoke (généré)
                      └─ IHackActionResult.ExecuteResultAsync → HackHttpResponse
UseExceptionPage : toute exception du pipeline → page d'erreur (sinon « 500 Internal Server Error » nu)
```

| Dossier | Contenu |
|---|---|
| `Hosting/` | `WebAppDefinition`, `HackWebApplicationBuilder`, `HackWebApplication`, `HackWebHost`, `HackWebJsonOptions`. |
| `Http/` | `HackHttpContext`, `HackHttpRequest`, `HackHttpResponse`, `HackHeaderDictionary`, `HackValueCollection` (requête, formulaire), cookies, `HackWebRequestData` / `HackWebResponseData` (échange indépendant du transport). |
| `Routing/` | `HackRouteTemplate` (analyseur partagé avec le générateur), `HackRoutePattern` (correspondance, génération d'URL), `HackEndpoint`, `HackEndpointRouter`, `HackEndpointDataSource`, `HackLinkGenerator`, `HackRouteValueDictionary`. |
| `Mvc/` | Attributs, `HackControllerBase` / `HackController` / `HackApiController`, `HackModelBinder`, `HackModelStateDictionary`, filtres, résultats, `HackUrlHelper`, `HackActionInvoker`, `HackResults`, contrat `HackControllerActionDescriptor`. |
| `Sessions/` | `HackSession`, `HackSessionOptions`, `HackSessionStore`. |
| `Diagnostics/` | `HackExceptionPage` (page d'erreur au style console). |
| `Tools/HackerOs.Tools.WebRouteGenerator` | Générateur de sources Roslyn (ADR 0082) : table de routes et `<Nom>WebHost`, erreurs `HOSWEB0001` à `HOSWEB0007` (`HOSWEB0008` : vue `.razor` et `.cshtml` au même chemin, ADR 0086). |

## Démarrage

- `WebAppDefinition` : `Kind` est `Web` (scellé), `Category` `internet`,
  `LaunchVisibility` `Hidden`, `Urls` à fournir. `Configure(builder)` tourne dans
  le processus du site à chaque démarrage ; par défaut il appelle
  `builder.Build().MapControllers()`.
- `HackWebApplicationBuilder` : `Services` (`IServiceCollection`), `Json`
  (`HackWebJsonOptions.SerializerOptions`, défauts web : camelCase), `Mvc`
  (`HackMvcOptions.Filters`, filtres globaux), `Build()`. Le fournisseur est
  construit avec `ValidateScopes` et `ValidateOnBuild`.
- `HackWebApplication` : `Use(...)` (deux formes, comme ASP.NET Core),
  `UseMiddleware<T>` (`IHackMiddleware`), `UseExceptionPage`,
  `UseStatusCodePages`, `UseCookieSession(options)`, `Map`, `MapMethods`,
  `MapGet`/`MapPost`/`MapPut`/`MapPatch`/`MapDelete`, `MapControllers`,
  `MapControllerRoute(name, pattern[, defaults])`, `MapDefaultControllerRoute`,
  `ProcessAsync(HackWebRequestData)`, événement `UnhandledException`.
- `MapControllers()` ajoute la route conventionnelle par défaut
  `{controller=Home}/{action=Index}/{id?}` si aucun `MapControllerRoute` n'est
  appelé, au gel du pipeline (première requête).
- `HackWebHost.CreateApplication(definition, routes)` construit l'application
  sans OS ni listener : c'est ainsi que les tests appellent un site
  (`ProcessAsync(HackWebRequestData.Create("GET", "http://intranet.hack/"))`),
  comme `TestServer` d'ASP.NET Core.

## Routage

Gabarits ASP.NET Core : littéraux (sans casse), `{p}`, `{p?}`, `{p=défaut}`,
`{*reste}` / `{**reste}`, contraintes `int`, `long`, `bool`, `guid`, `double`,
`decimal`, `float`, `datetime`, `alpha`, `required`, `min(n)`, `max(n)`,
`range(a,b)`, `length(n)`/`length(a,b)`, `minlength(n)`, `maxlength(n)`,
`regex(...)` (sans casse, délai 100 ms). `{{` et `}}` échappent une accolade.
Pas de segment complexe.

Choix de l'endpoint, comme le routage par endpoints d'ASP.NET Core :

1. `Order` le plus bas (routes d'attribut 0, routes conventionnelles 1, 2...) ;
2. puis le gabarit le plus précis, segment par segment : littéral, paramètre
   contraint, paramètre, facultatif, attrape-tout ;
3. puis un endpoint limité à des méthodes avant un endpoint « toutes méthodes » ;
4. puis l'ordre d'enregistrement. Deux candidats indiscernables :
   `HackAmbiguousRouteException` (500).

Le chemin connu mais la méthode refusée donne `405` avec `Allow` ; `OPTIONS`
sans endpoint donne `204` avec `Allow` ; `HEAD` utilise l'endpoint `GET` et
renvoie les en-têtes sans corps.

Routes d'attribut : `[Route]` sur le contrôleur (préfixe) et sur l'action,
`[HttpGet("...")]`... (`Name`, `Order`), jetons `[controller]`, `[action]`,
`[area]`, gabarit absolu `/...` ou `~/...` qui ignore le préfixe. Une action
sans route d'attribut d'un contrôleur à vues suit les routes conventionnelles
(`controller` et `action` en valeurs requises). Une action d'un
`HackApiController` doit avoir une route d'attribut (`HOSWEB0007`).

Génération d'URL : `Url.Action(action, controller, values)`,
`Url.RouteUrl(name, values)`, `Url.Content("~/...")`, `Url.IsLocalUrl` ; les
valeurs par défaut en fin de chemin sont omises, les valeurs en trop vont dans
la requête (`/Home/Details/7?sort=asc`).

## Contrôleurs

- Un contrôleur est une classe publique, non abstraite, non générique, dérivée
  de `HackControllerBase`, sans `[NonController]`. Son nom perd le suffixe
  `Controller`. Ses actions : méthodes publiques d'instance, non génériques,
  sans `[NonAction]`, hors membres du framework et `Dispose`.
  `[ActionName]` renomme une action.
- `HackController` (pages) : `Json(...)`, `OnActionExecuting` /
  `OnActionExecuted` (le contrôleur est le premier filtre).
- `HackApiController` : corps JSON par défaut pour les types complexes, `400`
  automatique (`application/problem+json`, `errors`) si `ModelState` est
  invalide, et tout statut d'erreur client sans corps (`NotFound()`,
  `BadRequest()`...) devient un `HackProblemDetails` (RFC 9457 : `type`,
  `title`, `status`, `traceId`).
- Membres de `HackControllerBase` : `HttpContext`, `Request`, `Response`,
  `RouteValues`, `ModelState`, `Url`, et les aides `Ok`, `NoContent`,
  `StatusCode`, `Content`, `File`, `Redirect`, `RedirectPermanent`,
  `LocalRedirect` (refuse une URL externe), `RedirectToAction`, `Created`,
  `CreatedAtAction` (URL absolue), `NotFound`, `BadRequest` (dont
  `BadRequest(ModelState)`), `Unauthorized`, `Forbid`, `Conflict`, `Problem`,
  `ValidationProblem`, et `TryValidateModel(model, prefix)` pour valider un
  modèle construit ou fusionné par l'action (un `PATCH`, ADR 0087 §3).
- Une action rend `void`, `Task`, `ValueTask`, un `IHackActionResult`, un objet
  (texte brut pour une `string`, JSON sinon, `204` pour `null`) ou une tâche de
  l'un d'eux. Un paramètre `CancellationToken` reçoit `RequestAborted`.

## Liaison de modèle et validation

| Paramètre | Source |
|---|---|
| Type simple (`IParsable<T>`, `string`, `enum`, `Nullable<T>`, tableau) | formulaire, puis route, puis requête (comme ASP.NET Core) ; route puis requête pour une API |
| `[FromRoute]`, `[FromQuery]`, `[FromForm]`, `[FromHeader]` (`Name =`) | la source nommée |
| Type complexe d'une API, ou `[FromBody]` | corps JSON (`415` si le corps n'est pas JSON) |
| Type complexe d'un contrôleur à vues | propriétés du formulaire (`Name=...&Tags=a&Tags=b`, case à cocher `on`) |
| `[FromServices]` | portée DI de la requête |
| `CancellationToken`, `HackHttpContext` | valeurs spéciales |

Un paramètre non nullable sans valeur par défaut est requis. Les attributs
`DataAnnotations` du paramètre et des propriétés du modèle sont vérifiés ; les
erreurs vont dans `ModelState` (clé : nom du paramètre ou de la propriété).

## Filtres

`IHackActionFilter` (`OnActionExecuting`, `OnActionExecuted`) et
`IHackAsyncActionFilter`, en attribut (`HackActionFilterAttribute`, `Order`) ou
global (`builder.Mvc.Filters`). Ordre : le contrôleur lui-même, puis globaux,
attributs du contrôleur, attributs de l'action ; le retour se fait dans l'ordre
inverse. Un filtre qui pose `context.Result` court-circuite l'action.

## Session

`app.UseCookieSession()` : cookie `.HackSession` (`HttpOnly`, `SameSite=Lax`,
`Path=/`), identifiant de 32 octets aléatoires en base64url, valeurs en mémoire
du processus du site, expiration après 20 minutes d'inactivité. Le cookie n'est
posé que si la session est écrite. `HttpContext.Session` : `GetString`,
`SetString`, `GetInt32`, `SetInt32`, `TryGetValue`, `Set`, `Remove`, `Clear`,
`Keys`, `Id`, `IsNew`. La session disparaît quand le site s'arrête (comme la
session en mémoire d'ASP.NET Core).

## Contrat pour `web/7` et `web/9`

`web/7` a réalisé sa part de ce contrat ([`asp-hack-razor-views.md`](asp-hack-razor-views.md)) ;
le contrat reste la référence de `web/9`.

- **Résultats.** Une vue est un `IHackActionResult` : `ExecuteResultAsync(HackActionContext)`
  reçoit `HttpContext`, `RouteValues`, `ActionDescriptor` et `ModelState`, et
  écrit dans `HttpContext.Response`. `View()` et `PartialView()` seront ajoutés à
  `HackController` (le contrôleur à vues), pas à `HackApiController`.
- **Recherche de vue.** `HackControllerActionDescriptor.ControllerName` et
  `ActionName` donnent `Views/<Contrôleur>/<Action>` ; le générateur de routes
  écrit aussi la table des vues (`HackWebGeneratedRoutes.Views`, ADR 0083 §2).
- **URL.** `IHackUrlHelper` (`Url` du contrôleur) est la base des helpers de
  vue (`HackLink`, `HackForm`) ; `HackLinkGenerator` est dans les services.
- **Middlewares.** `UseStaticFiles()` sera un middleware ordinaire
  (`Use(next => ...)`) qui répond avant le routage des contrôleurs ; un endpoint
  est choisi avant les middlewares (`HttpContext.Endpoint`), comme
  `UseRouting` d'ASP.NET Core au début du pipeline.
- **Services.** Toute la partie `web/7` (moteur de vues, anti-falsification)
  s'enregistre dans `builder.Services` ; le builder enregistre déjà
  `HackWebJsonOptions`, `HackMvcOptions`, `HackLinkGenerator` et le magasin de
  sessions.
- **Blazor (`web/9`, [`blazor-sites.md`](blazor-sites.md)).** Un site Blazor
  déclare son composant racine par `BlazorRoot` ; `HackWebHost` sert alors la
  page hôte et `blazor.boot.json` en repli après les routes de l'app, qui garde
  `HackWebApplication` pour ses routes de données (`MapGet`, contrôleurs d'API).

## Tests

- `Tests/HackerOs.AppSdk.Web.Tests` : gabarits et routeur
  (`RouteTemplateTests`, `EndpointRouterTests`), pipeline complet d'un site de
  test compilé avec le générateur (`MvcPipelineTests` : CRUD d'API, problèmes,
  `400` automatique, `415`, filtres, routes conventionnelles, formulaires,
  redirections, liens, session, fichiers, page d'erreur, DI par requête),
  générateur piloté sur des extraits de code (`WebRouteGeneratorTests` :
  `HOSWEB0001` à `HOSWEB0007`, table et entrée produites).
- `Tests/HackerOs.Platform.WorkerHost.Tests/MvcSiteTests` : un site MVC démarré
  par `curl` dans un OS complet, sur le fil principal et dans le worker `web`.

## Limites connues

- Un seul analyseur de gabarit : pas de segments complexes, pas de
  transformateurs de paramètres, pas de liaison de modèles imbriqués
  (`Address.City`) ni de dictionnaires.
- `HEAD` n'envoie pas de `Content-Length` du corps qui aurait été produit.
- Pas d'environnements (`Development`/`Production`) ni de `TempData`.
- ~~`curl -H 'Cookie: ...'` est ignoré par la passerelle réseau (`web/3`).~~
  Corrigé par `web/14` (ADR 0089) : l'en-tête atteint le site, fusionné avec
  le pot à cookies de la commande.

## Tâches

- [x] Bibliothèque `HackerOs.AppSdk.Web` : projet, solution, frontière du SDK.
- [x] HTTP : contexte, requête, réponse, en-têtes, cookies, formulaire, échange indépendant du transport.
- [x] Routage : gabarits, contraintes, préséance, `405`/`OPTIONS`/`HEAD`, génération d'URL.
- [x] Générateur de sources (ADR 0082) branché sur les apps, erreurs `HOSWEB0001` à `HOSWEB0007`.
- [x] Contrôleurs, actions, filtres, `OnActionExecuting`.
- [x] Liaison de modèle, `DataAnnotations`, `ModelState`, `400` automatique, `415`.
- [x] Résultats JSON, Content, File, Redirect (dont `LocalRedirect`, `RedirectToAction`), `Created*`, problèmes RFC 9457.
- [x] DI par requête, session par cookie, page d'erreur.
- [x] `WebAppDefinition`, `HackWebHost` et entrée générée ; manifeste produit par le générateur de manifestes.
- [x] Tests unitaires, du générateur et de bout en bout (`curl`, deux modes).
- [x] ADR 0082, ce document, index, plan.
