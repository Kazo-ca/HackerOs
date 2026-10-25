> [English version](../../../en/wasm/framework/asp-hack-cshtml-views.md)

# ASP.Hack : vues `.cshtml`

Chantier `web/8-cshtml-views` de la phase web. Décisions : [ADR 0076](../dev/adr/0076-asp-hack-mvc.md)
§8 et [ADR 0086](../dev/adr/0086-asp-hack-cshtml-views.md). Socle : [`asp-hack-razor-views.md`](asp-hack-razor-views.md)
(moteur de vues, layouts, sections, anti-falsification, `wwwroot`) et [`asp-hack-mvc.md`](asp-hack-mvc.md).
Plan : [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md).

## Objet

Un site ASP.Hack peut écrire ses vues comme dans ASP.NET Core MVC : des fichiers
`.cshtml` avec `@model`, `@inject`, `@section`, `@functions`, des modèles en
ligne, `_Layout.cshtml`, `_ViewStart.cshtml` et `_ViewImports.cshtml`. Le
compilateur Razor du SDK .NET les compile au build, sans ASP.NET MVC (qui ne
tourne pas dans le navigateur) : la classe de base est `HackRazorPage<TModel>`
d'ASP.Hack. `View()` trouve indifféremment `Views/<Contrôleur>/<Action>.razor`
ou `.cshtml`, et les deux genres se mêlent (vue `.cshtml` dans un layout
`.razor`, partial `.razor` dans une vue `.cshtml`, etc.).

## Preuve de faisabilité

L'ADR 0076 §8 demandait une preuve avant le code. Elle a été faite avec le SDK
.NET 10.0.402 sur un projet `Microsoft.NET.Sdk.Razor` minimal :

1. Le générateur de sources Razor compile un `.cshtml` en mode MVC même sans
   référence à MVC ; le C# produit appelle des méthodes de la classe de base
   (`WriteLiteral`, `Write`, `BeginWriteAttribute`, `DefineSection`,
   `PushWriter`...) et ne nomme d'ASP.NET que quelques attributs, trois espaces
   de noms, `HelperResult` et les types des cinq `@inject` implicites.
2. Un `_ViewImports.cshtml` racine remplace la classe de base
   (`@inherits HackRazorPage<TModel>`, `TModel` étant remplacé par le type de
   `@model`) et les cinq `@inject` implicites (une propriété de même nom gagne).
3. Avec ces noms déclarés comme types internes au site, une vue avec `@model`,
   `@inject`, `@section`, `@foreach`, `@if`, `@functions`, un `_ViewStart` et un
   `_Layout` compile et s'exécute ; le nommage des classes
   (`AspNetCoreGeneratedDocument.Views_Home_Index`, ou l'espace de noms du
   `@namespace` le plus proche suivi des dossiers) a été vérifié.

Le moteur interprété de repli (question 17 du plan) n'est donc pas construit.

## Exemple

```text
Intranet/
  Controllers/NotesController.cs
  Views/
    _ViewImports.cshtml      @using Intranet.Models
    _ViewStart.cshtml        @{ Layout = "_Layout"; }
    Shared/_Layout.cshtml
    Notes/Index.cshtml, Notes/Edit.cshtml, Shared/_NoteCard.cshtml
  wwwroot/css/site.css, wwwroot/js/notes.js
```

```cshtml
@* Views/Shared/_Layout.cshtml *@
<!DOCTYPE html>
<html>
<head>
    <title>@(ViewData.Title ?? "Intranet")</title>
    <link rel="stylesheet" href="@Url.Content("~/css/site.css")" />
</head>
<body>
    <nav>@Html.ActionLink("Notes", "Index", "Notes")</nav>
    <main>@RenderBody()</main>
    @RenderSection("Scripts", required: false)
</body>
</html>

@* Views/Notes/Index.cshtml *@
@model IReadOnlyList<Note>
@inject NoteStore Store
@{ ViewData.Title = "Notes"; }
<h1>@Model.Count notes</h1>
<ul>
    @foreach (Note note in Model)
    {
        @await Html.PartialAsync("_NoteCard", note)
    }
</ul>
@section Scripts { <script src="@Url.Content("~/js/notes.js")"></script> }

@* Views/Notes/Edit.cshtml *@
@model Note
@Html.ValidationSummary(excludePropertyErrors: true)
@using (Html.BeginForm("Edit", "Notes", new { id = Model.Id }))
{
    @Html.LabelFor(m => m.Title)
    @Html.TextBoxFor(m => m.Title)
    @Html.ValidationMessageFor(m => m.Title)
    <button type="submit">Save</button>
}
```

Le contrôleur ne change pas : `return View(store.All);`, `return View(note);`.

## Architecture

