# ASP.Hack : vues `.razor`, anti-falsification et `wwwroot`

Chantier `web/7-razor-views` de la phase web. Décisions : [ADR 0076](../dev/adr/0076-asp-hack-mvc.md)
§5 à §7 et [ADR 0083](../dev/adr/0083-asp-hack-razor-views.md). Socle : [`asp-hack-mvc.md`](asp-hack-mvc.md)
(pipeline, routage, contrôleurs). Plan : [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md). Les vues
`.cshtml`, qui partagent ce moteur et se mêlent aux vues `.razor`, sont décrites dans
[`asp-hack-cshtml-views.md`](asp-hack-cshtml-views.md) (`web/8`, ADR 0086).

## Objet

Un contrôleur de pages (`HackController`) répond `View()` : un composant Razor
du dossier `Views/` du projet, compilé au build, rendu en HTML statique par le
`HtmlRenderer` de Blazor dans le processus du site, avec son layout, ses
sections et ses partials. Les formulaires portent un jeton anti-falsification
et le `wwwroot/` du projet est servi en lecture seule par `UseStaticFiles()`.
Rien n'est cherché par réflexion : le générateur de sources de l'ADR 0082 écrit
aussi la table des vues.

## Exemple

```text
Intranet/
  IntranetApp.cs            [AppManifest] partial WebAppDefinition
  Controllers/NotesController.cs
  Views/
    _Imports.razor          @using HackerOs.AppSdk.Web.Views, @using Intranet.Views.Shared
    Shared/_Layout.razor
    Notes/Index.razor, Notes/Edit.razor, Notes/_NoteCard.razor
  wwwroot/css/site.css, wwwroot/js/notes.js
```

```csharp
public override void Configure(HackWebApplicationBuilder builder)
{
    builder.Services.AddSingleton<NoteStore>();
    HackWebApplication app = builder.Build();
    app.UseExceptionPage();
    app.UseStaticFiles();          // wwwroot/ embarqué, avant les contrôleurs
    app.UseCookieSession();
    app.MapControllers();
}

public sealed class NotesController(NoteStore store) : HackController
{
    public IHackActionResult Index() => View(store.All);                 // Views/Notes/Index.razor

    [HttpGet]
    public IHackActionResult Edit(int id) => store.Find(id) is Note n ? View(n) : NotFound();

    [HttpPost, ValidateAntiForgeryToken]
    public IHackActionResult Edit(Note note)
    {
        if (!ModelState.IsValid) { return View(note); }                  // le formulaire ré-affiché avec ses erreurs
        store.Save(note);
        return RedirectToAction(nameof(Index));
    }

    public IHackActionResult Card(int id) => PartialView("_NoteCard", store.Find(id));
}
```

```razor
@* Views/Shared/_Layout.razor *@
@inherits HackLayout
<!DOCTYPE html>
<html><head><title>@(ViewData.Title ?? "Intranet")</title>
<link rel="stylesheet" href="@Url.Content("~/css/site.css")" /></head>
<body><nav><HackLink Action="Index" Controller="Notes">Notes</HackLink></nav>
<main>@Body</main>
<HackRenderSection Name="Scripts" Required="false" /></body></html>

@* Views/Notes/Edit.razor *@
@inherits HackView<Note>
@layout _Layout
@{ ViewData.Title = Model.Title; }
<HackValidationSummary ModelOnly="true" />
<HackForm Action="Edit" RouteValues="@HackRoute.Values(new { id = Model.Id })" class="edit">
    <input name="Title" value="@Model.Title" /> <HackValidationMessage For="Title" />
    <button type="submit">Save</button>
</HackForm>
<HackSection Name="Scripts"><script src="@Url.Content("~/js/notes.js")"></script></HackSection>
```

## Architecture

```text
HackController.View(...)  → HackViewResult                      Views/HackViewResult.cs
  └─ HackViewEngine (service du site)                           Views/HackViewEngine.cs
       ├─ FindView : HackViewTable (générée) : Views/<Contrôleur>/<Nom>, Views/Shared/<Nom>, ou chemin
       ├─ EnsureModelType : HackView<TModel> contre le modèle
       └─ RenderAsync : HtmlRenderer sur la portée DI de la requête
            ├─ passe 1 : la vue (HackViewHost → CascadingValue<HackViewContext> → composant)
            ├─ passe 2..n : chaque @layout, avec le HTML précédent en Body
            └─ sections définies et non rendues → erreur
HackViewContext : requête, action, modèle, ModelState, ViewData, Url, Layout, sections, FindPartial
Helpers (Views/HackViewHelpers.cs) : HackLink, HackForm, HackAntiforgeryToken, HackValidationMessage,
  HackValidationSummary, HackSection, HackRenderSection, HackPartial, HackRoute.Values
Antiforgery/ : HackAntiforgery, HackAntiforgeryOptions, [ValidateAntiForgeryToken], [IgnoreAntiforgeryToken]
Hosting/HackStaticFiles.cs : HackStaticFileOptions, HackStaticFileMiddleware (UseStaticFiles)
Générateur : HackWebGeneratedRoutes.Views, passé par <Nom>WebHost ; build : wwwroot/** embarqué
```

