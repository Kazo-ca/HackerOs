# ADR 0076 : ASP.Hack MVC, pipeline, routage et vues

## Statut

Accepté le 2026-10-16 (proposé le même jour, puis validé par Benoit avec le
questionnaire de la section 4 du plan).

- **Phase du plan :** phase web ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Dépend de :** ADR 0073 (genre `Web`), ADR 0074 (`HackHttpListener`).

## Contexte

Benoit veut une couche par-dessus le SDK qui reprenne la façon de travailler
d'ASP.NET MVC (méthodes `GET`, `POST`, `PUT`, `PATCH`, `DELETE`...), sans
reprendre tout ASP.NET, avec des vues à la `.cshtml` et un contrôleur derrière.
ASP.NET Core lui-même ne tourne pas dans Blazor WebAssembly (pas de Kestrel ni
de référence `Microsoft.AspNetCore.App`).

## Décision

### 1. Bibliothèque

`Shared/HackerOs.AppSdk.Web` (bibliothèque du SDK, `HackerOsSdkLibrary`), qui ne
dépend que de `HackerOs.AppSdk`. Le site est un processus qui ouvre un
`HackHttpListener` sur ses `Urls` et passe chaque requête au pipeline.

### 2. Démarrage et pipeline

```csharp
public override void Configure(HackWebApplicationBuilder builder)
{
    builder.Services.AddSingleton<NotesStore>();
    var app = builder.Build();
    app.UseExceptionPage();     // page d'erreur détaillée en développement
    app.UseStaticFiles();       // wwwroot embarqué, lecture seule (ADR 0073 §2)
    app.UseCookieSession();
    app.Use(async (ctx, next) => { ...; await next(); });
    app.MapControllers();
    app.MapGet("/health", () => HackResults.Ok("up"));   // routes minimales
}
```

Un middleware reçoit `HackHttpContext` (`Request`, `Response`, `Items`,
`RequestServices`, `Session`, `RequestAborted`).

### 3. Contrôleurs et routage

- `HackController` (avec vues) et `HackApiController` (sans vues, erreurs en
  `application/problem+json`).
- Attributs aux noms d'ASP.NET : `[Route]`, `[HttpGet]`, `[HttpPost]`,
  `[HttpPut]`, `[HttpPatch]`, `[HttpDelete]`, `[HttpHead]`, `[HttpOptions]`,
  `[NonAction]`. Route conventionnelle par défaut
  `{controller=Home}/{action=Index}/{id?}`.
- Gabarits : segments littéraux, `{param}`, `{param?}`, valeur par défaut,
  `{*reste}`, contraintes `int`, `long`, `guid`, `bool`, `alpha`, `min`, `max`,
  `length`, `regex`.
- **La table de routes est générée au build** par le générateur de manifestes
  (pas de réflexion au démarrage) : compatible avec le trimming, plus rapide, et
  les conflits de routes sont des erreurs de build (`HOSWEB0001`...). Le même
  générateur pourra servir au compilateur dans l'OS.
- Méthode non permise : `405` avec `Allow` ; `HEAD` répond comme `GET` sans
  corps ; `OPTIONS` liste les méthodes.

### 4. Liaison de modèle et validation

Sources : `[FromRoute]`, `[FromQuery]`, `[FromForm]`, `[FromBody]` (JSON par
`System.Text.Json`), `[FromHeader]`, `[FromServices]`, avec inférence comme
ASP.NET (types simples depuis la route puis la requête, types complexes depuis le
corps pour une API, le formulaire pour un contrôleur à vues). Validation par
`DataAnnotations` dans `ModelState` ; un `HackApiController` répond `400`
automatiquement si le modèle est invalide.

### 5. Résultats

`IHackActionResult` : `View`, `PartialView`, `Json`, `Content`, `File`,
`Redirect`, `RedirectToAction`, `LocalRedirect`, `StatusCode`, `Ok`, `Created`,
`CreatedAtAction`, `NoContent`, `NotFound`, `BadRequest`, `Unauthorized`,
`Forbid`, `Problem`. Une action peut aussi rendre un objet (sérialisé en JSON)
ou une `Task` de ces types.

### 6. Services, session, sécurité

- Injection de dépendances par `Microsoft.Extensions.DependencyInjection`, une
  portée par requête ; les façades `Hack*` du SDK restent disponibles selon les
  capacités du site.
- Session par cookie (`HackSession`), en mémoire du processus du site.
- Jeton anti-falsification pour les formulaires `POST` des vues
  (`[ValidateAntiForgeryToken]`).
- Filtres d'action simples (`IHackActionFilter`, avant/après, en attribut ou
  global).
- Pas d'authentification complète en v1.

### 7. Vues `.razor`

Une vue est un composant Razor compilé au build avec un paramètre `Model` :

```razor
@inherits HackView<HomeModel>
@layout MainLayout
<h1>@Model.Title</h1>
<HackForm Action="Contact" Controller="Home"> ... </HackForm>
```

Elle est rendue en HTML statique par `HtmlRenderer` (.NET, comme les
composants rendus côté serveur d'ASP.NET 8), sans interactivité. Recherche par
convention `Views/<Contrôleur>/<Action>.razor` (`View()` sans type), ou par type
(`View<HomeIndexView>(model)`). Helpers : `Url.Action`, `HackForm`,
`HackLink`, `HackValidationMessage`.

### 8. Vues `.cshtml`

Le compilateur Razor sait produire du C# à partir d'un `.cshtml` au build. Le
chantier `web/8-cshtml-views` commence par une preuve : compiler un `.cshtml`
avec `@model`, `@inject`, `@section`, `_Layout` et `_ViewStart` contre une classe
de base `HackRazorPage<TModel>` d'ASP.Hack, sans référencer ASP.NET MVC. Si la
preuve réussit, `View()` trouve `Views/<Contrôleur>/<Action>.cshtml` aussi. Si
elle échoue, l'autre voie est déjà choisie : un moteur `.cshtml` interprété,
propre à ASP.Hack, qui couvre un sous-ensemble (`@model`, `@using`, `@if`,
`@foreach`, `@switch`, expressions `@Model.X`, `@section`, `_Layout`,
`_ViewStart`, partials) sans C# arbitraire dans la vue ; un ADR court en note
alors les limites avant le code. La compilation d'un `.cshtml` **dans l'OS** (moteur Razor et
Roslyn dans le navigateur) est hors périmètre, mais le choix d'une classe de
base propre à ASP.Hack la rend possible plus tard.

## Conséquences

- Le générateur de manifestes gagne la génération des routes des apps `Web`.
- Les sites MVC tournent dans le worker web partagé (ADR 0073 §1) ; leurs
  vues n'ont pas besoin du DOM.
- Les noms d'attributs identiques à ASP.NET vivent dans l'espace de noms
  `HackerOs.AppSdk.Web.Mvc` et ne peuvent pas entrer en conflit (ASP.NET n'est
  pas référencé).