```text
Build (build/HackerOs.Apps.targets, cible _HackerOsAspHackRazorViews)
  si le projet référence HackerOs.AppSdk.Web et a des .cshtml :
    + build/asp-hack/_ViewImports.cshtml lié en _ViewImports.cshtml racine
      (@inherits HackRazorPage<TModel>, Html/Json/Component/Url/ModelExpressionProvider)
    + build/asp-hack/HackRazorCompat.cs (types internes aux noms Microsoft.AspNetCore.*)
  compilateur Razor du SDK → classe par .cshtml
  générateur de routes (ADR 0082) → HackWebGeneratedRoutes.Views (genres Component et RazorPage)

Rendu (Shared/HackerOs.AppSdk.Web/Views/)
  HackViewEngine.RenderAsync
    ├─ vue .cshtml : _ViewStart.cshtml de Views/ puis des dossiers → Layout initial
    │                HackRazorPage.RenderAsync : @inject remplis, ExecuteAsync, sections → HTML
    ├─ vue .razor  : passe HtmlRenderer (ADR 0083)
    └─ layouts de l'intérieur vers l'extérieur, de l'un ou l'autre genre
HackRazorPage / HackRazorPage<TModel>    HackRazorPage.cs
HackHtmlHelper<TModel>, HackHtmlForm, HackJsonHelper, HackViewComponentHelper,
  HackModelExpressionProvider<TModel>    HackHtmlHelper.cs
IHackHtmlContent, HackHtmlString, HackHelperResult   HackHtmlContent.cs
HackViewKind, HackViewDescriptor.Kind, HackViewTable.Find(Type)   HackViewTable.cs
```

## Pages `.cshtml`

- **Classe de base.** Toute page dérive de `HackRazorPage<TModel>` (sans `@model`,
  `TModel` est `dynamic`, comme dans ASP.NET ; préférez `@model`). Membres :
  `Model`, `ViewData`, `ViewContext`, `Context` (la requête), `Request`,
  `ModelState`, `Layout`, `Output`, `RenderBody()`, `RenderSection(...)`,
  `RenderSectionAsync(...)`, `IsSectionDefined`, `IgnoreBody`, `IgnoreSection`.
- **Encodage.** `@valeur` est encodé en HTML (les lettres accentuées restent
  lisibles) ; `IHackHtmlContent` (`Html.Raw`, `RenderBody()`, helpers,
  modèles en ligne) et `MarkupString` sont écrits tels quels. Attributs comme
  ASP.NET : une partie `null` disparaît avec son espace (`class="note @(x ? "done" : null)"`),
  un attribut dont la seule valeur est `null` ou `false` disparaît, `true` écrit
  le nom (`checked="@note.Done"` → `checked="checked"`).
- **`@inject`.** Résolu dans la portée DI de la requête (les mêmes instances que
  le contrôleur). Un service absent est une erreur qui nomme la propriété.
- **`_ViewImports.cshtml`.** Celui de la racine du projet est fourni par le SDK
  (un projet qui en a un échoue au build) ; mettez vos `@using`, `@inject` ou
  `@namespace` dans `Views/_ViewImports.cshtml` ou un sous-dossier.
- **`_ViewStart.cshtml`.** Ceux de `Views/` puis de chaque dossier jusqu'à celui
  de la vue s'exécutent avant une vue (pas avant un partial ni un layout) ; leur
  `Layout` devient la valeur initiale de celui de la vue. Leur sortie est ignorée.
- **Layout.** `Layout = "_Layout"` est un nom de vue cherché comme `View()` le fait
  (dossier du contrôleur, puis `Shared/`), ou un chemin
  (`"~/Views/Shared/_Layout.cshtml"`) ; `Layout = null` : sans layout. Un layout
  `.cshtml` doit appeler `RenderBody()` (ou `IgnoreBody()`). Un layout peut
  avoir son propre `Layout` (imbrication) et redéfinir une section qu'il a
  rendue, pour le layout autour de lui :
  `@section Scripts { @RenderSection("Scripts", required: false) ... }`.
- **Sections.** `@section Nom { ... }` ; `@RenderSection("Nom")` (requise par
  défaut) ou `RenderSection("Nom", required: false)`. Une section définie mais
  jamais rendue est une erreur, comme pour les vues `.razor`. Les sections sont
  rendues en HTML dès la fin de la page qui les définit.
- **`ViewData`.** Partagé avec le contrôleur et les layouts ; une clé absente se
  lit `null` (`ViewData["Starts"]`), pour les deux genres de vues.

## Helpers