## Vues

- **Classes de base** (`HackerOs.AppSdk.Web.Views`) : `HackView` (`Model` non
  typé), `HackView<TModel>` (`Model` typé), `HackLayout` (`Body`). Toutes
  exposent `ViewContext`, `HttpContext`, `Request`, `ViewData`, `ModelState`,
  `Url`, `Layout`. Un composant quelconque peut aussi servir de vue (sans ces
  raccourcis) : `View<MonComposant>(model)`.
- **Recherche.** `View()` prend le nom de l'action ; `View("Edit")` cherche
  `Views/<Contrôleur>/Edit.razor` puis `Views/Shared/Edit.razor` ;
  `View("~/Views/Notes/Edit.razor")` ou `View("/Views/Notes/Edit")` désigne un
  chemin. Une vue introuvable est une `InvalidOperationException` qui liste
  les emplacements cherchés (page d'erreur `500` avec `UseExceptionPage`).
- **Modèle.** `View(model)` ; une vue `HackView<TModel>` refuse un modèle d'un
  autre type (`null` est accepté pour un type référence). Attention, comme dans
  ASP.NET MVC : `View("texte")` est un **nom de vue** ; pour un modèle `string`,
  écrire `View(viewName: null, model: texte)` ou utiliser une classe de modèle.
- **`ViewData`.** `HackViewDataDictionary` (clés sans casse, `Title`) partagé
  par le contrôleur, la vue, ses partials et ses layouts. Une clé absente se lit
  `null` (`web/8`, ADR 0086), comme `ViewData["x"]` dans ASP.NET.
- **Layout.** `@layout _Layout` dans la vue (ou dans `_Imports.razor` du
  dossier) ; un layout peut avoir son propre `@layout` (imbrication). Le layout
  se rend **après** la vue : il voit `ViewData["Title"]` et les sections. La
  vue peut changer `Layout` dans son code (`Layout = null` : sans layout). Un
  layout sans `@Body` est une erreur.
- **Sections.** `<HackSection Name="Scripts">...</HackSection>` dans la vue,
  `<HackRenderSection Name="Scripts" Required="false" />` dans le layout. Une
  section définie mais jamais rendue, ou requise mais absente, est une erreur
  (mêmes règles qu'ASP.NET MVC).
- **Partials.** `<HackPartial Name="_NoteCard" Model="note" />` (sans `Model` :
  le modèle de la page) ; `PartialView(...)` depuis le contrôleur rend une vue
  sans layout (fragment pour un script). Une vue typée est aussi utilisable
  comme composant : `<_NoteCard Model="note" />`.
- **Injection.** `@inject` reçoit les services de la portée de la requête (les
  mêmes instances que le contrôleur).

## Helpers d'URL et de formulaire

| Composant | Rend |
|---|---|
| `<HackLink Action Controller RouteValues Route Href>` | `<a href="...">` ; `href` produit par la table de routes (`Url.Action`, `Url.RouteUrl`) ou `Href` (`~/` résolu). Attributs supplémentaires recopiés. |
| `<HackForm Action Controller RouteValues Route Href Method="post" Antiforgery="true">` | `<form method action>` plus le champ anti-falsification (sauf `GET` ou `Antiforgery="false"`). |
| `<HackAntiforgeryToken />` | `<input type="hidden" name="__RequestVerificationToken">` pour un `<form>` écrit à la main. |
| `<HackValidationMessage For="Title" />` | `<span class="field-validation-error">première erreur</span>`, ou `field-validation-valid` vide. |
| `<HackValidationSummary ModelOnly />` | `<div class="validation-summary-errors"><ul><li>…</li></ul></div>`, rien si le modèle est valide. |
| `HackRoute.Values(new { id = 3 })` | Les valeurs de route d'un objet anonyme pour `RouteValues` (sûr au trimming). |

Dans le code d'une vue : `Url.Action(...)`, `Url.RouteUrl(...)`, `Url.Content("~/...")`.

## Anti-falsification

`HackAntiforgery` suit le schéma d'ASP.NET Core (ADR 0083 §5) : cookie
`.HackAntiforgery` (`HttpOnly`, `SameSite=Strict`) avec un secret aléatoire,
jeton de requête signé HMAC-SHA256 par formulaire, comparaison en temps
constant. Le jeton voyage dans le champ `__RequestVerificationToken` ou l'en-tête
`RequestVerificationToken`. `[ValidateAntiForgeryToken]` sur une action (ou un
contrôleur) refuse une requête sans jeton valide par `400 Bad Request` texte
dont le corps dit pourquoi ; `GET`, `HEAD`, `OPTIONS`, `TRACE` ne sont jamais
vérifiés ; `[IgnoreAntiforgeryToken]` dispense une action. Noms modifiables par
`builder.Antiforgery`. La clé vit en mémoire du processus : un site redémarré
refuse les formulaires affichés avant (recharger la page).

Dans l'OS, `curl` a un pot à cookies par exécution : `curl -d ... /Notes/Edit/1`
sans jeton obtient `400`, comme un site ASP.NET ; `-H 'Cookie: .HackAntiforgery=…'`
avec le jeton du formulaire rejoue un envoi à la main (ADR 0089). Le navigateur
de l'OS garde les cookies entre la page et l'envoi du formulaire.

## `wwwroot` du projet

- Le build (`build/HackerOs.Apps.targets`, cible `_HackerOsEmbedWebRoot`)
  embarque `wwwroot/**` d'un projet qui référence `HackerOs.AppSdk.Web` en
  ressources `wwwroot/<chemin>` ; `<HackerOsEmbedWebRoot>false</HackerOsEmbedWebRoot>`
  (ou `true` pour un projet qui ne référence pas la bibliothèque) force le
  choix. Ces fichiers ne sont pas des contenus du projet : ils n'entrent pas
  dans les ressources web statiques de l'hôte.
- `app.UseStaticFiles()` les sert en lecture seule : `Content-Type` par
  extension, `ETag` (`304` sur `If-None-Match`), `Cache-Control: public, max-age=3600`,
  `index.html`/`index.htm` d'un dossier, `405` hors `GET`/`HEAD`. Une requête
  sans fichier continue vers les contrôleurs. Options : `Assembly`,
  `ResourcePrefix`, `RequestPath` (`/static`), `DefaultFileNames`, `CacheControl`.
- Un layout référence ces fichiers par `Url.Content("~/css/site.css")`. La
  règle de l'ADR 0007 admet `<script src="...">` sans corps dans un `.razor` ;
  le JavaScript lui-même reste dans `wwwroot/js/`.

## Tests

- `Tests/HackerOs.AppSdk.Web.Tests` (102) : `Views/RazorViewTests` (table
  générée, layout imbriqué, titre, sections, partials, helpers, formulaire valide
  et invalide, jeton en champ et en en-tête, refus, `[IgnoreAntiforgeryToken]`,
  vue typée, partial, `Shared/`, chemin absolu, `@inject`, erreurs, fichiers
  statiques, `304`, `405`, chemins hors `wwwroot`), `Generator/WebRouteGeneratorTests`
  (table des vues et noms de classes du compilateur Razor, `@namespace`,
  `_Imports.razor`).
- `Tests/HackerOs.Platform.WorkerHost.Tests/MvcSiteTests` : la vue, son layout,
  le `wwwroot` et le refus d'un `POST` sans jeton par `curl` dans un OS complet,
  sur le fil principal et dans le worker `web`.

## Limites connues

- Pas de `TempData`, de `ViewBag` dynamique ni d'environnements.
- Un `@namespace` fixé hors du fichier ou d'un `_Imports.razor` (par exemple
  dans le `.csproj`) n'est pas lu par le générateur : la vue est alors
  introuvable par nom (utiliser `View<T>()`).
- `HEAD` d'un fichier statique n'envoie pas `Content-Length`.
- Les vues sont du HTML statique : pas d'événements Blazor ; le JavaScript du
  `wwwroot` et les sites Blazor interactifs (`web/9`) couvrent l'interactivité.

## Tâches

- [x] `HackViewEngine`, `HackViewContext`, `HackView`/`HackView<TModel>`/`HackLayout`, layouts imbriqués rendus après la vue, sections, partials, `@inject`.
- [x] `HackViewResult`, `View`/`PartialView`/`ViewData` sur `HackController`, erreurs explicites (vue introuvable, modèle, section, `@Body`).
- [x] Helpers `HackLink`, `HackForm`, `HackAntiforgeryToken`, `HackValidationMessage`, `HackValidationSummary`, `HackSection`, `HackRenderSection`, `HackPartial`, `HackRoute.Values`.
- [x] `HackAntiforgery`, `[ValidateAntiForgeryToken]`, `[IgnoreAntiforgeryToken]`.
- [x] Table des vues dans le générateur de sources (`HackWebGeneratedRoutes.Views`), hôte généré, `HackWebApplicationBuilder`/`HackWebHost` avec `HackViewTable`.
- [x] `wwwroot` embarqué au build, `UseStaticFiles()`, règle ADR 0007 ajustée pour `<script src>`.
- [x] Tests du pipeline, du générateur et de bout en bout (`curl`, deux modes).
- [x] ADR 0083, ce document, index, plan, `asp-hack-mvc.md`.