| Membre | Rend |
|---|---|
| `Html.Raw(html)`, `Html.Encode(v)` | HTML tel quel ; texte encodé. |
| `Html.ActionLink(texte, action, contrôleur?, routeValues?, htmlAttributes?)`, `Html.RouteLink(...)` | `<a href>` produit par la table de routes. |
| `using (Html.BeginForm(action?, contrôleur?, routeValues?, method: "post", antiforgery: true, htmlAttributes?))` | `<form action method>` ; à la fin, le champ anti-falsification (sauf `GET`) et `</form>`. |
| `Html.AntiForgeryToken()` | Le champ caché pour un `<form>` écrit à la main. |
| `Html.TextBox/Hidden/Password/TextArea/CheckBox(nom, ...)` | Champs ; après un `POST` refusé, la valeur postée revient et l'erreur ajoute `input-validation-error`. |
| `Html.TextBoxFor/HiddenFor/TextAreaFor/CheckBoxFor(m => m.X)`, `NameFor`, `IdFor`, `ValueFor`, `DisplayNameFor`, `LabelFor` | Champs liés au modèle par une chaîne de membres (`m => m.Address.City` → `Address.City`) ; `[Display(Name)]` respecté. |
| `Html.ValidationMessage(nom)`, `Html.ValidationMessageFor(m => m.X)`, `Html.ValidationSummary(excludePropertyErrors)` | Même HTML que `HackValidationMessage` et `HackValidationSummary` (ADR 0083). |
| `await Html.PartialAsync(nom, modèle?)`, `await Html.RenderPartialAsync(...)` | Un partial `.cshtml` ou `.razor` (dossier du contrôleur, puis `Shared/`, ou chemin). |
| `Json.Serialize(valeur)` | JSON avec les options du site (camelCase). |
| `@(await Component.InvokeAsync<TComposant>(paramètres))` | Un composant Razor rendu dans la page, avec le contexte de la vue (les helpers `.razor` de l'ADR 0083 marchent). Les parenthèses sont nécessaires à cause du `<` générique. |
| `Url.Action(...)`, `Url.RouteUrl(...)`, `Url.Content("~/...")` | Le `IHackUrlHelper` du site. |

`htmlAttributes` et les valeurs de route d'un objet anonyme passent par
`HackRoute.Values(new { @class = "nav" })` (un `_` devient `-` : `data_id` →
`data-id`).

## Mélange des genres

- Une vue `.cshtml` peut avoir un layout `.razor` (`Layout = "_Layout"` trouve
  `Shared/_Layout.razor`) : ses sections arrivent dans `<HackRenderSection>`.
- Une vue `.razor` peut prendre un layout `.cshtml` en fixant
  `Layout = typeof(AspNetCoreGeneratedDocument.Views_Shared__Layout)` ; ses
  `<HackSection>` sont rendues en HTML avant le layout.
- `<HackPartial Name="_Card" />` dans un `.razor` rend aussi un partial `.cshtml`.
- Un `.razor` et un `.cshtml` au même chemin sont l'erreur de build `HOSWEB0008`.

## Tests

- `Tests/HackerOs.AppSdk.Web.Tests/Views/CshtmlViewTests` : table générée et
  noms de classes, `_ViewStart` imbriqués, `@model`, `@inject`, layout, titre,
  section, attributs conditionnels, partials des deux genres, modèle en ligne,
  `Html.Raw`, `Component.InvokeAsync`, `Json.Serialize`, layouts imbriqués et
  section redéfinie, mélange dans les deux sens, `Layout = null`,
  `PartialView`, chemin absolu, formulaire lié au modèle avec anti-falsification
  et erreurs ré-affichées, erreurs (corps non rendu, section requise absente,
  section orpheline, mauvais modèle), emplacements cherchés.
- `Generator/WebRouteGeneratorTests` : noms des classes `.cshtml` (`@namespace`
  du fichier et d'un `_ViewImports.cshtml` avec les dossiers, caractères
  spéciaux), imports exclus, `HOSWEB0008`.
- `Tests/HackerOs.Platform.WorkerHost.Tests/MvcSiteTests` : une vue `.cshtml`
  avec `_ViewStart`, layout `.cshtml`, formulaire et section servie par `curl`
  dans un OS complet, sur le fil principal et dans le worker `web`.

## Limites connues

- Pas de tag helpers (`asp-action`, `asp-for`, `<partial>`) : utilisez les
  helpers HTML ou `Component.InvokeAsync`. Un `~/` écrit en dur dans un attribut
  n'est pas résolu : `@Url.Content("~/...")`.
- Pas de Razor Pages (`@page`), de `ViewBag`, de `TempData` ni de view
  components MVC ; `Component` rend des composants Razor.
- `_ViewStart.cshtml` hors de `Views/` n'est pas lu.
- Les sections sont rendues dès la fin de la page qui les définit, pas à la
  demande du layout (même HTML, sauf effet de bord).
- La compilation d'un `.cshtml` **dans l'OS** reste hors périmètre (ADR 0076 §8).

## Tâches

- [x] Preuve de faisabilité (compilation au build sans MVC) et ADR 0086.
- [x] `_ViewImports.cshtml` racine et types de compatibilité ajoutés au build, `RAZORSDK1004` en message.
- [x] `HackRazorPage`/`HackRazorPage<TModel>` : écriture, attributs, modèles en ligne, sections, corps, activation des `@inject`.
- [x] Helpers `Html`, `Json`, `Component`, `ModelExpressionProvider`.
- [x] Moteur : genres `Component`/`RazorPage`, `_ViewStart`, layouts `.cshtml`, mélange, partials des deux genres, `ViewData` sans clé → `null`.
- [x] Générateur : vues `.cshtml` dans la table, `HOSWEB0008`.
- [x] Règle de l'ADR 0007 étendue aux `.cshtml`.
- [x] Tests (pipeline, générateur, bout en bout dans les deux modes).
- [x] ADR 0086, ce document, index, plan, `asp-hack-mvc.md`, `asp-hack-razor-views.md`.
